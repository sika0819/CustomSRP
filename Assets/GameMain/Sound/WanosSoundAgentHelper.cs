using System;
using GameFramework;
using GameFramework.Sound;
using UnityEngine;
using UnityEngine.Audio;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>GF SoundAgentHelper backed by Wanos PostEvent.</summary>
    public sealed class WanosSoundAgentHelper : SoundAgentHelperBase
    {
        Transform _cachedTransform;
        WanosSoundAsset _asset;
        EntityLogic _bindingEntity;
        ulong _instanceId;
        bool _playing;
        bool _mute;
        bool _loop;
        int _priority;
        float _volume = 1f;
        float _pitch = 1f;
        float _panStereo;
        float _spatialBlend;
        float _maxDistance = 100f;
        float _dopplerLevel = 1f;
        float _volumeWhenPause = 1f;
        AudioMixerGroup _mixerGroup;
        EventHandler<ResetSoundAgentEventArgs> _resetHandler;

        public override bool IsPlaying => _playing;

        public override float Length => 0f;

        public override float Time
        {
            get => 0f;
            set { }
        }

        public override bool Mute
        {
            get => _mute;
            set
            {
                _mute = value;
                ApplyVolumeToEngine();
            }
        }

        public override bool Loop
        {
            get => _loop;
            set => _loop = value;
        }

        public override int Priority
        {
            get => _priority;
            set => _priority = value;
        }

        public override float Volume
        {
            get => _volume;
            set
            {
                _volume = Mathf.Clamp01(value);
                ApplyVolumeToEngine();
            }
        }

        public override float Pitch
        {
            get => _pitch;
            set => _pitch = value;
        }

        public override float PanStereo
        {
            get => _panStereo;
            set => _panStereo = value;
        }

        public override float SpatialBlend
        {
            get => _spatialBlend;
            set => _spatialBlend = value;
        }

        public override float MaxDistance
        {
            get => _maxDistance;
            set => _maxDistance = value;
        }

        public override float DopplerLevel
        {
            get => _dopplerLevel;
            set => _dopplerLevel = value;
        }

        public override AudioMixerGroup AudioMixerGroup
        {
            get => _mixerGroup;
            set => _mixerGroup = value;
        }

        public override event EventHandler<ResetSoundAgentEventArgs> ResetSoundAgent
        {
            add => _resetHandler += value;
            remove => _resetHandler -= value;
        }

        public override void Play(float fadeInSeconds)
        {
            if (_asset == null || string.IsNullOrEmpty(_asset.EventName))
                return;

            if (!string.IsNullOrEmpty(_asset.BankName))
            {
                if (!WanosBankStore.IsLoaded(_asset.BankName) && !WanosBankStore.Load(_asset.BankName))
                {
                    _playing = false;
                    return;
                }
            }

            if (!WanosEngine.IsEventPostAllowed && !WanosEngine.IsControlEvent(_asset.EventName))
            {
                _playing = false;
                return;
            }

            // Music BGM: PostEvent with Playing/Ended callback so MusicPlaylist can Break / auto-next.
            if (IsMusicAsset())
            {
                _instanceId = WanosNative.Instance.PostEvent(
                    0, _asset.EventName, WanosEventCallbackQueue.Entry);
                MusicPlaylist.Instance.AttachMusicVoice(_instanceId);
            }
            else
            {
                _instanceId = WanosNative.Instance.PostEvent(0, _asset.EventName);
            }

            _playing = _instanceId != 0 || WanosEngine.IsEventPostAllowed;
            ApplyVolumeToEngine();
            if (_playing && fadeInSeconds <= 0f && WanosEngine.IsPlaybackEnabled)
                WanosEngine.Unmute("agent-play");
        }

        public override void Stop(float fadeOutSeconds)
        {
            if (IsMusicAsset())
            {
                // Detach before Stop_Music so Ended from stop does not auto-next.
                if (_instanceId != 0)
                {
                    MusicPlaylist.Instance.DetachMusicVoice(_instanceId);
                    WanosNative.Instance.RemoveEventStateCallback(_instanceId);
                }
                WanosNative.Instance.PostEvent(0, "Stop_Music");
            }

            _playing = false;
            _instanceId = 0;
        }

        bool IsMusicAsset()
        {
            if (_asset == null)
                return false;
            if (!string.IsNullOrEmpty(_asset.EventName)
                && _asset.EventName.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return !string.IsNullOrEmpty(_asset.BankName)
                && _asset.BankName.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public override void Pause(float fadeOutSeconds)
        {
            _volumeWhenPause = _volume;
            if (_asset != null && _asset.EventName.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0)
                WanosNative.Instance.PostEvent(0, "Pause_Music");
            else
                WanosEngine.PauseAll();
            _playing = false;
        }

        public override void Resume(float fadeInSeconds)
        {
            _volume = _volumeWhenPause;
            if (_asset != null && _asset.EventName.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0)
                WanosNative.Instance.PostEvent(0, "Resume_Music");
            else
                WanosEngine.ResumeAll();
            _playing = true;
            ApplyVolumeToEngine();
        }

        public override void Reset()
        {
            if (_instanceId != 0)
            {
                MusicPlaylist.Instance.DetachMusicVoice(_instanceId);
                WanosNative.Instance.RemoveEventStateCallback(_instanceId);
            }
            _cachedTransform.localPosition = Vector3.zero;
            _asset = null;
            _bindingEntity = null;
            _instanceId = 0;
            _playing = false;
            _volumeWhenPause = 1f;
        }

        public override bool SetSoundAsset(object soundAsset)
        {
            if (soundAsset is WanosSoundAsset wanos)
            {
                _asset = wanos;
                return true;
            }

            if (soundAsset is string name && WanosSoundAsset.TryParse(name, out WanosSoundAsset parsed))
            {
                _asset = parsed;
                return true;
            }

            return false;
        }

        public override void SetBindingEntity(Entity bindingEntity)
        {
            _bindingEntity = bindingEntity != null ? bindingEntity.Logic : null;
            if (_bindingEntity != null)
            {
                UpdateAgentPosition();
                return;
            }

            FireReset();
        }

        public override void SetWorldPosition(Vector3 worldPosition)
        {
            _cachedTransform.position = worldPosition;
        }

        void Awake()
        {
            _cachedTransform = transform;
        }

        void Update()
        {
            if (_bindingEntity != null)
                UpdateAgentPosition();
        }

        void UpdateAgentPosition()
        {
            if (_bindingEntity != null && _bindingEntity.Available)
            {
                _cachedTransform.position = _bindingEntity.CachedTransform.position;
                return;
            }

            FireReset();
        }

        void FireReset()
        {
            if (_resetHandler == null)
                return;
            ResetSoundAgentEventArgs args = ResetSoundAgentEventArgs.Create();
            _resetHandler(this, args);
            ReferencePool.Release(args);
        }

        void ApplyVolumeToEngine()
        {
            float v = _mute ? 0f : _volume;
            // Bank RTPC is Fx_Volume (legacy GameData.wFX), not SFX_Volume.
            // 用 WanosEngine 的常量，别写字面量：引擎对不认识的 RTPC 名是静默丢弃的。
            if (_asset != null && _asset.EventName.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0)
                WanosEngine.SetVolumeParameter(WanosEngine.MusicVolumeParam, v);
            else
                WanosEngine.SetVolumeParameter(WanosEngine.FxVolumeParam, v);
        }
    }
}
