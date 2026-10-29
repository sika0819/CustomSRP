#if UNITY_EDITOR
using System;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.Native
{
    /// <summary>
    /// Editor 里的宿主。Query / Notify / Push 走和真机同一条桥，时序对齐 Android 壳：
    /// 先把票放进宿主，下一帧再 Push 106；N→U 只入队，等 <see cref="BoluoNativeBridge"/> Update 再 Drain。
    /// iOS 真机回调里当场 Drain，Editor 不走那条，避免 Play Mode 把「同栈生效」当成已经验过。
    /// </summary>
    public static class EditorNativeHost
    {
        /// <summary>宿主自己在换票，这段时间 Unity 不要走 Hybrid Query。</summary>
        public static bool HostOwnsHttp { get; private set; }

        /// <summary>壳上已有游客用户票。Unity Prefs 可以仍是空的。</summary>
        public static bool HasShellUser =>
            !string.IsNullOrEmpty(_shellUserToken) && !string.IsNullOrEmpty(_shellUserId);

        /// <summary>
        /// <see cref="CompleteGuestLogin"/> 写入的是壳侧占位票，不是 boluoDeviceLogin 签发的。
        /// 进岛不要拿它打 upgrade/bank。
        /// </summary>
        public static bool UsesLocalShellTickets { get; private set; }

        static string _deviceBlob = "";
        static string _userBlob = "";
        static int _armFrame = -1;
        static int _pendingHud;
        static bool _guestBusy;
        static bool _deviceRefreshStarted;
        static bool _handshakeLogged;
        static bool _leaveQueued;
        static bool _passPending;

        // 壳侧会话。GameEntry.Awake → ResetRuntime 会清 Query blob，不能把「先登录」放在那边。
        static string _shellDeviceToken = "";
        static uint _shellDeviceExpire;
        static string _shellUserToken = "";
        static string _shellUserId = "";

        /// <summary>107 已入队、尚未 Drain。这段时间不要再 Push 106。</summary>
        public static void NoteLeaveQueued()
        {
            _leaveQueued = true;
            _armFrame = -1;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnDomain()
        {
            ResetRuntime();
            ClearShellSession();
        }

        /// <summary>
        /// 清 Query blob 和交权帧。场景 Awake 会走这里，壳侧已登录的票留着。
        /// </summary>
        public static void ResetRuntime()
        {
            _deviceBlob = "";
            _userBlob = "";
            _armFrame = -1;
            _pendingHud = 0;
            _guestBusy = false;
            _deviceRefreshStarted = false;
            _handshakeLogged = false;
            _leaveQueued = false;
            _passPending = false;
            HostOwnsHttp = false;
        }

        /// <summary>域重载或测试收尾。Awake 不要调。</summary>
        public static void ClearShellSession()
        {
            _shellDeviceToken = "";
            _shellDeviceExpire = 0;
            _shellUserToken = "";
            _shellUserId = "";
            _passPending = false;
            UsesLocalShellTickets = false;
        }

        /// <summary>
        /// 壳上游客登录完成。只写入壳，不写 Unity Prefs，也不 Push。
        /// 用户 blob 在交票时用真机四段 <c>expire \t token \t userId \t 0</c>（saveLocal=0）。
        /// 这些票是占位，不会拿去打 upgrade/bank。
        /// </summary>
        public static void CompleteGuestLogin(
            string deviceToken, uint deviceExpire, string userToken, string userId)
        {
            _shellDeviceToken = deviceToken ?? "";
            _shellDeviceExpire = deviceExpire;
            _shellUserToken = userToken ?? "";
            _shellUserId = userId ?? "";
            UsesLocalShellTickets = true;
        }

        /// <summary>
        /// 把壳上的游客票交给 Unity。下一帧 Tick 再 Push 106，不从 Prefs 覆盖 blob。
        /// </summary>
        public static void PassGuestLoginToUnity()
        {
            if (!CopyShellToQueryBlobs())
                return;
            _passPending = true;
            _armFrame = -1;
        }

        /// <summary>
        /// 退登：先清壳上的用户票（设备票保留），再入队 107。不 Drain。
        /// </summary>
        public static void Logout()
        {
            _shellUserToken = "";
            _shellUserId = "";
            _passPending = false;
            _armFrame = -1;
            NativePushReceiver.SimulatePush(
                MsgType.HostLeaveIsland, MsgType.HostLeaveReasonLogout, 0, 0);
        }

        /// <summary>退登：宿主票先作废，之后的 Query(20) 才是空的。</summary>
        public static void RevokeUserTicket()
        {
            _userBlob = "";
            _armFrame = -1;
            _leaveQueued = false;
            _deviceRefreshStarted = false;
        }

        public static void OnNotify(int type, int a, int b, int c)
        {
            Log.Info($"[IMP] Editor host Notify type={type} a={a} b={b} c={c}");
            if (type == MsgType.UiHudIcon)
                _pendingHud = a;
        }

        public static int Query(int type, int a, int b, int c, string inBlob, out string outBlob)
        {
            outBlob = "";
            switch (type)
            {
                case MsgType.ProtocolHandshake:
                    outBlob = "1";
                    if (!_handshakeLogged)
                    {
                        _handshakeLogged = true;
                        Log.Info("[IMP] Editor host handshake version=1");
                    }

                    return BridgeCode.Ok;
                case MsgType.HostGetIdentity:
                    outBlob = string.Join(
                        "\t",
                        BoluoDeviceInfo.GetChannel(),
                        BoluoDeviceInfo.GetOriginEnd(),
                        BoluoDeviceInfo.GetAppId().ToString(),
                        BoluoApiEnvironment.GetEnvironmentType(),
                        BoluoDeviceInfo.GetIdentifier());
                    return BridgeCode.Ok;
                case MsgType.SessionGetToken:
                    return QueryToken(a, out outBlob);
                case MsgType.NowPlaying:
                    return BridgeCode.Ok;
                default:
                    Log.Debug($"[IMP] Editor host Query unsupported type={type} a={a} b={b} c={c}");
                    return BridgeCode.ErrUnsupported;
            }
        }

        /// <summary>游客按钮。正式会话已在盘上时不覆盖，交给 Tick 交权。</summary>
        public static void BeginGuestLogin()
        {
            if (_guestBusy || EditorLoginGate.CancelPendingLogin)
                return;
            if (EditorLoginGate.HasFormalSession)
                return;
            RunGuest().Forget();
        }

        public static void Tick()
        {
            if (_pendingHud != 0)
            {
                int icon = _pendingHud;
                _pendingHud = 0;
                NativeEditorHud.HandleHudIcon(icon);
            }

            if (_leaveQueued || EditorLoginGate.CancelPendingLogin)
            {
                _armFrame = -1;
                return;
            }

            if (HostOwnsHttp)
                return;

            // 壳先登录、Unity 后起来：不要等编辑器登录窗点游客。未 Arm 就自动交票。
            if (!_passPending && !HostIslandEntryGate.IsArmed && HasShellUser)
                PassGuestLoginToUnity();

            if (TryPushPassedGuestLogin())
                return;

            BoluoSession.LoadFromPrefs();
            if (HostIslandEntryGate.IsArmed
                && BoluoSession.DeviceToken == "editor_device"
                && !_deviceRefreshStarted)
            {
                _deviceRefreshStarted = true;
                ReplaceStandInDevice().Forget();
            }
            if (string.IsNullOrEmpty(BoluoSession.UserToken) || string.IsNullOrEmpty(BoluoSession.UserId))
            {
                _armFrame = -1;
                return;
            }

            if (HostIslandEntryGate.IsArmed)
            {
                _armFrame = -1;
                return;
            }

            // 正式票缺 deviceToken 时，宿主先自己换（跟壳一样），失败再带占位票交权，避免离线卡死。
            if (!BoluoSession.IsDeviceTokenValid() && !BoluoSession.IsGuest && !_deviceRefreshStarted)
            {
                _deviceRefreshStarted = true;
                RefreshDevice().Forget();
                return;
            }

            if (_armFrame < 0)
            {
                _armFrame = Time.frameCount + 1;
                return;
            }

            if (Time.frameCount < _armFrame)
                return;

            _armFrame = -1;
            if (!PublishSnapshot())
                return;

            Log.Info("[IMP] Editor host Push 106 after tickets");
            NativePushReceiver.SimulatePush(MsgType.HostArmIslandEntry);
        }

        /// <summary>
        /// 壳已交票：未 Arm 时下一帧 Push 106。不走 <see cref="PublishSnapshot"/>，
        /// 否则空 Prefs 会把刚拷进 Query 的 blob 清掉。
        /// </summary>
        static bool TryPushPassedGuestLogin()
        {
            if (!_passPending)
                return false;

            if (HostIslandEntryGate.IsArmed)
            {
                _armFrame = -1;
                return true;
            }

            if (_armFrame < 0)
            {
                _armFrame = Time.frameCount + 1;
                return true;
            }

            if (Time.frameCount < _armFrame)
                return true;

            _armFrame = -1;
            if (!CopyShellToQueryBlobs())
            {
                _passPending = false;
                return true;
            }

            _passPending = false;
            Log.Info("[IMP] Editor host Push 106 after shell pass");
            NativePushReceiver.SimulatePush(MsgType.HostArmIslandEntry);
            return true;
        }

        static bool CopyShellToQueryBlobs()
        {
            if (string.IsNullOrEmpty(_shellUserToken) || string.IsNullOrEmpty(_shellUserId))
            {
                _userBlob = "";
                return false;
            }

            if (!string.IsNullOrEmpty(_shellDeviceToken))
                _deviceBlob = _shellDeviceExpire + "\t" + _shellDeviceToken;

            uint exp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400u;
            _userBlob = exp + "\t" + _shellUserToken + "\t" + _shellUserId + "\t0";
            return true;
        }

        static int QueryToken(int kind, out string outBlob)
        {
            outBlob = kind == MsgType.TokenKindDevice ? _deviceBlob : _userBlob;
            if (kind != MsgType.TokenKindDevice && kind != MsgType.TokenKindUser)
            {
                outBlob = "";
                return BridgeCode.ErrBadArgs;
            }

            if (string.IsNullOrEmpty(outBlob))
                return BridgeCode.ErrNotReady;
            return BridgeCode.Ok;
        }

        static bool PublishSnapshot()
        {
            BoluoSession.LoadFromPrefs();
            if (string.IsNullOrEmpty(BoluoSession.UserToken) || string.IsNullOrEmpty(BoluoSession.UserId))
            {
                _userBlob = "";
                return false;
            }

            uint exp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400u;
            if (BoluoSession.IsDeviceTokenValid())
            {
                _deviceBlob = BoluoSession.DeviceTokenExpireTime + "\t" + BoluoSession.DeviceToken;
            }
            else
            {
                _deviceBlob = exp + "\teditor_device";
                Log.Warning("[IMP] Editor host deviceToken stand-in for Query(20)");
            }

            string token = BoluoSession.UserToken;
            string userId = BoluoSession.UserId;
            if (BoluoSession.IsGuest)
                _userBlob = exp + "\t" + token + "\t1\t" + userId;
            else
                _userBlob = exp + "\t" + token + "\t" + userId + "\t" + (BoluoSession.SaveLocal ? "1" : "0");
            return true;
        }

        static async UniTaskVoid ReplaceStandInDevice()
        {
            // 不抬 HostOwnsHttp：进岛 Query(20) 可能还在飞，抬了会让用户票改走 HTTP。
            // forceRefresh 本身会跳过 Hybrid Query。
            try
            {
                bool ok = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(
                    failFast: true, forceRefresh: true);
                Log.Info($"[IMP] Editor host stand-in device replace ok={ok}");
            }
            catch (Exception e)
            {
                Log.Warning($"[IMP] Editor host stand-in device replace failed: {e.Message}");
            }
        }

        static async UniTaskVoid RefreshDevice()
        {
            HostOwnsHttp = true;
            try
            {
                bool ok = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true);
                Log.Info($"[IMP] Editor host device refresh ok={ok}");
            }
            finally
            {
                HostOwnsHttp = false;
            }
        }

        static async UniTaskVoid RunGuest()
        {
            int generation = HostIslandEntryGate.Generation;
            _guestBusy = true;
            HostOwnsHttp = true;
            try
            {
                if (GuestLoginClient.ShouldTryRemoteInEditor())
                {
                    bool deviceOk = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true);
                    bool userOk = false;
                    if (deviceOk && generation == HostIslandEntryGate.Generation)
                        userOk = await BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true);
                    if (generation != HostIslandEntryGate.Generation)
                    {
                        Log.Info("[IMP] Editor host guest dropped — leave during login");
                        return;
                    }

                    if (!userOk || !GuestSession.IsLoggedIn)
                        GuestSession.ApplyLocalEditorGuest();
                    Log.Info(
                        $"[IMP] Editor host remote guest deviceOk={deviceOk} userOk={userOk} " +
                        $"userId={GuestSession.UserId}");
                }
                else
                {
                    if (generation != HostIslandEntryGate.Generation)
                        return;
                    GuestSession.ApplyLocalEditorGuest();
                    Log.Info($"[IMP] Editor host local guest userId={GuestSession.UserId}");
                }
            }
            finally
            {
                HostOwnsHttp = false;
                _guestBusy = false;
            }
        }
    }
}
#endif
