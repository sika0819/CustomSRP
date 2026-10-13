using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 与 Baked Light 同布局（BakedLightSceneData），Mixed Lighting = Shadowmask。
    /// Quality 建议 Distance Shadowmask（本工程 Ultra 默认即为 Distance）。
    /// 管线侧尚未采样 shadow mask / lightmap；本场景用于烘焙数据与后续接线对照。
    /// </summary>
    public static class CreateShadowMasksScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/ShadowMasks.unity";
        const string LightingSettingsPath =
            TestSceneUtility.Root + "/Settings/ShadowMasksLightingSettings.asset";

        [MenuItem("CustomSRP/Create Shadow Masks Scene")]
        public static void Create()
        {
            CreateBakedLightScene.CreateGiScene(
                ScenePath,
                LightingSettingsPath,
                MixedLightingMode.Shadowmask,
                "Shadow Masks");
        }
    }
}
