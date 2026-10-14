using UnityEditor;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace CustomSRP.Editor
{
    /// <summary>
    /// 烘焙 Island 山脊和海岸。菜单 CustomSRP → Terrain Detail。
    /// 不改高度图分辨率、alphamap 分辨率、pixel error、LOD、实例化或树草开关。
    /// </summary>
    public class TerrainDetailWindow : EditorWindow
    {
        const string Folder = "Assets/Terrain/Island/Editor";
        const string SettingsPath = Folder + "/IslandTerrainDetailSettings.asset";
        const string SourcePath = Folder + "/IslandTerrainDetail.source.asset";
        const string DefaultTargetPath = "Assets/Terrain/Island/IslandTerrainData.asset";

        TerrainDetailSettings settings;
        SerializedObject serialized;

        /// <summary>打开调参窗口。不需要把组件挂到场景物体上。</summary>
        [MenuItem("CustomSRP/Terrain Detail")]
        public static void Open()
        {
            GetWindow<TerrainDetailWindow>("Terrain Detail");
        }

        void OnEnable()
        {
            settings = EnsureSettings();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "细节写进现有高度图和 SWS_DemoRock，不增加层、不打开 _RidgeAmount、不改 LOD。" +
                "最短山脊波长限制在 4 个高度图像素。若 Scene 视图里地形三角形明显变多，把 frequency 调小或减小 amplitudeMeters。" +
                "海沫若还沿旧岸，选中 Sea，执行 Rebuild Ocean Mesh。",
                MessageType.Info);

            settings = EnsureSettings();
            if (serialized == null || serialized.targetObject != settings)
            {
                serialized = new SerializedObject(settings);
            }

            serialized.Update();
            DrawTarget();
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(settings.target == null))
            {
                if (GUILayout.Button("Capture Source"))
                {
                    Capture();
                }
            }

            using (new EditorGUI.DisabledScope(settings.target == null || settings.source == null))
            {
                if (GUILayout.Button("Apply"))
                {
                    Apply();
                }

                if (GUILayout.Button("Restore"))
                {
                    Restore();
                }
            }

            using (new EditorGUI.DisabledScope(settings.target == null))
            {
                if (GUILayout.Button("Fix Beach Sand Only"))
                {
                    FixSandRockSplat();
                }
            }

            EditorGUILayout.Space();
            DrawSettings(serialized);
            serialized.ApplyModifiedProperties();
            DrawWavelength();
        }

        /// <summary>
        /// 画出山脊、海岸、岩石参数。目标地形和快照在上面单独画，这里跳过。
        /// </summary>
        /// <param name="serializedSettings">参数资产的序列化对象。</param>
        static void DrawSettings(SerializedObject serializedSettings)
        {
            SerializedProperty property = serializedSettings.GetIterator();
            property.NextVisible(true);
            while (property.NextVisible(false))
            {
                if (property.name == "target" || property.name == "source")
                {
                    continue;
                }

                EditorGUILayout.PropertyField(property, true);
            }
        }

        /// <summary>
        /// 显示要写回的 TerrainData 和快照。快照只读，避免手改之后和 Apply 结果对不上。
        /// </summary>
        void DrawTarget()
        {
            EditorGUI.BeginChangeCheck();
            var target = (TerrainData)EditorGUILayout.ObjectField("Target", settings.target, typeof(TerrainData), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(settings, "Terrain Detail Target");
                settings.target = target;
                EditorUtility.SetDirty(settings);
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Source Snapshot", settings.source, typeof(TerrainData), false);
            }
        }

        /// <summary>
        /// 按当前倍频算出最短波长（米）。频率单位是周/米，显示的是夹紧之后的值。
        /// </summary>
        void DrawWavelength()
        {
            TerrainData target = settings.target;
            if (target == null)
            {
                return;
            }

            float texel = TerrainDetailProcessor.MinTexelMeters(target.size, target.heightmapResolution);
            float frequency = TerrainDetailProcessor.ClampFrequency(
                settings.frequency, settings.octaves, settings.lacunarity, texel);
            int octaves = Mathf.Clamp(settings.octaves, 1, 8);
            float lacunarity = Mathf.Clamp(settings.lacunarity, 1.5f, 3.5f);
            float highest = frequency * Mathf.Pow(lacunarity, octaves - 1);
            float shortest = 1f / Mathf.Max(highest, 1e-6f);
            EditorGUILayout.LabelField(
                "Shortest ridge",
                $"{shortest:0.0} m  ({TerrainDetailProcessor.MinWavelengthTexels} texels = {TerrainDetailProcessor.MinWavelengthTexels * texel:0.0} m)");
        }

        /// <summary>
        /// 找到或创建参数资产，并默认指向 IslandTerrainData。不覆盖已经调过的参数。
        /// </summary>
        TerrainDetailSettings EnsureSettings()
        {
            if (settings != null)
            {
                return settings;
            }

            var loaded = AssetDatabase.LoadAssetAtPath<TerrainDetailSettings>(SettingsPath);
            if (loaded != null)
            {
                if (loaded.target == null)
                {
                    loaded.target = AssetDatabase.LoadAssetAtPath<TerrainData>(DefaultTargetPath);
                }

                return loaded;
            }

            EnsureFolder();
            var created = CreateInstance<TerrainDetailSettings>();
            created.target = AssetDatabase.LoadAssetAtPath<TerrainData>(DefaultTargetPath);
            created.source = AssetDatabase.LoadAssetAtPath<TerrainData>(SourcePath);
            AssetDatabase.CreateAsset(created, SettingsPath);
            return created;
        }

        /// <summary>
        /// 把当前目标的高度和 splat 复制到 Editor 目录里的快照。
        /// 只改快照自己的分辨率，用来容纳这份拷贝，不会动场景里的地形。
        /// </summary>
        void Capture()
        {
            TerrainData target = settings.target;
            if (target == null)
            {
                Log.Error("[CustomSRP] Terrain detail capture needs a target TerrainData.");
                return;
            }

            if (settings.source != null && !EditorUtility.DisplayDialog(
                    "Capture Source",
                    "Replace the stored height and splat snapshot with the current terrain? Apply always starts from this snapshot.",
                    "Replace",
                    "Cancel"))
            {
                return;
            }

            if (!TerrainDetailProcessor.HasNoNullLayers(target, out string layerError))
            {
                Log.Error("[CustomSRP] " + layerError);
                return;
            }

            EnsureFolder();
            int res = target.heightmapResolution;
            int alphaW = target.alphamapWidth;
            int alphaH = target.alphamapHeight;
            float[,,] alphamaps = target.GetAlphamaps(0, 0, alphaW, alphaH);
            float[,] heights = target.GetHeights(0, 0, res, res);
            if (TerrainDetailProcessor.IsAlphamapDegenerate(alphamaps, out string splatError))
            {
                Log.Error("[CustomSRP] Refusing to capture: " + splatError);
                return;
            }

            TerrainData snapshot = settings.source;
            if (snapshot == null)
            {
                snapshot = AssetDatabase.LoadAssetAtPath<TerrainData>(SourcePath);
            }

            if (snapshot == null)
            {
                // CreateAsset first. Writing heights/splat before CreateAsset gets wiped on import.
                snapshot = new TerrainData { name = "IslandTerrainDetailSource" };
                AssetDatabase.CreateAsset(snapshot, SourcePath);
                snapshot = AssetDatabase.LoadAssetAtPath<TerrainData>(SourcePath);
            }

            snapshot.heightmapResolution = res;
            snapshot.alphamapResolution = target.alphamapResolution;
            snapshot.size = target.size;
            snapshot.terrainLayers = target.terrainLayers;
            snapshot.SetHeights(0, 0, heights);
            snapshot.SetAlphamaps(0, 0, alphamaps);
            if (TerrainDetailProcessor.IsAlphamapDegenerate(
                    snapshot.GetAlphamaps(0, 0, alphaW, alphaH), out splatError))
            {
                Log.Error(
                    "[CustomSRP] Capture wrote a wiped splat map (" + splatError +
                    "). The target was not changed. Try again or check terrain layers.");
                return;
            }

            settings.source = snapshot;
            EditorUtility.SetDirty(snapshot);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Log.Info(
                $"[CustomSRP] Captured terrain detail source {res}x{res} heights, " +
                $"{alphaW}x{alphaH} splat, {target.alphamapLayers} layers → {SourcePath}");
        }

        /// <summary>
        /// 只把海岸岩石拨回沙子。不改内陆草地/岩石，不改高度。
        /// </summary>
        void FixSandRockSplat()
        {
            TerrainData target = settings.target;
            Terrain terrain = FindTerrain(target);
            Budget budget = Budget.Read(terrain, target);
            if (!TerrainDetailProcessor.TryFixSandRockSplat(settings, target, out string error))
            {
                Log.Error("[CustomSRP] " + error);
                return;
            }

            budget.Restore(terrain, target);
            if (terrain != null)
            {
                terrain.Flush();
            }

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
            SceneView.RepaintAll();
            Log.Info("[CustomSRP] Beach sand restored. Inland grass/rock splat left unchanged.");
        }

        /// <summary>
        /// 从快照烘焙并写回。写回前由处理器登记 Undo。之后把 LOD 相关字段设回原值。
        /// </summary>
        void Apply()
        {
            TerrainData target = settings.target;
            Terrain terrain = FindTerrain(target);
            Budget budget = Budget.Read(terrain, target);
            if (!TerrainDetailProcessor.TryApply(settings, target, out string error))
            {
                Log.Error("[CustomSRP] " + error);
                return;
            }

            budget.Restore(terrain, target);
            if (terrain != null)
            {
                WarnIfRidgeShaderIsOn(terrain);
                terrain.Flush();
            }

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
            SceneView.RepaintAll();
            Log.Info(
                "[CustomSRP] Applied terrain detail from the source snapshot. " +
                "Heightmap resolution, alphamap resolution, and terrain LOD settings were left as they were.");
        }

        /// <summary>
        /// 把快照的高度和 splat 抄回目标。不改分辨率。可以用 Ctrl+Z 撤回。
        /// </summary>
        void Restore()
        {
            TerrainData target = settings.target;
            TerrainData source = settings.source;
            if (target == null || source == null)
            {
                Log.Error("[CustomSRP] Restore needs both a target and a source snapshot.");
                return;
            }

            if (source.heightmapResolution != target.heightmapResolution ||
                source.alphamapWidth != target.alphamapWidth ||
                source.alphamapHeight != target.alphamapHeight)
            {
                Log.Error("[CustomSRP] Source and target resolutions differ. Capture again before restoring.");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Restore Terrain",
                    "Copy the snapshot heights and splat back onto the target terrain?",
                    "Restore",
                    "Cancel"))
            {
                return;
            }

            Terrain terrain = FindTerrain(target);
            Budget budget = Budget.Read(terrain, target);
            int res = source.heightmapResolution;
            Undo.RegisterCompleteObjectUndo(target, "Restore Terrain Detail");
            target.SetHeights(0, 0, source.GetHeights(0, 0, res, res));
            target.SetAlphamaps(0, 0, source.GetAlphamaps(0, 0, source.alphamapWidth, source.alphamapHeight));
            budget.Restore(terrain, target);
            if (terrain != null)
            {
                terrain.Flush();
            }

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
            SceneView.RepaintAll();
            Log.Info("[CustomSRP] Restored terrain heights and splat from the source snapshot.");
        }

        /// <summary>
        /// 岩石已经画在 SWS_DemoRock 上。_RidgeAmount 大于 0 会让油画地形再采一张岩石贴图。
        /// 这里只警告，不改材质。
        /// </summary>
        /// <param name="terrain">场景里引用目标 TerrainData 的 Terrain。</param>
        static void WarnIfRidgeShaderIsOn(Terrain terrain)
        {
            Material material = terrain.materialTemplate;
            if (material == null || !material.HasProperty("_RidgeAmount"))
            {
                return;
            }

            if (material.GetFloat("_RidgeAmount") > 0.001f)
            {
                Log.Warning(
                    "[CustomSRP] Terrain material _RidgeAmount is above 0. Leave it at 0 so ridge rock stays in the SWS_DemoRock alphamap and the shader does not take an extra texture sample.");
            }
        }

        /// <summary>
        /// 场景里正在画这块 TerrainData 的 Terrain。没打开场景时返回空，资产仍然会写上。
        /// </summary>
        /// <param name="data">目标 TerrainData。</param>
        static Terrain FindTerrain(TerrainData data)
        {
            if (data == null)
            {
                return null;
            }

            var terrains = Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude);
            for (int i = 0; i < terrains.Length; i++)
            {
                if (terrains[i].terrainData == data)
                {
                    return terrains[i];
                }
            }

            return null;
        }

        static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets/Terrain/Island", "Editor");
            }
        }

        /// <summary>
        /// Apply 前后要保持不变的移动端预算。只在值被意外改掉时写回。
        /// </summary>
        struct Budget
        {
            public bool hasTerrain;
            public float pixelError;
            public int minLod;
            public float basemapDistance;
            public bool drawInstanced;
            public bool drawTreesAndFoliage;
            public int heightRes;
            public int alphaRes;

            /// <summary>
            /// 记下当前 LOD 和分辨率。heightRes / alphaRes 来自 TerrainData，其余来自 Terrain 组件。
            /// </summary>
            /// <param name="terrain">场景组件，可以是空。</param>
            /// <param name="data">目标 TerrainData。</param>
            public static Budget Read(Terrain terrain, TerrainData data)
            {
                var budget = new Budget
                {
                    heightRes = data.heightmapResolution,
                    alphaRes = data.alphamapResolution,
                };
                if (terrain == null)
                {
                    return budget;
                }

                budget.hasTerrain = true;
                budget.pixelError = terrain.heightmapPixelError;
                budget.minLod = terrain.heightmapMinimumLODSimplification;
                budget.basemapDistance = terrain.basemapDistance;
                budget.drawInstanced = terrain.drawInstanced;
                budget.drawTreesAndFoliage = terrain.drawTreesAndFoliage;
                return budget;
            }

            /// <summary>
            /// 若 SetHeights 碰过这些字段，设回 Read 时的值。分辨率不允许在这里重设，只报告。
            /// </summary>
            /// <param name="terrain">场景组件，可以是空。</param>
            /// <param name="data">刚写过的 TerrainData。</param>
            public void Restore(Terrain terrain, TerrainData data)
            {
                if (data.heightmapResolution != heightRes || data.alphamapResolution != alphaRes)
                {
                    Log.Error(
                        "[CustomSRP] Terrain detail changed heightmap or alphamap resolution. " +
                        "That increases mobile mesh cost and is not part of this tool.");
                }

                if (!hasTerrain || terrain == null)
                {
                    return;
                }

                if (!Mathf.Approximately(terrain.heightmapPixelError, pixelError))
                {
                    terrain.heightmapPixelError = pixelError;
                }

                if (terrain.heightmapMinimumLODSimplification != minLod)
                {
                    terrain.heightmapMinimumLODSimplification = minLod;
                }

                if (!Mathf.Approximately(terrain.basemapDistance, basemapDistance))
                {
                    terrain.basemapDistance = basemapDistance;
                }

                if (terrain.drawInstanced != drawInstanced)
                {
                    terrain.drawInstanced = drawInstanced;
                }

                if (terrain.drawTreesAndFoliage != drawTreesAndFoliage)
                {
                    terrain.drawTreesAndFoliage = drawTreesAndFoliage;
                }
            }
        }
    }
}
