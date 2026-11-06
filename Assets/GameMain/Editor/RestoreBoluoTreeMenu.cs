using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>
    /// 用 boluo-main 的 boluo1.fbx + 菠萝贴图重建预制体，
    /// 并按目标世界高度缩放写入 AllMapItem。
    /// MapItem 会覆盖实例根 localScale，故预制体根必须为 (1,1,1)。
    /// </summary>
    public static class RestoreBoluoTreeMenu
    {
        const string ModelPath = "Assets/GameMain/Environment/Models/JianZhu/boluo1.fbx";
        const string PrefabPath = "Assets/GameMain/Environment/Prefabs/DaoYu1/JianZhu/boluo1.prefab";
        const string MatPath = "Assets/GameMain/Environment/Materials/boluo1.mat";
        const string TrunkMatPath = "Assets/GameMain/Environment/Materials/Shugan_02.mat";
        const string PropMatPath = "Assets/GameMain/Environment/Materials/Zawu_01.mat";
        const string BaseColorPath = "Assets/GameMain/Environment/Textures/Boluo1/boluo1_BaseColor.png";
        const string NormalPath = "Assets/GameMain/Environment/Textures/Boluo1/boluo1_Normal.png";
        const string OilNprShaderName = "CustomSRP/OilNPR";
        const string FlagRelPath = "Temp/restore_boluo_tree.flag";

        // 放置点：XZ 沿用 DaoYu；Y=108.27 落在底下大石头上（纯地形约 106.27）
        static readonly Vector3 DaoYuPos = new Vector3(286.08f, 108.27f, 743.13f);
        static readonly Vector3 DaoYuEuler = new Vector3(0f, 103.186f, 0f);

        // 旧 boluo1 @ MapItem scale 0.157 的世界高度
        const float TargetWorldHeight = 13.914f;

        [InitializeOnLoadMethod]
        static void WatchFlag()
        {
            EditorApplication.delayCall += TryConsumeFlag;
            EditorApplication.update -= PollFlag;
            EditorApplication.update += PollFlag;
        }

        static double _nextPoll;

        static void PollFlag()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll)
                return;
            _nextPoll = EditorApplication.timeSinceStartup + 1.5;
            TryConsumeFlag();
        }

        static void TryConsumeFlag()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            string flag = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", FlagRelPath));
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { /* ignore */ }

            Restore();
        }

        [MenuItem("GameMain/Boluo/Restore Boluo Tree (boluo1)", false, 130)]
        public static void Restore()
        {
            if (!EnsureMaterial(out Material mat))
                return;
            if (!EnsurePrefab(mat, out float mapScale))
                return;
            Vector3 pos = SnapPosToTerrain(DaoYuPos, mapScale);
            if (!EnsureMapItem(pos, mapScale))
                return;
            Log.Info("[BoluoTree] restored boluo1 prefab + mapScale=" + mapScale +
                     " @ " + pos + " targetH=" + TargetWorldHeight);
        }

        /// <summary>
        /// 以预制体根 scale=1 量底部，MapItem 覆盖 localScale 后把底部落到地形（或 DaoYuPos.y）。
        /// </summary>
        static Vector3 SnapPosToTerrain(Vector3 pos, float mapScale)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
                return pos;

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(DaoYuEuler);
            go.transform.localScale = new Vector3(mapScale, mapScale, mapScale);

            var rends = go.GetComponentsInChildren<Renderer>(true);
            Bounds b = new Bounds();
            bool any = false;
            for (int i = 0; i < rends.Length; i++)
            {
                if (rends[i] == null)
                    continue;
                if (!any)
                {
                    b = rends[i].bounds;
                    any = true;
                }
                else
                    b.Encapsulate(rends[i].bounds);
            }

            float bottom = any ? b.min.y : pos.y;
            // DaoYuPos.y 是石头面等人工抬高；地形采样只作下限兜底，不能把树压回土里。
            float targetBottom = pos.y;
            Terrain[] terrains = Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude);
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null || t.terrainData == null)
                    continue;
                Vector3 tp = t.transform.position;
                Vector3 size = t.terrainData.size;
                if (pos.x < tp.x || pos.x > tp.x + size.x || pos.z < tp.z || pos.z > tp.z + size.z)
                    continue;
                float terrainY = t.SampleHeight(pos) + tp.y;
                if (terrainY > targetBottom)
                    targetBottom = terrainY;
                break;
            }

            Object.DestroyImmediate(go);
            pos.y += targetBottom - bottom;
            return pos;
        }

        static bool EnsureMaterial(out Material mat)
        {
            mat = null;
            var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>(BaseColorPath);
            if (baseMap == null)
            {
                Log.Error("[BoluoTree] Missing base color " + BaseColorPath);
                return false;
            }

            ConfigureTextureImporter(BaseColorPath, sRGB: true, normalMap: false);
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(NormalPath) != null)
                ConfigureTextureImporter(NormalPath, sRGB: false, normalMap: true);

            var shader = Shader.Find(OilNprShaderName);
            if (shader == null)
                shader = AssetDatabase.LoadAssetAtPath<Shader>(
                    "Assets/CustomSRP/Shaders/OilNPR.shader");
            if (shader == null)
            {
                Log.Error("[BoluoTree] Shader not found: " + OilNprShaderName);
                return false;
            }

            mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MatPath);
            }
            else
            {
                mat.shader = shader;
            }

            mat.SetTexture("_BaseMap", baseMap);
            mat.SetTexture("_MainTex", baseMap);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_UseBaseMap", 1f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.25f);
            mat.SetFloat("_Clipping", 0f);
            mat.DisableKeyword("_CLIPPING");
            mat.SetFloat("_ReceiveShadows", 1f);
            mat.EnableKeyword("_RECEIVE_SHADOWS");
            mat.SetFloat("_Shadows", 1f);
            mat.enableInstancing = true;
            mat.doubleSidedGI = true;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            Log.Info("[BoluoTree] material " + MatPath);
            return true;
        }

        static void ConfigureTextureImporter(string assetPath, bool sRGB, bool normalMap)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return;

            bool dirty = false;
            if (importer.sRGBTexture != sRGB)
            {
                importer.sRGBTexture = sRGB;
                dirty = true;
            }

            var want = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (importer.textureType != want)
            {
                importer.textureType = want;
                dirty = true;
            }

            if (importer.maxTextureSize < 2048)
            {
                importer.maxTextureSize = 2048;
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();
        }

        static bool EnsurePrefab(Material mat, out float mapScale)
        {
            mapScale = 1f;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null)
            {
                Log.Error("[BoluoTree] Missing model " + ModelPath);
                return false;
            }

            var modelImporter = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (modelImporter != null)
            {
                bool dirty = false;
                if (modelImporter.materialImportMode != ModelImporterMaterialImportMode.None)
                {
                    modelImporter.materialImportMode = ModelImporterMaterialImportMode.None;
                    dirty = true;
                }
                if (modelImporter.animationType != ModelImporterAnimationType.None)
                {
                    modelImporter.animationType = ModelImporterAnimationType.None;
                    dirty = true;
                }
                // boluo-main 的模型按文件单位导入（globalScale 1）。世界高度由 MapItem 缩放对齐。
                if (modelImporter.globalScale != 1f)
                {
                    modelImporter.globalScale = 1f;
                    dirty = true;
                }
                if (!modelImporter.useFileScale)
                {
                    modelImporter.useFileScale = true;
                    dirty = true;
                }
                if (dirty)
                {
                    modelImporter.SaveAndReimport();
                    model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                }
            }

            if (model == null)
            {
                Log.Error("[BoluoTree] Missing model after reimport " + ModelPath);
                return false;
            }

            string dir = Path.GetDirectoryName(PrefabPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            GameObject instance = Object.Instantiate(model);
            instance.name = "boluo1";
            // MapItem sets instance.transform.localScale = d.Scale (replaces root).
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            ApplyMaterial(instance, mat);

            var rends = instance.GetComponentsInChildren<Renderer>(true);
            Bounds b = new Bounds();
            bool any = false;
            for (int i = 0; i < rends.Length; i++)
            {
                if (rends[i] == null)
                    continue;
                if (!any)
                {
                    b = rends[i].bounds;
                    any = true;
                }
                else
                    b.Encapsulate(rends[i].bounds);
            }

            float unscaledH = any ? Mathf.Max(b.size.y, 1e-6f) : 1f;
            mapScale = TargetWorldHeight / unscaledH;

            PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
            Object.DestroyImmediate(instance);
            AssetDatabase.ImportAsset(PrefabPath);
            Log.Info("[BoluoTree] prefab saved " + PrefabPath +
                     " unscaledH=" + unscaledH + " mapScale=" + mapScale);
            return true;
        }

        /// <summary>
        /// boluo-main DaoYu 上的覆盖：叶 / 主体用菠萝贴图，Cylinder001 树干，Cylinder002 杂物图集。
        /// 合成成单个多子网格时按 FBX 顺序：Buoluoye001、030、Cylinder001、Cylinder002。
        /// </summary>
        static void ApplyMaterial(GameObject root, Material foliage)
        {
            var trunk = AssetDatabase.LoadAssetAtPath<Material>(TrunkMatPath);
            var prop = AssetDatabase.LoadAssetAtPath<Material>(PropMatPath);
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                    continue;

                var mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                string meshName = mesh != null ? mesh.name : string.Empty;
                string key = meshName + " " + r.gameObject.name;
                int subCount = mesh != null ? mesh.subMeshCount : 1;

                var mats = r.sharedMaterials;
                int slotCount = mats != null && mats.Length > 0 ? mats.Length : subCount;
                if (slotCount < 1)
                    slotCount = 1;
                if (mats == null || mats.Length != slotCount)
                    mats = new Material[slotCount];

                bool combined = slotCount >= 4
                    && key.IndexOf("Cylinder", System.StringComparison.OrdinalIgnoreCase) < 0;
                for (int m = 0; m < mats.Length; m++)
                {
                    Material use = foliage;
                    if (combined)
                    {
                        if (m == 2)
                            use = trunk != null ? trunk : foliage;
                        else if (m == 3)
                            use = prop != null ? prop : foliage;
                    }
                    else if (key.IndexOf("Cylinder002", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        use = prop != null ? prop : foliage;
                    else if (key.IndexOf("Cylinder", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        use = trunk != null ? trunk : foliage;
                    mats[m] = use;
                }

                r.sharedMaterials = mats;
                Bounds mb = mesh != null ? mesh.bounds : new Bounds();
                Debug.Log("[BoluoTree] go=" + r.gameObject.name +
                          " mesh=" + meshName +
                          " verts=" + (mesh != null ? mesh.vertexCount : 0) +
                          " sub=" + subCount +
                          " size=" + mb.size +
                          " mat=" + mats[0].name);
            }
        }

        static bool EnsureMapItem(Vector3 pos, float mapScale)
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.AllMapItemAssetPath);
            if (text == null || text.bytes == null)
            {
                Log.Error("[BoluoTree] Missing " + SceneAssets.AllMapItemAssetPath);
                return false;
            }

            var items = new List<MapItemData>(2560);
            if (!MapItemData.ReadBinaryTo(text.bytes, items))
            {
                Log.Error("[BoluoTree] AllMapItem.bytes parse failed");
                return false;
            }

            Vector3 scale = new Vector3(mapScale, mapScale, mapScale);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].PrefabName == "boluo1")
                {
                    items[i].Parent = "JianZhu";
                    items[i].ObjName = "boluo1";
                    items[i].Pos = pos;
                    items[i].Rotation = DaoYuEuler;
                    items[i].Scale = scale;
                    items[i].IsActive = true;
                    WriteAllMapItem(items);
                    Log.Info("[BoluoTree] AllMapItem updated boluo1 pos=" + pos +
                             " scale=" + mapScale);
                    return true;
                }
            }

            int nextIndex = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Index >= nextIndex)
                    nextIndex = items[i].Index + 1;
            }

            items.Add(new MapItemData
            {
                Index = nextIndex,
                Parent = "JianZhu",
                ObjName = "boluo1",
                Pos = pos,
                Rotation = DaoYuEuler,
                Scale = scale,
                IsActive = true,
            });
            WriteAllMapItem(items);
            Log.Info("[BoluoTree] AllMapItem appended boluo1 index=" + nextIndex +
                     " pos=" + pos + " scale=" + mapScale);
            return true;
        }

        static void WriteAllMapItem(List<MapItemData> items)
        {
            string absPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", SceneAssets.AllMapItemAssetPath));
            using (var fs = File.Create(absPath))
                MapItemData.WriteBinary(items, fs);
            AssetDatabase.ImportAsset(SceneAssets.AllMapItemAssetPath);

            string streaming = Path.GetFullPath(
                Path.Combine(Application.dataPath, "StreamingAssets/AllMapItem.bytes"));
            File.Copy(absPath, streaming, true);
            AssetDatabase.ImportAsset("Assets/StreamingAssets/AllMapItem.bytes");
        }
    }
}
