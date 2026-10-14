using System;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using UnityEditor;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    /// <summary>
    /// 环境·渠道·版本测试窗口：切换 API 环境 / 渠道号 / 版本号做联调。
    /// 覆盖值仅存 EditorPrefs（本机编辑器级）。
    /// </summary>
    public class BoluoEnvTestWindow : EditorWindow
    {
        static readonly (string Label, string Value)[] EnvOptions =
        {
            ("dev 开发", "dev"),
            ("test 测试", "test"),
            ("prerelease 预发", "prerelease"),
            ("product 生产", "product"),
        };

        static readonly string[] ChannelPresets =
        {
            "BOLUOCWGMJ",
            "BOLUOTESTANDROID",
            "BOLUOTESTIOS",
            "BOLUOANDROIDAPPSOTRE",
            "BOLUOIOSAPPSTORE",
            "BOLUOTESTHARMONY",
        };

        static readonly string[] OriginEndPresets =
        {
            BoluoDeviceInfo.DefaultOriginEndApp,
            BoluoDeviceInfo.DefaultOriginEndGmj,
        };

        int _envIndex;
        int _channelPresetIndex;
        string _customChannel = "";
        int _originEndPresetIndex;
        string _customOriginEnd = "";
        string _version = "";
        string _status = "就绪";

        [MenuItem("GameMain/Boluo/环境·渠道·版本")]
        public static void Open()
        {
            var win = GetWindow<BoluoEnvTestWindow>(false, "环境·渠道·版本", true);
            win.minSize = new Vector2(480, 420);
            win.Show();
        }

        void OnEnable()
        {
            string env = BoluoApiEnvironment.GetEditorOverride();
            if (string.IsNullOrEmpty(env))
                env = BoluoApiEnvironment.GetEnvironmentType();
            _envIndex = Mathf.Max(0, Array.FindIndex(EnvOptions, o => o.Value == env));

            string channel = EditorPrefs.GetString(BoluoDeviceInfo.EditorChannelOverrideKey, "");
            _channelPresetIndex = Array.IndexOf(ChannelPresets, channel);
            if (_channelPresetIndex < 0)
            {
                _channelPresetIndex = string.IsNullOrEmpty(channel)
                    ? 0
                    : ChannelPresets.Length;
                _customChannel = channel;
            }

            string originEnd = EditorPrefs.GetString(BoluoDeviceInfo.EditorOriginEndOverrideKey, "");
            _originEndPresetIndex = Array.IndexOf(OriginEndPresets, originEnd);
            if (_originEndPresetIndex < 0)
            {
                _originEndPresetIndex = string.IsNullOrEmpty(originEnd)
                    ? 0
                    : OriginEndPresets.Length;
                _customOriginEnd = originEnd;
            }

            _version = EditorPrefs.GetString(BoluoDeviceInfo.EditorVersionOverrideKey, "");
            if (string.IsNullOrEmpty(_version))
                _version = Application.version;
        }

        void OnInspectorUpdate()
        {
            if (Application.isPlaying)
                Repaint();
        }

        void OnGUI()
        {
            DrawCurrentSection();
            EditorGUILayout.Space(8);
            DrawOverrideSection();
            EditorGUILayout.Space(8);
            DrawPlayActions();
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(_status, MessageType.None);
        }

        void DrawCurrentSection()
        {
            EditorGUILayout.LabelField("当前生效", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string envOverride = BoluoApiEnvironment.GetEditorOverride();
                string env = BoluoApiEnvironment.GetEnvironmentType();
                EditorGUILayout.LabelField(
                    "API 环境",
                    $"{env}（{(string.IsNullOrEmpty(envOverride) ? "编译宏默认" : "窗口覆盖")}）");
                EditorGUILayout.LabelField("Boluo BaseUrl", BoluoApiEnvironment.ResolveBoluoBaseUrl());
                EditorGUILayout.LabelField("User API Base", BoluoApiEnvironment.ResolveUserApiBase());
                EditorGUILayout.LabelField("渠道号", $"{BoluoDeviceInfo.GetChannel()}  appId={BoluoDeviceInfo.GetAppId()}");
                EditorGUILayout.LabelField("originEnd", BoluoDeviceInfo.GetOriginEnd());
                EditorGUILayout.LabelField("版本号", BoluoDeviceInfo.GetVersion());
                EditorGUILayout.LabelField("Build Target", EditorUserBuildSettings.activeBuildTarget.ToString());

                if (Application.isPlaying)
                {
                    BoluoSession.LoadFromPrefs();
                    EditorGUILayout.LabelField(
                        "deviceToken",
                        string.IsNullOrEmpty(BoluoSession.DeviceToken)
                            ? "(空)"
                            : $"len={BoluoSession.DeviceToken.Length} expire={BoluoSession.DeviceTokenExpireTime} valid={BoluoSession.IsDeviceTokenValid()}");
                    EditorGUILayout.LabelField(
                        "userToken",
                        string.IsNullOrEmpty(BoluoSession.UserToken)
                            ? "(空)"
                            : $"len={BoluoSession.UserToken.Length} guest={BoluoSession.IsGuest}");
                }
            }
        }

        void DrawOverrideSection()
        {
            EditorGUILayout.LabelField(
                "覆盖设置（EditorPrefs，仅本机，不进安装包）",
                EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string[] envLabels = new string[EnvOptions.Length];
                for (int i = 0; i < EnvOptions.Length; i++)
                    envLabels[i] = EnvOptions[i].Label;
                _envIndex = EditorGUILayout.Popup("API 环境", _envIndex, envLabels);

                string[] channelLabels = new string[ChannelPresets.Length + 1];
                for (int i = 0; i < ChannelPresets.Length; i++)
                    channelLabels[i] = ChannelPresets[i];
                channelLabels[ChannelPresets.Length] = "自定义…";
                EditorGUI.BeginChangeCheck();
                _channelPresetIndex = EditorGUILayout.Popup("渠道号", _channelPresetIndex, channelLabels);
                if (EditorGUI.EndChangeCheck())
                    SuggestOriginEndForChannel(ResolveEditedChannel());
                if (_channelPresetIndex >= ChannelPresets.Length)
                    _customChannel = EditorGUILayout.TextField("自定义渠道", _customChannel);

                string[] originLabels = new string[OriginEndPresets.Length + 1];
                for (int i = 0; i < OriginEndPresets.Length; i++)
                    originLabels[i] = OriginEndPresets[i];
                originLabels[OriginEndPresets.Length] = "自定义…";
                _originEndPresetIndex = EditorGUILayout.Popup("originEnd", _originEndPresetIndex, originLabels);
                if (_originEndPresetIndex >= OriginEndPresets.Length)
                    _customOriginEnd = EditorGUILayout.TextField("自定义 originEnd", _customOriginEnd);

                EditorGUILayout.HelpBox(
                    "channel 与 originEnd 独立：闺蜜机常用 channel=BOLUOCWGMJ + originEnd=boluocwgmj。",
                    MessageType.None);

                _version = EditorGUILayout.TextField("版本号", _version);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("应用覆盖（立即生效）"))
                        ApplyAll();
                    if (GUILayout.Button("全部恢复默认"))
                        ResetAll();
                }
            }
        }

        void DrawPlayActions()
        {
            EditorGUILayout.LabelField("联调（Play）", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Application.isPlaying)
                {
                    EditorGUILayout.HelpBox(
                        "进入 Play 后可：换取 deviceToken（boluoRsa → boluoDeviceLogin）/ 重拉 upgrade/bank。\n" +
                        "菜单 GameMain/Boluo/Editor Guest Login/Try Remote TouristLogin 需开启才会在启动链换票。",
                        MessageType.Info);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("换取 deviceToken"))
                    {
                        _status = "正在 boluoRsa → boluoDeviceLogin…";
                        RefreshDeviceTokenAsync().Forget();
                    }

                    if (GUILayout.Button("重拉 upgrade/bank"))
                    {
                        _status = "正在 upgrade/bank…";
                        RefreshBankAsync().Forget();
                    }
                }

                if (GUILayout.Button("清空本地 deviceToken"))
                {
                    BoluoSession.ClearDeviceToken();
                    _status = "已清空 deviceToken";
                }

                if (GUILayout.Button("启动 Bank 下载（不等待）"))
                {
                    BankUpgradeBootstrap.Start(loadIntoEngine: false);
                    _status = "BankUpgradeBootstrap.Start 已触发";
                }
            }
        }

        async UniTaskVoid RefreshDeviceTokenAsync()
        {
            BoluoSession.ClearDeviceToken();
            bool ok = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync();
            if (ok)
                await BoluoDeviceLoginClient.EnsureUserTokenAsync();
            _status = ok
                ? $"deviceToken ok len={BoluoSession.DeviceToken.Length} expire={BoluoSession.DeviceTokenExpireTime} userLen={BoluoSession.UserToken?.Length ?? 0}"
                : "deviceToken 换票失败（看 Console）";
        }

        async UniTaskVoid RefreshBankAsync()
        {
            bool tokenOk = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync();
            if (!tokenOk)
            {
                _status = "deviceToken 无效，已跳过 bank";
                return;
            }

            await BoluoDeviceLoginClient.EnsureUserTokenAsync();
            var data = await BankUpgradeClient.RequestUpgradeBank();
            if (data == null)
            {
                _status = "upgrade/bank 失败（看 Console）";
                return;
            }

            int n = data.banks?.Count ?? 0;
            PresetBankCatalog.TryApplyUpgrade(data);
            _status = $"upgrade/bank ok banks={n} bankVersion={data.bankVersion} builder={data.builderAccountId}";
            if (n > 0)
            {
                await BankDownloader.DownloadBanks(
                    PresetBankCatalog.AllRemotes(),
                    loadIntoEngine: false,
                    prioritize: true);
                _status += $" — downloaded to {BankDownloader.PersistentBankPath}";
            }
        }

        void ApplyAll()
        {
            string env = EnvOptions[Mathf.Clamp(_envIndex, 0, EnvOptions.Length - 1)].Value;
            BoluoApiEnvironment.SetEditorOverride(env);
            // Keep legacy menu key in sync
            EditorPrefs.SetString(GuestLoginClient.ApiEnvKey, env);
            SignatureLib.ApplyEditorEnvironmentOverride(env);

            string channel = ResolveEditedChannel();
            if (string.IsNullOrEmpty(channel))
                EditorPrefs.DeleteKey(BoluoDeviceInfo.EditorChannelOverrideKey);
            else
                EditorPrefs.SetString(BoluoDeviceInfo.EditorChannelOverrideKey, channel);

            string originEnd = ResolveEditedOriginEnd();
            if (string.IsNullOrEmpty(originEnd))
                EditorPrefs.DeleteKey(BoluoDeviceInfo.EditorOriginEndOverrideKey);
            else
                EditorPrefs.SetString(BoluoDeviceInfo.EditorOriginEndOverrideKey, originEnd);

            string ver = (_version ?? "").Trim();
            if (string.IsNullOrEmpty(ver))
                EditorPrefs.DeleteKey(BoluoDeviceInfo.EditorVersionOverrideKey);
            else
                EditorPrefs.SetString(BoluoDeviceInfo.EditorVersionOverrideKey, ver);

            BoluoApiEnvironment.ClearCached();
            _status =
                $"已应用：env={env} channel={BoluoDeviceInfo.GetChannel()} " +
                $"originEnd={BoluoDeviceInfo.GetOriginEnd()} " +
                $"appId={BoluoDeviceInfo.GetAppId()} version={BoluoDeviceInfo.GetVersion()}";
        }

        void ResetAll()
        {
            BoluoApiEnvironment.ClearEditorOverride();
            SignatureLib.ResetEditorEnvironmentToCompileDefault();
            EditorPrefs.DeleteKey(BoluoDeviceInfo.EditorChannelOverrideKey);
            EditorPrefs.DeleteKey(BoluoDeviceInfo.EditorOriginEndOverrideKey);
            EditorPrefs.DeleteKey(BoluoDeviceInfo.EditorVersionOverrideKey);
            EditorPrefs.DeleteKey(GuestLoginClient.ApiEnvKey);
            BoluoApiEnvironment.ClearCached();
            OnEnable();
            _status = "已恢复默认";
        }

        string ResolveEditedChannel()
        {
            if (_channelPresetIndex < ChannelPresets.Length)
                return ChannelPresets[_channelPresetIndex];
            return (_customChannel ?? "").Trim();
        }

        string ResolveEditedOriginEnd()
        {
            if (_originEndPresetIndex < OriginEndPresets.Length)
                return OriginEndPresets[_originEndPresetIndex];
            return (_customOriginEnd ?? "").Trim();
        }

        void SuggestOriginEndForChannel(string channel)
        {
            if (string.Equals(channel, "BOLUOCWGMJ", StringComparison.OrdinalIgnoreCase))
            {
                _originEndPresetIndex = Array.IndexOf(OriginEndPresets, BoluoDeviceInfo.DefaultOriginEndGmj);
                if (_originEndPresetIndex < 0)
                    _originEndPresetIndex = 1;
            }
            else if (_originEndPresetIndex < OriginEndPresets.Length)
            {
                _originEndPresetIndex = Array.IndexOf(OriginEndPresets, BoluoDeviceInfo.DefaultOriginEndApp);
                if (_originEndPresetIndex < 0)
                    _originEndPresetIndex = 0;
            }
        }
    }
}
