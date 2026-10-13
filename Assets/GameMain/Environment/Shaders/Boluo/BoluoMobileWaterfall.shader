// CustomSRP 工程不使用 URP。原 Boluo/Mobile/Waterfall 依赖 URP Lighting/Depth。
// 瀑布竖帘请用 CustomSRP/OilWaterfallNPR（mesh UV + FallSheet 白帘油画）。
Shader "Boluo/Mobile/Waterfall"
{
    Fallback "CustomSRP/OilWaterfallNPR"
}
