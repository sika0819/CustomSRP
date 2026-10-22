using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace Game
{
    public class LoadingForm : GameFormLogic
    {
        public VideoClip PortraitClip;
        public VideoClip LandscapeClip;
        public Texture2D PortraitStill;
        public Texture2D LandscapeStill;

        VisualElement _still;
        VisualElement _video;
        VisualElement _fill;
        VisualElement _coverRoot;
        Label _progressLabel;
        Label _titleLabel;
        VideoPlayer _player;
        RenderTexture _texture;
        bool _landscape;
        float _coverAlpha = 1f;

        public float CoverAlpha => _coverAlpha;

        static readonly string[] HiddenPages =
        {
            "mobileDatainusepage",
            "filecorruptedpage",
            "storageFullstoragepage",
            "wifidatainusepage"
        };

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            for (int i = 0; i < HiddenPages.Length; i++)
            {
                VisualElement page = Root.Q(HiddenPages[i]);
                if (page != null)
                    page.style.display = DisplayStyle.None;
            }

            // Campfire backdrop + progress chrome. Hide bank-update / logo layers.
            string[] hiddenChrome =
            {
                "bankupdatebackground", "bankupdatevideo", "background2",
                "bankupdatehint", "labeltmp", "lower", "upper"
            };
            for (int i = 0; i < hiddenChrome.Length; i++)
            {
                VisualElement layer = Root.Q(hiddenChrome[i]);
                if (layer != null)
                    layer.style.display = DisplayStyle.None;
            }

            _coverRoot = Root.Q("root") ?? Root;
            _still = Root.Q("campfireStill");
            _video = Root.Q("campfireVideo");
            if (_still != null)
                _still.pickingMode = PickingMode.Ignore;
            if (_video != null)
            {
                _video.pickingMode = PickingMode.Ignore;
                // Before player/RT setup: empty video layer must not paint black over still.
                HideVideoLayer();
            }

            // Apply still as early as possible so first UITK paint is orientation 底图.
            ApplyStillForCurrentOrientation();

            VisualElement loading = Root.Q("uiloading");
            if (loading != null)
            {
                loading.style.display = DisplayStyle.Flex;
                loading.BringToFront();
            }

            _fill = Root.Q("fill");
            if (_fill != null)
            {
                _fill.style.transformOrigin = new TransformOrigin(Length.Percent(0), Length.Percent(50));
                _fill.style.width = Length.Percent(100);
            }

            _progressLabel = Root.Q<Label>("labelprogress");
            _titleLabel = Root.Q<Label>("labeltitle");
            if (_titleLabel != null)
            {
                _titleLabel.style.display = DisplayStyle.Flex;
                _titleLabel.style.color = Color.white;
            }

            SetCoverAlpha(1f);

            _player = GetComponent<VideoPlayer>();
            if (_player == null)
                _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = true;
            _player.waitForFirstFrame = true;
            _player.renderMode = VideoRenderMode.RenderTexture;
            // Stretch into RT — never letterbox; UI stretch-to-fill maps RT → screen.
            _player.aspectRatio = VideoAspectRatio.Stretch;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.prepareCompleted += OnPrepared;
            _player.started += OnVideoStarted;

            ScreenLayout.Changed += ApplyOrientation;
            ApplyOrientation();
            BootLoadingService.Bind(this);
        }

        public void SetProgress(float progress01, string title = null)
        {
            progress01 = Mathf.Clamp01(progress01);
            if (_fill != null)
            {
                // Boluo LoadUpdateUI fillAmount: left→right. UITK scale from left origin.
                float x = progress01;
                if (progress01 > 0.001f && x < 0.02f)
                    x = 0.02f;
                _fill.style.scale = new Scale(new Vector2(x, 1f));
                _fill.style.display = DisplayStyle.Flex;
            }

            if (_progressLabel != null)
            {
                _progressLabel.style.display = DisplayStyle.None;
                _progressLabel.text = Mathf.RoundToInt(progress01 * 100f) + "%";
            }

            if (title != null && _titleLabel != null)
                _titleLabel.text = title;
        }

        /// <summary>
        /// Boluo PrepareBlackFade: drop still/video/bar so the last second is a black veil, not a washed island.
        /// </summary>
        public void PrepareBlackFade()
        {
            if (_still != null)
                _still.style.display = DisplayStyle.None;
            if (_video != null)
                _video.style.display = DisplayStyle.None;
            VisualElement loading = Root != null ? Root.Q("uiloading") : null;
            if (loading != null)
                loading.style.display = DisplayStyle.None;
            if (_titleLabel != null)
                _titleLabel.style.display = DisplayStyle.None;
            if (_coverRoot != null)
            {
                _coverRoot.style.backgroundColor = Color.black;
                _coverRoot.pickingMode = PickingMode.Position;
            }
        }

        public void SetCoverAlpha(float alpha)
        {
            _coverAlpha = Mathf.Clamp01(alpha);
            if (_coverRoot != null)
                _coverRoot.style.opacity = _coverAlpha;
        }

        /// <summary>
        /// 退登重登：UIManager Close 后 UITK 偶发仍挡全屏黑罩。进岛前强制拆视觉。
        /// </summary>
        public void ForceVeilOff()
        {
            _coverAlpha = 0f;
            if (_coverRoot != null)
            {
                _coverRoot.style.opacity = 0f;
                _coverRoot.style.display = DisplayStyle.None;
                _coverRoot.pickingMode = PickingMode.Ignore;
            }

            if (Root != null)
            {
                Root.style.display = DisplayStyle.None;
                Root.pickingMode = PickingMode.Ignore;
            }

            UIDocument document = GetComponent<UIDocument>();
            if (document != null)
            {
                if (document.rootVisualElement != null)
                {
                    document.rootVisualElement.style.display = DisplayStyle.None;
                    document.rootVisualElement.pickingMode = PickingMode.Ignore;
                }

                document.enabled = false;
            }

            gameObject.SetActive(false);
        }

        /// <summary>
        /// 撤销 <see cref="ForceVeilOff"/> / PrepareBlackFade，供退登后再 Show 加载罩。
        /// </summary>
        public void RestoreCover()
        {
            gameObject.SetActive(true);
            UIDocument document = GetComponent<UIDocument>();
            if (document != null)
            {
                document.enabled = true;
                if (document.rootVisualElement != null)
                {
                    document.rootVisualElement.style.display = DisplayStyle.Flex;
                    document.rootVisualElement.pickingMode = PickingMode.Position;
                }
            }

            if (Root != null)
            {
                Root.style.display = DisplayStyle.Flex;
                Root.pickingMode = PickingMode.Position;
            }

            if (_coverRoot != null)
            {
                _coverRoot.style.display = DisplayStyle.Flex;
                _coverRoot.style.backgroundColor = StyleKeyword.Null;
                _coverRoot.pickingMode = PickingMode.Position;
            }

            if (_still != null)
                _still.style.display = DisplayStyle.Flex;
            // Do not show video until Prepare/first frame — black RT would flash over still.
            HideVideoLayer();
            VisualElement loading = Root != null ? Root.Q("uiloading") : null;
            if (loading != null)
            {
                loading.style.display = DisplayStyle.Flex;
                loading.BringToFront();
            }

            if (_titleLabel != null)
                _titleLabel.style.display = DisplayStyle.Flex;

            SetCoverAlpha(1f);
            ApplyOrientation();
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            ScreenLayout.Changed -= ApplyOrientation;
            if (_player != null)
            {
                _player.prepareCompleted -= OnPrepared;
                _player.started -= OnVideoStarted;
                _player.Stop();
                _player.targetTexture = null;
            }

            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
                _texture = null;
            }

            ForceVeilOff();
            base.OnClose(isShutdown, userData);
        }

        void ApplyStillForCurrentOrientation()
        {
            bool landscape = ScreenLayout.IsLandscapeForAssets;
            if (_still == null)
                return;
            Texture2D still = landscape ? LandscapeStill : PortraitStill;
            if (still != null)
                _still.style.backgroundImage = new StyleBackground(still);
            _still.style.display = DisplayStyle.Flex;
        }

        void ApplyOrientation()
        {
            bool landscape = ScreenLayout.IsLandscapeForAssets;
            // Still first: host tear-splash / first UITK frame must show 横竖屏底图, not black RT.
            ApplyStillForCurrentOrientation();
            BringProgressChromeFront();

            if (_player == null)
                return;

            VideoClip clip = landscape ? LandscapeClip : PortraitClip;
            if (clip == null)
            {
                HideVideoLayer();
                return;
            }

            // RT follows clip / design (2436×1125 landscape, 1125×2436 portrait).
            // Old 1920×1080 RT letterboxed the 2436×1124 landscape clip → top/bottom gaps.
            ResolveTargetSize(clip, landscape, out int tw, out int th);
            bool rebuildRt = _texture == null || _landscape != landscape
                || _texture.width != tw || _texture.height != th;
            if (rebuildRt)
            {
                if (_texture != null)
                {
                    _texture.Release();
                    Destroy(_texture);
                }

                _landscape = landscape;
                _texture = new RenderTexture(tw, th, 0);
                _player.targetTexture = _texture;
                // Bind RT but keep layer hidden — new RT clears black over still.
                if (_video != null)
                    _video.style.backgroundImage = Background.FromRenderTexture(_texture);
            }

            // New RT or new clip: always re-Prepare; never unhide until first frame.
            if (rebuildRt || _player.clip != clip)
            {
                HideVideoLayer();
                _player.Stop();
                _player.clip = clip;
                _player.Prepare();
            }
            else if (_player.isPrepared)
            {
                if (!_player.isPlaying)
                    _player.Play();
                ShowVideoLayer();
            }
            else
            {
                HideVideoLayer();
                _player.Prepare();
            }
        }

        void OnPrepared(VideoPlayer player)
        {
            if (player == null)
                return;
            player.Play();
            // waitForFirstFrame: started fires when RT has content; avoid premature unhide.
            if (!player.waitForFirstFrame)
                ShowVideoLayer();
        }

        void OnVideoStarted(VideoPlayer player)
        {
            ShowVideoLayer();
        }

        void HideVideoLayer()
        {
            if (_video != null)
                _video.style.display = DisplayStyle.None;
        }

        void ShowVideoLayer()
        {
            if (_video == null)
                return;
            if (_texture != null)
                _video.style.backgroundImage = Background.FromRenderTexture(_texture);
            _video.style.display = DisplayStyle.Flex;
            BringProgressChromeFront();
        }

        void BringProgressChromeFront()
        {
            VisualElement loading = Root != null ? Root.Q("uiloading") : null;
            if (loading != null)
                loading.BringToFront();
        }

        /// <summary>
        /// Design landscape d = 2436/1125. Prefer clip pixels when available so Stretch
        /// into RT is 1:1; screen aspect then only stretches at the UITK layer.
        /// </summary>
        static void ResolveTargetSize(VideoClip clip, bool landscape, out int width, out int height)
        {
            if (clip != null && clip.width > 0 && clip.height > 0)
            {
                width = (int)clip.width;
                height = (int)clip.height;
                return;
            }

            width = landscape ? 2436 : 1125;
            height = landscape ? 1125 : 2436;
        }
    }
}
