using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Resolves Terrain Inspector warnings: detail density cap (GPU instancing) and mixed detail texture formats.
    /// </summary>
    public static class TerrainDetailFixUtility
    {
        const string DetailBillboardPath = "Assets/Terrain/DetailBillboard.png";

        [MenuItem("CustomSRP/Fix Terrain Detail Warnings")]
        public static void FixFromSelectionOrScene()
        {
            var terrains = CollectTerrainsFromSelection();
            if (terrains.Count == 0)
            {
                Debug.LogWarning(
                    "[CustomSRP] No Terrain selected. Fixing all TerrainData assets in the project.");
                FixAllTerrainDataInProject();
                return;
            }

            foreach (var terrain in terrains)
                FixTerrain(terrain);

            AssetDatabase.SaveAssets();
            Debug.Log($"[CustomSRP] Fixed {terrains.Count} terrain(s). Re-select the Terrain to refresh warnings.");
        }

        public static void FixAllTerrainDataInProject()
        {
            EnsureDetailBillboardTexture();
            var billboard = AssetDatabase.LoadAssetAtPath<Texture2D>(DetailBillboardPath);
            var touchedData = new HashSet<TerrainData>();

            foreach (var guid in AssetDatabase.FindAssets("t:TerrainData"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
                if (data == null || !touchedData.Add(data))
                    continue;

                FixTerrainData(data, billboard);
                EditorUtility.SetDirty(data);
            }

            foreach (var terrain in Object.FindObjectsByType<Terrain>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (terrain == null)
                    continue;
                terrain.drawInstanced = true;
                EditorUtility.SetDirty(terrain);
                terrain.Flush();
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[CustomSRP] Terrain detail fix applied to all TerrainData assets.");
        }

        static List<Terrain> CollectTerrainsFromSelection()
        {
            var list = new List<Terrain>();
            foreach (var obj in Selection.objects)
            {
                if (obj is Terrain t)
                    list.Add(t);
                else if (obj is TerrainData data)
                {
                    foreach (var terrain in Object.FindObjectsByType<Terrain>(
                                 FindObjectsInactive.Include, FindObjectsSortMode.None))
                    {
                        if (terrain.terrainData == data)
                            list.Add(terrain);
                    }
                }
            }

            if (list.Count == 0)
            {
                foreach (var terrain in Object.FindObjectsByType<Terrain>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    list.Add(terrain);
            }

            return list;
        }

        static void FixTerrain(Terrain terrain)
        {
            if (terrain == null || terrain.terrainData == null)
                return;

            EnsureDetailBillboardTexture();
            var billboard = AssetDatabase.LoadAssetAtPath<Texture2D>(DetailBillboardPath);
            FixTerrainData(terrain.terrainData, billboard);

            terrain.drawInstanced = true;
            EditorUtility.SetDirty(terrain);
            EditorUtility.SetDirty(terrain.terrainData);
            terrain.Flush();
        }

        static void FixTerrainData(TerrainData data, Texture2D fallbackBillboard)
        {
            if (data == null)
                return;

            var prototypes = data.detailPrototypes;
            if (prototypes == null || prototypes.Length == 0)
                return;

            var textures = new List<Texture2D>();
            var changed = false;

            for (var i = 0; i < prototypes.Length; i++)
            {
                var proto = prototypes[i];

                if (!proto.useInstancing)
                {
                    proto.useInstancing = true;
                    changed = true;
                }

                if (proto.renderMode == DetailRenderMode.GrassBillboard)
                {
                    var path = proto.prototypeTexture != null
                        ? AssetDatabase.GetAssetPath(proto.prototypeTexture)
                        : null;
                    if (string.IsNullOrEmpty(path) && fallbackBillboard != null)
                    {
                        proto.prototypeTexture = fallbackBillboard;
                        changed = true;
                    }
                }

                if (proto.prototypeTexture != null)
                    textures.Add(proto.prototypeTexture);

                prototypes[i] = proto;
            }

            if (changed)
                data.detailPrototypes = prototypes;

            UnifyDetailTextureImports(textures);
        }

        static void EnsureDetailBillboardTexture()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Terrain"))
                AssetDatabase.CreateFolder("Assets", "Terrain");

            if (File.Exists(Path.Combine(Directory.GetCurrentDirectory(), DetailBillboardPath)))
            {
                UnifyDetailTextureImports(new[]
                {
                    AssetDatabase.LoadAssetAtPath<Texture2D>(DetailBillboardPath)
                });
                return;
            }

            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false);
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var n = Hash01(x, y) * 0.35f + Hash01(x * 3 + 17, y * 5 + 31) * 0.25f;
                    var g = (byte)Mathf.Clamp(70 + n * 110f, 0f, 255f);
                    var a = (byte)Mathf.Clamp(180 + n * 75f, 0f, 255f);
                    pixels[y * size + x] = new Color32(40, g, 35, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(DetailBillboardPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(DetailBillboardPath, ImportAssetOptions.ForceUpdate);
            UnifyDetailTextureImports(new[]
            {
                AssetDatabase.LoadAssetAtPath<Texture2D>(DetailBillboardPath)
            });
        }

        static float Hash01(int x, int y)
        {
            var h = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            return h - Mathf.Floor(h);
        }

        static void UnifyDetailTextureImports(IEnumerable<Texture2D> textures)
        {
            var seen = new HashSet<string>();
            foreach (var texture in textures)
            {
                if (texture == null)
                    continue;
                var path = AssetDatabase.GetAssetPath(texture);
                if (string.IsNullOrEmpty(path) || !seen.Add(path))
                    continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                    continue;

                importer.textureType = TextureImporterType.Default;
                importer.textureShape = TextureImporterShape.Texture2D;
                importer.sRGBTexture = true;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.npotScale = TextureImporterNPOTScale.ToNearest;

                ApplyPlatform(importer, "Standalone", TextureImporterFormat.DXT5);
                ApplyPlatform(importer, "Android", TextureImporterFormat.ASTC_6x6);
                ApplyPlatform(importer, "iPhone", TextureImporterFormat.ASTC_6x6);

                var defaults = importer.GetDefaultPlatformTextureSettings();
                defaults.textureCompression = TextureImporterCompression.Compressed;
                defaults.format = TextureImporterFormat.Automatic;
                importer.SetPlatformTextureSettings(defaults);

                importer.SaveAndReimport();
            }
        }

        static void ApplyPlatform(
            TextureImporter importer,
            string platform,
            TextureImporterFormat format)
        {
            var settings = importer.GetPlatformTextureSettings(platform);
            settings.overridden = true;
            settings.maxTextureSize = Mathf.Max(settings.maxTextureSize, 512);
            settings.resizeAlgorithm = TextureResizeAlgorithm.Mitchell;
            settings.textureCompression = TextureImporterCompression.Compressed;
            settings.format = format;
            importer.SetPlatformTextureSettings(settings);
        }
    }
}
