using System.Collections.Generic;
using Game.BoluoApi;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Editor
{
    /// <summary>
    /// 联调蜂窝下载弹窗门禁：模拟可达性 / 开闸，预览五层弹窗，跑无网→存储→流量决策链。
    /// </summary>
    public sealed class DownloadDialogGateTestWindow : EditorWindow
    {
        Label _policyLabel;
        Label _allowLabel;
        Label _reachLabel;
        Label _needPromptLabel;
        Label _canTxLabel;
        Label _status;
        Toggle _simulateMobile;
        TextField _sizeField;
        TextField _banksField;

        [MenuItem("GameMain/Boluo/下载弹窗·蜂窝门禁")]
        public static void Open()
        {
            var win = GetWindow<DownloadDialogGateTestWindow>(false, "下载弹窗·蜂窝门禁", true);
            win.minSize = new Vector2(460, 560);
            win.Show();
            win.Focus();
            win.RefreshStatus();
        }

        void OnEnable() => EditorApplication.update += OnEditorUpdate;

        void OnDisable() => EditorApplication.update -= OnEditorUpdate;

        float _nextRefresh;

        void OnEditorUpdate()
        {
            if (Time.realtimeSinceStartup < _nextRefresh)
                return;
            _nextRefresh = Time.realtimeSinceStartup + 0.5f;
            RefreshStatus();
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 14;
            root.style.paddingRight = 14;
            root.style.paddingTop = 12;
            root.style.paddingBottom = 12;

            root.Add(SectionTitle("下载弹窗 · 蜂窝门禁（Editor）"));
            root.Add(Hint(
                "Editor 默认「模拟真机」后，NeedsMobileDataPrompt 才会在「蜂窝 + 未开闸」时为真。" +
                "覆盖可达性不影响系统网络，只影响门禁判定与 CanTransmitNow。"));

            // —— 状态 ——
            var statusBox = Box();
            root.Add(statusBox);
            statusBox.Add(Bold("当前闸状态"));
            _policyLabel = AddLabel(statusBox);
            _allowLabel = AddLabel(statusBox);
            _reachLabel = AddLabel(statusBox);
            _needPromptLabel = AddLabel(statusBox);
            _canTxLabel = AddLabel(statusBox);
            statusBox.Add(Btn("刷新状态", RefreshStatus, 24));

            // —— 模拟 ——
            var simBox = Box();
            root.Add(simBox);
            simBox.Add(Bold("模拟"));
            _simulateMobile = new Toggle("模拟真机平台（开才弹流量窗）")
            {
                value = DownloadCellularGate.EditorSimulateMobilePlatform,
            };
            _simulateMobile.RegisterValueChangedCallback(e =>
            {
                DownloadCellularGate.EditorSimulateMobilePlatform = e.newValue;
                RefreshStatus();
                SetStatus("SimulateMobile=" + e.newValue);
            });
            simBox.Add(_simulateMobile);

            simBox.Add(Row(
                Btn("可达：Wi‑Fi", () => SetReach(NetworkReachability.ReachableViaLocalAreaNetwork), 28),
                Btn("可达：蜂窝", () => SetReach(NetworkReachability.ReachableViaCarrierDataNetwork), 28),
                Btn("可达：无网", () => SetReach(NetworkReachability.NotReachable), 28)));
            simBox.Add(Btn("清除可达覆盖（用真实 Reachability）", () =>
            {
                DownloadCellularGate.EditorClearReachabilityOverride();
                RefreshStatus();
                SetStatus("reach override cleared");
            }, 26));

            // —— 闸写回 ——
            var gateBox = Box();
            root.Add(gateBox);
            gateBox.Add(Bold("闸写回（对齐流量窗两按钮）"));
            gateBox.Add(Row(
                Btn("继续下载 → AllowAlways", () =>
                {
                    DownloadCellularGate.UserAllowCellularAlways();
                    RefreshStatus();
                    SetStatus("UserAllowCellularAlways");
                }, 28),
                Btn("切 Wi‑Fi → WifiOnly", () =>
                {
                    DownloadCellularGate.UserRequireWifiOnly();
                    RefreshStatus();
                    SetStatus("UserRequireWifiOnly");
                }, 28)));
            gateBox.Add(Btn("复位会话闸（WifiOnly + 关）", () =>
            {
                DownloadCellularGate.ResetForNewSession();
                RefreshStatus();
                SetStatus("ResetForNewSession");
            }, 26));

            // —— 弹窗预览 ——
            var dlgBox = Box();
            root.Add(dlgBox);
            dlgBox.Add(Bold("弹窗预览（需 Play 且 UI 已就绪）"));
            _sizeField = new TextField("体积数字") { value = "128" };
            dlgBox.Add(_sizeField);
            dlgBox.Add(Row(
                Btn("申请下载", () =>
                {
                    if (!RequirePlay()) return;
                    UiDialogService.ShowRequestDownload(
                        SizeText(),
                        () => SetStatus("RequestDownload → 立即下载"),
                        () => SetStatus("RequestDownload → 返回"));
                }, 26),
                Btn("下载进度", () =>
                {
                    if (!RequirePlay()) return;
                    int ticket = UiDialogService.OpenDownloadProgress(() => SetStatus("Progress 关闭"));
                    UiDialogService.SetDownloadProgress(ticket, 42f, 66f, 862f);
                    SetStatus("Progress ticket=" + ticket);
                }, 26)));
            dlgBox.Add(Row(
                Btn("蜂窝流量确认", () =>
                {
                    if (!RequirePlay()) return;
                    UiDialogService.ShowMobileDataConfirm(
                        SizeText(),
                        () =>
                        {
                            DownloadCellularGate.UserAllowCellularAlways();
                            RefreshStatus();
                            SetStatus("Mobile → 继续 → AllowAlways");
                        },
                        () =>
                        {
                            DownloadCellularGate.UserRequireWifiOnly();
                            RefreshStatus();
                            SetStatus("Mobile → 切 Wi‑Fi → WifiOnly");
                        });
                }, 26),
                Btn("存储不足", () =>
                {
                    if (!RequirePlay()) return;
                    UiDialogService.ShowInsufficientStorage(SizeText(), () => SetStatus("Storage 确认"));
                }, 26),
                Btn("无网络", () =>
                {
                    if (!RequirePlay()) return;
                    UiDialogService.ShowNoNetwork(() => SetStatus("Offline 确认"));
                }, 26)));

            // —— 决策链 ——
            var chainBox = Box();
            root.Add(chainBox);
            chainBox.Add(Bold("决策链 DownloadConsentSession.EnsureReady"));
            chainBox.Add(Hint(
                "无网 → 存储 → 流量。弹窗与 continue 由 Session 唯一拥有；窗已开时再次 EnsureReady 会替换回调。"));
            _banksField = new TextField("bank 名（逗号分隔，可空）")
            {
                value = "Music1_1,Ocean_Calm_Quad",
            };
            chainBox.Add(_banksField);
            chainBox.Add(Btn("跑一遍 EnsureReady", RunGateChain, 32));
            chainBox.Add(Btn("强制弹流量窗（绕过 Needs 判定）", () =>
            {
                if (!RequirePlay()) return;
                UiDialogService.ShowMobileDataConfirm(
                    SizeText(),
                    () =>
                    {
                        BankDownloader.PriorityBanks(ParseBanks());
                        DownloadCellularGate.UserAllowCellularAlways();
                        RefreshStatus();
                        SetStatus("Force mobile → AllowAlways + Priority");
                    },
                    () =>
                    {
                        DownloadCellularGate.UserRequireWifiOnly();
                        RefreshStatus();
                        SetStatus("Force mobile → WifiOnly");
                    });
            }, 28));

            _status = new Label("就绪");
            _status.style.marginTop = 10;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.color = new Color(0.7f, 0.85f, 0.7f);
            root.Add(_status);

            RefreshStatus();
        }

        void RunGateChain()
        {
            if (!RequirePlay())
                return;

            List<string> banks = ParseBanks();
            bool firedSync = false;
            DownloadConsentSession.EnsureReady(
                banks,
                onReady: () =>
                {
                    firedSync = true;
                    RefreshStatus();
                    SetStatus("EnsureReady → onReady banks=" + banks.Count);
                },
                new DownloadUiOptions
                {
                    OnCancel = () =>
                    {
                        RefreshStatus();
                        SetStatus("EnsureReady → OnCancel（无网/存储/切 Wi‑Fi）");
                    },
                });

            if (firedSync)
                SetStatus("EnsureReady 同步通过 → onReady banks=" + banks.Count);
            else if (DownloadCellularGate.NeedsMobileDataPrompt())
                SetStatus("EnsureReady：流量窗（看 Game 视图；再点可替换 continue）");
            else if (DownloadCellularGate.IsOffline())
                SetStatus("EnsureReady：无网 toast「网络异常」");
            RefreshStatus();
        }

        void SetReach(NetworkReachability r)
        {
            DownloadCellularGate.EditorReachabilityOverride = r;
            RefreshStatus();
            SetStatus("reach override=" + r);
        }

        void RefreshStatus()
        {
            if (_policyLabel == null)
                return;
            _policyLabel.text = "Policy = " + DownloadCellularGate.Policy;
            _allowLabel.text = "AllowMobileNetwork = " + DownloadCellularGate.AllowMobileNetwork;
            string overrideTxt = DownloadCellularGate.EditorReachabilityOverride.HasValue
                ? "override"
                : "real";
            _reachLabel.text =
                "Reachability = " + DownloadCellularGate.EditorEffectiveReachability
                + " (" + overrideTxt + ")";
            _needPromptLabel.text = "NeedsMobileDataPrompt = " + DownloadCellularGate.NeedsMobileDataPrompt();
            _canTxLabel.text = "CanTransmitNow = " + DownloadCellularGate.CanTransmitNow();
            if (_simulateMobile != null
                && _simulateMobile.value != DownloadCellularGate.EditorSimulateMobilePlatform)
                _simulateMobile.SetValueWithoutNotify(DownloadCellularGate.EditorSimulateMobilePlatform);
        }

        void SetStatus(string msg)
        {
            if (_status != null)
                _status.text = msg;
            Debug.Log("[DownloadDialogGateTest] " + msg);
        }

        bool RequirePlay()
        {
            if (Application.isPlaying && GameEntry.UI != null)
                return true;
            SetStatus("需要先 Play，且 GameEntry.UI 已就绪");
            EditorUtility.DisplayDialog(
                "下载弹窗·蜂窝门禁",
                "请先进入 Play，等岛内 UI 起来后再点弹窗按钮。",
                "知道了");
            return false;
        }

        string SizeText() =>
            string.IsNullOrWhiteSpace(_sizeField?.value) ? "128" : _sizeField.value.Trim();

        List<string> ParseBanks()
        {
            var list = new List<string>();
            string raw = _banksField?.value ?? "";
            string[] parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i]?.Trim();
                if (!string.IsNullOrEmpty(s))
                    list.Add(s);
            }

            return list;
        }

        static Label SectionTitle(string text)
        {
            var t = new Label(text);
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            t.style.fontSize = 15;
            t.style.marginBottom = 4;
            return t;
        }

        static Label Bold(string text)
        {
            var t = new Label(text);
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            t.style.marginBottom = 4;
            return t;
        }

        static Label Hint(string text)
        {
            var t = new Label(text);
            t.style.whiteSpace = WhiteSpace.Normal;
            t.style.marginBottom = 10;
            t.style.color = new Color(0.55f, 0.55f, 0.55f);
            return t;
        }

        static VisualElement Box()
        {
            var box = new VisualElement();
            box.style.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 0.35f);
            box.style.paddingLeft = 10;
            box.style.paddingRight = 10;
            box.style.paddingTop = 8;
            box.style.paddingBottom = 8;
            box.style.marginBottom = 10;
            box.style.borderTopLeftRadius = 4;
            box.style.borderTopRightRadius = 4;
            box.style.borderBottomLeftRadius = 4;
            box.style.borderBottomRightRadius = 4;
            return box;
        }

        static Label AddLabel(VisualElement parent)
        {
            var l = new Label("—");
            l.style.marginBottom = 2;
            parent.Add(l);
            return l;
        }

        static VisualElement Row(params VisualElement[] children)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 4;
            for (int i = 0; i < children.Length; i++)
            {
                children[i].style.flexGrow = 1;
                children[i].style.marginRight = i < children.Length - 1 ? 6 : 0;
                row.Add(children[i]);
            }

            return row;
        }

        static Button Btn(string text, System.Action onClick, float height)
        {
            var b = new Button(onClick) { text = text };
            b.focusable = false;
            b.style.height = height;
            b.style.marginBottom = 4;
            b.style.whiteSpace = WhiteSpace.Normal;
            return b;
        }
    }
}
