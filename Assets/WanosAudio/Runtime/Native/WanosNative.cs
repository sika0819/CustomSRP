using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace WanosAudio
{
    /// <summary>
    /// Minimal P/Invoke surface for WIAEngine. No Boluo business logic.
    /// </summary>
    public sealed class WanosNative
    {
        const string LogTag = nameof(WanosNative);

#if UNITY_IOS && !UNITY_EDITOR
        const string DllName = "__Internal";
#else
        const string DllName = "WIAEngine";
#endif

        static readonly Lazy<WanosNative> Lazy =
            new Lazy<WanosNative>(() => new WanosNative(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static WanosNative Instance => Lazy.Value;

        readonly object _gate = new object();
        bool _created;
        bool _bankDecodeQueryBroken;
        int _mixerJoinDepth;
        bool _loggedMixerJoin;

        WanosNative() { }

        public bool IsCreated
        {
            get
            {
                lock (_gate)
                    return _created;
            }
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult CreateEngine();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult DestoryEngine();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult InitDSP([MarshalAs(UnmanagedType.LPUTF8Str)] string processData);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult UninitDSP();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void SetEngineMode(int mode);

        // Editor 也要走 wrapper：OSXEditor 下 !UNITY_EDITOR 为假，否则 DSP 永不启动、揭罩无声。
#if UNITY_OSX
        // --- macOS wrapper: calls WIAEngine's private C++ methods via runtime dlsym ---
        // These are exposed as free C functions in libWanosAudioMac.dylib.
        // Non-macOS platforms fall back to EngineStart/EngineResume (below).
        [DllImport("libWanosAudioMac.dylib", CallingConvention = CallingConvention.Cdecl)]
        static extern int WanosDSP_StartEngine();

        [DllImport("libWanosAudioMac.dylib", CallingConvention = CallingConvention.Cdecl)]
        static extern int WanosDSP_StopEngine();

        [DllImport("libWanosAudioMac.dylib", CallingConvention = CallingConvention.Cdecl)]
        static extern int WanosDSP_IsEngineRunning();

        // WIAEngine 6000 bundle：IAE 首字段是 Impl*，Impl+0x10 是 WanosAudioDSP*。
        // StopDspTread 会 join 混音线程；StartProcess 会拆掉 DSP，这里只重启线程。
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true,
            EntryPoint = "_ZN3IAE22InteractiveAudioEngine16GetCurrentEngineEv")]
        static extern IntPtr GetCurrentEngine();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true,
            EntryPoint = "_ZN13WanosAudioDSP12StopDspTreadEv")]
        static extern void StopDspThread(IntPtr self);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true,
            EntryPoint = "_ZN13WanosAudioDSP13StartDspTreadEv")]
        static extern void StartDspThread(IntPtr self);
#endif

        // --- Core lifecycle (already wired) ---
        // CreateEngine / DestoryEngine / InitDSP / UninitDSP / SetEngineMode
        // EngineStart / EngineResume / EngineStop (legacy fallback)
        // StartEngine / StopEngine (macOS via wrapper + legacy fallback)

        // --- Engine info ---
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetEngineVersion")]
        static extern IntPtr GetEngineVersionNative();

        // --- Logging ---
        // libWIAEngine exports SetLogEnabled / SetLogLevel (no _impl suffix).
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetLogEnabled")]
        static extern void SetLogEnabled_impl([MarshalAs(UnmanagedType.I1)] bool enabled);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetLogLevel")]
        static extern void SetLogLevel_impl(int level);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr GetLogLevel();

        // --- Console log callback (native calls C# delegate via IntPtr) ---
        // signature: void OnLog(int level, const char* msg)
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "RegisterConsoleLogCB")]
        static extern WanosResult RegisterConsoleLogCBNative(IntPtr callback);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UnRegisterConsoleLogCB")]
        static extern void UnRegisterConsoleLogCBNative();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void ClearLogCallback();

        // --- Playback devices ---
        // Returns JSON: [{"device_id":"...","name":"...","is_default":true},...]
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetPlaybackDevices")]
        static extern IntPtr GetPlaybackDevicesNative();

        // Returns JSON: {"device_id":"...","name":"...","sample_rate":48000,...}
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetCurrentPlaybackDeviceInfo")]
        static extern IntPtr GetCurrentPlaybackDeviceInfoNative();

        // --- Banks ---
        // Returns JSON array of loaded bank info strings; native frees the pointer.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetLoadedBankList")]
        static extern IntPtr GetLoadedBankListNative();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "RemoveBank", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern WanosResult RemoveBankNative([MarshalAs(UnmanagedType.LPUTF8Str)] string bankName);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "IsBankDecoded", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern bool IsBankDecodedNative([MarshalAs(UnmanagedType.LPUTF8Str)] string bankName);

        // Engine export is `int IsBankDecoded(unsigned long long bank_id)` (0 = decoded).
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "IsBankDecoded")]
        static extern int IsBankDecodedByIdNative(ulong bankId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SaveBank", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern WanosResult SaveBankNative([MarshalAs(UnmanagedType.LPUTF8Str)] string bankName);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SavePlayBank", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern WanosResult SavePlayBankNative([MarshalAs(UnmanagedType.LPUTF8Str)] string bankName);

        // --- Global parameters ---
        // Float range defaults (optional export)
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetParameterFloatRange", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern WanosResult SetParameterFloatRangeNative(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string paramName, float min, float max);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetParameterFloatDefaultValue", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern WanosResult SetParameterFloatDefaultValueNative(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string paramName, float value);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetParameterIntRange", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern WanosResult SetParameterIntRangeNative(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string paramName, int min, int max);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetParameterIntDefaultValue", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern WanosResult SetParameterIntDefaultValueNative(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string paramName, int value);

        // --- Game object extensions ---
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UpdateGameObjName", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern void UpdateGameObjNameNative(ulong objId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UpdateGameObjFloatProperty", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern void UpdateGameObjFloatPropertyNative(ulong objId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string propertyName, float value);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UpdateGameObjSwitchByName", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern void UpdateGameObjSwitchByNameNative(ulong objId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string switchGroup,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string switchState);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UpdateGameObjSwitchById", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern void UpdateGameObjSwitchByIdNative(ulong objId, ulong switchGroupId, ulong switchStateId);

        // Attenuation — signatures must match Boluo WIAEngine (void, not WanosResult).
        // Wrong return type / wrong AttenuationParameter shape can make gain calls no-op → 全程满音量。
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetGameObjAttenuationGain")]
        static extern void SetGameObjAttenuationGainNative(ulong objId, float gain);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "setGameObjAttenuationGainEnable")]
        static extern void SetGameObjAttenuationGainEnableNative(ulong objId, [MarshalAs(UnmanagedType.I1)] bool enable);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetGameObjAttenuationLowPassFreq")]
        static extern void SetGameObjAttenuationLowPassFreqNative(ulong objId, float freqHz);

        // --- Spectrum data (visualization) ---
        // spectrumType: 0 = master, 1 = music, 2 = sfx, etc.
        // Returns pointer to float array of size numSamples; native owns the memory.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetSpectrumData")]
        static extern IntPtr GetSpectrumDataNative(int spectrumType, int numSamples, out int outNumSamples);

        // --- Event state callback (native calls C#) ---
        // signature: void OnEventState(ulong voiceId, int state, void* userData)
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "RegisterEventStateCallback")]
        static extern WanosResult RegisterEventStateCallbackNative(IntPtr callback);

        // Boluo WIAEngine: RemoveEventStateCallback(ulong instance_id) — per-voice, not global.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "RemoveEventStateCallback")]
        static extern int RemoveEventStateCallbackNative(ulong instanceId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "PostEvent")]
        static extern ulong PostEventNativeWithCallback(
            ulong objId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string eventName,
            WanosEventCallbackQueue.NativeEntry callback);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void EngineStart();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void EngineResume();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void EngineStop();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult SetPlaybackDevice(string playbackInfo);

        /// <summary>Align with Boluo WiaSdkMode.Game.</summary>
        const int EngineModeGame = 1;

        /// <summary>Align with Boluo WiaAudioBackend.DefaultDspJson.</summary>
        const string DefaultDspJson =
            "{\"sample_rate\":48000,\"buffer_size\":1024,\"frame_queue_size\":5,\"buffer_num\":50,\"null_event_wait_seconds\":30,\"device_position_info\":\"\"}";

        /// <summary>Align with Boluo WiaAudioBackend default device JSON.</summary>
        const string DefaultDeviceJson = "{\"device_id\":\"default\"}";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult ResetPlaybackDevice();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void SetDeocdeCachePath(string cachePath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult LoadBankFromPath(string bankPath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult UnloadBank(string bankUnloadTag);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult UnloadAllBanks();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "PostEvent")]
        static extern ulong PostEventNative(ulong objId, string eventName, IntPtr callback);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void SetGlobalParameter(string paramName, float value);

        /// <summary>
        /// Set a per-object RTPC parameter (e.g. distance for 3D attenuation curve).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetGameObjParameter", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern void SetGameObjParameterNative(ulong objId, string paramName, float value);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetMute")]
        static extern void SetMuteNative(bool mute);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosPlaybackState GetPlaybackState();

        /// <summary>Native may not export this — call via TryCallOptional.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult StopVoiceWithFadeNative(ulong voiceId, int fadeMs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern WanosResult StopAllVoicesNative(int fadeMs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CreateAvatar")]
        static extern void CreateAvatarNative(ulong avatarId, WanosPose6Dof pos);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void DeleteAvatar(ulong avatarId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UpdateAvatarPos")]
        static extern void UpdateAvatarPosNative(ulong avatarId, WanosPose6Dof pos);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void SetAvatarEnable(ulong avatarId, [MarshalAs(UnmanagedType.I1)] bool enable);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void CreateGameObj(
            ulong objId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string objName);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        static extern void DeleteGameObj(ulong objId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "UpdateGameObjPos")]
        static extern void UpdateGameObjPosNative(ulong objId, WanosPose6Dof posInfo);

        /// <summary>
        /// Native may not export this — call via TryCallOptional.
        /// Sets 3D position and attenuation radius for an emitter object.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetEmitterPositionAndRadius", BestFitMapping = false, ThrowOnUnmappableChar = true)]
        static extern void SetEmitterPositionAndRadiusNative(ulong objId, WanosPose6Dof posInfo, float radius);

        public WanosResult Create()
        {
            lock (_gate)
            {
                if (_created)
                    return WanosResult.Success;

                WanosResult result;
                try
                {
                    result = CreateEngine();
                }
                catch (DllNotFoundException e)
                {
                    Debug.LogWarning($"[{LogTag}] CreateEngine DLL missing: {e.Message}");
                    return WanosResult.CreateEngineFailed;
                }
                catch (EntryPointNotFoundException e)
                {
                    Debug.LogWarning($"[{LogTag}] CreateEngine entry missing: {e.Message}");
                    return WanosResult.CreateEngineFailed;
                }

                if (result != WanosResult.Success)
                    return result;

                _created = true;

                try
                {
                    SetEngineMode(EngineModeGame);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{LogTag}] SetEngineMode: {e.Message}");
                }

                try
                {
                    string cache = Path.Combine(Application.persistentDataPath, "WanosAudio");
                    Directory.CreateDirectory(cache);
                    SetDeocdeCachePath(cache);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{LogTag}] SetDeocdeCachePath: {e.Message}");
                }

                try
                {
                    WanosResult dsp = InitDSP(DefaultDspJson);
                    if (dsp != WanosResult.Success)
                        Debug.LogWarning($"[{LogTag}] InitDSP result={dsp}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{LogTag}] InitDSP: {e.Message}");
                }

                return result;
            }
        }

        /// <summary>
        /// Start DSP after playback device is open.
        /// macOS: uses WanosDSP_StartEngine() via libWanosAudioMac.dylib wrapper.
        /// Other platforms: try optional EngineStart / EngineResume exports.
        /// </summary>
        public bool StartEngine()
        {
            if (!_created)
                return false;

#if UNITY_OSX
            // macOS Editor + Player: call the runtime dlsym wrapper (was !UNITY_EDITOR — Editor silent).
            try
            {
                int r = WanosDSP_StartEngine();
                if (r == 0)
                    Debug.Log($"[{LogTag}] StartEngine macOS wrapper OK (r={r})");
                else
                    Debug.LogWarning($"[{LogTag}] StartEngine macOS wrapper returned {r}");
                return r == 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] StartEngine macOS wrapper: {e.Message}");
            }
#endif

            // Non-macOS / fallback: try EngineStart / EngineResume
            bool started = TryCallOptional("EngineStart", EngineStart);
            bool resumed = TryCallOptional("EngineResume", EngineResume);
            if (!started && !resumed)
                Debug.Log($"[{LogTag}] StartEngine: no EngineStart/EngineResume export");
            return true;
        }

        /// <summary>
        /// Stop DSP thread. Safe on all platforms.
        /// macOS: calls WanosDSP_StopEngine() via wrapper.
        /// Other platforms: calls optional EngineStop.
        /// </summary>
        public void StopEngine()
        {
            if (!_created)
                return;

#if UNITY_OSX
            try
            {
                int r = WanosDSP_StopEngine();
                Debug.Log($"[{LogTag}] StopEngine macOS wrapper returned {r}");
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] StopEngine macOS wrapper: {e.Message}");
            }
#endif

            TryCallOptional("EngineStop", EngineStop);
            Debug.Log($"[{LogTag}] StopEngine done");
        }

        /// <summary>
        /// Call an optional native export; missing entry is non-fatal.
        /// Exposed public so WanosEngine.StopEngine can use it.
        /// </summary>
        public static bool TryCallOptional(string name, Action call)
        {
            try
            {
                call();
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] {name}: {e.Message}");
                return false;
            }
        }

        public WanosResult Destroy(string reason = null)
        {
            lock (_gate)
            {
                if (!_created)
                    return WanosResult.Success;

                string tag = string.IsNullOrEmpty(reason) ? "Destroy" : reason;
                Debug.Log($"[{LogTag}] Destroy begin ({tag})");
                try
                {
                    TrySetMute(true);
                    try { ResetPlaybackDevice(); }
                    catch (Exception e) { Debug.LogWarning($"[{LogTag}] ResetPlaybackDevice: {e.Message}"); }
                    try { UninitDSP(); }
                    catch (Exception e) { Debug.LogWarning($"[{LogTag}] UninitDSP: {e.Message}"); }
                    try { UnloadAllBanks(); }
                    catch (Exception e) { Debug.LogWarning($"[{LogTag}] UnloadAllBanks: {e.Message}"); }
                    WanosResult r = DestoryEngine();
                    _created = false;
                    Debug.Log($"[{LogTag}] Destroy done ({tag}) result={r}");
                    return r;
                }
                catch (Exception e)
                {
                    _created = false;
                    Debug.LogWarning($"[{LogTag}] Destroy({tag}): {e.Message}");
                    return WanosResult.InvalidEngine;
                }
            }
        }

        public WanosResult LoadBank(string path)
        {
            if (string.IsNullOrEmpty(path))
                return WanosResult.InvalidFilePath;
            WanosResult result = WanosResult.BankOpenFailed;
            RunWithMixerJoined(() =>
            {
                try
                {
                    result = LoadBankFromPath(path);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{LogTag}] LoadBank({path}): {e.Message}");
                    result = WanosResult.BankOpenFailed;
                }
            });
            return result;
        }

        public WanosResult UnloadBankByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return WanosResult.InvalidFilePath;
            WanosResult result = WanosResult.InvalidEngine;
            RunWithMixerJoined(() =>
            {
                try
                {
                    result = UnloadBank(tag);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{LogTag}] UnloadBank({tag}): {e.Message}");
                    result = WanosResult.InvalidEngine;
                }
            });
            return result;
        }

        /// <summary>
        /// LoadBank / UnloadBank 会改正在被混音的组图。
        /// DSP 线程在 AudioGroupInst::getDataInternal 里读到半截节点就是 SIGSEGV（编辑器直接退出）。
        /// 先 join 混音线程，改完再 StartDspTread。不要走 StartProcess，那会拆掉 DSP。
        /// </summary>
        void RunWithMixerJoined(Action body)
        {
            if (body == null)
                return;

#if !UNITY_OSX
            body();
            return;
#else
            bool outer = _mixerJoinDepth == 0;
            bool restart = false;
            IntPtr dsp = IntPtr.Zero;
            if (outer && _created)
            {
                dsp = TryGetMixer();
                if (dsp != IntPtr.Zero)
                {
                    // StopDspTread 看的是 +0x80 的运行标志，不是 IsProcessRunning 的 +0x81。
                    try
                    {
                        restart = Marshal.ReadByte(dsp, 0x80) != 0;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[{LogTag}] mixer flag read failed: {e.Message}");
                        dsp = IntPtr.Zero;
                        restart = false;
                    }
                }

                if (restart)
                {
                    if (!_loggedMixerJoin)
                    {
                        _loggedMixerJoin = true;
                        Debug.Log($"[{LogTag}] join mixer before bank mutation");
                    }

                    try
                    {
                        StopDspThread(dsp);
                        // join 之后 std::thread id 必须是 0。非 0 再 StartDspTread 会 std::terminate。
                        if (Marshal.ReadInt64(dsp, 0x88) != 0)
                        {
                            Debug.LogError($"[{LogTag}] mixer thread still live after join — skip restart");
                            restart = false;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[{LogTag}] StopDspThread: {e.Message}");
                        restart = false;
                    }
                }
            }

            _mixerJoinDepth++;
            try
            {
                body();
            }
            finally
            {
                _mixerJoinDepth--;
                if (outer && restart && dsp != IntPtr.Zero)
                {
                    try
                    {
                        StartDspThread(dsp);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[{LogTag}] StartDspThread: {e.Message}");
                    }
                }
            }
#endif
        }

#if UNITY_OSX
        static IntPtr TryGetMixer()
        {
            try
            {
                IntPtr engine = GetCurrentEngine();
                if (!LooksLikeObject(engine))
                    return IntPtr.Zero;
                IntPtr impl = Marshal.ReadIntPtr(engine);
                if (!LooksLikeObject(impl))
                    return IntPtr.Zero;
                IntPtr dsp = Marshal.ReadIntPtr(impl, 0x10);
                return LooksLikeObject(dsp) ? dsp : IntPtr.Zero;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] mixer lookup failed: {e.Message}");
                return IntPtr.Zero;
            }
        }

        static bool LooksLikeObject(IntPtr p)
        {
            long v = p.ToInt64();
            return v > 0x10000L && (v & 7L) == 0;
        }
#endif

        /// <summary>
        /// Stop a specific voice with optional fade-out (ms).
        /// Native export optional — uses TryCallOptional so missing entry is not fatal.
        /// </summary>
        public void StopVoiceWithFade(ulong voiceId, int fadeMs)
        {
            if (voiceId == 0)
                return;
            TryCallOptional("StopVoiceWithFade", () => StopVoiceWithFadeNative(voiceId, fadeMs));
        }

        public void StopAllVoices(int fadeMs)
        {
            TryCallOptional("StopAllVoices", () => StopAllVoicesNative(fadeMs));
        }

        public ulong PostEvent(ulong objId, string eventName)
        {
            return PostEvent(objId, eventName, callback: null);
        }

        /// <summary>
        /// Post an event; when <paramref name="callback"/> is set, native reports Playing/Ended
        /// for that voice (Boluo music Break / auto-next path).
        /// </summary>
        public ulong PostEvent(ulong objId, string eventName, WanosEventCallbackQueue.NativeEntry callback)
        {
            if (string.IsNullOrEmpty(eventName))
                return 0;
            if (!WanosEngine.ShouldAllowPostEvent(eventName))
            {
                Debug.LogWarning($"[{LogTag}] PostEvent BLOCKED objId={objId} event={eventName} (IsCreated={WanosNative.Instance.IsCreated} eventPostAllowed=?)");
                return 0;
            }
            try
            {
                ulong voice = callback != null
                    ? PostEventNativeWithCallback(objId, eventName, callback)
                    : PostEventNative(objId, eventName, IntPtr.Zero);
                Debug.Log($"[{LogTag}] PostEvent objId={objId} event={eventName} cb={(callback != null)} -> voice={voice}");
                return voice;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] PostEvent({eventName}): {e.Message}");
                return 0;
            }
        }

        public void CreateAvatar(ulong avatarId, WanosPose6Dof pos)
        {
            if (!_created)
                return;
            try
            {
                CreateAvatarNative(avatarId, pos);
                SetAvatarEnable(avatarId, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] CreateAvatar({avatarId}): {e.Message}");
            }
        }

        public void UpdateAvatarPos(ulong avatarId, WanosPose6Dof pos)
        {
            if (!_created)
                return;
            try
            {
                UpdateAvatarPosNative(avatarId, pos);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] UpdateAvatarPos({avatarId}): {e.Message}");
            }
        }

        public void DestroyAvatar(ulong avatarId)
        {
            if (!_created)
                return;
            try
            {
                DeleteAvatar(avatarId);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] DeleteAvatar({avatarId}): {e.Message}");
            }
        }

        public void RegisterGameObj(ulong objId, string debugName)
        {
            if (!_created)
                return;
            try
            {
                CreateGameObj(objId, string.IsNullOrEmpty(debugName) ? "WanosEmitter" : debugName);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] CreateGameObj({objId}): {e.Message}");
            }
        }

        public void UpdateGameObjPos(ulong objId, WanosPose6Dof pose)
        {
            if (!_created)
                return;
            try
            {
                UpdateGameObjPosNative(objId, pose);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] UpdateGameObjPos({objId}): {e.Message}");
            }
        }

        /// <summary>
        /// Set 3D position and attenuation radius for an emitter.
        /// Native export optional — uses TryCallOptional so missing entry is not fatal.
        /// </summary>
        public void SetEmitterPositionAndRadius(ulong objId, WanosPose6Dof pose, float radius)
        {
            if (!_created)
                return;
            TryCallOptional("SetEmitterPositionAndRadius", () => {
                SetEmitterPositionAndRadiusNative(objId, pose, radius);
                Debug.Log($"[WanosNative] SetEmitterPositionAndRadius objId={objId} radius={radius}");
            });
        }

        public void UnregisterGameObj(ulong objId)
        {
            if (!_created)
                return;
            try
            {
                DeleteGameObj(objId);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] DeleteGameObj({objId}): {e.Message}");
            }
        }

        public void SetParameter(string name, float value)
        {
            if (string.IsNullOrEmpty(name) || !_created)
                return;
            try
            {
                WarnIfVolumeScaleMismatch(name, value);
                SetGlobalParameter(name, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] SetParameter({name}): {e.Message}");
            }
        }

        /// <summary>
        /// 写已经是 0–100 量纲的引擎值（供 <see cref="WanosEngine.SetVolumeParameter"/> 换算后调用）。
        /// 走这里可以跳过 0–1 量纲护栏——护栏只针对「直接透传 UI 值」的误用。
        /// </summary>
        internal void SetParameterEngineValue(string name, float engineValue)
        {
            if (string.IsNullOrEmpty(name) || !_created)
                return;
            try
            {
                SetGlobalParameter(name, engineValue);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] SetParameter({name}): {e.Message}");
            }
        }

        /// <summary>
        /// 量纲 + 名字双护栏。
        /// <para>
        /// 量纲：音量 RTPC 在引擎里的量程是 0–100（bank: Range max = 100，Default = 80）。
        /// 若有人把 0–1 的 UI 值直接透传进来，实际音量 = value%，表现为「有播放但听不到」。
        /// 只在明显是 0–1 量纲时报警（0 &lt; value &lt; 1.5），不拦截合法的 0（静音）与 1.5–100。
        /// </para>
        /// <para>
        /// 名字：引擎对不认识的 RTPC 名是**静默丢弃**（无报错、无日志），音量永远停在默认值。
        /// 历史踩坑：写成 <c>SFX_Volume</c>，正确名是 <c>Fx_Volume</c>（legacy GameData.wFX）。
        /// 所以凡是名字里带 "Volume" 的参数，都必须命中白名单，否则报错。
        /// </para>
        /// </summary>
        static void WarnIfVolumeScaleMismatch(string name, float value)
        {
            bool isKnownVolume = name == WanosEngine.MusicVolumeParam || name == WanosEngine.FxVolumeParam;
            if (!isKnownVolume)
            {
                // 只拦「看起来像音量」的名字，避免误伤天气等业务 RTPC。
                if (name.IndexOf("Volume", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Debug.LogError($"[{LogTag}] SetParameter({name}) 未知音量 RTPC 名。" +
                                   $"合法名只有 {WanosEngine.MusicVolumeParam} / {WanosEngine.FxVolumeParam}" +
                                   "（注意不是 SFX_Volume）。引擎会静默丢弃不认识的 RTPC，音量不会变且无任何报错。");
                }
                return;
            }

            if (value > 0f && value < 1.5f)
            {
                Debug.LogError($"[{LogTag}] SetParameter({name}, {value}) 量纲错误：音量 RTPC 量程是 0–100，" +
                               $"收到 0–1 的值（实际音量仅 {value:F2}% → 听不到）。" +
                               "请改用 WanosEngine.SetVolumeParameter(name, ui0to1)（内部 ×100）。");
            }
            else if (value > 100f)
            {
                Debug.LogError($"[{LogTag}] SetParameter({name}, {value}) 超出 RTPC 量程 0–100，会被引擎钳制。");
            }
        }

        /// <summary>
        /// Set a per-object RTPC parameter (e.g. distance for 3D attenuation curve).
        /// Native export optional — uses TryCallOptional so missing entry is not fatal.
        /// </summary>
        public void SetGameObjParameter(ulong objId, string paramName, float value)
        {
            if (!_created || string.IsNullOrEmpty(paramName))
                return;
            try
            {
                SetGameObjParameterNative(objId, paramName, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] SetGameObjParameter({objId},{paramName}): {e.Message}");
            }
        }

        public bool OpenDefaultPlaybackDevice()
        {
            try
            {
                ResetPlaybackDevice();
                WanosResult r = SetPlaybackDevice(DefaultDeviceJson);
                if (r != WanosResult.Success)
                {
                    Debug.LogError($"[{LogTag}] OpenDefaultPlaybackDevice result={r}");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[{LogTag}] OpenDefaultPlaybackDevice: {e.Message}");
                return false;
            }
        }

        public bool TrySetMute(bool mute)
        {
            try
            {
                SetMuteNative(mute);
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        public WanosPlaybackState PlaybackState
        {
            get
            {
                try
                {
                    return GetPlaybackState();
                }
                catch
                {
                    return WanosPlaybackState.Uninitialized;
                }
            }
        }

        // ---- Engine info ----

        /// <summary>Native version string, e.g. "WIAEngine 1.2.3".</summary>
        public string GetEngineVersion()
        {
            try
            {
                return PtrToStringUTF8(GetEngineVersionNative());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] GetEngineVersion: {e.Message}");
                return string.Empty;
            }
        }

        // ---- Logging ----

        public void SetLogEnabled(bool enabled) =>
            TryCallOptional("SetLogEnabled", () => SetLogEnabled_impl(enabled));

        public void SetLogLevel(int level) =>
            TryCallOptional("SetLogLevel", () => SetLogLevel_impl(level));

        // ---- Console log callback ----

        /// <summary>
        /// Register a C# delegate as native log sink. Native passes level + UTF-8 message.
        /// Pass IntPtr.Zero to clear.
        /// </summary>
        public void RegisterConsoleLogCallback(IntPtr callbackPtr)
        {
            TryCallOptional("RegisterConsoleLogCB", () => {
                var r = RegisterConsoleLogCBNative(callbackPtr);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] RegisterConsoleLogCB result={r}");
            });
        }

        public void UnregisterConsoleLogCallback() =>
            TryCallOptional("UnRegisterConsoleLogCB", UnRegisterConsoleLogCBNative);

        // ---- Playback devices ----

        /// <summary>
        /// Enumerate playback devices.
        /// Returns JSON array: [{"device_id":"...","name":"...","is_default":true},...]
        /// </summary>
        public string GetPlaybackDevices()
        {
            try
            {
                return PtrToStringUTF8(GetPlaybackDevicesNative());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] GetPlaybackDevices: {e.Message}");
                return "[]";
            }
        }

        /// <summary>
        /// Get currently active playback device info as JSON.
        /// </summary>
        public string GetCurrentPlaybackDeviceInfo()
        {
            try
            {
                return PtrToStringUTF8(GetCurrentPlaybackDeviceInfoNative());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] GetCurrentPlaybackDeviceInfo: {e.Message}");
                return "{}";
            }
        }

        // ---- Banks ----

        /// <summary>
        /// Returns JSON array of loaded bank names. Empty array on failure.
        /// </summary>
        public string GetLoadedBankList()
        {
            try
            {
                return PtrToStringUTF8(GetLoadedBankListNative());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] GetLoadedBankList: {e.Message}");
                return "[]";
            }
        }

        public bool RemoveBank(string bankName)
        {
            return TryCallOptional("RemoveBank", () => {
                var r = RemoveBankNative(bankName);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] RemoveBank({bankName}) result={r}");
            });
        }

        public bool IsBankDecoded(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            try
            {
                return IsBankDecodedNative(bankName);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] IsBankDecoded({bankName}): {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// <c>IsBankDecoded(unsigned long long)</c>. True when the engine reports the bank decoded (return 0).
        /// False when not yet decoded, the id is 0, or this export is unavailable.
        /// </summary>
        public bool IsBankDecoded(ulong bankId)
        {
            if (bankId == 0 || _bankDecodeQueryBroken)
                return false;
            try
            {
                return IsBankDecodedByIdNative(bankId) == 0;
            }
            catch (Exception e)
            {
                _bankDecodeQueryBroken = true;
                Debug.LogWarning($"[{LogTag}] IsBankDecoded(id) unavailable: {e.Message}");
                return false;
            }
        }

        public bool BankDecodeQueryAvailable => !_bankDecodeQueryBroken;

        public bool SaveBank(string bankName)
        {
            return TryCallOptional("SaveBank", () => {
                var r = SaveBankNative(bankName);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] SaveBank({bankName}) result={r}");
            });
        }

        public bool SavePlayBank(string bankName)
        {
            return TryCallOptional("SavePlayBank", () => {
                var r = SavePlayBankNative(bankName);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] SavePlayBank({bankName}) result={r}");
            });
        }

        // ---- Parameter metadata ----

        public bool SetParameterFloatRange(string paramName, float min, float max)
        {
            if (string.IsNullOrEmpty(paramName))
                return false;
            return TryCallOptional("SetParameterFloatRange", () => {
                var r = SetParameterFloatRangeNative(paramName, min, max);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] SetParameterFloatRange({paramName}) result={r}");
            });
        }

        public bool SetParameterFloatDefaultValue(string paramName, float value)
        {
            if (string.IsNullOrEmpty(paramName))
                return false;
            return TryCallOptional("SetParameterFloatDefaultValue", () => {
                var r = SetParameterFloatDefaultValueNative(paramName, value);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] SetParameterFloatDefaultValue({paramName}) result={r}");
            });
        }

        public bool SetParameterIntRange(string paramName, int min, int max)
        {
            if (string.IsNullOrEmpty(paramName))
                return false;
            return TryCallOptional("SetParameterIntRange", () => {
                var r = SetParameterIntRangeNative(paramName, min, max);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] SetParameterIntRange({paramName}) result={r}");
            });
        }

        public bool SetParameterIntDefaultValue(string paramName, int value)
        {
            if (string.IsNullOrEmpty(paramName))
                return false;
            return TryCallOptional("SetParameterIntDefaultValue", () => {
                var r = SetParameterIntDefaultValueNative(paramName, value);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] SetParameterIntDefaultValue({paramName}) result={r}");
            });
        }

        // ---- Game object extensions ----

        public void UpdateGameObjName(ulong objId, string name)
        {
            if (!_created || string.IsNullOrEmpty(name))
                return;
            TryCallOptional("UpdateGameObjName", () => UpdateGameObjNameNative(objId, name));
        }

        public void UpdateGameObjFloatProperty(ulong objId, string propertyName, float value)
        {
            if (!_created || string.IsNullOrEmpty(propertyName))
                return;
            TryCallOptional("UpdateGameObjFloatProperty", () =>
                UpdateGameObjFloatPropertyNative(objId, propertyName, value));
        }

        public void UpdateGameObjSwitchByName(ulong objId, string switchGroup, string switchState)
        {
            if (!_created || string.IsNullOrEmpty(switchGroup) || string.IsNullOrEmpty(switchState))
                return;
            TryCallOptional("UpdateGameObjSwitchByName", () =>
                UpdateGameObjSwitchByNameNative(objId, switchGroup, switchState));
        }

        public void UpdateGameObjSwitchById(ulong objId, ulong switchGroupId, ulong switchStateId)
        {
            if (!_created)
                return;
            TryCallOptional("UpdateGameObjSwitchById", () =>
                UpdateGameObjSwitchByIdNative(objId, switchGroupId, switchStateId));
        }

        /// <summary>Boluo CurveForGain path — must not be wrapped in TryCallOptional (签名已对齐原生 void)。</summary>
        public void SetGameObjAttenuationGain(ulong objId, float gainDb)
        {
            if (!_created)
                return;
            try
            {
                SetGameObjAttenuationGainNative(objId, gainDb);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] SetGameObjAttenuationGain({objId}): {e.Message}");
            }
        }

        /// <summary>Enable per-object attenuation gain (required before <see cref="SetGameObjAttenuationGain"/>).</summary>
        public void SetGameObjAttenuationGainEnable(ulong objId, bool enable)
        {
            if (!_created)
                return;
            try
            {
                SetGameObjAttenuationGainEnableNative(objId, enable);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] setGameObjAttenuationGainEnable({objId}): {e.Message}");
            }
        }

        public void SetGameObjAttenuationLowPassFreq(ulong objId, float freqHz)
        {
            if (!_created)
                return;
            try
            {
                SetGameObjAttenuationLowPassFreqNative(objId, freqHz);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] SetGameObjAttenuationLowPassFreq({objId}): {e.Message}");
            }
        }

        // ---- Spectrum data ----

        /// <summary>
        /// Read FFT spectrum data from native DSP.
        /// </summary>
        /// <param name="spectrumType">0=master, 1=music, 2=sfx (platform-dependent)</param>
        /// <param name="numSamples">Requested sample count</param>
        /// <returns>Float array, or empty array on failure</returns>
        public float[] GetSpectrumData(int spectrumType = 0, int numSamples = 256)
        {
            try
            {
                IntPtr ptr = GetSpectrumDataNative(spectrumType, numSamples, out int outNum);
                if (ptr == IntPtr.Zero || outNum <= 0)
                    return Array.Empty<float>();
                float[] result = new float[outNum];
                Marshal.Copy(ptr, result, 0, outNum);
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] GetSpectrumData: {e.Message}");
                return Array.Empty<float>();
            }
        }

        // ---- Event state callback ----

        /// <summary>
        /// Register a C# delegate to receive voice state change callbacks from native.
        /// Pass IntPtr.Zero to clear.
        /// </summary>
        public void RegisterEventStateCallback(IntPtr callbackPtr)
        {
            TryCallOptional("RegisterEventStateCallback", () => {
                var r = RegisterEventStateCallbackNative(callbackPtr);
                if (r != WanosResult.Success)
                    Debug.LogWarning($"[{LogTag}] RegisterEventStateCallback result={r}");
            });
        }

        /// <summary>Detach Playing/Ended callback for one voice (Boluo <c>RemoveEventStateCallback(instance)</c>).</summary>
        public void RemoveEventStateCallback(ulong instanceId)
        {
            if (instanceId == 0)
                return;
            TryCallOptional("RemoveEventStateCallback", () => RemoveEventStateCallbackNative(instanceId));
        }

        // ---- Utility ----

        /// <summary>
        /// Marshal UTF-8 native string (owned by native) to managed string, then free native copy.
        /// </summary>
        static string PtrToStringUTF8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return string.Empty;
            try
            {
                string s = Marshal.PtrToStringUTF8(ptr);
                // Native owns the allocation — we assume it is safe to free via CoTaskMemFree.
                Marshal.FreeCoTaskMem(ptr);
                return s ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
