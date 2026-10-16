using System;
using System.Collections.Generic;
using UnityEngine;

namespace WanosAudio
{
    /// <summary>Wanos voice playback state (mirrors native AudioVoiceState).</summary>
    public enum AudioVoiceState
    {
        Unknown = 0,
        Playing = 1,
        Stopped = 2,
        Paused = 3,
    }

    /// <summary>
    /// Interaction facade over Wanos: fixed logical emitters, listener pose, world emitters.
    /// No GameFramework / Boluo business logic.
    /// </summary>
    public static class WanosInteraction
    {
        public const ulong ListenerObjectId = 1;
        public const ulong DefaultObjectId = 100;
        public const ulong MusicObjectId = 101;
        public const ulong RainObjectId = 102;
        public const ulong WindObjectId = 103;
        public const ulong RainWaterObjectId = 104;

        const ulong DynamicObjectIdStart = 1000;

        static readonly HashSet<ulong> RegisteredObjects = new HashSet<ulong>();
        static readonly Dictionary<EntityId, ulong> EmitterBindings = new Dictionary<EntityId, ulong>(32);

        const string ListenerFollowHostName = "WanosListener";

        static bool _listenerReady;
        static ulong _nextDynamicId = DynamicObjectIdStart;
        static Transform _listenerSource;
        static GameObject _listenerFollowHost;

        /// <summary>
        /// Native voice stopped/started callback forwarded from <see cref="WanosNative"/>.
        /// 注册方：`AmbienceZoneService` 用它做 voice 回收。
        /// </summary>
        public static event Action<ulong, AudioVoiceState> VoiceStateChanged;

        public static bool IsReady => WanosEngine.IsLoaded && WanosEngine.IsEventPostAllowed;

        public static void ResetSession()
        {
            StopListenerFollow();
            RegisteredObjects.Clear();
            EmitterBindings.Clear();
            _listenerReady = false;
            _nextDynamicId = DynamicObjectIdStart;
            _listenerSource = null;
            VoiceStateChanged = null;
        }

        /// <summary>Register listener + fixed 2D/weather object IDs after playback is enabled.</summary>
        public static void EnsureLogicalObjects()
        {
            if (!WanosNative.Instance.IsCreated)
                return;

            if (!_listenerReady)
            {
                WanosNative.Instance.CreateAvatar(ListenerObjectId, WanosPose6Dof.Identity);
                _listenerReady = true;
            }

            EnsureObject(DefaultObjectId, "Default");
            EnsureObject(MusicObjectId, "Music");
            EnsureObject(RainObjectId, "Rain");
            EnsureObject(WindObjectId, "Wind");
            EnsureObject(RainWaterObjectId, "Rain_Water");
        }

        /// <summary>Optional override; null falls back to <see cref="Camera.main"/>.</summary>
        public static void SetListenerSource(Transform source) => _listenerSource = source;

        /// <summary>
        /// Drive <see cref="TickListener"/> every LateUpdate so the avatar tracks the camera.
        /// Safe to call repeatedly.
        /// </summary>
        public static void StartListenerFollow()
        {
            if (_listenerFollowHost != null)
                return;

            _listenerFollowHost = new GameObject(ListenerFollowHostName);
            UnityEngine.Object.DontDestroyOnLoad(_listenerFollowHost);
            _listenerFollowHost.AddComponent<ListenerFollowDriver>();
        }

        public static void StopListenerFollow()
        {
            if (_listenerFollowHost == null)
                return;
            UnityEngine.Object.Destroy(_listenerFollowHost);
            _listenerFollowHost = null;
        }

        public static void TickListener()
        {
            if (!_listenerReady || !WanosNative.Instance.IsCreated)
                return;
            if (!TryResolveListener(out Transform t))
                return;
            WanosNative.Instance.UpdateAvatarPos(ListenerObjectId, WanosPose6Dof.FromTransform(t));
        }

        public static bool TryGetListenerPosition(out Vector3 position)
        {
            if (!TryResolveListener(out Transform t))
            {
                position = default;
                return false;
            }
            position = t.position;
            return true;
        }

        static bool TryResolveListener(out Transform t)
        {
            t = _listenerSource;
            if (t != null)
                return true;
            Camera cam = Camera.main;
            if (cam == null)
                return false;
            t = cam.transform;
            return true;
        }

        sealed class ListenerFollowDriver : MonoBehaviour
        {
            void LateUpdate() => TickListener();
        }

        public static void EnsureObject(ulong objectId, string debugName)
        {
            if (!WanosNative.Instance.IsCreated)
                return;
            if (!RegisteredObjects.Add(objectId))
                return;
            WanosNative.Instance.RegisterGameObj(objectId, debugName);
            WanosNative.Instance.UpdateGameObjPos(objectId, WanosPose6Dof.Identity);
        }

        public static void SetObjectPosition(ulong objectId, Vector3 worldPosition)
        {
            EnsureObject(objectId, "WanosEmitter");
            WanosNative.Instance.UpdateGameObjPos(objectId, WanosPose6Dof.FromPosition(worldPosition));
        }

        public static ulong EnsureEmitter(GameObject go, Vector3 worldPosition)
        {
            return EnsureEmitter(go, worldPosition, 0f);
        }

        /// <summary>
        /// Register or update a dynamic emitter GameObject with position and attenuation radius.
        /// </summary>
        /// <param name="go">The emitter GameObject.</param>
        /// <param name="worldPosition">World position of the emitter.</param>
        /// <param name="radius">Attenuation radius in meters. 0 uses engine default.</param>
        /// <returns>The native object ID assigned to this emitter.</returns>
        public static ulong EnsureEmitter(GameObject go, Vector3 worldPosition, float radius)
        {
            if (go == null)
                return DefaultObjectId;

            // radius 参数保留兼容；原生无 SetEmitterPositionAndRadius，3D 靠 UpdateGameObjPos + AttenuationGain。
            _ = radius;

            EntityId key = go.GetEntityId();
            if (EmitterBindings.TryGetValue(key, out ulong existing))
            {
                WanosNative.Instance.UpdateGameObjPos(existing, WanosPose6Dof.FromPosition(worldPosition));
                return existing;
            }

            ulong id = _nextDynamicId++;
            EnsureObject(id, go.name);
            WanosNative.Instance.UpdateGameObjPos(id, WanosPose6Dof.FromPosition(worldPosition));
            // Boluo：OnEnable 先 enable gain，再靠 Curve 推 dB。新建 emitter 立刻打开闸。
            WanosNative.Instance.SetGameObjAttenuationGainEnable(id, true);
            EmitterBindings[key] = id;
            return id;
        }

        /// <summary>Resolve native object id for a bound emitter GameObject.</summary>
        public static bool TryGetEmitterObjectId(GameObject go, out ulong objectId)
        {
            objectId = 0;
            if (go == null)
                return false;
            return EmitterBindings.TryGetValue(go.GetEntityId(), out objectId);
        }

        public static void ReleaseEmitter(GameObject go)
        {
            if (go == null)
                return;
            EntityId key = go.GetEntityId();
            if (!EmitterBindings.TryGetValue(key, out ulong id))
                return;
            EmitterBindings.Remove(key);
            RegisteredObjects.Remove(id);
            WanosNative.Instance.UnregisterGameObj(id);
        }

        /// <summary>
        /// Set an RTPC parameter for a specific emitter object.
        /// Use this to push distance values for 3D attenuation curves defined in the Wanos bank.
        /// Boluo banks use the parameter name <c>Distance</c> (capital D).
        /// </summary>
        /// <param name="objectId">The native object ID of the emitter.</param>
        /// <param name="paramName">RTPC parameter name (e.g. "Distance").</param>
        /// <param name="value">Parameter value (e.g. distance in meters).</param>
        public static void SetEmitterParameter(ulong objectId, string paramName, float value)
        {
            if (!WanosNative.Instance.IsCreated)
                return;
            WanosNative.Instance.SetGameObjParameter(objectId, paramName, value);
        }

        /// <summary>
        /// Set an RTPC parameter for an emitter GameObject.
        /// </summary>
        public static void SetEmitterParameter(GameObject go, string paramName, float value)
        {
            if (go == null)
                return;
            EntityId key = go.GetEntityId();
            if (!EmitterBindings.TryGetValue(key, out ulong id))
                return;
            SetEmitterParameter(id, paramName, value);
        }

        /// <summary>
        /// Enable + set per-object attenuation gain (dB), matching Boluo <c>WanosCurveForGain</c>.
        /// 必须在 PostEvent 之后再调一次：部分 bank 在 Post 时会重置 object 衰减闸。
        /// </summary>
        public static void SetEmitterAttenuationGain(GameObject go, float gainDb)
        {
            if (go == null || !WanosNative.Instance.IsCreated)
                return;
            if (!EmitterBindings.TryGetValue(go.GetEntityId(), out ulong id))
                return;
            ApplyAttenuationGain(id, gainDb);
        }

        public static void ApplyAttenuationGain(ulong objectId, float gainDb)
        {
            if (objectId == 0 || !WanosNative.Instance.IsCreated)
                return;
            WanosNative.Instance.SetGameObjAttenuationGainEnable(objectId, true);
            WanosNative.Instance.SetGameObjAttenuationGain(objectId, gainDb);
        }

        /// <summary>
        /// Push Distance RTPC + attenuation gain for a live emitter (pose already updated).
        /// </summary>
        public static void ApplyEmitterDistanceAttenuation(GameObject go, float distanceMeters, float gainDb)
        {
            if (go == null || !TryGetEmitterObjectId(go, out ulong id))
                return;
            WanosNative.Instance.SetGameObjParameter(id, "Distance", distanceMeters);
            ApplyAttenuationGain(id, gainDb);
        }

        public static ulong PostEvent(string eventName, ulong objectId = DefaultObjectId)
        {
            if (string.IsNullOrEmpty(eventName) || !IsReady)
                return 0;
            EnsureObject(objectId, "Logical");
            return WanosNative.Instance.PostEvent(objectId, eventName);
        }

        public static ulong PostEvent(string eventName, GameObject emitter)
        {
            if (string.IsNullOrEmpty(eventName) || !IsReady || emitter == null)
                return 0;
            ulong id = EnsureEmitter(emitter, emitter.transform.position);
            return WanosNative.Instance.PostEvent(id, eventName);
        }

        public static bool RetainBank(string bankName) => WanosBankStore.Retain(bankName);

        public static void ReleaseBank(string bankName) => WanosBankStore.Release(bankName);

        /// <summary>
        /// Stop a specific voice with fade.
        /// </summary>
        public static void StopVoice(ulong voiceId, int fadeMs)
        {
            if (voiceId == 0)
                return;
            if (!WanosNative.Instance.IsCreated)
                return;

            // Stop via posting a stop event on the object's control bus is the only way
            // without native VoiceStop(voiceId, fadeMs). Post "Stop_Voice" is not a thing.
            // Instead we rely on the native event callback to fire Stopped → caller will
            // have set up the callback via PostEventWithCallback when the voice was started.
            // Since PostEvent currently doesn't register a callback, we use WanosNative
            // to post a native stop if available, otherwise the voice naturally stops via
            // the container loop.
            try
            {
                WanosNative.Instance.StopVoiceWithFade(voiceId, fadeMs);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WanosInteraction] StopVoice({voiceId}, {fadeMs}ms): {e.Message}");
            }
        }

        public static void ShutdownObjects()
        {
            foreach (ulong id in RegisteredObjects)
            {
                if (id == ListenerObjectId)
                    continue;
                try
                {
                    WanosNative.Instance.UnregisterGameObj(id);
                }
                catch (Exception)
                {
                    // ignored
                }
            }

            if (_listenerReady)
            {
                WanosNative.Instance.DestroyAvatar(ListenerObjectId);
                _listenerReady = false;
            }

            RegisteredObjects.Clear();
            EmitterBindings.Clear();
            _nextDynamicId = DynamicObjectIdStart;
            _listenerSource = null;
            StopListenerFollow();
        }
    }
}
