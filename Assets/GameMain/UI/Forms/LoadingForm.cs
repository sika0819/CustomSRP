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
        VideoPlayer _player;
        RenderTexture _texture;
        bool _landscape;

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

            _still = Root.Q("campfireStill");
            _video = Root.Q("campfireVideo");
            if (_video != null)
                _video.pickingMode = PickingMode.Ignore;

            _player = GetComponent<VideoPlayer>();
            if (_player == null)
                _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = true;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.prepareCompleted += OnPrepared;

            OnClick("enterIslandButton", () => GameEntry.Event.Fire(this, EnterIslandEventArgs.Create()));
            ScreenLayout.Changed += ApplyOrientation;
            ApplyOrientation();
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
