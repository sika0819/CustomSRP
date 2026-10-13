using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>
    /// 一键生成 AssetRaw 目录、YooAssetSettings、UpdateSetting、AssetBundleCollectorSetting。
    /// 对齐 BoluoUnity DefaultPackage 分组（按 CustomSRP 现有资源裁剪）。
    /// </summary>
    public static class YooAssetCollectorSetup
    {
        static readonly string[] AssetRawFolders =
        {
            "Assets/AssetRaw/Configs",
            "Assets/AssetRaw/Audios",
            "Assets/AssetRaw/UI",
            "Assets/AssetRaw/Scenes",
            "Assets/AssetRaw/Island",
            "Assets/AssetRaw/Ocean",
            "Assets/AssetRaw/SkyWeather",
            "Assets/AssetRaw/Effects",
            "Assets/AssetRaw/Fonts",
            "Assets/AssetRaw/DLL",
            "Assets/AssetArt/Atlas",
        };

        [MenuItem("Game/YooAsset/Setup AssetRaw + Collector + Settings", false, 50)]
        [MenuItem("Game/YooAsset/Setup AssetRaw + Collector + Settings (Compatibility)", false, 51)]
        public static void SetupAll()
        {
            EnsureFolders();
            EnsurePlaceholderConfigs();
            EnsureYooAssetSettings();
            EnsureUpdateSetting();
            EnsureCollectorSetting();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log.Info("[YooAsset] Setup AssetRaw + Collector + Settings done.");
            Log.Info("[YooAsset] AssetRaw / Collector / Settings ready. Check YooAsset/AssetBundle Collector if needed.");
        }

        static void EnsureFolders()
        {
            foreach (string folder in AssetRawFolders)
            {
                if (AssetDatabase.IsValidFolder(folder))
                    continue;

                string[] parts = folder.Split('/');
                string cur = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = $"{cur}/{parts[i]}";
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(cur, parts[i]);
                    cur = next;
                }
            }
        }

        static void EnsurePlaceholderConfigs()
        {
            const string path = "Assets/AssetRaw/Configs/yoo_boot.json";
            if (File.Exists(path))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "{\n  \"packageName\": \"DefaultPackage\",\n  \"version\": \"0.1.0\",\n  \"yooFolder\": \"package\"\n}\n");
            AssetDatabase.ImportAsset(path);
        }

        static void EnsureYooAssetSettings()
        {
            const string folder = "Assets/GameMain/Settings/Resources";
            EnsureFolderPath(folder);
            const string path = folder + "/YooAssetSettings.asset";
            var existing = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (existing != null)
            {
                var so = new SerializedObject(existing);
                SerializedProperty folderName = so.FindProperty("DefaultYooFolderName");
                if (folderName != null && folderName.stringValue != "package")
                {
                    folderName.stringValue = "package";
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(existing);
                }

                return;
            }

            System.Type settingsType = typeof(YooAsset.YooAssets).Assembly.GetType("YooAsset.YooAssetSettings");
            if (settingsType == null)
            {
                Log.Error("[YooAsset] YooAssetSettings type not found");
                return;
            }

            ScriptableObject asset = ScriptableObject.CreateInstance(settingsType);
            AssetDatabase.CreateAsset(asset, path);
            var serialized = new SerializedObject(asset);
            serialized.FindProperty("DefaultYooFolderName").stringValue = "package";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        static void EnsureUpdateSetting()
        {
            const string folder = "Assets/GameMain/Settings/Resources";
            EnsureFolderPath(folder);
            const string path = folder + "/UpdateSetting.asset";
            if (AssetDatabase.LoadAssetAtPath<UpdateSetting>(path) != null)
                return;

            UpdateSetting setting = ScriptableObject.CreateInstance<UpdateSetting>();
            AssetDatabase.CreateAsset(setting, path);
        }

        static void EnsureCollectorSetting()
        {
            // SettingLoader 要求工程内只有一份 AssetBundleCollectorSetting。
            // 若尚未存在，先落到 Assets/Editor/AssetBundleCollector/。
            string[] guids = AssetDatabase.FindAssets("t:AssetBundleCollectorSetting");
            if (guids.Length == 0)
            {
                EnsureFolderPath("Assets/Editor/AssetBundleCollector");
                AssetBundleCollectorSetting created = ScriptableObject.CreateInstance<AssetBundleCollectorSetting>();
                AssetDatabase.CreateAsset(created, "Assets/Editor/AssetBundleCollector/AssetBundleCollectorSetting.asset");
                AssetDatabase.SaveAssets();
            }

            AssetBundleCollectorSetting setting = AssetBundleCollectorSettingData.Setting;
            setting.ShowPackageView = true;
            setting.ShowEditorAlias = true;
            setting.UniqueBundleName = true;
            setting.Packages = new List<AssetBundleCollectorPackage>
            {
                BuildDefaultPackage(),
            };
            AssetBundleCollectorSettingData.SaveFile();
        }

        static AssetBundleCollectorPackage BuildDefaultPackage()
        {
            var package = new AssetBundleCollectorPackage
            {
                PackageName = "DefaultPackage",
                EnableAddressable = true,
                // Loads use full paths (…/LoadingForm.prefab); extensionless dual-map collides.
                SupportExtensionless = false,
                LocationToLower = false,
                IncludeAssetGUID = false,
                AutoCollectShaders = true,
                IgnoreRuleName = nameof(NormalIgnoreRule),
                Groups = new List<AssetBundleCollectorGroup>(),
            };

            // CollectAll：确保 AllMapItem.bytes 进包（Address = AllMapItem）
            AddGroup(package, "Configs", "Assets/AssetRaw/Configs", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));
            AddGroup(package, "Audios", "Assets/AssetRaw/Audios", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));
            AddGroup(package, "UIRaw", "Assets/AssetRaw/UI", nameof(AddressByFileName), nameof(PackSeparately), nameof(CollectAll));
            AddGroup(package, "Scenes", "Assets/AssetRaw/Scenes", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectScene));
            // Address = file name (IslandTerrain / IslandOcean) for YooAsset LoadAssetAsync.
            AddGroup(package, "Island", "Assets/AssetRaw/Island", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));
            AddGroup(package, "Ocean", "Assets/AssetRaw/Ocean", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));
            AddGroup(package, "SkyWeather", "Assets/AssetRaw/SkyWeather", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));
            AddGroup(package, "Effects", "Assets/AssetRaw/Effects", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));
            AddGroup(package, "Fonts", "Assets/AssetRaw/Fonts", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));
            AddGroup(package, "DLL", "Assets/AssetRaw/DLL", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));

            // Prefabs only — .uxml/.uss ride as deps. CollectAll + SupportExtensionless
            // dual-mapped LoadingForm.prefab/.uxml → "Location have existed" warnings.
            AddGroup(package, "GameUI", "Assets/GameMain/UI", "AddressByFolderAndFileNameAndExt", nameof(PackSeparately), nameof(CollectPrefab));
            AddGroup(package, "GameEntity", "Assets/GameMain/Entity", nameof(AddressByFolderAndFileName), nameof(PackDirectory), nameof(CollectAll));
            AddGroup(package, "IslandPresets", "Assets/GameMain/Environment/IslandPresets", nameof(AddressByFileName), nameof(PackDirectory), nameof(CollectAll));
            // MapItem 预制体：用内置规则，地址 = 文件夹名_文件名（Lvshu_Maple 1）
            AddGroup(
                package,
                "DaoYu1MapItem",
                "Assets/GameMain/Environment/Prefabs/DaoYu1",
                nameof(AddressByFolderAndFileName),
                nameof(PackSeparately),
                nameof(CollectPrefab));
            // 篝火配套氛围：光柱 / 蝴蝶 / 萤火虫 / 鱼蟹（Address = 文件名）
            AddGroup(
                package,
                "IslandFX",
                "Assets/GameMain/Environment/FX",
                nameof(AddressByFileName),
                nameof(PackSeparately),
                nameof(CollectPrefab));
            // 油画火焰材质（图集经 mat 依赖打进包；与 IslandFX Prefab 组分离避免地址冲突）
            AddGroup(
                package,
                "IslandFXMats",
                "Assets/GameMain/Environment/FX/FX Mats",
                nameof(AddressByFileName),
                nameof(PackDirectory),
                nameof(CollectAll));
            // UnityFire 噪声火焰（CustomSRP/NoiseFire 材质 + 纹理）
            AddGroup(
                package,
                "IslandUnityFire",
                "Assets/GameMain/Environment/FX/UnityFire",
                nameof(AddressByFileName),
                nameof(PackDirectory),
                nameof(CollectAll));

            return package;
        }

        static void AddGroup(
            AssetBundleCollectorPackage package,
            string groupName,
            string collectPath,
            string addressRule,
            string packRule,
            string filterRule)
        {
            if (!AssetDatabase.IsValidFolder(collectPath) && !File.Exists(collectPath))
            {
                // 允许文件路径；若文件夹尚未创建则跳过。
                Log.Warning($"[YooAsset] Skip collector path (missing): {collectPath}");
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(collectPath);
            var group = new AssetBundleCollectorGroup
            {
                GroupName = groupName,
                ActiveRuleName = nameof(EnableGroup),
                Collectors = new List<AssetBundleCollector>
                {
                    new AssetBundleCollector
                    {
                        CollectPath = collectPath,
                        CollectorGUID = guid,
                        CollectorType = ECollectorType.MainAssetCollector,
                        AddressRuleName = addressRule,
                        PackRuleName = packRule,
                        FilterRuleName = filterRule,
                    },
                },
            };
            package.Groups.Add(group);
        }

        static void EnsureFolderPath(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string[] parts = folder.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{cur}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
