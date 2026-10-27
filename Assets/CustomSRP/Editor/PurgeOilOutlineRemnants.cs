using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// OilNPR hull outline (_OUTLINE_ON / OilOutline pass) has been removed.
    /// Strip leftover keywords and serialized props from Materials (keep _OutlineBrushMap*).
    /// </summary>
    public static class PurgeOilOutlineRemnants
    {
        static readonly string[] FloatProps =
        {
            "_Outline",
            "_OutlineBreak",
            "_OutlineNoise",
            "_OutlineNoiseScale",
            "_OutlineWidth",
            "_OutlineWobble",
            "_OutlineBrushScale"
        };

        [MenuItem("Window/CustomSRP/Purge Oil Outline Remnants")]
        public static void RunFromMenu()
        {
            int n = PurgeAll();
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog(
                "Purge Oil Outline",
                $"已清理 {n} 个材质的 _OUTLINE_ON / outline 残留属性。",
                "OK");
        }

        public static int PurgeAll()
        {
            string[] guids = AssetDatabase.FindAssets("t:Material");
            int updated = 0;
            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/"))
                        continue;

                    EditorUtility.DisplayProgressBar(
                        "Purge Oil Outline",
                        path,
                        (float)i / guids.Length);

                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat == null)
                        continue;

                    if (PurgeMaterial(mat))
                    {
                        EditorUtility.SetDirty(mat);
                        updated++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return updated;
        }

        static bool PurgeMaterial(Material mat)
        {
            var so = new SerializedObject(mat);
            bool dirty = false;

            if (mat.IsKeywordEnabled("_OUTLINE_ON"))
            {
                mat.DisableKeyword("_OUTLINE_ON");
                dirty = true;
            }

            dirty |= RemoveKeywordEntries(so.FindProperty("m_InvalidKeywords"));
            dirty |= RemoveKeywordEntries(so.FindProperty("m_ValidKeywords"));

            var floats = so.FindProperty("m_SavedProperties.m_Floats");
            if (floats != null && floats.isArray)
            {
                for (int i = floats.arraySize - 1; i >= 0; i--)
                {
                    string name = floats.GetArrayElementAtIndex(i)
                        .FindPropertyRelative("first").stringValue;
                    if (IsOutlineFloat(name))
                    {
                        floats.DeleteArrayElementAtIndex(i);
                        dirty = true;
                    }
                }
            }

            var colors = so.FindProperty("m_SavedProperties.m_Colors");
            if (colors != null && colors.isArray)
            {
                for (int i = colors.arraySize - 1; i >= 0; i--)
                {
                    string name = colors.GetArrayElementAtIndex(i)
                        .FindPropertyRelative("first").stringValue;
                    if (name == "_OutlineColor")
                    {
                        colors.DeleteArrayElementAtIndex(i);
                        dirty = true;
                    }
                }
            }

            if (dirty)
                so.ApplyModifiedPropertiesWithoutUndo();
            return dirty;
        }

        static bool IsOutlineFloat(string name)
        {
            for (int i = 0; i < FloatProps.Length; i++)
            {
                if (FloatProps[i] == name)
                    return true;
            }

            return false;
        }

        static bool RemoveKeywordEntries(SerializedProperty list)
        {
            if (list == null || !list.isArray)
                return false;
            bool removed = false;
            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                if (list.GetArrayElementAtIndex(i).stringValue == "_OUTLINE_ON")
                {
                    list.DeleteArrayElementAtIndex(i);
                    removed = true;
                }
            }

            return removed;
        }
    }
}
