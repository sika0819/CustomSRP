namespace Game
{
    public static class SceneAssets
    {
        public const string Launch = "Assets/GameMain/Scenes/Launch.unity";

        /// <summary>YooAsset location (AddressByFileName under AssetRaw/Island).</summary>
        public const string IslandTerrainLocation = "IslandTerrain";
        public const string IslandTerrainPrefabPath = "Assets/AssetRaw/Island/IslandTerrain.prefab";

        /// <summary>YooAsset location (AddressByFileName under AssetRaw/Ocean).</summary>
        public const string IslandOceanLocation = "IslandOcean";
        public const string IslandOceanPrefabPath = "Assets/AssetRaw/Ocean/IslandOcean.prefab";

        /// <summary>Boluo DaoYu rivers + waterfall FX (AddressByFileName under AssetRaw/Island).</summary>
        public const string IslandInlandWaterLocation = "IslandInlandWater";
        public const string IslandInlandWaterPrefabPath = "Assets/AssetRaw/Island/IslandInlandWater.prefab";

        /// <summary>YooAsset location (AddressByFileName under AssetRaw/Configs).</summary>
        public const string AllMapItemLocation = "AllMapItem";
        public const string AllMapItemAssetPath = "Assets/AssetRaw/Configs/AllMapItem.bytes";

        /// <summary>Default island spawn pose (AddressByFileName under AssetRaw/Configs).</summary>
        public const string IslandSpawnPoseLocation = "IslandSpawnPose";
        public const string IslandSpawnPoseAssetPath =
            "Assets/AssetRaw/Configs/IslandSpawnPose.asset";

        /// <summary>GPU 草地种点 sidecar（SGB1，与 AllMapItem 同 Configs 采集）。</summary>
        public const string IslandGrassBakeLocation = "Island_GrassBake";
        public const string IslandGrassBakeAssetPath =
            "Assets/AssetRaw/Configs/Island_GrassBake.bytes";

        /// <summary>篝火 / FX / inland pieces 位姿（BLM1，与 AllMapItem 同目录）。</summary>
        public const string MapItemExtrasLocation = "MapItemExtras";
        public const string MapItemExtrasAssetPath = "Assets/AssetRaw/Configs/MapItemExtras.bytes";

        /// <summary>分类空节点层级（AddressByFileName under AssetRaw/Island）。</summary>
        public const string MapItemLocation = "MapItem";
        public const string MapItemPrefabPath = "Assets/AssetRaw/Island/MapItem.prefab";

        public const string DaoYu1PrefabRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1/";

        /// <summary>Boluo Arts/FX 氛围预制体（AddressByFileName → FX_SunBeam_01）。</summary>
        public const string IslandFxRoot = "Assets/GameMain/Environment/FX/";

        /// <summary>UnityFire 噪声火焰材质（AddressByFileName under Environment/FX/UnityFire）。</summary>
        public const string CampfireNoiseFireLocation = "Campfire_NoiseFire";
        public const string CampfireNoiseFireMatPath =
            "Assets/GameMain/Environment/FX/UnityFire/Materials/Campfire_NoiseFire.mat";

        /// <summary>
        /// Yoo address：与 Collector <c>AddressByFolderAndFileName</c> 对齐
        /// （例：Lvshu_Maple 1；根目录预制体用文件名）。
        /// </summary>
        public static string DaoYu1Location(string parent, string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return string.Empty;
            if (string.IsNullOrEmpty(parent) || parent == "null")
                return prefabName;
            return parent + "_" + prefabName;
        }

        public static string DaoYu1EditorPath(string parent, string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return null;
            if (string.IsNullOrEmpty(parent) || parent == "null")
                return DaoYu1PrefabRoot + prefabName + ".prefab";
            return DaoYu1PrefabRoot + parent + "/" + prefabName + ".prefab";
        }

        public static string FxLocation(string prefabName) =>
            string.IsNullOrEmpty(prefabName) ? string.Empty : prefabName;

        public static string FxEditorPath(string prefabName) =>
            string.IsNullOrEmpty(prefabName) ? null : IslandFxRoot + prefabName + ".prefab";
    }
}
