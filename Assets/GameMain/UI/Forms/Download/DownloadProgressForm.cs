using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Download progress dialog. Closing the card does not stop the downloader.
    /// </summary>
    public class DownloadProgressForm : GameFormLogic
    {
        /// <summary>上滑已经结束时，至少留出一帧再关，百分比才画得出来。</summary>
        public const float MinVisibleSeconds = 0.05f;

        public class UserData
        {
            public Action OnCancel;
            public int Ticket;
        }

        UserData _userData;
        NoticeCardPresenter _card;
        bool _progressPresented;
        int _presentedFrame = -1;
        bool _transferCloseArmed;
        IVisualElementScheduledItem _heldCloseSchedule;

        protected override bool CloseWhenAnyButtonClicked => false;
        // 蒙层原地出现；卡片仍上滑（GameFormLogic 滑 stage）。
        protected override bool SlidesUpFromBottom => true;
        protected override bool CloseOnBackdropClick => true;
        protected override bool PinDocumentOrder => true;
        // 进度窗只提示；固墙拦住进区，开着仍可走开。
        protected override bool AllowsLocomotionWhileOpen => true;

        /// <summary>百分比已经写进这张卡片。</summary>
        public bool HasPresentedProgress => _progressPresented;

        /// <summary>百分比已套上，上滑结束，且不是刚写上的这一帧。</summary>
        public bool IsTransferCloseReady =>
            _progressPresented
            && OpenSlideRemaining <= 0.001f
            && _presentedFrame >= 0
            && _presentedFrame != Time.frameCount;

        protected override void OnBackdropDismiss()
        {
            _userData?.OnCancel?.Invoke();
            CloseSelf();
        }

        public override void OnOpen(object userData)
        {
            _progressPresented = false;
            _presentedFrame = -1;
            CancelHeldCloseSchedule();
            base.OnOpen(userData);
            _userData = userData as UserData;
            int ticket = _userData != null ? _userData.Ticket : 0;
            if (UiDialogService.IsProgressRetired(ticket) || UiDialogService.IsProgressOpen(ticket))
            {
                // 票据已结束，或同一张进度窗还开着：关掉这张重复的，不要走 OnCancel。
                if (Root != null)
                    Root.schedule.Execute(Dismiss);
                return;
            }

            _card = new NoticeCardPresenter();
            _card.Bind(Root);
            _card.Apply(new NoticeCardContent
            {
                Title = "场景资源加载中...",
                ShowBody = false,
                Icon = NoticeCardIcon.Downloading,
                PrimaryLabel = "我知道了",
                ShowSecondary = false,
                ShowProgress = true,
            });

            OnClick("primary", () =>
            {
                _userData?.OnCancel?.Invoke();
                CloseSelf();
            });

            // Attach 内部若发现票已退役会 Dismiss（Close 早于异步 OnOpen）。
            // 传输完成但百分比还没套上时不会退役，这里先套上进度再等上滑结束。
            UiDialogService.AttachProgressForm(this, ticket);
            if (UiDialogService.IsProgressRetired(ticket))
                return;
        }

        /// <summary>
        /// 传输已完成。等百分比留在屏幕上（上滑结束，至少一帧）再关。
        /// 可重复调用：刚套上进度时会按新的等待时间重排。
        /// </summary>
        public void ArmTransferCompleteClose()
        {
            int ticket = _userData != null ? _userData.Ticket : 0;
            CancelHeldCloseSchedule();
            // 没有视觉树时没法排程；仍交给 service 退役（已套进度则关，否则丢票）。
            if (Root == null)
            {
                UiDialogService.CompleteHeldDownloadClose(ticket);
                return;
            }

            _transferCloseArmed = true;
            float wait = Mathf.Max(MinVisibleSeconds, OpenSlideRemaining);
            _heldCloseSchedule = Root.schedule.Execute(() => OnTransferCloseDue(ticket))
                .StartingIn((long)(wait * 1000f));
        }

        public void Dismiss()
        {
            CloseSelfImmediate();
        }

        /// <summary>
        /// percent 为 0–100。体积未知时 downloadedMb 与 totalMb 传 0。
        /// </summary>
        public void SetProgress(float percent, float downloadedMb = 0f, float totalMb = 0f)
        {
            if (_card == null)
                return;

            _card.SetProgress(percent, downloadedMb, totalMb);
            _progressPresented = true;
            _presentedFrame = Time.frameCount;
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            CancelHeldCloseSchedule();
            UiDialogService.DetachProgressForm(this);
            _card?.Unbind();
            _card = null;
            _userData = null;
            base.OnClose(isShutdown, userData);
        }

        void OnTransferCloseDue(int ticket)
        {
            _transferCloseArmed = false;
            _heldCloseSchedule = null;
            UiDialogService.CompleteHeldDownloadClose(ticket);
        }

        void CancelHeldCloseSchedule()
        {
            _heldCloseSchedule?.Pause();
            _heldCloseSchedule = null;
            _transferCloseArmed = false;
        }
    }
}
