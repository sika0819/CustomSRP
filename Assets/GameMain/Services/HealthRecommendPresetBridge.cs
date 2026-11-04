using Game.Native;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// iOS 健康智慧推荐 / 压力档 → 下载（若未齐）→ 切预设。
    /// 静默 KickSilentAll 不走这里。须 <see cref="BoluoGameSave.Recommend"/> 开。
    /// </summary>
    public static class HealthRecommendPresetBridge
    {
        static bool _subscribed;

        /// <summary>
        /// 压力 0–4 → 预设 id（对齐常见疗愈档；后续可换 IntelligentRecommend 配置）。
        /// 0 放松→午后放松(3)；1 轻度→假日躺平(8)；2 中度→专注工作(5)；
        /// 3 偏高→委屈泛滥(4)；4 高压→放空发呆(6)。
        /// </summary>
        static readonly int[] PressureToPresetId = { 3, 8, 5, 4, 6 };

        public static void EnsureSubscribed()
        {
            if (_subscribed || GameEntry.Event == null)
                return;
            GameEntry.Event.Subscribe(NativeHealthPressureEventArgs.EventId, OnHealthPressure);
            _subscribed = true;
        }

        public static void Unsubscribe()
        {
            if (!_subscribed || GameEntry.Event == null)
                return;
            GameEntry.Event.Unsubscribe(NativeHealthPressureEventArgs.EventId, OnHealthPressure);
            _subscribed = false;
        }

        /// <summary>原生 <c>applyWanosScheme</c> / 健康推荐指定 scheme。</summary>
        public static void ApplySchemeFromNative(int schemeId)
        {
            if (schemeId <= 0)
                return;
            Log.Info("[HealthRecommend] ApplySchemeFromNative scheme=" + schemeId);
            BoluoGameSave.SetRecommend(true);
            PresetEnterCoordinator.RequestEnter(
                schemeId,
                PendingDownloadIntent.EnterSource.HealthRecommend);
        }

        static void OnHealthPressure(object sender, GameFramework.Event.GameEventArgs e)
        {
            if (e is not NativeHealthPressureEventArgs args)
                return;

            int level = Mathf.Clamp(args.Level, 0, PressureToPresetId.Length - 1);
            BoluoGameSave.SetLastPressureLevel(level, (uint)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            if (!BoluoGameSave.Recommend)
            {
                Log.Info(
                    "[HealthRecommend] pressure=" + level + " — Recommend off, skip preset enter");
                return;
            }

            int presetId = PressureToPresetId[level];
            Log.Info(
                "[HealthRecommend] pressure=" + level + " → preset=" + presetId + " (download then enter)");
            PresetEnterCoordinator.RequestEnter(
                presetId,
                PendingDownloadIntent.EnterSource.HealthRecommend);
        }
    }
}
