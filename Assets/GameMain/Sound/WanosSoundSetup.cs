using System.Reflection;
using GameFramework;
using GameFramework.Sound;
using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// Configures SoundComponent helpers/groups and Sound IResourceManager before first PlaySound.
    /// Call from GameEntry.Awake (order 100) so it runs after SoundComponent.Awake and before its Start.
    /// </summary>
    public static class WanosSoundSetup
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        static bool _helpersConfigured;
        static bool _groupsReady;
        static readonly WanosSoundResourceManager SoundResources = new WanosSoundResourceManager();

        public static void ConfigureHelpersBeforeSoundStart()
        {
            if (_helpersConfigured)
                return;

            SoundComponent sound =
                UnityGameFramework.Runtime.GameEntry.GetComponent<SoundComponent>();
            if (sound == null)
                return;

            // FullName is enough for GameFramework Utility.Assembly.GetType (scans loaded assemblies).
            SetPrivate(sound, "m_SoundHelperTypeName", typeof(WanosSoundHelper).FullName);
            SetPrivate(sound, "m_SoundAgentHelperTypeName", typeof(WanosSoundAgentHelper).FullName);
            _helpersConfigured = true;
        }

        public static void BindAfterBuiltinInit()
        {
            WanosBankStore.SetPaths(WanosBankPaths.Instance);

            ISoundManager manager = GameFrameworkEntry.GetModule<ISoundManager>();
            if (manager != null)
                manager.SetResourceManager(SoundResources);

            EnsureSoundGroups();
        }

        public static void EnsureSoundGroups()
        {
            if (_groupsReady || GameEntry.Sound == null)
                return;

            EnsureGroup("Music", agentCount: 2);
            EnsureGroup("Sound", agentCount: 4);
            EnsureGroup("Ambient", agentCount: 4);
            _groupsReady = true;
        }

        public static void ResetSession()
        {
            _groupsReady = false;
            WanosEngine.ResetSession();
        }

        static void EnsureGroup(string name, int agentCount)
        {
            if (GameEntry.Sound.HasSoundGroup(name))
                return;
            GameEntry.Sound.AddSoundGroup(name, false, false, 1f, agentCount);
        }

        static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, Flags);
            if (field != null)
                field.SetValue(target, value);
        }
    }
}
