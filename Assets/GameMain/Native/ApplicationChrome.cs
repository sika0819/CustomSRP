#if UNITY_ANDROID && !UNITY_EDITOR
#define USE_ANDROID
#endif
#if UNITY_IOS && !UNITY_EDITOR
#define USE_IOS
#endif

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Native
{
    /// <summary>
    /// 系统栏显隐：Android IMMERSIVE_STICKY；iOS 藏状态栏 + Home 指示条。
    /// </summary>
    public static class ApplicationChrome
    {
        public enum States
        {
            Unknown,
            Visible,
            VisibleOverContent,
            TranslucentOverContent,
            Hidden
        }

#if USE_ANDROID
        const int VIEW_SYSTEM_UI_FLAG_HIDE_NAVIGATION = 2;
        const int VIEW_SYSTEM_UI_FLAG_FULLSCREEN = 4;
        const int VIEW_SYSTEM_UI_FLAG_LAYOUT_STABLE = 256;
        const int VIEW_SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION = 512;
        const int VIEW_SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN = 1024;
        const int VIEW_SYSTEM_UI_FLAG_LIGHT_STATUS_BAR = 0x00002000;
        const int VIEW_SYSTEM_UI_FLAG_IMMERSIVE_STICKY = 4096;

        const int WINDOW_FLAG_FORCE_NOT_FULLSCREEN = 0x00000800;
        const int WINDOW_FLAG_LAYOUT_IN_SCREEN = 0x00000100;
        const int WINDOW_FLAG_TRANSLUCENT_STATUS = 0x04000000;
        const int WINDOW_FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS = unchecked((int)0x80000000);

        static int _systemUiVisibilityValue;
        static int _flagsValue;
#endif

        static States _statusBarState = States.VisibleOverContent;
#if USE_ANDROID
        static uint _statusBarColor = 0xff000000;
#endif
        static bool _isStatusBarTranslucent;
        static bool _statusBarIconsDark;

        static ApplicationChrome()
        {
            ApplyUiStates();
            ApplyUiColors();
        }

        public static States StatusBarState
        {
            get => _statusBarState;
            set
            {
                if (_statusBarState == value)
                    return;
                _statusBarState = value;
                ApplyUiStates();
            }
        }

        public static bool StatusBarIconsDark
        {
            get => _statusBarIconsDark;
            set
            {
                if (_statusBarIconsDark == value)
                    return;
                _statusBarIconsDark = value;
                ApplyUiStates();
            }
        }

        static void ApplyUiStates()
        {
#if USE_ANDROID
            ApplyUiStatesAndroid();
#endif
#if USE_IOS
            ApplyUiStatesIos();
#endif
        }

        static void ApplyUiColors()
        {
#if USE_ANDROID
            RunOnAndroidUiThread(ApplyUiColorsAndroidInThread);
#endif
        }

#if USE_ANDROID
        static void ApplyUiStatesAndroid()
        {
            int newFlagsValue = 0;
            int newSystemUiVisibilityValue = 0;

            if (_statusBarColor != 0xff000000)
                newFlagsValue |= WINDOW_FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS;

            switch (_statusBarState)
            {
                case States.Visible:
                    _isStatusBarTranslucent = false;
                    newFlagsValue |= WINDOW_FLAG_FORCE_NOT_FULLSCREEN;
                    break;
                case States.VisibleOverContent:
                    _isStatusBarTranslucent = false;
                    newFlagsValue |= WINDOW_FLAG_FORCE_NOT_FULLSCREEN | WINDOW_FLAG_LAYOUT_IN_SCREEN;
                    newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN;
                    break;
                case States.TranslucentOverContent:
                    _isStatusBarTranslucent = true;
                    newFlagsValue |= WINDOW_FLAG_FORCE_NOT_FULLSCREEN | WINDOW_FLAG_LAYOUT_IN_SCREEN | WINDOW_FLAG_TRANSLUCENT_STATUS;
                    newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN;
                    break;
                case States.Hidden:
                    // 只用 SYSTEM_UI_FLAG 藏栏，禁止 WINDOW_FLAG_FULLSCREEN（会把 Screen 短时按竖屏上报）。
                    newFlagsValue |= WINDOW_FLAG_LAYOUT_IN_SCREEN;
                    if (_isStatusBarTranslucent)
                        newFlagsValue |= WINDOW_FLAG_TRANSLUCENT_STATUS;
                    newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_LAYOUT_STABLE;
                    newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN;
                    newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_FULLSCREEN;
                    newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION;
                    newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_HIDE_NAVIGATION;
                    newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_IMMERSIVE_STICKY;
                    break;
            }

            if (_statusBarIconsDark && _statusBarState != States.Hidden)
            {
                newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_LAYOUT_STABLE;
                newSystemUiVisibilityValue |= VIEW_SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
            }

            // 不要改 Screen.fullScreen：false 会让 Unity 按窗口缓冲上报尺寸，
            // 横→竖→横后 Screen 停在竖屏，UI 一直走竖屏适配。
            SetFlags(newFlagsValue);
            SetSystemUiVisibility(newSystemUiVisibilityValue);
        }

        static void RunOnAndroidUiThread(Action target)
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                activity.Call("runOnUiThread", new AndroidJavaRunnable(target));
            }
        }

        static void SetSystemUiVisibility(int value)
        {
            if (_systemUiVisibilityValue == value)
                return;
            _systemUiVisibilityValue = value;
            RunOnAndroidUiThread(SetSystemUiVisibilityInThread);
        }

        static void SetSystemUiVisibilityInThread()
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var window = activity.Call<AndroidJavaObject>("getWindow"))
            using (var view = window.Call<AndroidJavaObject>("getDecorView"))
            {
                view.Call("setSystemUiVisibility", _systemUiVisibilityValue);
            }
        }

        static void SetFlags(int value)
        {
            if (_flagsValue == value)
                return;
            _flagsValue = value;
            RunOnAndroidUiThread(SetFlagsInThread);
        }

        static void SetFlagsInThread()
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var window = activity.Call<AndroidJavaObject>("getWindow"))
            {
                window.Call("setFlags", _flagsValue, -1);
            }
        }

        static void ApplyUiColorsAndroidInThread()
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var window = activity.Call<AndroidJavaObject>("getWindow"))
            {
                window.Call("setStatusBarColor", unchecked((int)_statusBarColor));
            }
        }
#endif

#if USE_IOS
        [DllImport("__Internal")]
        static extern void BoluoStatusBar_SetHidden(int hidden);

        static void ApplyUiStatesIos()
        {
            BoluoStatusBar_SetHidden(_statusBarState == States.Hidden ? 1 : 0);
        }
#endif
    }
}
