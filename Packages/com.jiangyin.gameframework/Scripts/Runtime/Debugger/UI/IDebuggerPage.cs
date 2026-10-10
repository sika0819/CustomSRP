using UnityEngine.UIElements;

namespace UnityGameFramework.Runtime
{
    interface IDebuggerPage
    {
        string Title { get; }

        void Initialize();

        void Shutdown();

        void OnEnter();

        void OnLeave();

        void OnUpdate(float elapseSeconds, float realElapseSeconds);

        /// <summary>Build or rebuild the panel content into <paramref name="root"/>.</summary>
        void Build(VisualElement root);

        /// <summary>Refresh live values without rebuilding the tree.</summary>
        void Refresh();
    }
}
