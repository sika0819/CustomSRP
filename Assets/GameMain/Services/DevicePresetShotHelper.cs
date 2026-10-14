using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 真机无 UI 截图桥：adb 写入 persistentDataPath/preset_shot_cmd.txt，
    /// 格式 <c>shot N</c>（N=预设 Id 1..10）或 <c>restore</c>。
    /// 切预设、藏 HUD/Debugger 后打 log <c>[DevicePresetShot] READY id=N</c>。
    /// </summary>
    public sealed class DevicePresetShotHelper : MonoBehaviour
    {
        const string CmdFileName = "preset_shot_cmd.txt";
        const string LogTag = "[DevicePresetShot]";
        const float SettleSeconds = 2.5f;
        const float PollSeconds = 0.35f;

        float _nextPoll;
        bool _busy;
        bool _uiHidden;
        DebuggerComponent _debugger;
        bool _debuggerWasActive = true;
        UIDocument[] _docs;
        float[] _docOpacities;

        public static DevicePresetShotHelper Ensure()
        {
            DevicePresetShotHelper existing = FindAnyObjectByType<DevicePresetShotHelper>();
            if (existing != null)
                return existing;

            var go = new GameObject("DevicePresetShotHelper");
            DontDestroyOnLoad(go);
            return go.AddComponent<DevicePresetShotHelper>();
        }

        void Update()
        {
            if (_busy || Time.unscaledTime < _nextPoll)
                return;

            _nextPoll = Time.unscaledTime + PollSeconds;
            TryConsumeCommand();
        }

        void TryConsumeCommand()
        {
            string path = Path.Combine(Application.persistentDataPath, CmdFileName);
            if (!File.Exists(path))
                return;

            string raw;
            try
            {
                raw = File.ReadAllText(path).Trim();
                File.Delete(path);
            }
            catch (System.Exception e)
            {
                Log.Warning($"{LogTag} read/delete failed: {e.Message}");
                return;
            }

            if (string.IsNullOrEmpty(raw))
                return;

            Log.Info($"{LogTag} cmd={raw}");
            if (raw.StartsWith("shot", System.StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = raw.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out int id) || id < 1)
                {
                    Log.Warning($"{LogTag} bad shot cmd: {raw}");
                    return;
                }

                StartCoroutine(RunShot(id));
                return;
            }

            if (raw.Equals("restore", System.StringComparison.OrdinalIgnoreCase))
            {
                RestoreUi();
                Log.Info($"{LogTag} RESTORED");
            }
        }

        IEnumerator RunShot(int presetId)
        {
            _busy = true;
            HideUi();

            // 直接 Apply，跳过 veil/标签，画面干净。
            if (GameEntry.Presets != null)
                GameEntry.Presets.Apply(presetId, teleportPlayer: true);
            else
                Log.Warning($"{LogTag} Presets null");

            yield return new WaitForSecondsRealtime(SettleSeconds);

            // 再藏一次，防止切预设时 UI 又被刷出来。
            HideUi();
            yield return null;
            yield return null;

            Log.Info($"{LogTag} READY id={presetId}");
            _busy = false;
        }

        void HideUi()
        {
            if (!_uiHidden)
            {
                _debugger = FindAnyObjectByType<DebuggerComponent>();
                if (_debugger != null)
                {
                    _debuggerWasActive = _debugger.ActiveWindow;
                    _debugger.ActiveWindow = false;
                }

                _docs = FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude);
                _docOpacities = new float[_docs.Length];
                for (int i = 0; i < _docs.Length; i++)
                {
                    VisualElement root = _docs[i] != null ? _docs[i].rootVisualElement : null;
                    if (root == null)
                    {
                        _docOpacities[i] = 1f;
                        continue;
                    }

                    _docOpacities[i] = root.resolvedStyle.opacity;
                    root.style.opacity = 0f;
                    root.pickingMode = PickingMode.Ignore;
                }

                _uiHidden = true;
            }
            else if (_docs != null)
            {
                for (int i = 0; i < _docs.Length; i++)
                {
                    VisualElement root = _docs[i] != null ? _docs[i].rootVisualElement : null;
                    if (root == null)
                        continue;
                    root.style.opacity = 0f;
                    root.pickingMode = PickingMode.Ignore;
                }
            }
        }

        void RestoreUi()
        {
            if (!_uiHidden)
                return;

            if (_debugger != null)
                _debugger.ActiveWindow = _debuggerWasActive;

            if (_docs != null && _docOpacities != null)
            {
                for (int i = 0; i < _docs.Length; i++)
                {
                    VisualElement root = _docs[i] != null ? _docs[i].rootVisualElement : null;
                    if (root == null)
                        continue;
                    root.style.opacity = _docOpacities[i];
                    root.pickingMode = PickingMode.Position;
                }
            }

            _docs = null;
            _docOpacities = null;
            _uiHidden = false;
        }
    }
}
