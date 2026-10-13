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
                _video.pickingMode = PickingMode.Ignore;

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
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.prepareCompleted += OnPrepared;

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

        public override void OnClose(bool isShutdown, object userData)
        {
            ScreenLayout.Changed -= ApplyOrientation;
            if (_player != null)
            {
                _player.prepareCompleted -= OnPrepared;
                _player.Stop();
                _player.targetTexture = null;
            }

            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
                _texture = null;
            }

            base.OnClose(isShutdown, userData);
        }

        void ApplyOrientation()
        {
            bool landscape = ScreenLayout.IsLandscapeForAssets;
            if (_still != null)
            {
                Texture2D still = landscape ? LandscapeStill : PortraitStill;
                if (still != null)
                    _still.style.backgroundImage = new StyleBackground(still);
            }

            if (_player == null)
                return;

            VideoClip clip = landscape ? LandscapeClip : PortraitClip;
            if (clip == null)
                return;

            if (_texture == null || _landscape != landscape)
            {
                if (_texture != null)
                {
                    _texture.Release();
                    Destroy(_texture);
                }

                _landscape = landscape;
                _texture = new RenderTexture(landscape ? 1920 : 1080, landscape ? 1080 : 1920, 0);
                _player.targetTexture = _texture;
                if (_video != null)
                    _video.style.backgroundImage = Background.FromRenderTexture(_texture);
            }

            if (_player.clip != clip)
            {
                _player.Stop();
                _player.clip = clip;
                _player.Prepare();
            }
            else if (!_player.isPlaying && _player.isPrepared)
            {
                _player.Play();
            }
        }

        void OnPrepared(VideoPlayer player)
        {
            player.Play();
        }
    }
}
