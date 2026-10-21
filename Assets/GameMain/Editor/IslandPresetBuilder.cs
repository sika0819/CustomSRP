using System.IO;
using UnityEditor;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    public static class IslandPresetBuilder
    {
        const string OutFolder = "Assets/GameMain/Environment/IslandPresets";
        const string ResourcesFolder = "Assets/GameMain/Resources/IslandPresets";
        const string CoverFolder = "Assets/GameMain/UI/Textures/PresetItem";

        // Boluo GameData.schemeInfos (10) — first ten only shown in YuShe UI.
        static readonly (string name, string desc)[] Names =
        {
            ("清晨起床", "新的一天\n从海边苏醒"),
            ("独自旅行", "一个人待会儿\n也挺好"),
            ("午后放松", "躺下来\n让思绪慢慢飘远"),
            ("委屈泛滥", "你没说的委屈\n我都听得见"),
            ("专注工作", "让自然的节奏帮你聚焦"),
            ("放空发呆", "什么都不想\n就这样躺着"),
            ("灵感唤醒", "把思绪交给节奏\n让声音帮你集中"),
            ("假日躺平", "就这样躺着\n听风的声音"),
            ("学习冲刺", "让脑中杂音\n慢慢被风吹走"),
            ("准备入睡", "闭上眼\n深山会守护你的梦"),
            ("海边漫步", "潮汐与风"),
            ("山间黄昏", "暮色渐起"),
            ("林间正午", "树影斑驳"),
            ("清晨林间", "雾气未散"),
        };

        // Extracted from Boluo PresetsScheme_1..14 (WeatherType: 0 Wind,1 Rain,2 Foggy,3 Cloud)
        // cameraOffset / cameraToTargetDis / playerHeight drive StayBehind framing.
        static readonly (
            Vector3 pos, float yaw, Vector3 camOffset, float camDist, float playerHeight,
            float hours, int weather, int level)[] Data =
        {
            (new Vector3(610.0767f, 12.80065f, 706.1781f), 130.443f, new Vector3(346f, 0f, 0f), 3f, 6f, 6f + 13f / 60f, 3, 1),
            (new Vector3(560.7702f, 23.856f, 582.0682f), -81.77f, Vector3.zero, 3f, 6f, 8f + 27f / 60f, 3, 1),
            // 午后放松：Boluo 17:00 + Atmosphere 3（暖金雾）；眼睛在 (468, 地面+PlayerHeight, 411.1)。
            (new Vector3(468f, 0f, 411.1f), -168.898f, new Vector3(358.33f, 0f, 0f), 0f, 4f, 17f, 2, 1),
            (new Vector3(390.6678f, 75.45603f, 480.2867f), -193.155f, new Vector3(350f, 0f, 0f), 0.1f, 6f, 22.5f, 1, 1),
            (new Vector3(325.88f, 81.40367f, 481f), -92.137f, new Vector3(350.3f, 357f, 0f), 2.9f, 3.5f, 8.5f, 3, 1),
            (new Vector3(233.5713f, 105.0471f, 663.811f), -12.449f, new Vector3(355.03f, 1.5f, 0f), 3f, 6f, 23f, 1, 3),
            (new Vector3(504.6723f, 38.82168f, 463.8487f), -94.024f, new Vector3(349.79f, 0f, 0f), 3f, 4.5f, 8f, 3, 1),
            (new Vector3(603.174f, 16.41668f, 650.0313f), 65.484f, new Vector3(1.85f, 359.19f, 0f), 3f, 6f, 12f, 3, 1),
            (new Vector3(566.793f, 26.33587f, 550.52f), -172.025f, new Vector3(340f, 4.1f, 0f), 1.5f, 3f, 11.5f, 3, 1),
            // 准备入睡：Boluo 20:00 冷蓝夜；眼睛在该世界坐标，朝向 151.857。
            (new Vector3(344.9015f, 81.05421f, 401.83f), 151.857f, Vector3.zero, 0f, 8f, 20f, 3, 1),
            (new Vector3(543.28f, 35.47f, 524f), 0f, Vector3.zero, 3f, 6f, 10f, 3, 0),
            (new Vector3(621.23f, 9.18f, 675.29f), 0f, Vector3.zero, 3f, 6f, 19f, 1, 2),
            (new Vector3(250.22f, 104.6f, 644.04f), 0f, Vector3.zero, 3f, 6f, 12f, 3, 0),
            (new Vector3(425.29f, 38.89f, 396.35f), 0f, Vector3.zero, 3f, 6f, 9f, 3, 0),
        };

        // ExpSquared densities relative to default haze 0.001 (WeatherService / OilAtmosphereDriver).
        // Foggy L1/L2/L3 ≈ 1.6× / 1.9× / 2.2× of that base via WeatherFogScale.
        static readonly float[] FogDensities =
        {
            0.0010f,  // 1 清晨起床 Cloud — reference
            0.0008f,  // 2 独自旅行 Cloud morning
            0.0016f,  // 3 午后放松 Foggy L1
            0.0012f,  // 4 委屈泛滥 Rain L1 night
            0.0008f,  // 5 专注工作 Cloud
            0.0015f,  // 6 放空发呆 Rain L3 night
            0.0009f,  // 7 灵感唤醒 Cloud
            0.00075f, // 8 假日躺平 Cloud noon (lighter)
            0.0007f,  // 9 学习冲刺 Cloud near-noon
            0.00085f, // 10 准备入睡 Cloud night
            0.0007f,  // 11 海边漫步 Cloud L0
            0.00135f, // 12 山间黄昏 Rain L2
            0.0007f,  // 13 林间正午 Cloud L0
            0.00075f, // 14 清晨林间 Cloud L0
        };

        // Boluo AtmosphereProfile: Camera_1 / Camera_5 / 3 / 7(1) / 8 / 10.
        static readonly IslandLookKind[] LookKinds =
        {
            IslandLookKind.Canonical,      // 1 清晨起床
            IslandLookKind.Canonical,      // 2 独自旅行
            IslandLookKind.WarmAfternoon,  // 3 午后放松
            IslandLookKind.NightCold,      // 4 委屈泛滥
            IslandLookKind.CoolFocus,      // 5 专注工作
            IslandLookKind.NightCold,      // 6 放空发呆
            IslandLookKind.CyanMorning,    // 7 灵感唤醒
            IslandLookKind.WarmNoon,       // 8 假日躺平
            IslandLookKind.Canonical,      // 9 学习冲刺
            IslandLookKind.NightCold,      // 10 准备入睡
            IslandLookKind.Canonical,      // 11 海边漫步
            IslandLookKind.NightCold,      // 12 山间黄昏
            IslandLookKind.WarmNoon,       // 13 林间正午
            IslandLookKind.Canonical,      // 14 清晨林间
        };

        // Boluo PresetsScheme_1..14 banknames (YuShe download gate).
        static readonly string[][] BankNames =
        {
            new[] { "Music4_4", "River_ADD", "Ocean_RD", "Ocean_Rising_tide_LR", "Ocean_Ebb_tide_LR", "Ocean_Calm_Quad" },
            new[] { "Music5_3", "Boluodao_Forest" },
            new[] { "Music4_1", "Boluodao_Grassland" },
            new[] { "Music4_5", "Boluodao_Plateau", "Campfire", "River_Quad", "Waterfall_Big_Quad", "Waterfall_Small_Quad", "Lake_RD", "Lake_Quad" },
            new[] { "Music1_4", "Boluodao_Plateau", "Campfire" },
            new[] { "Music3_4", "Campfire", "Boluodao_Plateau" },
            new[] { "Music6_1", "Lake_Quad", "Lake_RD", "Waterfall_Big_Quad" },
            new[] { "Music6_4", "River_ADD", "Ocean_RD", "Ocean_Rising_tide_LR", "Ocean_Ebb_tide_LR", "Ocean_Calm_Quad" },
            new[] { "Music2_3", "Boluodao_Forest" },
            new[] { "Music3_6", "Boluodao_Plateau", "Boluodao_Grassland", "Waterfall_Big_Quad" },
            new[] { "Music6_1", "Boluodao_Forest" },
            new[] { "Music1_2" },
            new[] { "Music3_1", "Boluodao_Plateau", "Campfire" },
            new[] { "Music2_4", "Boluodao_Grassland", "Lake_Quad", "Waterfall_Big_Quad", "Lake_RD" },
        };

        [MenuItem("GameMain/Boluo/Build Island Preset Assets")]
        public static void Build()
        {
            Directory.CreateDirectory(OutFolder);
            Directory.CreateDirectory(ResourcesFolder);

            var list = new IslandPresetAsset[Data.Length];
            for (int i = 0; i < Data.Length; i++)
            {
                int id = i + 1;
                string path = $"{OutFolder}/IslandPreset_{id:00}.asset";
                IslandPresetAsset asset = AssetDatabase.LoadAssetAtPath<IslandPresetAsset>(path);
                if (asset == null)
                {
                    asset = ScriptableObject.CreateInstance<IslandPresetAsset>();
                    AssetDatabase.CreateAsset(asset, path);
                }

                asset.Id = id;
                asset.DisplayName = Names[i].name;
                asset.Description = Names[i].desc;
                asset.TargetPos = Data[i].pos;
                asset.TargetRotateY = Data[i].yaw;
                asset.CameraOffset = Data[i].camOffset;
                asset.CameraToTargetDis = Data[i].camDist;
                asset.PlayerHeight = Data[i].playerHeight;
                asset.TimeHours = Data[i].hours;
                asset.WeatherKind = (IslandWeatherKind)Data[i].weather;
                asset.WeatherLevel = Data[i].level;
                if (asset.WeatherLevel <= 0 && asset.WeatherKind != IslandWeatherKind.Clear)
                    asset.WeatherLevel = 1;
                asset.FogDensity = FogDensities[i];
                asset.LookKind = LookKinds[i];
                asset.LightColorOverride = new Color(0f, 0f, 0f, 0f);
                asset.BankNames = BankNames[i];

                int coverId = id <= 10 ? id : ((id - 1) % 10) + 1;
                asset.Cover = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CoverFolder}/zhanweitu{coverId}.png");

                EditorUtility.SetDirty(asset);
                list[i] = asset;

                string resPath = $"{ResourcesFolder}/IslandPreset_{id:00}.asset";
                AssetDatabase.DeleteAsset(resPath);
                AssetDatabase.CopyAsset(path, resPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log.Info($"[IslandPreset] Built {list.Length} presets → {OutFolder} + Resources");
        }
    }
}
