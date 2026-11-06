package com.wanos.app.health;

/**
 * 负一屏播放 / 暂停 / 切歌状态机（时钟由调用方注入）。
 * 连点只落地最后一次播放或暂停；切歌在 in-flight 与同向防抖内不改曲目，
 * 接受的切歌作废尚未落地的暂停。定时到点切歌保持暂停。
 */
public final class BoluoNowPlayingRemoteSession {

    private final int trackCount;
    private int unityIndex;
    private int nativeIndex;
    private int lastSkipDirection;
    private long lastAcceptedSkipAtMs = -1L;
    private long pendingAtMs = -1L;
    private boolean pendingWantMute;
    private Boolean lastFlushedWantMute;

    private boolean isPlaying;
    private boolean userManuallyPaused;
    private boolean pausedForTimerEnd;
    private boolean skipInFlight;
    private int enginePauseCount;
    private int engineResumeCount;
    private int acceptedSkipCount;
    private int ignoredSkipCount;
    private int resyncCount;

    public BoluoNowPlayingRemoteSession(
            int trackCount,
            boolean isPlaying,
            boolean userManuallyPaused,
            int trackIndex) {
        if (trackCount <= 0) {
            throw new IllegalArgumentException("trackCount");
        }
        this.trackCount = trackCount;
        if (trackIndex < 0 || trackIndex >= trackCount) {
            trackIndex = 0;
        }
        this.unityIndex = trackIndex;
        this.nativeIndex = trackIndex;
        this.isPlaying = isPlaying;
        this.userManuallyPaused = userManuallyPaused;
    }

    public BoluoNowPlayingRemoteSession(int trackCount, boolean isPlaying, boolean userManuallyPaused) {
        this(trackCount, isPlaying, userManuallyPaused, 0);
    }

    public synchronized boolean isPlaying() {
        return isPlaying;
    }

    public synchronized boolean isUserManuallyPaused() {
        return userManuallyPaused;
    }

    public synchronized boolean isPausedForTimerEnd() {
        return pausedForTimerEnd;
    }

    /** 负一屏当前曲。Unity 自有切歌后不得被滞后的原生索引盖回。 */
    public synchronized int getTrackIndex() {
        return unityIndex;
    }

    public synchronized boolean isSkipInFlight() {
        return skipInFlight;
    }

    public synchronized boolean hasPendingPlayPause() {
        return pendingAtMs >= 0;
    }

    public synchronized long getPendingAtMs() {
        return pendingAtMs;
    }

    public synchronized int getEnginePauseCount() {
        return enginePauseCount;
    }

    public synchronized int getEngineResumeCount() {
        return engineResumeCount;
    }

    public synchronized int getAcceptedSkipCount() {
        return acceptedSkipCount;
    }

    public synchronized int getIgnoredSkipCount() {
        return ignoredSkipCount;
    }

    public synchronized int getResyncCount() {
        return resyncCount;
    }

    /** 负一屏卡片播放态。系统挂起由调用方另算。 */
    public synchronized boolean cardShowsPlaying() {
        return !userManuallyPaused;
    }

    /**
     * 用引擎当前出声 / 用户暂停闩覆盖会话，不碰 pending 和切歌 in-flight。
     * 外部路径（焦点、退登）改过闩之后，下一笔远程键前调用。
     */
    public synchronized void adoptPlayback(boolean audible, boolean manuallyPaused) {
        isPlaying = audible;
        userManuallyPaused = manuallyPaused;
    }

    public synchronized void cancelPendingPlayPause() {
        pendingAtMs = -1L;
        lastFlushedWantMute = null;
    }

    /**
     * 定时到点。置暂停并丢掉尚未落地的播放，避免到点后合并窗口又把声音拉起来。
     */
    public synchronized void notePausedForTimerEnd(boolean paused) {
        pausedForTimerEnd = paused;
        if (!paused) {
            return;
        }
        userManuallyPaused = true;
        isPlaying = false;
        cancelPendingPlayPause();
    }

    /** @param reason 空串为负一屏真实播放；{@code ui} 只刷卡 */
    public synchronized void pressPlay(long nowMs, String reason) {
        if (!BoluoNowPlayingRemoteCommandPolicy.shouldDriveEngineResumeFromNowPlayingPlay(reason)) {
            return;
        }
        if (pausedForTimerEnd) {
            return;
        }
        schedulePlayPause(false, nowMs);
    }

    public synchronized void pressPlay(long nowMs) {
        pressPlay(nowMs, "");
    }

    public synchronized void pressPause(long nowMs) {
        schedulePlayPause(true, nowMs);
    }

    /**
     * 用调用方看到的引擎状态播种后再记播放/暂停。连点播种不会清掉上一笔 pending，只改最终意图。
     * {@code wantPause == false} 且 reason 为 {@code ui} 时整笔忽略。
     */
    public synchronized void seedAndPress(
            boolean audible,
            boolean manuallyPaused,
            boolean wantPause,
            long nowMs,
            String playReason) {
        if (!wantPause
                && !BoluoNowPlayingRemoteCommandPolicy.shouldDriveEngineResumeFromNowPlayingPlay(playReason)) {
            return;
        }
        if (!wantPause && pausedForTimerEnd) {
            return;
        }
        isPlaying = audible;
        userManuallyPaused = manuallyPaused;
        schedulePlayPause(wantPause, nowMs);
    }

    public synchronized boolean pressNext(long nowMs) {
        return acceptSkip(+1, nowMs);
    }

    public synchronized boolean pressPrev(long nowMs) {
        return acceptSkip(-1, nowMs);
    }

    /**
     * 先判定忽略（忽略不改播放态），接受后再用引擎状态播种，避免连点切歌把已接受的播放态盖成暂停。
     */
    public synchronized boolean seedAndSkip(
            boolean audible,
            boolean manuallyPaused,
            int direction,
            long nowMs) {
        if (direction == 0) {
            direction = 1;
        }
        if (!beginSkip(direction, nowMs)) {
            return false;
        }
        isPlaying = audible;
        userManuallyPaused = manuallyPaused;
        finishSkip(direction, nowMs);
        return true;
    }

    public synchronized void noteSkipSettled() {
        if (!skipInFlight) {
            return;
        }
        skipInFlight = false;
        nativeIndex = unityIndex;
    }

    public synchronized void tick(long nowMs) {
        lastFlushedWantMute = null;
        if (skipInFlight
                && lastAcceptedSkipAtMs >= 0
                && nowMs - lastAcceptedSkipAtMs >= BoluoNowPlayingRemoteCommandPolicy.SKIP_IN_FLIGHT_TIMEOUT_MS) {
            noteSkipSettled();
        }
        if (pendingAtMs < 0 || nowMs < pendingAtMs) {
            return;
        }
        boolean wantMute = pendingWantMute;
        pendingAtMs = -1L;
        lastFlushedWantMute = wantMute;
        flushPlayPause(wantMute);
    }

    /** 最近一次 {@link #tick} 跨过合并点时的意图；没有则为 null。读完即清。 */
    public synchronized Boolean pollFlushedWantMute() {
        Boolean want = lastFlushedWantMute;
        lastFlushedWantMute = null;
        return want;
    }

    private void schedulePlayPause(boolean wantMute, long nowMs) {
        pendingWantMute = wantMute;
        if (BoluoNowPlayingRemoteCommandPolicy.shouldCoalescePlayPause()) {
            pendingAtMs = nowMs + BoluoNowPlayingRemoteCommandPolicy.PLAY_PAUSE_COALESCE_MS;
        } else {
            lastFlushedWantMute = wantMute;
            flushPlayPause(wantMute);
        }
    }

    private void flushPlayPause(boolean wantMute) {
        boolean already = BoluoNowPlayingRemoteCommandPolicy.shouldSkipFlushBecauseAlreadyConsistent(
                wantMute, !isPlaying, userManuallyPaused);
        if (already) {
            return;
        }
        if (wantMute) {
            userManuallyPaused = true;
            isPlaying = false;
            enginePauseCount++;
            return;
        }
        if (pausedForTimerEnd) {
            return;
        }
        pausedForTimerEnd = false;
        userManuallyPaused = false;
        isPlaying = true;
        engineResumeCount++;
    }

    private boolean acceptSkip(int direction, long nowMs) {
        if (direction == 0) {
            direction = 1;
        }
        if (!beginSkip(direction, nowMs)) {
            return false;
        }
        finishSkip(direction, nowMs);
        return true;
    }

    /** @return false 表示这次 skip 被丢掉（播放态保持原样） */
    private boolean beginSkip(int direction, long nowMs) {
        long elapsed = lastAcceptedSkipAtMs < 0
                ? Long.MAX_VALUE
                : nowMs - lastAcceptedSkipAtMs;
        boolean sameDir = lastSkipDirection != 0 && lastSkipDirection == direction;
        if (skipInFlight && elapsed >= BoluoNowPlayingRemoteCommandPolicy.SKIP_IN_FLIGHT_TIMEOUT_MS) {
            noteSkipSettled();
        }
        if (BoluoNowPlayingRemoteCommandPolicy.shouldIgnoreSkip(skipInFlight, sameDir, elapsed)) {
            ignoredSkipCount++;
            return false;
        }
        return true;
    }

    private void finishSkip(int direction, long nowMs) {
        if (BoluoNowPlayingRemoteCommandPolicy.shouldCancelPendingPlayPauseOnSkip()) {
            pendingAtMs = -1L;
            lastFlushedWantMute = null;
        }
        skipInFlight = true;
        lastSkipDirection = direction;
        lastAcceptedSkipAtMs = nowMs;
        acceptedSkipCount++;

        int laggedNative = nativeIndex;
        unityIndex = wrap(unityIndex + direction);
        if (BoluoNowPlayingRemoteCommandPolicy.shouldResyncAfterUnityOwnedSkip()) {
            unityIndex = laggedNative;
            resyncCount++;
        }
        if (!pausedForTimerEnd) {
            userManuallyPaused = false;
            isPlaying = true;
        }
    }

    private int wrap(int index) {
        if (trackCount == 1) {
            return 0;
        }
        return ((index % trackCount) + trackCount) % trackCount;
    }
}
