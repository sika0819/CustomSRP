using System;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    [Serializable]
    public class IslandPlayerProp
    {
        public float posx;
        public float posy;
        public float posz;
        public float rotx;
        public float roty;
        public float rotz;
    }

    /// <summary>
    /// 岛屿角色位置：进岛按优先级恢复；周期写入；Bank 申请/下载中抑制。
    /// </summary>
    public static class IslandPlayerPosSave
    {
        public const float SaveIntervalSeconds = 10f;
        public const float SaveStartDelaySeconds = 10f;

        static int _suppressCount;
        static float _nextSaveAt = -1f;
        static bool _armed;

        /// <summary>下次进岛用默认出生点（首次登录 / 退出登录后）。由 Init 消费并清除。</summary>
        public static bool UseDefaultPlayerPosNextLoad { get; set; }

        public static bool ShouldSuppressSave => _suppressCount > 0;

        public static void PushSaveSuppress() => _suppressCount++;

        public static void PopSaveSuppress()
        {
            if (_suppressCount > 0)
                _suppressCount--;
        }

        public static void ClearSaveSuppress() => _suppressCount = 0;

        public static void ResetSession()
        {
            _armed = false;
            _nextSaveAt = -1f;
            ClearSaveSuppress();
        }

        /// <summary>
        /// 进岛相机就绪后调用：按优先级设位，并启动周期保存。
        /// </summary>
        public static void InitAndArm(IslandJoystickCamera cam)
        {
            if (cam == null)
                return;

            ApplySpawnPriority(cam);
            _armed = true;
            _nextSaveAt = Time.unscaledTime + SaveStartDelaySeconds;
        }

        public static void Tick()
        {
            if (!_armed)
                return;
            if (Time.unscaledTime < _nextSaveAt)
                return;
            _nextSaveAt = Time.unscaledTime + SaveIntervalSeconds;
            TrySaveCurrent();
        }

        public static void TrySaveCurrent()
        {
            if (ShouldSuppressSave)
                return;
            var cam = IslandJoystickCamera.Current;
            if (cam == null)
                return;

            Transform t = cam.transform;
            var prop = new IslandPlayerProp
            {
                posx = t.position.x,
                posy = t.position.y,
                posz = t.position.z,
                rotx = t.eulerAngles.x,
                roty = t.eulerAngles.y,
                rotz = t.eulerAngles.z,
            };
            BoluoGamePersistence.PersistPlayerPositionJson(JsonUtility.ToJson(prop));
        }

        /// <summary>写当前坐标并立刻 flush（进后台 / 离岛）。</summary>
        public static void TrySaveCurrentAndFlush()
        {
            TrySaveCurrent();
            BoluoGamePersistence.PlayerPrefsSave();
        }

        static void ApplySpawnPriority(IslandJoystickCamera cam)
        {
            void ApplyDefault() =>
                cam.Teleport(IslandSpawnPose.DefaultPos, IslandSpawnPose.DefaultYawDegrees);

            if (UseDefaultPlayerPosNextLoad)
            {
                UseDefaultPlayerPosNextLoad = false;
                ApplyDefault();
                Log.Info("[IslandPlayerPosSave] default spawn (UseDefaultPlayerPosNextLoad)");
                return;
            }

            if (!BoluoGamePersistence.HasDaoYuFirstRunCompleteMarker())
            {
                ApplyDefault();
                BoluoGamePersistence.SetDaoYuFirstRunCompleteMarker();
                Log.Info("[IslandPlayerPosSave] default spawn (first run)");
                return;
            }

            if (BoluoGamePersistence.HasPlayerPositionSaved())
            {
                string json = BoluoGamePersistence.LoadPlayerPositionJson();
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        var prop = JsonUtility.FromJson<IslandPlayerProp>(json);
                        if (prop != null)
                        {
                            cam.Teleport(
                                new Vector3(prop.posx, prop.posy, prop.posz),
                                prop.roty);
                            Log.Info(
                                $"[IslandPlayerPosSave] restored pos=({prop.posx:F1},{prop.posy:F1},{prop.posz:F1}) yaw={prop.roty:F1}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[IslandPlayerPosSave] parse failed: {ex.Message}");
                    }
                }
            }

            ApplyDefault();
            Log.Info("[IslandPlayerPosSave] default spawn (no save)");
        }
    }
}
