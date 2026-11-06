package com.wanos.app.health;

/**
 * 负一屏 / 锁屏 / 通知栏远程键防呆（纯逻辑，无 Android 依赖）。
 * 与 Unity {@code BoluoNowPlayingRemoteCommandPolicy} 对齐。
 */
public final class BoluoNowPlayingRemoteCommandPolicy {

    private BoluoNowPlayingRemoteCommandPolicy() {}

    /** 播放/暂停 last-wins 合并窗口。连点只落地最终意图。 */
    public static final long PLAY_PAUSE_COALESCE_MS = 250L;

    /** 同方向切歌防抖。挡住 MediaStyle 与 Session 连发。 */
    public static final long SKIP_DEBOUNCE_MS = 400L;

    /**
     * 引擎切歌未 settle（Playing / 超时）时忽略后续 skip。
     * 超时后放行，避免 in-flight 泄漏永久吞键。
     */
    public static final long SKIP_IN_FLIGHT_TIMEOUT_MS = 2000L;

    public static boolean shouldCoalescePlayPause() {
        return true;
    }

    /**
     * 切歌防呆：in-flight 忽略任意方向；同方向短窗忽略；反方向在非 in-flight 时立刻撤回。
     *
     * @param elapsedSinceLastAcceptedSkipMs 距上一笔已接受 skip 的毫秒；从未 skip 传极大值
     */
    public static boolean shouldIgnoreSkip(
            boolean skipInFlight,
            boolean sameDirectionAsLast,
            long elapsedSinceLastAcceptedSkipMs) {
        return shouldIgnoreSkip(
                skipInFlight,
                sameDirectionAsLast,
                elapsedSinceLastAcceptedSkipMs,
                SKIP_DEBOUNCE_MS,
                SKIP_IN_FLIGHT_TIMEOUT_MS);
    }

    public static boolean shouldIgnoreSkip(
            boolean skipInFlight,
            boolean sameDirectionAsLast,
            long elapsedSinceLastAcceptedSkipMs,
            long skipDebounceMs,
            long skipInFlightTimeoutMs) {
        if (elapsedSinceLastAcceptedSkipMs < 0) {
            elapsedSinceLastAcceptedSkipMs = Long.MAX_VALUE;
        }
        if (skipInFlight && elapsedSinceLastAcceptedSkipMs < skipInFlightTimeoutMs) {
            return true;
        }
        return sameDirectionAsLast && elapsedSinceLastAcceptedSkipMs < skipDebounceMs;
    }

    /** skip 一旦接受，必须作废尚未 flush 的 play/pause，避免新曲被补一刀暂停。 */
    public static boolean shouldCancelPendingPlayPauseOnSkip() {
        return true;
    }

    /** Unity 自有切歌后禁止立刻 Resync，滞后原生索引会把曲目盖回去。 */
    public static boolean shouldResyncAfterUnityOwnedSkip() {
        return false;
    }

    /**
     * play/pause flush 时状态已一致则不碰设备。
     * mute 须同时 userManuallyPaused：冷启动已静音不能当成已暂停。
     * play 须同时 !userManuallyPaused：未静音但暂停闩仍在时不能把续播吞掉。
     */
    public static boolean shouldSkipFlushBecauseAlreadyConsistent(
            boolean wantMute,
            boolean isMuted,
            boolean userManuallyPaused) {
        return wantMute
                ? (isMuted && userManuallyPaused)
                : (!isMuted && !userManuallyPaused);
    }

    /**
     * reason {@code ui} 只刷卡，不驱引擎。负一屏真实播放传空串或其它 reason。
     */
    public static boolean shouldDriveEngineResumeFromNowPlayingPlay(String reason) {
        return !"ui".equals(reason);
    }

    /**
     * 切歌 settle 睡醒后：代际已被更新的切歌顶掉，或用户已经暂停，则禁止再 Post 播放。
     * 暂停仍可以保留这次切歌的曲目下标。
     */
    public static boolean shouldAudiblyCommitSkip(
            long generationAtStart,
            long generationNow,
            boolean userPaused) {
        return shouldCommitAudible(generationAtStart, generationNow, userPaused);
    }

    /**
     * 播放 / 暂停 / 切歌 settle 之后才能出声。
     * 代际已经往后走，或用户已经暂停，就禁止再 Post、再把卡片改成播放。
     */
    public static boolean shouldCommitAudible(
            long generationAtStart,
            long generationNow,
            boolean userPaused) {
        return generationAtStart == generationNow && !userPaused;
    }
}
