// CustomSRP 工程不使用 URP。原 Boluo/Mobile/Ocean 依赖 URP Lighting/Depth，
// 真机打包会报 include 失败。材质请用 CustomSRP/OilOceanNPR；此名仅作兼容回退。
Shader "Boluo/Mobile/Ocean"
{
    Fallback "CustomSRP/OilOceanNPR"
}
