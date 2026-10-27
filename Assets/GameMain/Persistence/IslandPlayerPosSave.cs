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

        /// <summary>下次进岛用默认出生点（注销清桶等）。普通退登不置位，以便登回同账号恢复分桶坐标。由 Init 消费并清除。</summary>
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
        /// 此时地形可能尚未 Instantiate。设位后锁住眼睛，避免重力下坠或朝向被改掉；
        /// 地形就绪后 <see cref="ReplantAfterTerrainReady"/> 在罩下重贴，揭罩后再松锁。
        /// </summary>
        public static void InitAndArm(IslandJoystickCamera cam)
        {
            if (cam == null)
                return;

            ApplySpawnPriority(cam);
            cam.HoldSpawnPose();
            _armed = true;
            _nextSaveAt = Time.unscaledTime + SaveStartDelaySeconds;
            Log.Info(
                $"[IslandPlayerPosSave] armed disk={BoluoGamePersistence.AllowsAccountProgressDisk} " +
                $"guest={GuestSession.IsGuest} userId={GuestSession.UserId ?? ""} " +
                $"hasPos={BoluoGamePersistence.HasPlayerPositionSaved()}");
        }

        /// <summary>
        /// 地形+海就绪后重跑出生优先级：<see cref="IslandJoystickCamera.Teleport"/> 会
        /// <c>SampleGround</c>，此时高度图可用，避免二次进岛仍停在无碰撞时的作者 Y 或海床。
        /// </summary>
        public static void ReplantAfterTerrainReady(IslandJoystickCamera cam)
        {
            if (cam == null)
                return;

            ApplySpawnPriority(cam);
            Log.Info(
                "[IslandPlayerPosSave] replant after terrain " +
                $"pos={cam.transform.position.ToString("F1")} " +
                $"yaw={cam.YawDegrees:F1}");
        }

        public static void Tick()
        {
            if (!_armed)
                return;
            if (Time.unscaledTime < _nextSaveAt)
                return;
            _nextSaveAt = Time.unscaledTime + SaveIntervalSeconds;
            TrySaveCurrent(flush: true);
        }

        /// <param name="flush">true 时立刻 <c>PlayerPrefs.Save</c>（周期 / 退登 / 进后台）。</param>
        /// <param name="ignoreSuppress">退登/杀进程路径忽略下载抑制，避免最后一刀落空。</param>
        public static bool TrySaveCurrent(bool flush = false, bool ignoreSuppress = false)
        {
            if (!ignoreSuppress && ShouldSuppressSave)
            {
                Log.Info($"[IslandPlayerPosSave] skip save (suppress={_suppressCount})");
                return false;
            }

            if (!_armed && !ignoreSuppress)
                return false;

            if (!BoluoGamePersistence.AllowsAccountProgressDisk)
            {
                Log.Info(
                    $"[IslandPlayerPosSave] skip save — disk gated " +
                    $"loggedIn={GuestSession.IsLoggedIn} guest={GuestSession.IsGuest} " +
                    $"userId={GuestSession.UserId ?? ""}");
                return false;
            }

            var cam = IslandJoystickCamera.Current;
            if (cam == null)
            {
                Log.Info("[IslandPlayerPosSave] skip save — no camera");
                return false;
            }

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
            string json = JsonUtility.ToJson(prop);
            if (!BoluoGamePersistence.PersistPlayerPositionJson(json))
            {
                Log.Warning("[IslandPlayerPosSave] PersistPlayerPositionJson returned false");
                return false;
            }

            // 同周期一并落盘天气与 BGM（userId 门控在 Persist / Capture 内）
            var weather = GameEntry.Weather;
            if (weather != null)
                BoluoGameSave.CaptureIslandAtmosphere(
                    weather.Kind, weather.Level, weather.Hours,
                    weather.Period, weather.LookKind, weather.FogDensity);
            BoluoGameSave.CaptureMusicFromPlaylist(GameEntry.Music);

            if (flush)
                BoluoGamePersistence.PlayerPrefsSave();

            Log.Info(
                $"[IslandPlayerPosSave] saved ok pos=({prop.posx:F1},{prop.posy:F1},{prop.posz:F1}) " +
                $"yaw={prop.roty:F1} flush={flush} key=PlayerPos.{GuestSession.UserId}");
            return true;
        }

        /// <summary>写当前坐标并立刻 flush（进后台 / 离岛 / 退登前）。忽略 suppress。</summary>
        public static bool TrySaveCurrentAndFlush() =>
            TrySaveCurrent(flush: true, ignoreSuppress: true);

        static void ApplySpawnPriority(IslandJoystickCamera cam)
        {
            void ApplyDefault() =>
                cam.Teleport(IslandSpawnPose.DefaultPos, IslandSpawnPose.DefaultYawDegrees);

            // 游客不读写进度盘：始终默认出生，避免迁到正式账号坐标或残留 guest 桶。
            if (GuestSession.IsGuest)
            {
                UseDefaultPlayerPosNextLoad = false;
                ApplyDefault();
                Log.Info("[IslandPlayerPosSave] default spawn (guest — no progress disk)");
                return;
            }

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
                BoluoGamePersistence.PlayerPrefsSave();
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
                            cam.SetPitchDegrees(prop.rotx);
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
            Log.Info(
                $"[IslandPlayerPosSave] default spawn (no save) " +
                $"disk={BoluoGamePersistence.AllowsAccountProgressDisk} " +
                $"guest={GuestSession.IsGuest} userId={GuestSession.UserId ?? ""}");
        }
    }
}
