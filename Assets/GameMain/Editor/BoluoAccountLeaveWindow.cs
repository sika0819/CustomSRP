using Game.Native;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    /// <summary>
    /// Editor UITK 个人中心联调窗：模拟原生「退出登录 / 注销账号」。
    /// 走与真机相同的 <see cref="MsgType.HostLeaveIsland"/> Push（a=0 退登保留桶，a=1 注销清桶）。
    /// 点岛内个人中心图标时由 <see cref="NativeEditorHud"/> 自动弹出。
    /// </summary>
    public class BoluoAccountLeaveWindow : EditorWindow
    {
        Label _sessionKind;
        Label _sessionUserId;
        Label _status;
        VisualElement _confirmOverlay;
        Label _confirmTitle;
        Label _confirmBody;
        Button _confirmOk;
        int _pendingLeaveReason = -1;
        bool _blockConfirmClick;

        [InitializeOnLoadMethod]
        static void HookNativeProfile()
        {
            NativeEditorHud.OpenAccountLeaveWindow = Open;
        }

        [MenuItem("GameMain/Boluo/个人中心·退登注销")]
        public static void Open()
        {
            var win = GetWindow<BoluoAccountLeaveWindow>(false, "个人中心·退登注销", true);
            win.minSize = new Vector2(420, 380);
            win.Show();
            win.Focus();
            win.RefreshSession();
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 14;
            root.style.paddingRight = 14;
            root.style.paddingTop = 12;
            root.style.paddingBottom = 12;

            var title = new Label("个人中心（Editor 模拟原生）");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 15;
            title.style.marginBottom = 4;
            root.Add(title);

            var hint = new Label(
                "对齐真机 HostLeaveIsland：退出登录 a=0 保留进度桶；注销成功 a=1 清空当前 userId 桶并退回 GuestLogin。");
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 10;
            hint.style.color = new Color(0.55f, 0.55f, 0.55f);
            root.Add(hint);

            var sessionBox = new VisualElement();
            sessionBox.style.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 0.35f);
            sessionBox.style.paddingLeft = 10;
            sessionBox.style.paddingRight = 10;
            sessionBox.style.paddingTop = 8;
            sessionBox.style.paddingBottom = 8;
            sessionBox.style.marginBottom = 12;
            sessionBox.style.borderTopLeftRadius = 4;
            sessionBox.style.borderTopRightRadius = 4;
            sessionBox.style.borderBottomLeftRadius = 4;
            sessionBox.style.borderBottomRightRadius = 4;
            root.Add(sessionBox);

            var sessionHeader = new Label("当前会话");
            sessionHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            sessionHeader.style.marginBottom = 4;
            sessionBox.Add(sessionHeader);

            _sessionKind = new Label();
            sessionBox.Add(_sessionKind);
            _sessionUserId = new Label();
            sessionBox.Add(_sessionUserId);

            var refresh = new Button(RefreshSession) { text = "刷新会话" };
            refresh.focusable = false;
            refresh.style.marginTop = 6;
            refresh.style.height = 24;
            sessionBox.Add(refresh);

            var logout = new Button(() => LeaveNow(MsgType.HostLeaveReasonLogout))
            {
                text = "退出登录",
            };
            logout.focusable = false;
            logout.style.height = 36;
            logout.style.marginBottom = 8;
            root.Add(logout);

            var deleteAccount = new Button(() => ShowConfirm(
                MsgType.HostLeaveReasonAccountDeleted,
                "确定注销账号？",
                "清空当前账号进度桶 + 会话，下次进岛用默认出生点。等同原生注销成功 HostLeaveIsland a=1。不可恢复本地桶。",
                "注销并清桶"))
            {
                text = "注销账号",
            };
            deleteAccount.focusable = false;
            deleteAccount.style.height = 36;
            deleteAccount.style.marginBottom = 8;
            deleteAccount.style.backgroundColor = new Color(0.55f, 0.18f, 0.16f, 0.85f);
            root.Add(deleteAccount);

            var closeHud = new Button(() =>
            {
                if (Application.isPlaying)
                    NativeEditorHud.Close();
                _status.text = "已关闭 Play 内个人中心遮罩";
            })
            {
                text = "关闭 Play 内遮罩",
            };
            closeHud.focusable = false;
            closeHud.style.height = 28;
            closeHud.style.marginBottom = 10;
            root.Add(closeHud);

            _status = new Label("就绪 — 需在 Play Mode 下操作");
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = 4;
            root.Add(_status);

            BuildConfirmOverlay(root);
            RefreshSession();
        }

        void BuildConfirmOverlay(VisualElement root)
        {
            _confirmOverlay = new VisualElement();
            _confirmOverlay.style.position = Position.Absolute;
            _confirmOverlay.style.left = 0;
            _confirmOverlay.style.right = 0;
            _confirmOverlay.style.top = 0;
            _confirmOverlay.style.bottom = 0;
            _confirmOverlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            _confirmOverlay.pickingMode = PickingMode.Ignore;
            _confirmOverlay.style.display = DisplayStyle.None;
            _confirmOverlay.style.justifyContent = Justify.Center;
            _confirmOverlay.style.alignItems = Align.Center;
            root.Add(_confirmOverlay);

            var card = new VisualElement();
            card.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 0.98f);
            card.style.paddingLeft = 16;
            card.style.paddingRight = 16;
            card.style.paddingTop = 14;
            card.style.paddingBottom = 14;
            card.style.width = 340;
            card.style.borderTopLeftRadius = 6;
            card.style.borderTopRightRadius = 6;
            card.style.borderBottomLeftRadius = 6;
            card.style.borderBottomRightRadius = 6;
            _confirmOverlay.Add(card);

            _confirmTitle = new Label();
            _confirmTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _confirmTitle.style.fontSize = 14;
            _confirmTitle.style.marginBottom = 8;
            card.Add(_confirmTitle);

            _confirmBody = new Label();
            _confirmBody.style.whiteSpace = WhiteSpace.Normal;
            _confirmBody.style.marginBottom = 14;
            card.Add(_confirmBody);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.FlexEnd;
            card.Add(row);

            var cancel = new Button(HideConfirm) { text = "取消" };
            cancel.focusable = false;
            cancel.style.width = 88;
            cancel.style.height = 28;
            cancel.style.marginRight = 8;
            row.Add(cancel);

            _confirmOk = new Button(ConfirmLeave) { text = "确定" };
            _confirmOk.focusable = false;
            _confirmOk.style.width = 110;
            _confirmOk.style.height = 28;
            row.Add(_confirmOk);
        }

        /// <summary>
        /// 只有 UITK 的 EditorWindow，第一次点击会被用来聚焦，按钮要点第二次才触发。
        /// 空 OnGUI 让这次 MouseDown 既聚焦又把点击交给按钮。不要在 OnInspectorUpdate 里 Repaint，
        /// 重绘会把正在进行的点击打断。
        /// </summary>
        void OnGUI()
        {
        }

        void OnFocus() => RefreshSession();

        void RefreshSession()
        {
            if (_sessionKind == null)
                return;

            GuestSession.LoadFromPrefs();
            string uid = string.IsNullOrEmpty(GuestSession.UserId) ? "(空)" : GuestSession.UserId;
            string kind = !GuestSession.IsLoggedIn
                ? "未登录"
                : (GuestSession.IsGuest ? "游客" : "正式账号");
            _sessionKind.text = $"状态：{kind}";
            _sessionUserId.text = $"userId：{uid}";

            if (!Application.isPlaying && _status != null)
                _status.text = "请先进入 Play，再点退登 / 注销";
        }

        void ShowConfirm(int leaveReason, string title, string body, string okLabel)
        {
            if (!Application.isPlaying)
            {
                _status.text = "请先进入 Play Mode";
                return;
            }

            _pendingLeaveReason = leaveReason;
            // 这次点击若穿透到刚显示的「确定」，不要当成确认。当前事件结束后再接受点击。
            _blockConfirmClick = true;
            EditorApplication.delayCall += () => _blockConfirmClick = false;
            _confirmTitle.text = title;
            _confirmBody.text = body;
            _confirmOk.text = okLabel;
            _confirmOverlay.pickingMode = PickingMode.Position;
            _confirmOverlay.style.display = DisplayStyle.Flex;
        }

        void HideConfirm()
        {
            _pendingLeaveReason = -1;
            _blockConfirmClick = false;
            if (_confirmOverlay == null)
                return;
            _confirmOverlay.pickingMode = PickingMode.Ignore;
            _confirmOverlay.style.display = DisplayStyle.None;
        }

        void ConfirmLeave()
        {
            if (_blockConfirmClick)
                return;
            LeaveNow(_pendingLeaveReason);
        }

        void LeaveNow(int reason)
        {
            if (reason < 0)
                return;
            if (!Application.isPlaying)
            {
                _status.text = "请先进入 Play Mode";
                HideConfirm();
                return;
            }

            HideConfirm();
            string beforeUid = GuestSession.UserId ?? "";
            // 与真机一致：走 Push → HostIslandEntryGate.Leave（清会话 / 可选清桶 + 退回 GuestLogin）。
            // 点击在本帧 Update 之后。先拆游客补闩再 Drain，否则清票瞬间登录窗会 BeginGuestLogin，
            // 第一次退登被新票拉回岛。
            BoluoPhoneLoginWindow.DisarmGuestProceed();
            NativePushReceiver.SimulatePush(MsgType.HostLeaveIsland, reason, 0, 0);
            EditorLoginGate.ResetSessionFlags();
            NativeEditorHud.Close();
            NativePushReceiver.Drain();

            string label = reason == MsgType.HostLeaveReasonAccountDeleted
                ? $"已模拟注销 a=1（清桶 userId={beforeUid}）→ GuestLogin"
                : $"已模拟退登 a=0（保留桶 userId={beforeUid}）→ GuestLogin";
            _status.text = label;
            Log.Info($"[EditorAccountLeave] {label}");

            RefreshSession();
            // 方便立刻再登：弹出编辑器登录窗。
            BoluoPhoneLoginWindow.Open();
        }
    }
}
