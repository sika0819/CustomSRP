using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// deviceToken 被服务端拒绝时静默换票。挤下线、退登、注销不换。
    /// </summary>
    public sealed class DeviceTokenRefreshPolicyTests
    {
        [Test]
        public void Mismatch_Refreshes()
        {
            Assert.IsTrue(BankUpgradeClient.IsSilentDeviceTokenRefresh("deviceToken 不一致"));
            Assert.IsTrue(BankUpgradeClient.IsSilentDeviceTokenRefresh("deviceToken不一致"));
        }

        [Test]
        public void Expired_Or_Auth_Refreshes()
        {
            Assert.IsTrue(BankUpgradeClient.IsSilentDeviceTokenRefresh("deviceToken 已过期"));
            Assert.IsTrue(BankUpgradeClient.IsSilentDeviceTokenRefresh("deviceToken 鉴权失败"));
            Assert.IsTrue(BankUpgradeClient.IsSilentDeviceTokenRefresh("deviceToken无效"));
        }

        [Test]
        public void Kick_Logout_Cancel_DoNotRefresh()
        {
            Assert.IsFalse(BankUpgradeClient.IsSilentDeviceTokenRefresh("用户Token不一致"));
            Assert.IsFalse(BankUpgradeClient.IsSilentDeviceTokenRefresh("当前账号已在其他设备登录"));
            Assert.IsFalse(BankUpgradeClient.IsSilentDeviceTokenRefresh("用户已注销"));
            Assert.IsFalse(BankUpgradeClient.IsSilentDeviceTokenRefresh("deviceToken 不一致，用户已退出"));
        }

        [Test]
        public void Empty_Or_Unrelated_DoesNotRefresh()
        {
            Assert.IsFalse(BankUpgradeClient.IsSilentDeviceTokenRefresh(null));
            Assert.IsFalse(BankUpgradeClient.IsSilentDeviceTokenRefresh(""));
            Assert.IsFalse(BankUpgradeClient.IsSilentDeviceTokenRefresh("无网络"));
        }
    }
}
