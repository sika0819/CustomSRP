using System.Collections.Generic;
using Game;
using Game.BoluoApi;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 一键清理 Boluo 工程相关的 EditorPrefs / PlayerPrefs。
    /// 不会 <c>DeleteAll</c>，只删已知的 key（避免误清 YooAsset / 第三方 plugin 的数据）。
    /// </summary>
    public class BoluoPrefsCleanerWindow : EditorWindow
    {
        // 已知 EditorPrefs key 全部用 const 引用，避免字符串硬编码漏改
        static readonly (string Label, string Key, string Note)[] EditorPrefsKeys =
        {
            ("环境·渠道·版本窗口 · 渠道", BoluoDeviceInfo.EditorChannelOverrideKey, "BoluoEnvTestWindow 写入的 channel 覆盖"),
            ("环境·渠道·版本窗口 · originEnd", BoluoDeviceInfo.EditorOriginEndOverrideKey, "BoluoEnvTestWindow 写入的 originEnd 覆盖"),
            ("环境·渠道·版本窗口 · 版本号", BoluoDeviceInfo.EditorVersionOverrideKey, "BoluoEnvTestWindow 写入的 version 覆盖"),
            ("API 环境覆盖 (dev/test/prerelease/product)", BoluoApiEnvironment.EditorEnvOverrideKey, "BoluoApiEnvironment.SetEditorOverride 写入"),
            ("GuestLogin 旧 API 环境 (legacy 同步)", GuestLoginClient.ApiEnvKey, "Game.Editor.ApiEnv 旧键，ResolveUserApiBase 启动时同步到 EditorEnvOverrideKey"),
            ("UGF 日志宏已应用标记", "CustomSRP.UGFLogMacrosBootstrap.Applied", "标记 UGFLogMacrosBootstrap 是否已铺好所有 target 的日志宏；删除后下次打开工程会重铺"),
        };

        // PlayerPrefs · 会话票（Boluo.* 命名空间）
        static readonly (string Label, string Key, string Note)[] SessionKeys =
        {
            ("设备 Token (Boluo.DeviceToken)", "Boluo.DeviceToken", null),
            ("设备 Token 过期时间 (Boluo.DeviceTokenExpire)", "Boluo.DeviceTokenExpire", null),
            ("设备 Token 写入时版本号 (Boluo.DeviceTokenVersion)", "Boluo.DeviceTokenVersion", "version 变了会让 IsDeviceTokenValid 返回 false；建议重下"),
            ("用户 Token (Boluo.UserToken)", "Boluo.UserToken", null),
            ("用户 Id (Boluo.UserId)", "Boluo.UserId", null),
            ("是否游客 (Boluo.IsGuest)", "Boluo.IsGuest", "int 0/1"),
        };

        // PlayerPrefs · 旧版字符串字面量（GuestSession.DeleteAll 用的）
        static readonly (string Label, string Key, string Note)[] LegacySessionKeys =
        {
            ("旧用户 Token (Game.UserToken)", "Game.UserToken", "GuestSession 旧版 hardcoded key"),
            ("旧用户 Id (Game.UserId)", "Game.UserId", null),
            ("旧游客标记 (Game.IsGuest)", "Game.IsGuest", null),
        };

        // PlayerPrefs · 环境 / 流式相关
        static readonly (string Label, string Key, string Note)[] EnvKeys =
        {
            ("BoluoApiEnvironmentType (运行时缓存)", "BoluoApiEnvironmentType", "BoluoApiEnvironment.SetCached 写入"),
            ("GameEnvStreamingOverride (Lite 流式)", "GameEnvStreamingOverride", "EnvStreamingConfig.OverridePrefsKey"),
        };

        // PlayerPrefs · 游戏进度（BoluoGamePersistence.Keys）
        static readonly (string Label, string Key, string Note)[] ProgressKeys =
        {
            ("主存档 JSON (BoluoSaveData)", BoluoGamePersistence.Keys.BoluoSaveData, "音量/天气联动/疗愈/推荐/压力频控"),
            ("角色位置 (PlayerPos)", BoluoGamePersistence.Keys.PlayerPos, "正式账号周期 ~10s；游客不读写"),
            ("首进岛完成 (DaoYuFirstRunComplete)", BoluoGamePersistence.Keys.DaoYuFirstRunComplete, "游客不读写"),
            ("教程标记 (TutorialType)", BoluoGamePersistence.Keys.TutorialType, "游客不写"),
            ("功能介绍 (GongNengView)", BoluoGamePersistence.Keys.GongNengView, "游客不写"),
            ("位置权限已请求", BoluoGamePersistence.Keys.LocationPermissionRequested, "游客不写；写时 Flush"),
            ("下载未播红点 JSON", BoluoGamePersistence.Keys.DownloadedUnplayed, "游客不读不写"),
            ("世界睡眠日弹窗日期", BoluoGamePersistence.Keys.ActivityWorldSleepPopupDate, "游客不读写"),
            ("Apple 用户 ID", BoluoGamePersistence.Keys.AppleUserIdKey, "游客可写"),
            ("首次联网探测完成", BoluoGamePersistence.Keys.FirstNetworkProbeComplete, null),
            ("账号密码节流手机号索引", BoluoGamePersistence.Keys.AccPwPhoneRegistry, "换行分隔"),
        };

        Vector2 _scroll;

        [MenuItem("GameMain/Boluo/Clear PlayerPrefs & EditorPrefs")]
        public static void Open()
        {
            var win = GetWindow<BoluoPrefsCleanerWindow>(false, "清理 Boluo 偏好", true);
            win.minSize = new Vector2(560, 480);
            win.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("清理 Boluo 工程偏好", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "只删本工程已知的 key（不会 DeleteAll）。\n" +
                "清完不会自动 Save，关闭时点每个区下方的「保存」按钮即可。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawSection("EditorPrefs（本机编辑器级）", EditorPrefsKeys);
            EditorGUILayout.Space(10);
            DrawSection("PlayerPrefs · 会话票（登录态）", SessionKeys);
            EditorGUILayout.Space(10);
            DrawSection("PlayerPrefs · 旧版会话 key（Game.*）", LegacySessionKeys);
            EditorGUILayout.Space(10);
            DrawSection("PlayerPrefs · 环境 / 流式", EnvKeys);
            EditorGUILayout.Space(10);
            DrawSection("PlayerPrefs · 游戏进度（BoluoGamePersistence）", ProgressKeys);

            EditorGUILayout.EndScrollView();
        }

        static void DrawSection(string title, (string Label, string Key, string Note)[] rows)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                int hits = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    bool exists = HasKey(row.Key);
                    if (exists) hits++;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(new GUIContent(row.Label, row.Key), GUILayout.Width(360));
                        EditorGUILayout.LabelField(exists ? "已设" : "(空)", GUILayout.Width(60));
                        if (!string.IsNullOrEmpty(row.Note))
                        {
                            EditorGUILayout.LabelField(row.Note, EditorStyles.miniLabel);
                        }
                    }
                }
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(hits == 0))
                    {
                        if (GUILayout.Button($"清掉这 {hits} 个 key", GUILayout.Height(22)))
                        {
                            if (EditorUtility.DisplayDialog(
                                "清掉 EditorPrefs / PlayerPrefs",
                                $"确认删除这 {hits} 个 key？\n\n" + string.Join("\n", ExistsList(rows)),
                                "确认清掉", "取消"))
                            {
                                DeleteAll(rows);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                    if (GUILayout.Button("保存 (Save)", GUILayout.Height(22), GUILayout.Width(100)))
                    {
                        PlayerPrefs.Save();
                        Debug.Log("[BoluoPrefsCleaner] PlayerPrefs.Save() done");
                    }
                }
            }
        }

        static bool HasKey(string key)
        {
#if UNITY_EDITOR
            if (IsEditorKey(key)) return EditorPrefs.HasKey(key);
#endif
            return PlayerPrefs.HasKey(key);
        }

        static void DeleteAll((string Label, string Key, string Note)[] rows)
        {
            int n = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                if (HasKey(rows[i].Key))
                {
#if UNITY_EDITOR
                    if (IsEditorKey(rows[i].Key))
                        EditorPrefs.DeleteKey(rows[i].Key);
                    else
#endif
                        PlayerPrefs.DeleteKey(rows[i].Key);
                    Debug.Log($"[BoluoPrefsCleaner] deleted {(IsEditorKey(rows[i].Key) ? "EditorPrefs" : "PlayerPrefs")} {rows[i].Key}");
                    n++;
                }
            }
            PlayerPrefs.Save();
            Debug.Log($"[BoluoPrefsCleaner] done, removed {n} key(s)");
        }

        static List<string> ExistsList((string Label, string Key, string Note)[] rows)
        {
            var list = new List<string>(rows.Length);
            for (int i = 0; i < rows.Length; i++)
                if (HasKey(rows[i].Key)) list.Add(rows[i].Key);
            return list;
        }

        // 哪些 key 走 EditorPrefs 是写死的（这些 key 的 const 在 #if UNITY_EDITOR 内，运行时不存在）
        // 这里只做"看起来像 Editor 覆盖"的最粗判断：常量在 #if UNITY_EDITOR 里，所以 Editor 永远走这个分支。
        static bool IsEditorKey(string key)
        {
            return key == BoluoDeviceInfo.EditorChannelOverrideKey
                || key == BoluoDeviceInfo.EditorOriginEndOverrideKey
                || key == BoluoDeviceInfo.EditorVersionOverrideKey
                || key == BoluoApiEnvironment.EditorEnvOverrideKey
                || key == GuestLoginClient.ApiEnvKey
                || key == "CustomSRP.UGFLogMacrosBootstrap.Applied";
        }
    }
}
