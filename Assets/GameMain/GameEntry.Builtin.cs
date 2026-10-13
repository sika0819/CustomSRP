using UnityGameFramework.Runtime;

namespace Game
{
    public partial class GameEntry
    {
        public static BaseComponent Base { get; private set; }
        public static ProcedureComponent Procedure { get; private set; }
        public static EventComponent Event { get; private set; }
        public static ResourceComponent Resource { get; private set; }
        public static UIComponent UI { get; private set; }
        public static SceneComponent Scene { get; private set; }
        public static EntityComponent Entity { get; private set; }
        public static SettingComponent Setting { get; private set; }

        static void InitBuiltinComponents()
        {
            Base = UnityGameFramework.Runtime.GameEntry.GetComponent<BaseComponent>();
            Procedure = UnityGameFramework.Runtime.GameEntry.GetComponent<ProcedureComponent>();
            Event = UnityGameFramework.Runtime.GameEntry.GetComponent<EventComponent>();
            Resource = UnityGameFramework.Runtime.GameEntry.GetComponent<ResourceComponent>();
            UI = UnityGameFramework.Runtime.GameEntry.GetComponent<UIComponent>();
            Scene = UnityGameFramework.Runtime.GameEntry.GetComponent<SceneComponent>();
            Entity = UnityGameFramework.Runtime.GameEntry.GetComponent<EntityComponent>();
            Setting = UnityGameFramework.Runtime.GameEntry.GetComponent<SettingComponent>();
        }

        static void ClearBuiltinComponents()
        {
            Base = null;
            Procedure = null;
            Event = null;
            Resource = null;
            UI = null;
            Scene = null;
            Entity = null;
            Setting = null;
        }
    }
}
