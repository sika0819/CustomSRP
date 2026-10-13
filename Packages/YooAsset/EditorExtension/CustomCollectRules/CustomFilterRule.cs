using System.IO;
using YooAsset.Editor;

/// <summary>
/// DaoYu1 只收 prefab 作主资源。贴图 / 材质 / LOD / FBX 走 prefab 依赖；
/// CollectAll 会把未引用的 Billboard、源网格、法线剩图打进包。
/// </summary>
[DisplayName("收集 DaoYu1 地图道具（仅 prefab）")]
public class CollectDaoYu1MapItem : IFilterRule
{
    public string FindAssetType => EAssetSearchType.All.ToString();

    public bool IsCollectAsset(FilterRuleData data)
    {
        return Path.GetExtension(data.AssetPath).Equals(".prefab", System.StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// DaoYu1/Materials/Season 只把可寻址 SO 收成主资源（季节 profile/catalog）。
/// 同夹季变体材质走 prefab 依赖，不要 CollectAll。
/// </summary>
[DisplayName("收集 DaoYu1 季节可寻址资产")]
public class CollectDaoYu1Deps : IFilterRule
{
    public string FindAssetType => EAssetSearchType.All.ToString();

    public bool IsCollectAsset(FilterRuleData data)
    {
        return Path.GetExtension(data.AssetPath).Equals(".asset", System.StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// SkyWeather 只收 Settings / Atmosphere / Weather SO。
/// 贴图材质网格走 Settings 依赖。Moon.jpg / SkyBrush / WaterBrush / CloudMap_Filled 是编辑器源，不要当主资源。
/// </summary>
[DisplayName("收集 SkyWeather 可寻址资产（贴图作依赖）")]
public class CollectSkyWeather : IFilterRule
{
    public string FindAssetType => EAssetSearchType.All.ToString();

    public bool IsCollectAsset(FilterRuleData data)
    {
        return Path.GetExtension(data.AssetPath).Equals(".asset", System.StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Ocean 收 OceanSettings / RiverMeshSet / 河面 RIV1 bytes。
/// 材质 / 浪法线 / 泡沫走 Settings 依赖。
/// </summary>
[DisplayName("收集 Ocean 可寻址资产（材质贴图作依赖）")]
public class CollectOcean : IFilterRule
{
    public string FindAssetType => EAssetSearchType.All.ToString();

    public bool IsCollectAsset(FilterRuleData data)
    {
        string ext = Path.GetExtension(data.AssetPath);
        return ext.Equals(".asset", System.StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bytes", System.StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 热更配置只收 bytes / SO。about.txt、MusicLists.json（真机走 StreamingAssets）、.bak 不进包。
/// </summary>
[DisplayName("收集热更配置（bytes/asset）")]
public class CollectConfigs : IFilterRule
{
    public string FindAssetType => EAssetSearchType.All.ToString();

    public bool IsCollectAsset(FilterRuleData data)
    {
        string ext = Path.GetExtension(data.AssetPath);
        return ext.Equals(".bytes", System.StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".asset", System.StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 业务 shader 进包；Hidden 烘焙 / 手绘滤镜 / 程序化贴图生成不进热更。
/// .hlsl 作 shader 依赖，不要 CollectAll 当主资源。
/// </summary>
[DisplayName("收集运行时 Boluo Shader（排除 Hidden 烘焙）")]
public class CollectBoluoRuntimeShaders : IFilterRule
{
    static readonly string[] EditorOnlyNames =
    {
        "OctahedralImpostorBake.shader",
        "BoluoHoyoStylizeTex.shader",
        "BoluoProceduralTextureGen.shader",
        "HiddenBillboardFoliageMask.shader",
    };

    public string FindAssetType => EAssetSearchType.Shader.ToString();

    public bool IsCollectAsset(FilterRuleData data)
    {
        string path = data.AssetPath.Replace('\\', '/');
        if (!path.EndsWith(".shader", System.StringComparison.OrdinalIgnoreCase))
            return false;
        string name = Path.GetFileName(path);
        for (int i = 0; i < EditorOnlyNames.Length; i++)
        {
            if (name.Equals(EditorOnlyNames[i], System.StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
