using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;
using Object = UnityEngine.Object;
using UGF = UnityGameFramework.Runtime.GameEntry;

namespace Game
{
    /// <summary>
    /// Boluo BootLoadingService subset over <see cref="LoadingForm"/>.
    /// Progress never goes backward; world spawn only reports; uncover fades the cover.
    /// </summary>
    public static class BootLoadingService
    {
        /// <summary>
        /// GuestLogin 会话起点。退登重进用 <see cref="Show"/> 写回这里，不沿用岛内 0.70。
        /// </summary>
        public const float LoginStart = 0.28f;

        public const float VisualPrepareMin = 0.70f;
        public const float TerrainPrepareMax = 0.86f;
        public const float CameraPrepareMax = 0.93f;
        public const float ItemsPrepareMax = 1f;

        static bool _visible;
        static bool _hiding;
        static float _shownProgress;
        static float _targetProgress;
        static string _tip = "正在进入世界...";
        static Coroutine _smooth;
        static Coroutine _fade;
        static Coroutine _poolRelease;
        static Coroutine _extinguish;
        static LoadingCoverLatch _latch;

        public static bool Suppressed { get; private set; }
        static LoadingForm _form;

        public static bool IsVisible =>
            _visible && !_hiding && (AliveForm() != null || BootEarlyCover.IsShowing);

        /// <summary>当前加载罩文案。退登卸岛时是「登录中」。</summary>
        public static string Tip => _tip;

        /// <summary>
        /// <see cref="FadeHide"/> 协程还没跑完。揭罩只等这个。
        /// 淡出结束后 <see cref="HasForm"/> 仍为 true：界面先留着，关掉 Document 会把篝火静帧冻在屏幕上。
        /// </summary>
        public static bool IsHiding => _hiding;

        /// <summary>
        /// 揭罩 <see cref="Hide"/> 之后后台铺装仍会让已经发出的 OpenUIForm 晚到。
        /// 晚到的 OnOpen 不得再把加载罩铺满。
        /// </summary>
        public static bool AcceptOpen => _visible && !_hiding;

        public static bool HasForm =>
            GameEntry.UI != null && GameEntry.UI.HasUIForm(UiAssets.LoadingForm);

        /// <summary>
        /// Clears static fade/progress state. Required when Domain Reload is off,
        /// or when a previous FadeHide was interrupted by exiting Play Mode.
        /// Does not hide <see cref="BootEarlyCover"/> — Launch Awake owns first paint.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void ResetSession()
        {
            _visible = false;
            _hiding = false;
            _shownProgress = 0f;
            _targetProgress = 0f;
            _tip = "正在进入世界...";
            _smooth = null;
            _fade = null;
            _poolRelease = null;
            _extinguish = null;
            _form = null;
            Suppressed = false;
        }

        public static void Bind(LoadingForm form)
        {
            if (form == null)
                return;
            // 近场就绪就会揭罩，草地/氛围还在后台 ReportProgress → EnsureForm → Open。
            // 这个 Open 的回调若在 Hide 之后才到，再把 _visible 打回 true，罩就撤不掉。
            if (!AcceptOpen)
            {
                Log.Warning("[BootLoadingService] Bind rejected — cover already dismissed");
                form.ForceVeilOff();
                return;
            }

            _form = form;
            _hiding = false;
            _visible = true;
            _form.SetCoverAlpha(1f);
            ApplyImmediate(_shownProgress, _tip);
            // Same frame: drop scene early cover so still→video handoff has no double stack.
            BootEarlyCover.HideIfAny();
        }

        public static void Show(string tip = "正在进入世界...", float progress = 0.08f)
        {
            // 退登重登：上一轮 FadeHideRoutine 若只把 _fade 置空而不 Stop，会继续跑到 Hide()，
            // 把 GuestLogin / 二次进岛刚 Show 的罩拆掉 → 真机「卡在加载界面」。
            StopRoutines();
            StopExtinguish();
            Suppressed = false;
            _hiding = false;
            // 关掉的 Document 不算活罩。先丢掉引用，再按 UIManager 里仍打开的实例恢复。
            if (!IsCoverLive(_form))
                _form = null;

            _tip = string.IsNullOrEmpty(tip) ? _tip : tip;
            _visible = true;
            float p = Mathf.Clamp01(progress);
            _shownProgress = p;
            _targetProgress = p;
            EnsureForm();
            LoadingForm form = _form != null ? _form : null;
            if (form != null)
            {
                // 有 Form 就恢复这一棵树并藏早期罩，避免退登重进叠两层。
                form.RestoreCover();
                BootEarlyCover.HideIfAny();
            }
            else
                BootEarlyCover.Instance?.Show(_tip, _shownProgress);
            ApplyImmediate(_shownProgress, _tip);
            EnsureSmoothRunner();
        }

        public static void ReportProgress(float progress, string tip = null)
        {
            if (!_visible || _hiding)
                return;

            progress = Mathf.Clamp01(progress);
            if (progress + 0.0005f < _shownProgress)
                progress = _shownProgress;

            _targetProgress = progress;
            if (!string.IsNullOrEmpty(tip))
                _tip = tip;

            EnsureForm();
            EnsureSmoothRunner();
        }

        public static void ReportPrepareProgress(float from, float to, int ready, int left, string tip)
        {
            int total = ready + left;
            float t = total <= 0 ? 1f : ready / (float)total;
            ReportProgress(Mathf.Lerp(from, to, t), tip);
        }

        public static void FadeHide(float duration = 1f)
        {
            // 二次进岛：上一轮 Leave 可能把 _hiding 钉死；若 form 仍在却直接 return，
            // 会卡在「即将进入...」且 Uncover 以为 IsVisible=false 不再强关。
            if (_hiding)
            {
                Log.Warning("[BootLoadingService] FadeHide while _hiding — force Hide then continue");
                Hide();
                // Hide 后若业务仍要淡出，下面 !_visible 会再 Hide 一次（幂等）。
            }

            if (!_visible)
            {
                Hide();
                return;
            }

            // 不要在这里 Open 新的 LoadingForm。刚关掉再打开会用默认不透明度整页闪出来。
            // 早期罩若还开着，LoadingForm 淡出后会从底下露出来，看起来像加载页又闪了一下。
            BootEarlyCover.HideIfAny();
            MonoBehaviour host = GameEntry.Base;
            LoadingForm form = AliveForm();
            if (host == null || form == null)
            {
                // No LoadingForm yet (early cover only) or host gone — drop everything.
                Hide();
                return;
            }

            form.BeginDismiss();
            _tip = "即将进入...";
            _targetProgress = 1f;
            _hiding = true;
            if (_fade != null)
                host.StopCoroutine(_fade);
            _fade = host.StartCoroutine(FadeHideRoutine(duration));
        }

        public static void Hide()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            StopRoutines();
            // 先把篝火树透明度打成 0，Document 先留着。同一帧 Close / enabled=false
            // 会让面板停在上一帧，真机揭罩后仍满屏「正在铺装草地」。
            ExtinguishOnce();
            Suppressed = true;
            EnsureLatch();

            _form = null;
            _visible = false;
            _hiding = false;
            _shownProgress = 0f;
            _targetProgress = 0f;
            Log.Info("[Hitch] BootLoading Hide ms=" + sw.ElapsedMilliseconds);
        }

        /// <summary>
        /// Only scene leftovers without <see cref="UIForm"/> (never opened via GF).
        /// GF-managed instances always have UIForm after CreateUIForm — leave those alone.
        /// </summary>
        static void DestroyOrphanLoadingForms()
        {
            LoadingForm[] forms = Object.FindObjectsByType<LoadingForm>(FindObjectsInactive.Include);
            int n = 0;
            for (int i = 0; i < forms.Length; i++)
            {
                LoadingForm f = forms[i];
                if (f == null)
                    continue;
                if (f.GetComponent<UIForm>() != null)
                    continue;
                f.ForceVeilOff();
                Object.Destroy(f.gameObject);
                n++;
            }

            if (n > 0)
                Log.Warning("[BootLoadingService] DestroyOrphanLoadingForms count=" + n);
        }

        /// <summary>
        /// Drop unused UI instance-pool entries. The loading cover stays spawned, so it is not released.
        /// Must run after UIManager has recycled Close → Unspawn; same-frame is too early.
        /// </summary>
        public static void ReleaseUnusedUiPool() => ScheduleReleaseUnusedUiInstances();

        /// <summary>
        /// Drop unused UI instance-pool entries (incl. Targets already destroyed by older Hide).
        /// Must run after UIManager has recycled Close → Unspawn; same-frame is too early.
        /// </summary>
        static void ScheduleReleaseUnusedUiInstances()
        {
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
            {
                ReleaseUnusedUiInstancesNow();
                return;
            }

            if (_poolRelease != null)
                host.StopCoroutine(_poolRelease);
            _poolRelease = host.StartCoroutine(ReleaseUnusedUiInstancesNextFrame());
        }

        static IEnumerator ReleaseUnusedUiInstancesNextFrame()
        {
            // UIManager.Update recycles Close → pool on the same GameFramework tick;
            // wait one player frame so Unspawn has landed before ReleaseAllUnused.
            yield return null;
            _poolRelease = null;
            ReleaseUnusedUiInstancesNow();
        }

        static void ReleaseUnusedUiInstancesNow()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ObjectPoolComponent pools = UGF.GetComponent<ObjectPoolComponent>();
            pools?.ReleaseAllUnused();
            Log.Info("[Hitch] ReleaseAllUnused ms=" + sw.ElapsedMilliseconds);
        }

        static IEnumerator FadeHideRoutine(float duration)
        {
            // 二次进岛 Prefill 把帧率打到 <1fps 时，按帧淡出可达数十秒；用墙钟硬切。
            const float WallClockCapSec = 1.2f;
            float wallStart = Time.realtimeSinceStartup;

            float settle = 0.12f;
            while (settle > 0f)
            {
                if (Time.realtimeSinceStartup - wallStart > WallClockCapSec)
                    break;
                settle -= Time.unscaledDeltaTime;
                TickSmooth(Time.unscaledDeltaTime * 4f);
                yield return null;
            }

            AliveForm()?.PrepareBlackFade();

            float dur = Mathf.Max(0.05f, duration);
            float t = 0f;
            LoadingForm form = AliveForm();
            float from = form != null ? form.CoverAlpha : 1f;
            if (from < 0.01f)
                from = 1f;

            while (t < dur)
            {
                if (Time.realtimeSinceStartup - wallStart > WallClockCapSec)
                    break;
                t += Time.unscaledDeltaTime;
                float a = Mathf.Lerp(from, 0f, Mathf.Clamp01(t / dur));
                AliveForm()?.SetCoverAlpha(a);
                yield return null;
            }

            Hide();
        }

        static void EnsureForm()
        {
            if (AliveForm() != null)
                return;
            _form = null;
            if (GameEntry.UI == null)
                return;

            var uiForm = GameEntry.UI.GetUIForm(UiAssets.LoadingForm);
            _form = uiForm?.Logic as LoadingForm;
            // HasUIForm 但 Logic 已毁：先关组再开；勿 Destroy（会毒化 UI 实例池）。
            if (_form == null && GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
            {
                UiAssets.CloseGroup("Loading");
                uiForm = null;
            }

            if (_form == null)
            {
                // 清掉池里已毁 Target（旧 ForceDestroy 遗留），否则同步 Spawn → CreateUIForm 报 invalid。
                ReleaseUnusedUiInstancesNow();
                // Close 同帧可能仍 HasUIForm：仍尝试 Open；GF 对同 asset 重复 Open 会失败并打日志，下帧 EnsureForm 再试。
                if (!GameEntry.UI.HasUIForm(UiAssets.LoadingForm)
                    && !GameEntry.UI.IsLoadingUIForm(UiAssets.LoadingForm))
                {
                    GameEntry.UI.OpenUIForm(UiAssets.LoadingForm, "Loading");
                }

                uiForm = GameEntry.UI.GetUIForm(UiAssets.LoadingForm);
                _form = uiForm?.Logic as LoadingForm;
            }

            AliveForm()?.SetCoverAlpha(1f);
        }

        static LoadingForm AliveForm()
        {
            // Unity fake-null: destroyed LoadingForm still looks non-null to C#.
            // Document 被 ForceVeilOff 关掉时不算活罩，调用方会再 Show 而不是只 ReportProgress。
            return IsCoverLive(_form) ? _form : null;
        }

        static bool IsCoverLive(LoadingForm form)
        {
            if (form == null || !form.isActiveAndEnabled)
                return false;
            UIDocument document = form.GetComponent<UIDocument>();
            return document == null || document.enabled;
        }

        static void EnsureSmoothRunner()
        {
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
                return;
            if (_smooth != null)
                return;
            _smooth = host.StartCoroutine(SmoothRoutine());
        }

        static IEnumerator SmoothRoutine()
        {
            while (_visible && !_hiding)
            {
                TickSmooth(Time.unscaledDeltaTime);
                yield return null;
            }

            _smooth = null;
        }

        static void TickSmooth(float dt)
        {
            float speed = Mathf.Max(0.35f, (_targetProgress - _shownProgress) * 2.5f);
            _shownProgress = Mathf.MoveTowards(_shownProgress, _targetProgress, speed * dt);
            if (_shownProgress > _targetProgress)
                _shownProgress = _targetProgress;
            ApplyImmediate(_shownProgress, _tip);
        }

        static void ApplyImmediate(float progress, string tip)
        {
            LoadingForm form = AliveForm();
            if (form != null)
            {
                form.SetProgress(progress, tip);
                return;
            }

            BootEarlyCover.Instance?.SetProgress(progress, tip);
        }

        public static void HoldCoverHidden() => ExtinguishOnce();

        static void EnsureLatch()
        {
            if (_latch != null)
                return;
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
                return;
            _latch = host.GetComponent<LoadingCoverLatch>();
            if (_latch == null)
                _latch = host.gameObject.AddComponent<LoadingCoverLatch>();
        }

        static void ScheduleExtinguish()
        {
            ExtinguishOnce();
            MonoBehaviour host = GameEntry.Base;
            if (host == null || !host.isActiveAndEnabled)
                return;

            if (_extinguish != null)
                host.StopCoroutine(_extinguish);
            _extinguish = host.StartCoroutine(ExtinguishRoutine());
        }

        static void StopExtinguish()
        {
            MonoBehaviour host = GameEntry.Base;
            if (host != null && _extinguish != null)
                host.StopCoroutine(_extinguish);
            _extinguish = null;
        }

        static IEnumerator ExtinguishRoutine()
        {
            // Update 里藏树之后，要等本帧 DrawUIOverlay 把透明结果画出去，再关 Document。
            yield return new WaitForEndOfFrame();
            for (int i = 0; i < 6; i++)
            {
                if (_visible && !_hiding)
                {
                    _extinguish = null;
                    yield break;
                }

                ExtinguishOnce();
                yield return new WaitForEndOfFrame();
            }

            if (!_visible || _hiding)
                Log.Info("[BootLoadingService] cover stayed transparent, skip disarm");
            _extinguish = null;
        }

        static void DisarmCoverDocuments()
        {
            BootEarlyCover.Instance?.Disarm();
            LoadingForm[] forms = Object.FindObjectsByType<LoadingForm>(FindObjectsInactive.Include);
            for (int i = 0; i < forms.Length; i++)
            {
                if (forms[i] != null)
                    forms[i].DisarmDocument();
            }

            if (GameEntry.UI != null)
            {
                if (GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
                {
                    var uiForm = GameEntry.UI.GetUIForm(UiAssets.LoadingForm);
                    if (uiForm != null)
                        GameEntry.UI.CloseUIForm(uiForm.SerialId);
                }

                UiAssets.CloseGroup("Loading");
            }

            DestroyOrphanLoadingForms();
            ScheduleReleaseUnusedUiInstances();
            Log.Info("[BootLoadingService] disarm after paint");
        }

        /// <summary>
        /// 共享 PanelSettings 上，改 sortingOrder / 禁用 Document 的顺序不对时，
        /// campfire 树会脱离 UIDocument 仍留在面板里。按名字从面板摘掉。
        /// </summary>
        static void ExtinguishOnce()
        {
            // 先记住面板再关 Document，否则 root 被清掉就找不到残留的篝火树。
            UIDocument[] docs = Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include);
            var panels = new List<IPanel>(4);
            for (int i = 0; i < docs.Length; i++)
            {
                VisualElement root = docs[i] != null ? docs[i].rootVisualElement : null;
                IPanel panel = root != null ? root.panel : null;
                if (panel == null || panels.Contains(panel))
                    continue;
                panels.Add(panel);
            }

            BootEarlyCover.HideIfAny();
            LoadingForm[] forms = Object.FindObjectsByType<LoadingForm>(FindObjectsInactive.Include);
            for (int i = 0; i < forms.Length; i++)
            {
                if (forms[i] != null)
                    forms[i].ForceVeilOff();
            }

            for (int p = 0; p < panels.Count; p++)
            {
                IPanel panel = panels[p];
                if (panel?.visualTree == null)
                    continue;
                var stills = new List<VisualElement>(4);
                panel.visualTree.Query("campfireStill").ForEach(stills.Add);
                for (int s = 0; s < stills.Count; s++)
                {
                    VisualElement top = stills[s];
                    while (top.parent != null && top.parent != panel.visualTree)
                        top = top.parent;
                    top.style.display = DisplayStyle.None;
                    top.style.opacity = 0f;
                    top.pickingMode = PickingMode.Ignore;
                }
            }
        }

        static void StopRoutines()
        {
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
            {
                _smooth = null;
                _fade = null;
                return;
            }

            if (_smooth != null)
            {
                host.StopCoroutine(_smooth);
                _smooth = null;
            }

            if (_fade != null)
            {
                host.StopCoroutine(_fade);
                _fade = null;
            }

            // Do not stop _poolRelease here — Show() calls StopRoutines and must not
            // cancel a Hide()-scheduled pool purge that clears poisoned instances.
        }
    }

    /// <summary>
    /// 揭罩后每帧把篝火树压成透明。关掉 UIDocument 会把上一帧冻在屏幕上。
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class LoadingCoverLatch : MonoBehaviour
    {
        bool _holding;
        float _holdUntil;

        void LateUpdate()
        {
            if (!BootLoadingService.Suppressed)
            {
                _holding = false;
                return;
            }

            if (!_holding)
            {
                _holding = true;
                _holdUntil = Time.unscaledTime + 5f;
            }

            if (Time.unscaledTime > _holdUntil)
                return;

            BootLoadingService.HoldCoverHidden();
        }
    }
}
