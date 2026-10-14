#if UNITY_EDITOR
using System;
using UnityGameFramework.Runtime;
using UnityEngine;

namespace Game.Native
{
    /// <summary>Editor Play Mode 模拟原生个人中心 / 定时 / 健康页，走同一套 Overlay Push。</summary>
    public static class NativeEditorHud
    {
        enum Page { None, Profile, Timer, TimerEnd, Health }

        static Page _page;
        static int _draftMinutes = 15;
        static int _remaining;
        static DateTime _endUtc;
        static bool _timerRunning;
        static Vector2 _scroll;

        public static bool IsShowing => _page != Page.None;

        public static void HandleHudIcon(int iconId)
        {
            if (iconId == MsgType.HudIconHealth)
                Open(Page.Health);
            else if (iconId == MsgType.HudIconProfile)
                Open(Page.Profile);
            else if (iconId == MsgType.HudIconTimer)
                Open(Page.Timer);
        }

        public static void Close()
        {
            _page = Page.None;
            NativePushReceiver.SimulatePush(MsgType.UiOverlay, 0, 1, 0);
        }

        public static void Tick()
        {
            if (!_timerRunning)
                return;

            var left = (int)Math.Ceiling((_endUtc - DateTime.UtcNow).TotalSeconds);
            if (left <= 0)
            {
                _timerRunning = false;
                _remaining = 0;
                NativePushReceiver.SimulatePush(MsgType.TimerProgress, 0, -1, 0);
                Open(Page.TimerEnd);
                return;
            }

            _remaining = left;
        }

        public static void OnGUI()
        {
            if (_page == Page.None)
                return;

            var dim = new Color(0f, 0f, 0f, 0.55f);
            var prev = GUI.color;
            GUI.color = dim;
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none);
            GUI.color = prev;

            float w = Mathf.Min(520f, Screen.width - 40f);
            float h = _page == Page.Timer ? 340f : 280f;
            var box = new Rect((Screen.width - w) * 0.5f, Screen.height - h - 24f, w, h);
            if (Event.current.type == EventType.MouseDown && !box.Contains(Event.current.mousePosition))
            {
                Close();
                Event.current.Use();
                return;
            }
            GUI.Box(box, GUIContent.none);
            GUILayout.BeginArea(new Rect(box.x + 16f, box.y + 12f, box.width - 32f, box.height - 24f));
            _scroll = GUILayout.BeginScrollView(_scroll);

            switch (_page)
            {
                case Page.Profile:
                    DrawProfile();
                    break;
                case Page.Timer:
                    DrawTimer();
                    break;
                case Page.TimerEnd:
                    DrawTimerEnd();
                    break;
                case Page.Health:
                    DrawHealth();
                    break;
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static void Open(Page page)
        {
            _page = page;
            // 与真机一致：个人中心 / 定时 / 健康均不藏右侧 HUD。
            NativePushReceiver.SimulatePush(MsgType.UiOverlay, 1, 0, 0);
        }

        static void DrawProfile()
        {
            GUILayout.Label("个人中心（Editor 模拟原生页）");
            GUILayout.Space(8);
            GUILayout.Label("真机走 Android / iOS NativeHudOverlay + GET /api/commonUser/getUserinfo。");
            GUILayout.Label("Editor 无宿主 token，Query(20) 返回 -2 属预期。");
            GUILayout.Space(12);
            if (GUILayout.Button("关闭", GUILayout.Height(36)))
                Close();
        }

        static void DrawHealth()
        {
            GUILayout.Label("健康（Editor 模拟原生页）");
            GUILayout.Space(8);
            GUILayout.Label("真机走 Android / iOS NativeHudOverlay，选压力 Push 101。");
            GUILayout.Space(8);
            DrawHealthChip("放松", 0);
            DrawHealthChip("轻度", 1);
            DrawHealthChip("中度", 2);
            DrawHealthChip("偏高", 3);
            DrawHealthChip("高压", 4);
            GUILayout.Space(12);
            if (GUILayout.Button("关闭", GUILayout.Height(36)))
                Close();
        }

        static void DrawHealthChip(string label, int level)
        {
            if (!GUILayout.Button(label, GUILayout.Height(32)))
                return;
            NativePushReceiver.SimulatePush(MsgType.HealthPressureLevel, level, 0, 0);
            Log.Info($"[IMP] Editor HealthPressureLevel level={level}");
        }

        static void DrawTimer()
        {
            GUILayout.Label("定时（Editor 模拟原生页）");
            GUILayout.Space(6);
            if (_timerRunning)
                GUILayout.Label($"剩余 {_remaining / 60}:{_remaining % 60:D2}");
            else
                GUILayout.Label("选择时长后开始");

            GUILayout.Label($"将定时 {_draftMinutes} 分钟");
            GUILayout.BeginHorizontal();
            foreach (int m in new[] { 5, 15, 30, 45, 60 })
            {
                if (GUILayout.Button($"{m}分"))
                    _draftMinutes = m;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            if (GUILayout.Button(_timerRunning ? "更新并关闭" : "开始并关闭", GUILayout.Height(36)))
            {
                StartTimer(_draftMinutes);
                Close();
            }

            if (_timerRunning && GUILayout.Button("取消定时", GUILayout.Height(32)))
            {
                CancelTimer();
                Close();
            }

            if (GUILayout.Button("关闭", GUILayout.Height(32)))
                Close();
        }

        static void DrawTimerEnd()
        {
            GUILayout.Label("定时结束");
            GUILayout.Space(8);
            GUILayout.Label("时间到了，岛内音乐已暂停。");
            GUILayout.Space(12);
            if (GUILayout.Button("知道了", GUILayout.Height(36)))
                Close();
        }

        static void StartTimer(int minutes)
        {
            var m = Mathf.Max(1, minutes);
            _draftMinutes = m;
            _remaining = m * 60;
            _endUtc = DateTime.UtcNow.AddSeconds(_remaining);
            _timerRunning = true;
            NativePushReceiver.SimulatePush(MsgType.TimerProgress, _remaining, MsgType.TimerRingSeconds, 0);
        }

        static void CancelTimer()
        {
            _timerRunning = false;
            _remaining = 0;
            NativePushReceiver.SimulatePush(MsgType.TimerProgress, 0, 0, 0);
        }
    }
}
#endif
