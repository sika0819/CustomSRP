using Game.BoluoApi;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    public sealed class BoluoAppLaunchCacheTests
    {
        [TearDown]
        public void TearDown()
        {
            BoluoAppLaunchCache.EditorClearOverride();
            PlayerPrefs.DeleteKey(BoluoAppLaunchCache.ResponseJsonKey);
            PlayerPrefs.Save();
        }

        [Test]
        public void MissingCache_WifiConfirmDisabled()
        {
            PlayerPrefs.DeleteKey(BoluoAppLaunchCache.ResponseJsonKey);
            Assert.IsFalse(BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled());
        }

        [Test]
        public void FeatureOn_WifiConfirmEnabled()
        {
            WriteFeature(cancelAccount: 1);
            Assert.IsTrue(BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled());
        }

        [Test]
        public void FeatureOff_WifiConfirmDisabled()
        {
            WriteFeature(cancelAccount: 2);
            Assert.IsFalse(BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled());
        }

        [Test]
        public void EditorOverride_BeatsPrefs()
        {
            WriteFeature(cancelAccount: 2);
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            Assert.IsTrue(BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled());
        }

        [Test]
        public void TryLoad_ParsesNestedFeature()
        {
            WriteFeature(cancelAccount: 1);
            Assert.IsTrue(BoluoAppLaunchCache.TryLoad(out BoluoAppLaunchCache.AppLaunchRoot root));
            Assert.IsNotNull(root.data);
            Assert.IsNotNull(root.data.feature);
            Assert.AreEqual(1, root.data.feature.cancelAccount);
        }

        static void WriteFeature(int cancelAccount)
        {
            string json =
                "{\"code\":0,\"msg\":\"ok\",\"data\":{" +
                "\"deviceLegal\":true,\"storeReview\":2," +
                "\"feature\":{\"touristLogin\":1,\"wyOneClickLogin\":1," +
                "\"wechatLogin\":1,\"cancelAccount\":" + cancelAccount + "}," +
                "\"backgroundPlayDisable\":false}}";
            PlayerPrefs.SetString(BoluoAppLaunchCache.ResponseJsonKey, json);
            PlayerPrefs.Save();
        }
    }
}
