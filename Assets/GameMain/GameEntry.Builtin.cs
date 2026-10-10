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

        static void InitBuiltinComponents()
        {
            Base = UnityGameFramework.Runtime.GameEntry.GetComponent<BaseComponent>();
            Procedure = UnityGameFramework.Runtime.GameEntry.GetComponent<ProcedureComponent>();
            Event = UnityGameFramework.Runtime.GameEntry.GetComponent<EventComponent>();
            Resource = UnityGameFramework.Runtime.GameEntry.GetComponent<ResourceComponent>();
            UI = UnityGameFramework.Runtime.GameEntry.GetComponent<UIComponent>();
        }
    }
}
