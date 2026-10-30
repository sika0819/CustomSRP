using System;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// iPhone / iPod 锁竖屏。iPad 允许竖屏和左右横屏。
    /// Android 方向由宿主 MainActivity 决定，这里不改。
    /// </summary>
    public static class DeviceOrientationPolicy
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Apply()
        {
#if UNITY_IOS && !UNITY_EDITOR
            bool pad = IsIpad();
            Screen.autorotateToPortrait = true;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = pad;
            Screen.autorotateToLandscapeRight = pad;
            Screen.orientation = pad
                ? ScreenOrientation.AutoRotation
                : ScreenOrientation.Portrait;
#endif
        }

        static bool IsIpad()
        {
            string model = SystemInfo.deviceModel;
            return !string.IsNullOrEmpty(model)
                && model.StartsWith("iPad", StringComparison.Ordinal);
        }
    }
}
