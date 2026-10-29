using UnityEngine;

namespace Game
{
    /// <summary>
    /// 岛屿预设。挂在 Island 场景里。退登不卸场景，播放态不能留在这只服务上。
    /// 手动预设 id 写入当前账号存档；退登清内存，下次进岛按该账号桶恢复。
    /// </summary>
    public sealed class PresetService : MonoBehaviour
    {
        public const string PresetFolder = "Assets/GameMain/Environment/IslandPresets";
        public const int UiPresetCount = 10;

        IslandPresetAsset[] _presets = System.Array.Empty<IslandPresetAsset>();
        public int CurrentId { get; private set; }

        /// <summary>0 idle / 1 switching (veil + weather) / 2 playing — Boluo curSchemeState.</summary>
        public int SchemeState { get; private set; }

        public bool IsTransitioning => SchemeState == 1;

        public IslandPresetAsset[] Presets => _presets;

        public static PresetService Instance { get; private set; }

        public static PresetService Ensure()
        {
            PresetService service = IslandServiceHost.Ensure(Instance, "PresetService");
            Instance = service;
            return service;
        }

        void Awake()
        {
            Instance = this;
            GameEntry.Presets = this;
            ReloadFromResources();
        }

        void OnDestroy()
        {
            WeatherHoldChanged = null;
            if (Instance == this)
                Instance = null;
            if (GameEntry.Presets == this)
                GameEntry.Presets = null;
        }

        public void SetPresets(IslandPresetAsset[] presets)
        {
            _presets = presets ?? System.Array.Empty<IslandPresetAsset>();
        }

        public void ReloadFromResources()
        {
            // Editor Resource mode: load via Resources folder copy or assigned list.
            IslandPresetAsset[] loaded = Resources.LoadAll<IslandPresetAsset>("IslandPresets");
            if (loaded != null && loaded.Length > 0)
            {
                System.Array.Sort(loaded, (a, b) => a.Id.CompareTo(b.Id));
                _presets = loaded;
            }

            WarnMissingCovers();
        }

        /// <summary>Boluo YuShe list shows schemeInfos[0..9] only.</summary>
        public int GetUiCount()
        {
            int n = _presets.Length;
            return n > UiPresetCount ? UiPresetCount : n;
        }

        public IslandPresetAsset GetById(int id)
        {
            for (int i = 0; i < _presets.Length; i++)
            {
                if (_presets[i] != null && _presets[i].Id == id)
                    return _presets[i];
            }
            return null;
        }

        public IslandPresetAsset GetByIndex(int index)
        {
            if (index < 0 || index >= _presets.Length)
                return null;
            return _presets[index];
        }

        public void RequestTransition(int id)
        {
            if (GetById(id) == null)
                return;

            // 无 iris 订阅方时勿只抬 SchemeState=1：否则 IsTransitioning 永真，行走/开预设全死。
            bool hasIrisHandler = GameEntry.Event != null
                && GameEntry.Event.Count(PresetTransitionRequestEventArgs.EventId) > 0;
            if (hasIrisHandler)
            {
                SchemeState = 1;
                GameEntry.Event.Fire(this, PresetTransitionRequestEventArgs.Create(id));
                return;
            }

            Apply(id, teleportPlayer: true);
            MarkPlaying();
        }

        /// <summary>Called when the HUD veil finishes and the preset is live.</summary>
        public void MarkPlaying()
        {
            if (CurrentId > 0)
                SchemeState = 2;
        }

        public void Apply(int id, bool teleportPlayer = true)
        {
            IslandPresetAsset preset = GetById(id);
            if (preset == null)
                return;

            // Doc: BGM 不停播，落点强制切曲 —— 播本预设 Music* bank（先 Stop_Music 再 Play，不叠声）。
            GameEntry.Music?.ForceRetargetAtLanding(ResolvePresetMusicBank(preset));
            // Drop every content 3D voice before teleport so old emitters never follow.
            // Iris path already cleared + held; direct Apply clears here then retriggers below.
            IslandAudioSession.Instance?.ClearContent3D();

            CurrentId = id;
            BoluoGameSave.CapturePresetScheme(id);
            GameEntry.Weather?.SetLookKind(preset.LookKind);
            GameEntry.Weather?.SetTimeOfDay(preset.TimeHours);
            GameEntry.Weather?.ClearPanelTransitionLock();
            GameEntry.Weather?.SetWeather(preset.WeatherKind, preset.WeatherLevel);
            // 关雾 / Clear：强制 0.003，避免旧资产仍写 0.007~0.01。
            float fog = preset.WeatherLevel <= 0 || preset.WeatherKind == IslandWeatherKind.Clear
                ? 0.003f
                : preset.FogDensity;
            GameEntry.Weather?.SetFogDensity(fog);
            // Per-preset light color override (alpha=0 means "use the time-of-day table").
            Color? lightOverride = preset.LightColorOverride.a > 0.001f
                ? (Color?)preset.LightColorOverride
                : null;
            GameEntry.Weather?.SetLightColorOverride(lightOverride);

            if (teleportPlayer)
                TeleportCamera(preset);

            // Iris transition holds precip + zone ambience until reveal; direct Apply retriggers now.
            if (SchemeState != 1)
                IslandAudioSession.Instance?.RetriggerContentAtListener();

            // Veil path keeps SchemeState=1 until MarkPlaying; direct Apply is already live.
            if (SchemeState != 1)
                SchemeState = 2;

            if (GameEntry.Event != null)
                GameEntry.Event.Fire(this, PresetChangedEventArgs.Create(preset));
        }

        /// <summary>
        /// 菠萝 CharacterController 中心在原点、skin 0.08，注视点是胶囊中心。
        /// StayBehind 的 cameraPitch 为 0，相机与注视点同高，距离用 CameraToTargetDis，朝向再乘 CameraOffset。
        /// </summary>
        const float BoluoCapsuleSkin = 0.08f;
        const float DefaultCapsuleHeight = 6f;
        const float DefaultFollowDistance = 3f;

        void TeleportCamera(IslandPresetAsset preset)
        {
            IslandJoystickCamera cam = IslandJoystickCamera.Current;
            if (cam == null && Camera.main != null)
                cam = Camera.main.GetComponent<IslandJoystickCamera>();
            if (cam == null)
                return;
            if (!cam.isActiveAndEnabled)
                cam.enabled = true;

            float capsule = preset.PlayerHeight > 0.1f ? preset.PlayerHeight : DefaultCapsuleHeight;
            Vector3 look;
            if (cam.TryGroundUnder(preset.TargetPos, out Vector3 feet))
            {
                feet.x = preset.TargetPos.x;
                feet.z = preset.TargetPos.z;
                look = feet + Vector3.up * (capsule * 0.5f + BoluoCapsuleSkin);
            }
            else
            {
                // 采不到地面时，TargetPos.y 已是菠萝角色 transform（胶囊中心），不要再加半高。
                look = preset.TargetPos;
            }

            float dist = preset.CameraToTargetDis > 0.01f ? preset.CameraToTargetDis : DefaultFollowDistance;
            Quaternion yawOnly = Quaternion.Euler(0f, preset.TargetRotateY, 0f);
            Vector3 eye = look + yawOnly * (Vector3.back * dist);
            Vector3 toLook = look - eye;
            Quaternion offset = Quaternion.Euler(preset.CameraOffset);
            Quaternion face = toLook.sqrMagnitude < 1e-8f
                ? yawOnly * offset
                : Quaternion.LookRotation(toLook, Vector3.up) * offset;
            Vector3 euler = face.eulerAngles;
            float pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            float yaw = euler.y > 180f ? euler.y - 360f : euler.y;
            cam.TeleportEye(eye, yaw, Mathf.Clamp(pitch, -85f, 85f));
            // 二次进岛的按帧流送补不上这次瞬移。黑场里按落点把附近物体铺上，再揭虹膜。
            EnvItemStreamerLite.CatchUpAtFocus();
        }

        /// <summary>Interrupt (form close / uncover) — drop switching latch without a stuck busy state.</summary>
        public void AbortTransition()
        {
            SchemeState = CurrentId > 0 ? 2 : 0;
        }

        /// <summary>
        /// 退登 / 换账号：清掉本趟播放态。不写盘，账号桶里的 curSchemeID 保留。
        /// </summary>
        public void ClearPlaybackForAccountSwitch()
        {
            AbortWeatherHold();
            CurrentId = 0;
            SchemeState = 0;
        }

        /// <summary>
        /// 进岛：按当前账号存档恢复播放态。只改 CurrentId / SchemeState，不传送、不改天气。
        /// 智能推荐或未选预设则没有任何卡片处于播放中。
        /// </summary>
        public void RestorePlaybackFromAccount()
        {
            BoluoGameSave.EnsureLoaded();
            int id = BoluoGameSave.PresetSchemeId;
            if (id > 0 && GetById(id) != null)
            {
                CurrentId = id;
                SchemeState = 2;
                return;
            }

            CurrentId = 0;
            SchemeState = 0;
        }

        public bool IsWeatherHeld { get; private set; }

        public event System.Action WeatherHoldChanged;

        /// <summary>
        /// 虹膜关闭期间压住雨景、降水声和环境声加载。完全揭开后才 <see cref="ReleaseWeatherHold"/>。
        /// </summary>
        public void BeginWeatherHold()
        {
            if (IsWeatherHeld)
                return;
            IsWeatherHeld = true;
            IslandAudioSession.Instance?.Duck.BeginSilent();
            IslandAudioSession.Instance?.Ambience.SetPresetTransitionHeld(true);
            WeatherHoldChanged?.Invoke();
        }

        public void ReleaseWeatherHold()
        {
            if (!IsWeatherHeld)
                return;
            IsWeatherHeld = false;
            IslandAudioSession.Instance?.Ambience.SetPresetTransitionHeld(false);
            WeatherHoldChanged?.Invoke();
            IslandAudioSession.Instance?.Weather.Refresh();
            IslandAudioSession.Instance?.RetriggerContentAtListener();
        }

        /// <summary>打断 / 离岛：放下闩，不重新触发环境声。</summary>
        public void AbortWeatherHold()
        {
            if (!IsWeatherHeld)
                return;
            IsWeatherHeld = false;
            IslandAudioSession.Instance?.Ambience.SetPresetTransitionHeld(false);
            IslandAudioSession.Instance?.Duck.SnapFull();
            WeatherHoldChanged?.Invoke();
        }

        /// <summary>First <c>Music*</c> entry in <see cref="IslandPresetAsset.BankNames"/>, or null.</summary>
        static string ResolvePresetMusicBank(IslandPresetAsset preset)
        {
            string[] banks = preset != null ? preset.BankNames : null;
            if (banks == null)
                return null;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (AmbienceEventCatalog.IsMusicBank(bn))
                    return bn;
            }
            return null;
        }

        public void ApplyIndex(int index, bool teleportPlayer = true)
        {
            IslandPresetAsset preset = GetByIndex(index);
            if (preset == null)
                return;
            Apply(preset.Id, teleportPlayer);
        }

        /// <summary>Cover must be assigned on the asset (UI/Textures/PresetItem) — no Resources.Load fill-in.</summary>
        void WarnMissingCovers()
        {
            for (int i = 0; i < _presets.Length; i++)
            {
                IslandPresetAsset preset = _presets[i];
                if (preset == null || preset.Cover != null)
                    continue;
                Debug.LogWarning($"[PresetService] Preset id={preset.Id} missing Cover (assign UI/Textures/PresetItem)");
            }
        }
    }
}
