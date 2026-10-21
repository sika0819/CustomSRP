namespace WanosAudio
{
    /// <summary>
    /// 后台播放策略回调，用于避免 WanosAudio 和 BridgeLib 之间的循环依赖。
    /// 最终是否禁用 = 服务端机型策略 OR 用户设置关闭后台播放。
    /// </summary>
    public static class BackgroundPlayPolicyCallback
    {
        /// <summary>
        /// 获取后台播放是否禁用的回调函数
        /// </summary>
        public delegate bool GetBackgroundPlayDisableDelegate();

        private static GetBackgroundPlayDisableDelegate _getBackgroundPlayDisable;

        /// <summary>
        /// 设置获取后台播放策略的回调函数
        /// </summary>
        /// <param name="callback">回调函数，返回 true 表示禁用后台播放，false 表示允许后台播放</param>
        public static void SetBackgroundPlayDisableCallback(GetBackgroundPlayDisableDelegate callback)
        {
            _getBackgroundPlayDisable = callback;
        }

        public static void ClearCallback()
        {
            _getBackgroundPlayDisable = null;
        }

        /// <summary>
        /// 获取后台播放是否禁用
        /// </summary>
        /// <returns>true 表示禁用后台播放，false 表示允许后台播放</returns>
        public static bool GetBackgroundPlayDisable()
        {
            bool serverOrDeviceDisable = false;
            if (_getBackgroundPlayDisable != null)
                serverOrDeviceDisable = _getBackgroundPlayDisable();

            return serverOrDeviceDisable || UserBackgroundPlayPolicy.GetUserBackgroundPlayDisable();
        }
    }
}
