using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Editor
{
    /// <summary>
    /// 不进 Play，用申请下载那张 UXML 在编辑器窗口里看白底和居中。
    /// </summary>
    public sealed class RequestDownloadPreviewWindow : EditorWindow
    {
        const string TreePath = "Assets/GameMain/UI/Forms/Download/RequestDownloadForm.uxml";
        const float DesignWidth = 1125f;
        const float DesignHeight = 2436f;

        VisualElement _page;

        [MenuItem("GameMain/Boluo/预览申请下载弹窗")]
        public static void Open()
        {
            var win = GetWindow<RequestDownloadPreviewWindow>(false, "申请下载", true);
            win.minSize = new Vector2(390, 700);
            win.Show();
            win.Focus();
        }

        void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.style.flexGrow = 1;
            root.style.backgroundColor = new Color(0.10f, 0.14f, 0.12f, 1f);
            root.style.overflow = Overflow.Hidden;

            _page = new VisualElement();
            _page.style.position = Position.Absolute;
            _page.style.left = 0;
            _page.style.top = 0;
            _page.style.width = DesignWidth;
            _page.style.height = DesignHeight;
            _page.style.transformOrigin = new TransformOrigin(Length.Percent(0), Length.Percent(0));
            root.Add(_page);

            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(TreePath);
            if (tree == null)
            {
                root.Add(new Label("找不到 " + TreePath));
                return;
            }

            TemplateContainer instance = tree.Instantiate();
            instance.style.position = Position.Absolute;
            instance.style.left = 0;
            instance.style.right = 0;
            instance.style.top = 0;
            instance.style.bottom = 0;
            _page.Add(instance);

            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Request("128");
            var presenter = new NoticeCardPresenter();
            presenter.Bind(instance);
            presenter.Apply(new NoticeCardContent
            {
                Title = copy.Title,
                Body = copy.Body,
                Icon = NoticeCardIcon.NeedDownload,
                PrimaryLabel = copy.PrimaryLabel,
                SecondaryLabel = copy.SecondaryLabel,
                ShowSecondary = copy.ShowSecondary,
            });

            root.RegisterCallback<GeometryChangedEvent>(_ => Fit());
            root.schedule.Execute(Fit).StartingIn(0);
        }

        void Fit()
        {
            if (_page == null)
                return;
            Rect rect = rootVisualElement.contentRect;
            if (rect.width < 1f || rect.height < 1f)
                return;

            float scale = Mathf.Min(rect.width / DesignWidth, rect.height / DesignHeight);
            _page.style.scale = new Scale(new Vector3(scale, scale, 1f));
            _page.style.left = (rect.width - DesignWidth * scale) * 0.5f;
            _page.style.top = (rect.height - DesignHeight * scale) * 0.5f;
        }
    }

    static class RequestDownloadPreviewBoot
    {
        static string TriggerPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/pop-request-download.trigger"));

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            EditorApplication.update -= Tick;
            if (!File.Exists(TriggerPath))
                return;
            File.Delete(TriggerPath);
            RequestDownloadPreviewWindow.Open();
        }
    }
}
