using System;
using System.Reflection;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Runtime bridge to <c>CustomSRP.OilFoliageVolumeFit.ApplyOn</c>.
    /// <c>Game.asmdef</c> cannot reference Assembly-CSharp, so we resolve by name.
    /// </summary>
    public static class EnvStreamerFoliageVolumeFit
    {
        const string TypeName = "CustomSRP.OilFoliageVolumeFit, Assembly-CSharp";

        static Type _fitType;
        static MethodInfo _apply;
        static bool _resolved;

        public static void ApplyOn(GameObject root)
        {
            if (root == null || !Ensure())
                return;

            Component fit = root.GetComponent(_fitType);
            if (fit == null)
                fit = root.AddComponent(_fitType);
            _apply.Invoke(fit, null);
        }

        static bool Ensure()
        {
            if (_resolved)
                return _fitType != null && _apply != null;

            _resolved = true;
            _fitType = Type.GetType(TypeName);
            if (_fitType == null)
                return false;

            _apply = _fitType.GetMethod(
                "Apply",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            return _apply != null;
        }
    }
}
