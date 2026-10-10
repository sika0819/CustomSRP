using UnityEngine;

namespace Game
{
    [DefaultExecutionOrder(-10000)]
    public partial class GameEntry : MonoBehaviour
    {
        void Start()
        {
            InitBuiltinComponents();
#if UNITY_EDITOR
            if (Base != null)
                Base.EditorResourceMode = true;
#endif
            InitCustomComponents();
            EnsureUiGroups();
        }

        static void EnsureUiGroups()
        {
            if (UI == null)
                return;

            EnsureGroup("Loading", 0);
            EnsureGroup("Main", 1);
            EnsureGroup("Panel", 2);
            EnsureGroup("Dialog", 3);
        }

        static void EnsureGroup(string name, int depth)
        {
            if (!UI.HasUIGroup(name))
                UI.AddUIGroup(name, depth);
        }
    }
}
