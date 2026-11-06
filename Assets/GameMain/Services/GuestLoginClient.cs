namespace Game
{
    /// <summary>
    /// Editor 游客登录开关与旧 API 环境键。
    /// 进岛走 EditorNativeHost → BoluoDeviceLoginClient。
    /// </summary>
    public static class GuestLoginClient
    {
        public const string EditorTryRemoteKey = "Game.Editor.TryRemoteGuestLogin";

        /// <summary>旧键；环境以 <see cref="Game.BoluoApi.BoluoApiEnvironment"/> 为准。</summary>
        public const string ApiEnvKey = "Game.Editor.ApiEnv";

        public static bool ShouldTryRemoteInEditor()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorPrefs.GetBool(EditorTryRemoteKey, false);
#else
            return false;
#endif
        }
    }
}
