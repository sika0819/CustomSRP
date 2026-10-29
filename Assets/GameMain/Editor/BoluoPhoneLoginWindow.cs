using System;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    /// <summary>
    /// Editor-only UITK 登录联调窗（游客 / 手机号）。菜单 <c>GameMain/Boluo/编辑器登录</c>。
    /// 不进真机包；进 Play 且无已落盘会话（正式/游客）时自动弹出。
    /// 注销确认期：登录 data.isCancel → 游戏内 <see cref="UiDialogService.ShowCancelAccountReminder"/>；
    /// 「退出」清会话；「登录并终止注销」调 loginAndStopCancel 后进岛。
    /// </summary>
    public class BoluoPhoneLoginWindow : EditorWindow
    {
        const string PrefPhone = "Game.Editor.PhoneLogin.Mobile";
        const string PrefPassword = "Game.Editor.PhoneLogin.Password";
        const string PrefNation = "Game.Editor.PhoneLogin.Nation";
        const string PrefTab = "Game.Editor.PhoneLogin.Tab";
        const string PrefForceCancelDialog = "Game.Editor.PhoneLogin.ForceCancelDialog";

        Label _sessionKind;
        Label _sessionUserId;
        Label _sessionDevice;
        Label _sessionUserToken;
        Label _envLabel;
        Label _status;
        Label _guestRemoteHint;
        TextField _nationField;
        TextField _mobileField;
        TextField _codeField;
        TextField _passwordField;
        Button _sendCodeBtn;
        Button _mobileLoginBtn;
        Button _passwordLoginBtn;
        Button _guestBtn;
        Toggle _forceCancelToggle;
        VisualElement _guestPage;
        VisualElement _phonePage;
        ToolbarToggle _tabGuest;
        ToolbarToggle _tabPhone;

        bool _busy;
        bool _cancelDialogOpen;
        /// <summary>
        /// 游客按钮已点过：GuestLogin.OnEnter 会 Reset 门闩，需在等待期反复补 Request。
        /// </summary>
        bool _guestProceedLatched;
        double _codeCooldownUntil;
        int _tab;

        [InitializeOnLoadMethod]
        static void AutoOpenOnPlay()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode
                || state == PlayModeStateChange.EnteredEditMode)
            {
                // EditorWindow 跨 Play 存活；async 中断时 _busy 会钉死导致按钮永远灰。
                foreach (var w in Resources.FindObjectsOfTypeAll<BoluoPhoneLoginWindow>())
                    w?.ClearTransientUiState();
                return;
            }

            if (state != PlayModeStateChange.EnteredPlayMode)
                return;
            EditorApplication.delayCall += () =>
            {
                if (!Application.isPlaying)
                    return;
                foreach (var w in Resources.FindObjectsOfTypeAll<BoluoPhoneLoginWindow>())
                    w?.ClearTransientUiState();
                if (EditorLoginGate.HasAnySession)
                    return;
                Open();
            };
        }

        void ClearTransientUiState()
        {
            _busy = false;
            _cancelDialogOpen = false;
            _guestProceedLatched = false;
            RefreshBusyUi();
        }

        /// <summary>
        /// 退登 Drain 之前拆掉游客补闩。否则清票后的 OnInspectorUpdate 会再走 BeginGuestLogin，
        /// 第一次退登被新游客票拉回岛，要点第二次才留下。
        /// </summary>
        public static void DisarmGuestProceed()
        {
            foreach (var win in Resources.FindObjectsOfTypeAll<BoluoPhoneLoginWindow>())
                win._guestProceedLatched = false;
        }

        [MenuItem("GameMain/Boluo/编辑器登录")]
        public static void Open()
        {
            DisarmGuestProceed();
            var win = GetWindow<BoluoPhoneLoginWindow>(false, "编辑器登录", true);
            win.minSize = new Vector2(440, 520);
            // 退登再开窗：清掉上一轮游客 latch，避免 OnEnter Reset 后被自动补闩绕过登录。
            win._guestProceedLatched = false;
            win.Show();
            win.Focus();
            win.RefreshSession();
            win.RefreshBusyUi();
        }

        void CreateGUI()
        {
            _tab = Mathf.Clamp(EditorPrefs.GetInt(PrefTab, 1), 0, 1);

            var root = rootVisualElement;
            root.style.paddingLeft = 14;
            root.style.paddingRight = 14;
            root.style.paddingTop = 12;
            root.style.paddingBottom = 12;

            var title = new Label("编辑器登录（仅 Editor）");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 15;
            title.style.marginBottom = 4;
            root.Add(title);

            var hint = new Label(
                "无会话时 Play 启动链会等本窗口放行。游客/正式账号均落盘 token，主动退出前下次 Play 自动复用。真机包不含此窗。");
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 10;
            hint.style.color = new Color(0.55f, 0.55f, 0.55f);
            root.Add(hint);

            root.Add(BuildSessionBox());

            var tabs = new Toolbar();
            tabs.style.marginBottom = 8;
            root.Add(tabs);

            _tabGuest = new ToolbarToggle { text = "游客登录" };
            _tabPhone = new ToolbarToggle { text = "手机登录" };
            _tabGuest.RegisterValueChangedCallback(evt =>
            {
                if (!evt.newValue)
                    return;
                SelectTab(0);
            });
            _tabPhone.RegisterValueChangedCallback(evt =>
            {
                if (!evt.newValue)
                    return;
                SelectTab(1);
            });
            tabs.Add(_tabGuest);
            tabs.Add(_tabPhone);

            _guestPage = BuildGuestPage();
            _phonePage = BuildPhonePage();
            root.Add(_guestPage);
            root.Add(_phonePage);

            _status = new Label("就绪 — 进入 Play 后选择登录方式");
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = 10;
            root.Add(_status);

            SelectTab(_tab, persist: false);
            RefreshSession();
            RefreshBusyUi();
        }

        VisualElement BuildSessionBox()
        {
            var box = new VisualElement();
            box.style.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 0.35f);
            box.style.paddingLeft = 10;
            box.style.paddingRight = 10;
            box.style.paddingTop = 8;
            box.style.paddingBottom = 8;
            box.style.marginBottom = 12;
            box.style.borderTopLeftRadius = 4;
            box.style.borderTopRightRadius = 4;
            box.style.borderBottomLeftRadius = 4;
            box.style.borderBottomRightRadius = 4;

            var header = new Label("当前会话");
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 4;
            box.Add(header);

            _envLabel = new Label();
            box.Add(_envLabel);
            _sessionKind = new Label();
            box.Add(_sessionKind);
            _sessionUserId = new Label();
            box.Add(_sessionUserId);
            _sessionDevice = new Label();
            box.Add(_sessionDevice);
            _sessionUserToken = new Label();
            box.Add(_sessionUserToken);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 6;
            box.Add(row);

            var refresh = new Button(RefreshSession) { text = "刷新" };
            refresh.style.height = 24;
            refresh.style.flexGrow = 1;
            refresh.style.marginRight = 6;
            row.Add(refresh);

            var clearUser = new Button(() =>
            {
                GuestSession.Clear();
                EditorLoginGate.ResetSessionFlags();
                _guestProceedLatched = false;
                SetStatus("已清空 userToken / userId");
                RefreshSession();
                RefreshBusyUi();
            })
            {
                text = "清空用户会话",
            };
            clearUser.style.height = 24;
            clearUser.style.flexGrow = 1;
            clearUser.style.marginRight = 6;
            row.Add(clearUser);

            var clearDevice = new Button(() =>
            {
                BoluoSession.ClearDeviceToken();
                SetStatus("已清空 deviceToken");
                RefreshSession();
            })
            {
                text = "清空 deviceToken",
            };
            clearDevice.style.height = 24;
            clearDevice.style.flexGrow = 1;
            row.Add(clearDevice);

            return box;
        }

        VisualElement BuildGuestPage()
        {
            var page = new VisualElement();

            var tip = new Label(
                "游客会话：本地或远端 touristLogin（菜单 Try Remote TouristLogin）。" +
                "token 落盘可复用；岛内进度仍不落盘（默认出生点）。主动「清空用户会话」/退登后需重新登录。");
            tip.style.whiteSpace = WhiteSpace.Normal;
            tip.style.marginBottom = 8;
            tip.style.color = new Color(0.55f, 0.55f, 0.55f);
            page.Add(tip);

            _guestRemoteHint = new Label();
            _guestRemoteHint.style.marginBottom = 10;
            page.Add(_guestRemoteHint);

            _guestBtn = new Button(OnGuestLoginClicked) { text = "游客登录进岛" };
            _guestBtn.style.height = 36;
            page.Add(_guestBtn);

            return page;
        }

        VisualElement BuildPhonePage()
        {
            var page = new VisualElement();

            var tip = new Label(
                "正式账号：验证码或密码。写入 isGuest=false，可测存档分桶 / 进度恢复。" +
                " 若账号处于注销确认期（isCancel），登录后弹出游戏内注销提醒。");
            tip.style.whiteSpace = WhiteSpace.Normal;
            tip.style.marginBottom = 8;
            tip.style.color = new Color(0.55f, 0.55f, 0.55f);
            page.Add(tip);

            _forceCancelToggle = new Toggle("强制弹出注销提醒（联调，无视服务端 isCancel）")
            {
                value = EditorPrefs.GetBool(PrefForceCancelDialog, false),
            };
            _forceCancelToggle.style.marginBottom = 8;
            _forceCancelToggle.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetBool(PrefForceCancelDialog, evt.newValue));
            page.Add(_forceCancelToggle);

            _nationField = new TextField("区号")
            {
                value = EditorPrefs.GetString(PrefNation, "+86"),
            };
            _nationField.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetString(PrefNation, evt.newValue ?? "+86"));
            page.Add(_nationField);

            _mobileField = new TextField("手机号")
            {
                value = EditorPrefs.GetString(PrefPhone, ""),
            };
            _mobileField.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetString(PrefPhone, evt.newValue ?? ""));
            page.Add(_mobileField);

            var codeRow = new VisualElement();
            codeRow.style.flexDirection = FlexDirection.Row;
            codeRow.style.marginTop = 4;
            page.Add(codeRow);

            _codeField = new TextField("验证码");
            _codeField.style.flexGrow = 1;
            codeRow.Add(_codeField);

            _sendCodeBtn = new Button(() => SendCodeAsync().Forget()) { text = "获取验证码" };
            _sendCodeBtn.style.width = 110;
            _sendCodeBtn.style.height = 22;
            _sendCodeBtn.style.marginLeft = 6;
            _sendCodeBtn.style.alignSelf = Align.FlexEnd;
            codeRow.Add(_sendCodeBtn);

            _mobileLoginBtn = new Button(() => MobileLoginAsync().Forget()) { text = "验证码登录" };
            _mobileLoginBtn.style.height = 28;
            _mobileLoginBtn.style.marginTop = 8;
            page.Add(_mobileLoginBtn);

            _passwordField = new TextField("密码")
            {
                value = EditorPrefs.GetString(PrefPassword, ""),
                isPasswordField = true,
            };
            _passwordField.style.marginTop = 10;
            _passwordField.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetString(PrefPassword, evt.newValue ?? ""));
            page.Add(_passwordField);

            _passwordLoginBtn = new Button(() => PasswordLoginAsync().Forget()) { text = "密码登录" };
            _passwordLoginBtn.style.height = 28;
            _passwordLoginBtn.style.marginTop = 8;
            page.Add(_passwordLoginBtn);

            return page;
        }

        void SelectTab(int tab, bool persist = true)
        {
            _tab = tab;
            if (persist)
                EditorPrefs.SetInt(PrefTab, tab);

            if (_tabGuest != null)
            {
                _tabGuest.SetValueWithoutNotify(tab == 0);
                _tabPhone.SetValueWithoutNotify(tab == 1);
            }

            if (_guestPage != null)
                _guestPage.style.display = tab == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (_phonePage != null)
                _phonePage.style.display = tab == 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OnInspectorUpdate()
        {
            if (!Application.isPlaying && !_busy)
                return;
            // GuestLogin.OnEnter 会 ResetSessionFlags；已点游客则补闩，避免「点了没进岛」。
            if (Application.isPlaying && _guestProceedLatched)
            {
                if (EditorLoginGate.HasAnySession && !EditorLoginGate.CancelPendingLogin)
                    _guestProceedLatched = false;
                else if (!EditorLoginGate.HasFormalSession
                    && !EditorLoginGate.CancelPendingLogin)
                    Game.Native.EditorNativeHost.BeginGuestLogin();
            }

            RefreshBusyUi();
            if (Application.isPlaying)
                RefreshSession();
        }

        void OnFocus() => RefreshSession();

        void RefreshSession()
        {
            if (_sessionKind == null)
                return;

            string env = BoluoApiEnvironment.GetEnvironmentType();
            if (_envLabel != null)
                _envLabel.text = $"API：{env}  ·  {BoluoApiEnvironment.ResolveUserApiBase()}";

            GuestSession.LoadFromPrefs();
            BoluoSession.LoadFromPrefs();
            string uid = string.IsNullOrEmpty(GuestSession.UserId) ? "(空)" : GuestSession.UserId;
            string kind = !GuestSession.IsLoggedIn
                ? "未登录"
                : (GuestSession.IsGuest ? "游客" : "正式账号");
            _sessionKind.text = $"状态：{kind}";
            _sessionUserId.text = $"userId：{uid}";
            _sessionDevice.text = string.IsNullOrEmpty(BoluoSession.DeviceToken)
                ? "deviceToken：(空)"
                : $"deviceToken：len={BoluoSession.DeviceToken.Length} valid={BoluoSession.IsDeviceTokenValid()}";
            _sessionUserToken.text = string.IsNullOrEmpty(BoluoSession.UserToken)
                ? "userToken：(空)"
                : $"userToken：len={BoluoSession.UserToken.Length}";

            if (_guestRemoteHint != null)
            {
                bool tryRemote = EditorPrefs.GetBool(GuestLoginClient.EditorTryRemoteKey, false);
                _guestRemoteHint.text = tryRemote
                    ? "换票：远端 touristLogin（失败回退本地）"
                    : "换票：本地 Editor 游客";
            }

            if (!Application.isPlaying && _status != null
                && (_status.text == null || _status.text.StartsWith("就绪", StringComparison.Ordinal)))
                _status.text = "请先进入 Play，再点游客 / 手机登录";
        }

        void RefreshBusyUi()
        {
            bool play = Application.isPlaying;
            bool cancelBlock = _cancelDialogOpen || EditorLoginGate.CancelPendingLogin;
            // 游客不跟手机请求的 _busy：手机 API 挂起时仍可切游客进岛。
            bool guestGate = play && !cancelBlock;
            bool phoneGate = guestGate && !_busy;
            int left = CodeCooldownLeft();

            if (_guestBtn != null)
                _guestBtn.SetEnabled(guestGate);
            if (_mobileLoginBtn != null)
                _mobileLoginBtn.SetEnabled(phoneGate);
            if (_passwordLoginBtn != null)
                _passwordLoginBtn.SetEnabled(phoneGate);
            if (_sendCodeBtn != null)
            {
                _sendCodeBtn.SetEnabled(phoneGate && left <= 0);
                _sendCodeBtn.text = left > 0 ? $"重新发送({left})" : "获取验证码";
            }
        }

        int CodeCooldownLeft()
        {
            double left = _codeCooldownUntil - EditorApplication.timeSinceStartup;
            return left <= 0 ? 0 : Mathf.CeilToInt((float)left);
        }

        void OnGuestLoginClicked()
        {
            if (!Application.isPlaying)
            {
                SetStatus("请先进入 Play Mode");
                return;
            }

            _guestProceedLatched = true;
            Game.Native.EditorNativeHost.BeginGuestLogin();
            SetStatus(EditorLoginGate.HasFormalSession
                ? "已有正式会话，宿主写票后交权"
                : "已请求游客登录 — 宿主写票后下一帧交权");
            Log.Info("[EditorLogin] guest proceed requested");
        }

        async UniTaskVoid SendCodeAsync()
        {
            if (_busy || !Application.isPlaying)
                return;
            _busy = true;
            RefreshBusyUi();
            SetStatus("发送验证码…");
            try
            {
                var r = await BoluoPhoneLoginClient.RequestVerifyCodeAsync(
                    _mobileField?.value, _nationField?.value);
                if (r.Ok)
                {
                    _codeCooldownUntil = EditorApplication.timeSinceStartup + 60;
                    SetStatus("验证码已发送");
                }
                else
                    SetStatus(r.Message);
            }
            catch (Exception e)
            {
                SetStatus("发送异常: " + e.Message);
                Log.Warning("[EditorLogin] send code: " + e);
            }
            finally
            {
                _busy = false;
                RefreshBusyUi();
            }
        }

        async UniTaskVoid MobileLoginAsync()
        {
            if (_busy || !Application.isPlaying)
                return;
            _busy = true;
            RefreshBusyUi();
            SetStatus("验证码登录…");
            try
            {
                var r = await BoluoPhoneLoginClient.MobileLoginAsync(
                    _mobileField?.value, _codeField?.value, _nationField?.value);
                HandleLoginResult(r, "mobileLogin");
            }
            catch (Exception e)
            {
                SetStatus("登录异常: " + e.Message);
                Log.Warning("[EditorLogin] mobileLogin: " + e);
            }
            finally
            {
                _busy = false;
                RefreshBusyUi();
            }
        }

        async UniTaskVoid PasswordLoginAsync()
        {
            if (_busy || !Application.isPlaying)
                return;
            _busy = true;
            RefreshBusyUi();
            SetStatus("密码登录…");
            try
            {
                var r = await BoluoPhoneLoginClient.PasswordLoginAsync(
                    _mobileField?.value, _passwordField?.value, _nationField?.value);
                HandleLoginResult(r, "passwordLogin");
            }
            catch (Exception e)
            {
                SetStatus("登录异常: " + e.Message);
                Log.Warning("[EditorLogin] passwordLogin: " + e);
            }
            finally
            {
                _busy = false;
                RefreshBusyUi();
            }
        }

        void HandleLoginResult(
            BoluoPhoneLoginClient.LoginResult r,
            string tag)
        {
            if (!r.Ok)
            {
                SetStatus(r.Message);
                return;
            }

            PersistPhone();
            RefreshSession();

            bool forceCancel = EditorPrefs.GetBool(PrefForceCancelDialog, false);
            bool isCancel = r.IsCancel || forceCancel;
            if (!isCancel)
            {
                SetStatus($"登录成功 userId={r.UserId} — 宿主下一帧交权");
                return;
            }

            // 对齐原生：会话已落盘，但 CancelPending 挡进岛，等注销提醒弹窗。
            EditorLoginGate.SetCancelPendingLogin(true);
            string body = BoluoPhoneLoginClient.ResolveCancelBodyText(
                forceCancel && string.IsNullOrEmpty(r.CancelMsg)
                    ? "【联调强制】当前账号申请了注销，目前处于确认期。点「登录并终止注销」调 loginAndStopCancel；点「退出」清会话。"
                    : r.CancelMsg);
            SetStatus(
                $"注销确认期 userId={r.UserId} — 游戏内弹窗（退出=取消登录 / 终止注销=进岛）");
            Log.Info(
                $"[EditorLogin] {tag} isCancel pending userId={r.UserId} force={forceCancel}");
            ShowCancelAccountReminder(body);
        }

        void ShowCancelAccountReminder(string body)
        {
            if (!Application.isPlaying || _cancelDialogOpen)
                return;
            if (GameEntry.UI == null)
            {
                SetStatus("UI 未就绪，无法弹出注销提醒");
                EditorLoginGate.SetCancelPendingLogin(false);
                GuestSession.Clear();
                RefreshSession();
                return;
            }

            _cancelDialogOpen = true;
            // Loading 盖住 Dialog 时先收起，对齐启动无网弹窗。
            BootLoadingService.Hide();
            UiDialogService.ShowCancelAccountReminder(
                onLoginAndStopCancel: () => OnStopCancelClicked().Forget(),
                onCancel: OnCancelReminderExit,
                body: body);
        }

        async UniTaskVoid OnStopCancelClicked()
        {
            _cancelDialogOpen = false;
            if (!Application.isPlaying)
            {
                EditorLoginGate.SetCancelPendingLogin(false);
                return;
            }

            _busy = true;
            RefreshBusyUi();
            SetStatus("loginAndStopCancel…");
            try
            {
                var r = await BoluoPhoneLoginClient.LoginAndStopCancelAsync();
                if (!r.Ok)
                {
                    // 对齐 iOS：终止失败 → 清会话回登录，可重试。
                    GuestSession.Clear();
                    EditorLoginGate.ResetSessionFlags();
                    SetStatus("终止注销失败: " + r.Message);
                    Log.Warning("[EditorLogin] loginAndStopCancel fail: " + r.Message);
                    RefreshSession();
                    BootLoadingService.Show("请在「编辑器登录」窗口选择登录方式…", 0.45f);
                    return;
                }

                EditorLoginGate.SetCancelPendingLogin(false);
                SetStatus($"已终止注销并登录 userId={r.UserId} — 宿主下一帧交权");
                Log.Info($"[EditorLogin] loginAndStopCancel ok userId={r.UserId}");
                RefreshSession();
                BootLoadingService.Show("正在进入世界...", BootLoadingService.VisualPrepareMin);
            }
            catch (Exception e)
            {
                GuestSession.Clear();
                EditorLoginGate.ResetSessionFlags();
                SetStatus("终止注销异常: " + e.Message);
                Log.Warning("[EditorLogin] loginAndStopCancel: " + e);
                RefreshSession();
                BootLoadingService.Show("请在「编辑器登录」窗口选择登录方式…", 0.45f);
            }
            finally
            {
                _busy = false;
                RefreshBusyUi();
            }
        }

        void OnCancelReminderExit()
        {
            _cancelDialogOpen = false;
            // 「退出」：清会话，停留登录页（可再登）。
            GuestSession.Clear();
            EditorLoginGate.ResetSessionFlags();
            SetStatus("已退出注销提醒 — 会话已清，可重新登录");
            Log.Info("[EditorLogin] cancel reminder exit — session cleared");
            RefreshSession();
            if (Application.isPlaying)
                BootLoadingService.Show("请在「编辑器登录」窗口选择登录方式…", 0.45f);
        }

        void PersistPhone()
        {
            EditorPrefs.SetString(PrefPhone, _mobileField?.value ?? "");
            EditorPrefs.SetString(PrefNation, _nationField?.value ?? "+86");
            EditorPrefs.SetString(PrefPassword, _passwordField?.value ?? "");
        }

        void SetStatus(string text)
        {
            if (_status != null)
                _status.text = text ?? "";
        }
    }
}
