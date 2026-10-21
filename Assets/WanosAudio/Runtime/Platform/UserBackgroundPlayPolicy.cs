namespace WanosAudio
{
    /// <summary>
    /// 用户可配置「后台播放」关闭（可选覆盖）。主路径仍是服务端机型策略 + 原生后台权限请求
    ///（Android <c>RequestPowerOptimization</c> / 引导页后台保活；<c>SetBackgroundPlayingAllowed</c>）。
    /// 系统设置页不再提供独立「后台播放」开关，避免与权限弹窗双源。
    /// disable=true 表示禁止后台播放。
    /// </summary>
    public static class UserBackgroundPlayPolicy
    {
        public const string PrefsKey = "boluo_user_background_play_disable";

        /// <summary>用户是否关闭后台播放（true=关后台播放）。</summary>
        public static bool GetUserBackgroundPlayDisable()
        {
            if (!UnityEngine.PlayerPrefs.HasKey(PrefsKey))
                return false;
            return UnityEngine.PlayerPrefs.GetInt(PrefsKey, 0) != 0;
        }

        public static void SetUserBackgroundPlayDisable(bool disable)
        {
            UnityEngine.PlayerPrefs.SetInt(PrefsKey, disable ? 1 : 0);
            UnityEngine.PlayerPrefs.Save();
        }
    }
}
