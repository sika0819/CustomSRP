using System.Collections;
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
        bool _dismissing;

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
            // 揭罩之后才送达的 Open：不要先把 Document 打开，否则会整页闪回加载罩。
            if (!BootLoadingService.AcceptOpen)
            {
                _dismissing = true;
                base.OnOpen(userData);
                Debug.LogWarning("[LoadingForm] stale open after cover dismissed");
                RejectStaleOpen();
                return;
            }

            _dismissing = false;
            // ForceVeilOff 会关掉 Document。框架再次打开只 SetActive，不会把 enabled 设回来。
            // 必须在 base.OnOpen 读 rootVisualElement 之前启用，否则 Root 为空，Bind 不会跑。
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
            UIDocument document = GetComponent<UIDocument>();
            if (document != null && !document.enabled)
                document.enabled = true;

            base.OnOpen(userData);
            if (Root == null)
                return;

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

            RebindChrome(Root);
            // Before player/RT setup: empty video layer must not paint black over still.
            HideVideoLayer();

            // Apply still as early as possible so first UITK paint is orientation 底图.
            ApplyStillForCurrentOrientation();

            VisualElement loading = Root.Q("uiloading");
            if (loading != null)
            {
                loading.style.display = DisplayStyle.Flex;
                loading.BringToFront();
            }

            if (_titleLabel != null)
            {
                _titleLabel.style.display = DisplayStyle.Flex;
                _titleLabel.style.color = Color.white;
            }

            if (_coverRoot != null)
            {
                _coverRoot.style.display = DisplayStyle.Flex;
                _coverRoot.pickingMode = PickingMode.Position;
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
        /// 淡出开始后禁止再把静帧 / 视频铺回来。视频首帧若赶在揭罩时到达，会把加载页再画一帧。
        /// </summary>
        public void BeginDismiss()
        {
            _dismissing = true;
        }

        /// <summary>
        /// Boluo PrepareBlackFade: drop still/video/bar so the last second is a black veil, not a washed island.
        /// </summary>
        public void PrepareBlackFade()
        {
            _dismissing = true;
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
        /// Hide 之后才进 OnOpen 的实例：立刻拆视觉，下一帧再 Close，避免在 OnOpen 里重入关界面。
        /// </summary>
        public void RejectStaleOpen()
        {
            _dismissing = true;
            ForceVeilOff();
            int serial = UIForm != null ? UIForm.SerialId : 0;
            MonoBehaviour host = GameEntry.Base;
            if (serial == 0 || host == null || !host.isActiveAndEnabled)
            {
                if (serial != 0 && GameEntry.UI != null)
                    GameEntry.UI.CloseUIForm(serial);
                return;
            }

            host.StartCoroutine(CloseRejectedNextFrame(serial));
        }

        IEnumerator CloseRejectedNextFrame(int serial)
        {
            yield return null;
            if (this == null || !_dismissing)
                yield break;
            if (GameEntry.UI != null)
                GameEntry.UI.CloseUIForm(serial);
        }

        public override void OnDepthChanged(int uiGroupDepth, int depthInUIGroup)
        {
            // 关闭过程里改 sortingOrder 会把已经摘掉的视觉树再挂回面板，罩就撤不掉。
            if (_dismissing)
                return;
            base.OnDepthChanged(uiGroupDepth, depthInUIGroup);
        }

        public void ForceVeilOff()
        {
            _dismissing = true;
            _coverAlpha = 0f;
            ReleaseVideoNow();
            ConcealTree();
        }

        /// <summary>退登卸 GPU 前调用。硬解和目标 RT 不能留到下一趟进岛。</summary>
        public void ReleasePlaybackForLeave() => ReleaseVideoNow();

        /// <summary>
        /// Stop 不够：Android 硬解和目标 RT 会留到 OnClose。罩不关界面时解码器就叠到下一趟进岛上。
        /// </summary>
        void ReleaseVideoNow()
        {
            if (_player != null)
            {
                _player.Stop();
                _player.clip = null;
                _player.targetTexture = null;
            }

            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
                _texture = null;
            }

            if (_video != null)
                _video.style.backgroundImage = new StyleBackground(StyleKeyword.None);
        }

        public void DisarmDocument()
        {
            _dismissing = true;
            DisableDocumentNow();
        }

        void ConcealTree()
        {
            if (_coverRoot != null)
            {
                _coverRoot.style.opacity = 0f;
                _coverRoot.style.display = DisplayStyle.None;
                _coverRoot.pickingMode = PickingMode.Ignore;
            }

            if (Root != null)
            {
                Root.style.display = DisplayStyle.None;
                Root.style.opacity = 0f;
                Root.pickingMode = PickingMode.Ignore;
            }

            UIDocument document = GetComponent<UIDocument>();
            VisualElement panelRoot = document != null ? document.rootVisualElement : null;
            if (panelRoot != null)
            {
                panelRoot.style.display = DisplayStyle.None;
                panelRoot.style.opacity = 0f;
                panelRoot.pickingMode = PickingMode.Ignore;
            }
        }

        void DisableDocumentNow()
        {
            UIDocument document = GetComponent<UIDocument>();
            if (document == null)
                return;

            VisualElement panelRoot = document.rootVisualElement;
            if (panelRoot != null)
            {
                panelRoot.style.display = DisplayStyle.None;
                panelRoot.style.opacity = 0f;
                panelRoot.RemoveFromHierarchy();
            }

            document.enabled = false;
        }

        /// <summary>
        /// 撤销 <see cref="ForceVeilOff"/> / PrepareBlackFade，供退登后再 Show 加载罩。
        /// </summary>
        public void RestoreCover()
        {
            _dismissing = false;
            gameObject.SetActive(true);
            UIDocument document = GetComponent<UIDocument>();
            VisualElement tree = null;
            if (document != null)
            {
                document.enabled = true;
                tree = document.rootVisualElement;
                if (tree != null)
                {
                    tree.style.display = DisplayStyle.Flex;
                    tree.pickingMode = PickingMode.Position;
                }
            }

            // 启用 Document 会换一棵树。按新树重新取出节点，不要改已经脱离面板的旧引用。
            if (tree != null)
                BindRoot(tree);
            RebindChrome(tree ?? Root);

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
            VisualElement loading = tree != null ? tree.Q("uiloading") : null;
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

        void RebindChrome(VisualElement tree)
        {
            if (tree == null)
                return;

            _coverRoot = tree.Q("root") ?? tree;
            _still = tree.Q("campfireStill");
            _video = tree.Q("campfireVideo");
            if (_still != null)
                _still.pickingMode = PickingMode.Ignore;
            if (_video != null)
                _video.pickingMode = PickingMode.Ignore;

            _fill = tree.Q("fill");
            if (_fill != null)
            {
                _fill.style.transformOrigin = new TransformOrigin(Length.Percent(0), Length.Percent(50));
                _fill.style.width = Length.Percent(100);
            }

            _progressLabel = tree.Q<Label>("labelprogress");
            _titleLabel = tree.Q<Label>("labeltitle");
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
            if (_dismissing)
                return;
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
            if (_dismissing)
                return;
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
            if (_dismissing || _video == null)
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
