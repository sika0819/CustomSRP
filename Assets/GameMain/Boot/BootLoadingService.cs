using System.Collections;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Boluo BootLoadingService subset over <see cref="LoadingForm"/>.
    /// Progress never goes backward; world spawn only reports; uncover fades the cover.
    /// </summary>
    public static class BootLoadingService
    {
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
        static LoadingForm _form;

        public static bool IsVisible => _visible && !_hiding && AliveForm() != null;

        public static bool HasForm =>
            GameEntry.UI != null && GameEntry.UI.HasUIForm(UiAssets.LoadingForm);

        /// <summary>
        /// Clears static fade/progress state. Required when Domain Reload is off,
        /// or when a previous FadeHide was interrupted by exiting Play Mode.
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
            _form = null;
        }

        public static void Bind(LoadingForm form)
        {
            if (form == null)
                return;
            _form = form;
            _hiding = false;
            _visible = true;
            _form.SetCoverAlpha(1f);
            ApplyImmediate(_shownProgress, _tip);
        }

        public static void Show(string tip = "正在进入世界...", float progress = 0.08f)
        {
            // Never inherit a stuck fade from last Play / interrupted coroutine.
            if (_hiding || AliveForm() == null)
            {
                _hiding = false;
                _smooth = null;
                _fade = null;
                if (AliveForm() == null)
                    _form = null;
            }

            _tip = string.IsNullOrEmpty(tip) ? _tip : tip;
            _visible = true;
            float p = Mathf.Clamp01(progress);
            _shownProgress = p;
            _targetProgress = p;
            EnsureForm();
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
            if (_hiding)
                return;
            if (!_visible)
            {
                Hide();
                return;
            }

            EnsureForm();
            MonoBehaviour host = GameEntry.Base;
            LoadingForm form = AliveForm();
            if (host == null || form == null)
            {
                Hide();
                return;
            }

            _tip = "即将进入...";
            _targetProgress = 1f;
            _hiding = true;
            if (_fade != null)
                host.StopCoroutine(_fade);
            _fade = host.StartCoroutine(FadeHideRoutine(duration));
        }

        public static void Hide()
        {
            StopRoutines();
            LoadingForm form = AliveForm();
            if (form != null
                && GameEntry.UI != null
                && GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
            {
                form.SetCoverAlpha(1f);
                GameEntry.UI.CloseUIForm(GameEntry.UI.GetUIForm(UiAssets.LoadingForm).SerialId);
            }

            _form = null;
            _visible = false;
            _hiding = false;
            _shownProgress = 0f;
            _targetProgress = 0f;
        }

        static IEnumerator FadeHideRoutine(float duration)
        {
            float settle = 0.12f;
            while (settle > 0f)
            {
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
            if (_form == null && !GameEntry.UI.HasUIForm(UiAssets.LoadingForm))
            {
                GameEntry.UI.OpenUIForm(UiAssets.LoadingForm, "Loading");
                uiForm = GameEntry.UI.GetUIForm(UiAssets.LoadingForm);
                _form = uiForm?.Logic as LoadingForm;
            }

            AliveForm()?.SetCoverAlpha(1f);
        }

        static LoadingForm AliveForm()
        {
            // Unity fake-null: destroyed LoadingForm still looks non-null to C#.
            return _form != null ? _form : null;
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
            AliveForm()?.SetProgress(progress, tip);
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
        }
    }
}
