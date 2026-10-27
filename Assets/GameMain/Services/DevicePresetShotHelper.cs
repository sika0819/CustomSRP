using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 真机无 UI 截图桥：adb 写入 persistentDataPath/preset_shot_cmd.txt，
    /// 格式 <c>shot N</c>（N=预设 Id 1..10）、<c>restore</c>、或 <c>pose</c>。
    /// 切预设、藏 HUD/Debugger 后打 log <c>[DevicePresetShot] READY id=N</c>。
    /// <c>pose</c> 写出当前相机机位到 <c>preset_pose.txt</c> 并打 log。
    /// </summary>
    public sealed class DevicePresetShotHelper : MonoBehaviour
    {
        const string CmdFileName = "preset_shot_cmd.txt";
        const string PoseFileName = "preset_pose.txt";
        const string LogTag = "[DevicePresetShot]";
        /// <summary>MapItem CatchUp 结束后再等一小会儿，让天气/雾/草绑定稳住。</summary>
        const float SettleSeconds = 0.75f;
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
                return;
            }

            if (raw.Equals("pose", System.StringComparison.OrdinalIgnoreCase))
            {
                DumpCameraPose();
            }
        }

        void DumpCameraPose()
        {
            IslandJoystickCamera rig = IslandJoystickCamera.Current;
            Transform t = rig != null ? rig.transform : Camera.main != null ? Camera.main.transform : null;
            if (t == null)
            {
                Log.Warning($"{LogTag} pose: no camera");
                return;
            }

            Vector3 p = t.position;
            Vector3 e = t.eulerAngles;
            float pitch = e.x > 180f ? e.x - 360f : e.x;
            string line =
                $"pos={p.x:F4},{p.y:F4},{p.z:F4} yaw={e.y:F3} pitch={pitch:F3} eyeHeight={(rig != null ? rig.EyeHeight : 0f):F2}";
            Log.Info($"{LogTag} POSE {line}");

            string path = Path.Combine(Application.persistentDataPath, PoseFileName);
            try
            {
                File.WriteAllText(path, line + "\n");
                Log.Info($"{LogTag} wrote {path}");
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{LogTag} pose write failed: {ex.Message}");
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

            // 等一帧让相机落点写入 focus，再铺齐附近 MapItem。
            yield return null;
            yield return WaitMapItemsCaughtUp();
            yield return new WaitForSecondsRealtime(SettleSeconds);

            // 再藏一次，防止切预设时 UI 又被刷出来。
            HideUi();
            yield return null;
            yield return null;

            Log.Info($"{LogTag} READY id={presetId}");
            _busy = false;
        }

        IEnumerator WaitMapItemsCaughtUp()
        {
            EnvMapItemSpawner spawner = GameEntry.World != null
                ? GameEntry.World.MapItemSpawner
                : null;
            if (spawner == null || !spawner.IsReady)
            {
                Log.Warning($"{LogTag} MapItemSpawner not ready — settle only");
                yield break;
            }

            yield return spawner.CatchUpAroundFocus();
            Log.Info($"{LogTag} map items caught up spawned={spawner.SpawnedCount}");
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
