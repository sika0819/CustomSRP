using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Tests
{
    public sealed class DownloadDialogCopyTests
    {
        [Test]
        public void Request_UsesDocBodyAndButtons()
        {
            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Request("xxx");
            Assert.AreEqual("前方区域未下载", copy.Title);
            Assert.AreEqual(
                "此区域需要额外下载资源包（约xxxMB），下载后即可进入并体验完整内容。",
                copy.Body);
            Assert.AreEqual("立即下载", copy.PrimaryLabel);
            Assert.AreEqual("返回", copy.SecondaryLabel);
            Assert.IsTrue(copy.ShowSecondary);
        }

        [Test]
        public void Progress_DocNumbers_SentenceMatchesParts()
        {
            DownloadDialogCopy.ProgressCopy copy = DownloadDialogCopy.Progress(66f, 862f, 5f);
            Assert.AreEqual("正在为你下载资源：", copy.Prefix);
            Assert.AreEqual("正在为你下载资源：66MB/862MB  速度5MB/秒", copy.Sentence);
            Assert.AreEqual("66", copy.Downloaded);
            Assert.AreEqual("MB/862MB  速度5MB/秒", copy.SizeDetail);
            Assert.AreEqual(
                copy.Prefix + copy.Downloaded + copy.SizeDetail,
                copy.Sentence);
            // 剩余 796MB / 5MB/s ≈ 159s → 剩余3m
            Assert.AreEqual("剩余3m", copy.Eta);
        }

        [Test]
        public void Progress_SecondsEta_HasNoSpace()
        {
            // 剩余 20MB / 5MB/s = 4s
            DownloadDialogCopy.ProgressCopy copy = DownloadDialogCopy.Progress(66f, 86f, 5f);
            Assert.AreEqual("剩余4s", copy.Eta);
        }

        [Test]
        public void Progress_UnknownSpeed_ShowsDashSpeedAndEmptyEta()
        {
            DownloadDialogCopy.ProgressCopy copy = DownloadDialogCopy.Progress(66f, 862f, -1f);
            Assert.AreEqual(string.Empty, copy.Eta);
            Assert.AreEqual("正在为你下载资源：66MB/862MB  速度--MB/秒", copy.Sentence);
            Assert.AreEqual("MB/862MB  速度--MB/秒", copy.SizeDetail);
        }

        [Test]
        public void Presenter_UnknownSpeed_KeepsPreviousEta()
        {
            var root = new VisualElement();
            var card = new VisualElement();
            card.AddToClassList("notice-card");
            root.Add(card);

            var title = new Label { name = "title" };
            var body = new Label { name = "body" };
            var primary = new Button { name = "primary" };
            primary.Add(new Label { name = "primaryLabel" });
            var secondary = new Button { name = "secondary" };
            secondary.Add(new Label { name = "secondaryLabel" });
            var fill = new VisualElement { name = "progress-fill" };
            var sentence = new Label { name = "sentence" };
            var eta = new Label { name = "eta", text = string.Empty };
            var downloaded = new Label { name = "downloaded" };
            var sizeDetail = new Label { name = "size-detail" };
            card.Add(title);
            card.Add(body);
            card.Add(primary);
            card.Add(secondary);
            card.Add(fill);
            card.Add(sentence);
            card.Add(eta);
            card.Add(downloaded);
            card.Add(sizeDetail);

            var presenter = new NoticeCardPresenter();
            presenter.Bind(root);
            presenter.Apply(new NoticeCardContent
            {
                Title = "场景资源加载中...",
                ShowBody = false,
                Icon = NoticeCardIcon.Downloading,
                PrimaryLabel = "我知道了",
                ShowSecondary = false,
                ShowProgress = true,
            });

            // 模拟已有速度算出的剩余，再丢速度：应保持「剩余4s」，不写成「剩余--」。
            eta.text = "剩余4s";
            presenter.SetProgress(7.66f, 66f, 862f);
            Assert.AreEqual("剩余4s", eta.text);
            Assert.AreEqual("MB/862MB  速度--MB/秒", sizeDetail.text);

            presenter.Unbind();
        }

        [Test]
        public void Progress_Completed_ShowsDone()
        {
            DownloadDialogCopy.ProgressCopy copy = DownloadDialogCopy.Progress(862f, 862f, 5f, completed: true);
            Assert.AreEqual("完成", copy.Eta);
        }

        [Test]
        public void Progress_OverOneMinute_UsesMinutesCap()
        {
            // 剩余很大 → 封顶 60m
            DownloadDialogCopy.ProgressCopy copy = DownloadDialogCopy.Progress(0f, 10000f, 1f);
            Assert.AreEqual("剩余60m", copy.Eta);
        }

        [Test]
        public void Mobile_UsesDocCopy()
        {
            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Mobile("500");
            Assert.AreEqual("正在使用蜂窝数据", copy.Title);
            Assert.AreEqual(
                "需要下载约 500MB 音频资源，当前使用移动数据，下载可能产生流量费用。",
                copy.Body);
            Assert.AreEqual("继续下载", copy.PrimaryLabel);
            Assert.AreEqual("切换Wi-Fi后下载", copy.SecondaryLabel);
            Assert.IsTrue(copy.ShowSecondary);
        }

        [Test]
        public void Storage_KeepsClosingQuoteBeforeComma()
        {
            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Storage("600");
            Assert.AreEqual("温馨提示", copy.Title);
            Assert.AreEqual(
                "设备存储空间不足，需至少预留600MB空间才能下载，建议清理存储空间后重试”，无法启动下载。",
                copy.Body);
            Assert.AreEqual("我知道了", copy.PrimaryLabel);
            Assert.IsFalse(copy.ShowSecondary);
            Assert.IsTrue(copy.Body.Contains("重试”，无法启动下载。"));
        }

        [Test]
        public void Offline_BodyHasNoPeriod_PrimaryIsGotIt()
        {
            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Offline();
            Assert.AreEqual("无网络连接", copy.Title);
            Assert.AreEqual("当前设备无网络，无法下载资源包，请开启网络后继续", copy.Body);
            Assert.IsFalse(copy.Body.EndsWith("。"));
            Assert.AreEqual("我知道了", copy.PrimaryLabel);
            Assert.IsFalse(copy.ShowSecondary);
        }

        [Test]
        public void OfflineToast_IsNetworkAnomaly()
        {
            Assert.AreEqual("网络异常", DownloadDialogCopy.OfflineToast);
        }

        [Test]
        public void Presenter_ProgressTextsMatchCopy()
        {
            var root = new VisualElement();
            var card = new VisualElement();
            card.AddToClassList("notice-card");
            root.Add(card);

            var title = new Label { name = "title" };
            var body = new Label { name = "body" };
            var primary = new Button { name = "primary" };
            primary.Add(new Label { name = "primaryLabel" });
            var secondary = new Button { name = "secondary" };
            secondary.Add(new Label { name = "secondaryLabel" });
            var fill = new VisualElement { name = "progress-fill" };
            var sentence = new Label { name = "sentence" };
            var eta = new Label { name = "eta" };
            var downloaded = new Label { name = "downloaded" };
            var sizeDetail = new Label { name = "size-detail" };
            card.Add(title);
            card.Add(body);
            card.Add(primary);
            card.Add(secondary);
            card.Add(fill);
            card.Add(sentence);
            card.Add(eta);
            card.Add(downloaded);
            card.Add(sizeDetail);

            var presenter = new NoticeCardPresenter();
            presenter.Bind(root);
            presenter.Apply(new NoticeCardContent
            {
                Title = "场景资源加载中...",
                ShowBody = false,
                Icon = NoticeCardIcon.Downloading,
                PrimaryLabel = "我知道了",
                ShowSecondary = false,
                ShowProgress = true,
            });
            presenter.SetProgress(7.66f, 66f, 862f);

            DownloadDialogCopy.ProgressCopy expected = DownloadDialogCopy.Progress(66f, 862f, -1f);
            Assert.AreEqual("场景资源加载中...", title.text);
            Assert.AreEqual("我知道了", primary.Q<Label>("primaryLabel").text);
            Assert.AreEqual(expected.Prefix, sentence.text);
            Assert.AreEqual(expected.Eta, eta.text);
            Assert.AreEqual(expected.Downloaded, downloaded.text);
            Assert.AreEqual(expected.SizeDetail, sizeDetail.text);

            presenter.Unbind();
        }
    }
}
