package com.wanos.app.health;

/**
 * 微信语音 / MEDIA 打断后的续播门闩（对齐 mobile-audio-focus）。
 * <p>
 * 按住说话期间禁止续播（禁止只拉起音效）。
 * 微信语音结束（录音/通话已停，且不是音乐视频抢占）允许后台成对恢复。
 * QQ / B 站等音乐视频只在回到波萝或用户点负一屏时恢复，后台不自动续。
 * 冷启动引擎默认 mute、且没有丢焦闩，不算这次打断。
 */
public final class BoluoWeChatVoiceResumePolicy {

    private BoluoWeChatVoiceResumePolicy() {
    }

    public static final class Input {
        public final boolean userPaused;
        public final boolean shouldStop;
        /** {@code reclaimFocusOnForeground} 或已回到前台。 */
        public final boolean returnedToBoluo;
        public final boolean voicesPausedForFocusLoss;
        public final boolean musicEngineMuted;
        public final boolean pendingResume;
        /** 仍按住说话 / 通话模式 / 微信 VOICE 轨还在。后台拿它挡住恢复。 */
        public final boolean weChatVoiceStillActive;
        /** 本轮见过 QQ 音乐 / B 站等 MEDIA。微信语音本身不记。 */
        public final boolean competingMediaEpisode;

        public Input(boolean userPaused,
                     boolean shouldStop,
                     boolean returnedToBoluo,
                     boolean voicesPausedForFocusLoss,
                     boolean musicEngineMuted,
                     boolean pendingResume,
                     boolean weChatVoiceStillActive,
                     boolean competingMediaEpisode) {
            this.userPaused = userPaused;
            this.shouldStop = shouldStop;
            this.returnedToBoluo = returnedToBoluo;
            this.voicesPausedForFocusLoss = voicesPausedForFocusLoss;
            this.musicEngineMuted = musicEngineMuted;
            this.pendingResume = pendingResume;
            this.weChatVoiceStillActive = weChatVoiceStillActive;
            this.competingMediaEpisode = competingMediaEpisode;
        }
    }

    public static final class Plan {
        public final boolean resumeMusic;
        public final boolean resumeSoundEffects;
        public final boolean unmute;
        public final String reason;

        private Plan(boolean resumeMusic, boolean resumeSoundEffects, boolean unmute, String reason) {
            this.resumeMusic = resumeMusic;
            this.resumeSoundEffects = resumeSoundEffects;
            this.unmute = unmute;
            this.reason = reason;
        }

        /** 音乐、音效、解静音三者同时要。 */
        public boolean restoresMusicAndEffects() {
            return resumeMusic && resumeSoundEffects && unmute;
        }
    }

    public static Plan plan(Input in) {
        if (in.userPaused || in.shouldStop) {
            return hold("user_stopped");
        }
        // 丢焦闩，或闩丢了但仍 pending 且 mute：才是微信语音打断。
        // 仅 pending、引擎还在播时禁止 Resume_ALL（会把活实例打成 Ended 切歌）。
        boolean interrupted = in.voicesPausedForFocusLoss
                || (in.musicEngineMuted && in.pendingResume);
        if (!interrupted) {
            return hold("not_interrupted");
        }
        if (in.returnedToBoluo) {
            // 回到波萝：音乐、音效、解静音一起做。只拉音效会留下音乐仍 Pause。
            return restore("return_to_boluo");
        }
        if (in.weChatVoiceStillActive) {
            return hold("wechat_voice_hold");
        }
        if (in.competingMediaEpisode) {
            // 音乐/视频：后台不续。回前台走上面的 return_to_boluo，负一屏播放不经过这里。
            return hold("wait_foreground_or_user");
        }
        // 微信语音已结束：后台也可以成对恢复。
        return restore("wechat_voice_ended_bg");
    }

    private static Plan hold(String reason) {
        return new Plan(false, false, false, reason);
    }

    private static Plan restore(String reason) {
        return new Plan(true, true, true, reason);
    }
}
