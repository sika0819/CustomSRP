namespace Game
{
    public class DialogForm : GameFormLogic
    {
        protected override bool CloseWhenAnyButtonClicked => true;
        protected override bool SlidesUpFromBottom => true;
    }

    public class TimerPauseForm : DialogForm
    {
    }

    public class MobileDataForm : DialogForm
    {
    }

    public class DownloadAskForm : DialogForm
    {
    }

    public class NetworkRequiredForm : DialogForm
    {
    }

    public class DownloadProgressForm : DialogForm
    {
    }

    public class StorageFullForm : DialogForm
    {
    }
}
