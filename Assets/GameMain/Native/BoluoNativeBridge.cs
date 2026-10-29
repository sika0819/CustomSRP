using UnityEngine;

namespace Game.Native
{
    /// <summary>主线程 Drain Push 队列。建在 Launch 里，Launch 不卸载。</summary>
    public sealed class BoluoNativeBridge : MonoBehaviour
    {
        static BoluoNativeBridge _instance;

        public static BoluoNativeBridge Instance => _instance;

        public static void EnsureExists()
        {
            if (_instance != null)
                return;
            var go = new GameObject("BoluoNativeBridge");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<BoluoNativeBridge>();
            NativePushReceiver.EnsureRegistered();
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            NativePushReceiver.EnsureRegistered();
        }

        void Update()
        {
#if UNITY_EDITOR
            EditorNativeHost.Tick();
#endif
            NativePushReceiver.Drain();
            Game.NowPlayingService.Tick();
#if UNITY_EDITOR
            NativeEditorHud.Tick();
            NativePushReceiver.Drain();
#endif
        }

        void OnApplicationPause(bool paused)
        {
            NativePushReceiver.Drain();
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
                NativePushReceiver.Drain();
        }

#if UNITY_EDITOR
        void OnGUI()
        {
            NativeEditorHud.OnGUI();
        }
#endif

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
