# Boluo 全量环境资产迁移记录（无低模排除）

生成时间（UTC）：2026-10-10T07:48:29Z

## 摘要

- 种子预制体（DaoYu1）：168
- 种子模型（Arts/Model）：219
- 额外种子（Polygon + Presets + InGame）：207
- 已拷贝文件对（资产+.meta）：1017
- 闭包逻辑资产数：1017
- 总体积（约）：1706.98 MB
- 缺失 GUID：455
- Blockers：163

## 策略

- **已取消** `SM_*` / `rpgpp_*` / PolygonNatureBiomes 低模排除
- 不复制：`.cs`、Wanos、HybridCLR DLL、Scripts/Editor
- GPUI / URP 组件记入 blockers；Unity Validate 菜单会剥离 Missing Script

## 种子预制体（节选）

- `Assets/Arts/Prefabs/DaoYu1/BanksChecker/BanksChecker.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Common/River_01.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Common/River_02.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Common/River_03.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Common/River_04.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Bush 1.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Bush 2.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Evergreen Bush 1.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Evergreen Bush 2.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Herb Bush.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Red Bush.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_02.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Palm_02.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Palm_04.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Tropical_01.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Tropical_02.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Tropical_03.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Tropical_04.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Winter Berry Bush 1.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Winter Berry Bush 2.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Winter Berry Bush 3.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Hua/Flowers 3.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Hua/SM_Env_Lillies_01.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Hua/SM_Env_Lillies_03.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Hua/SM_Env_Wildflowers_01.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Hua/SM_Env_Wildflowers_01_LOD0.prefab`
- `Assets/Arts/Prefabs/DaoYu1/Hua/Scene_cao_007.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/House_01.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/SM_Env_Dock_02.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/Tent 2.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_barrel_01b.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_barrel_01d.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_barrel_stack_01b.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_basket_03b.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_bench_wood_01a.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_bench_wood_03.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_box_wood_01.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_building_closed_01.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_building_closed_03.prefab`
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_container_wood_01.prefab`
- … 共 168 个

## 预制体级 Blocker

- `Assets/Arts/Prefabs/DaoYu1/BanksChecker/BanksChecker.prefab`：HotUpdate script SceneBanksCheckCollider.cs
- `Assets/Arts/Prefabs/DaoYu1/BanksChecker/BanksChecker.prefab`：HotUpdate script SceneBanksCheckCollider.cs
- `Assets/Arts/Prefabs/DaoYu1/BanksChecker/BanksChecker.prefab`：HotUpdate script SceneBanksChecker.cs
- `Assets/Arts/Prefabs/DaoYu1/BanksChecker/BanksChecker.prefab`：HotUpdate script SceneBanksChecker.cs
- `Assets/Arts/Prefabs/DaoYu1/BanksChecker/BanksChecker.prefab`：HotUpdate script SceneBanksCheckCollider.cs
- `Assets/Arts/Prefabs/DaoYu1/BanksChecker/BanksChecker.prefab`：HotUpdate script SceneBanksChecker.cs
- `Assets/Arts/Prefabs/DaoYu1/BanksChecker/BanksChecker.prefab`：HotUpdate script SceneBanksCheckCollider.cs
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Bush 1.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Bush 2.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Evergreen Bush 1.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Evergreen Bush 2.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Herb Bush.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Red Bush.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_02.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Palm_02.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Palm_04.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Tropical_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Tropical_02.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Tropical_03.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/SM_Env_Bush_Tropical_04.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Winter Berry Bush 1.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Winter Berry Bush 2.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Guanmu/Winter Berry Bush 3.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Hua/Flowers 3.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Hua/SM_Env_Lillies_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Hua/SM_Env_Lillies_03.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Hua/SM_Env_Wildflowers_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Hua/SM_Env_Wildflowers_01_LOD0.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Hua/Scene_cao_007.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/House_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/SM_Env_Dock_02.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/Tent 2.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_barrel_01b.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_barrel_01d.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_barrel_stack_01b.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_basket_03b.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_bench_wood_01a.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_bench_wood_03.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_box_wood_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_building_closed_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_building_closed_03.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_container_wood_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_crate_01b.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_crate_02a.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_planks_01b.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_shed_wood_02a_log.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_shed_wood_02b_log.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_stairs_entrance_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_wagon_01b.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/Golden Larch 1.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/Golden Larch 5.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/Larch 3.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/Maple 1.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/Maple 2.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Birch_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Birch_02.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Birch_03.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Forest_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Forest_02.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Forest_03.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Fruit_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Fruit_02.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Lvshu/SM_Env_Tree_Fruit_03.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Other/Forest Mountain 1.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Other/Forest Mountain 4.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Other/Huodui.prefab`：URP UniversalAdditionalLightData
- `Assets/Arts/Prefabs/DaoYu1/Other/Huodui.prefab`：FireController (gameplay)
- `Assets/Arts/Prefabs/DaoYu1/Other/Mountain 2.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Other/Mountain 3.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Other/SM_Veh_Canoe_Animated.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/Qiao/SWS_DemoPier.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Ground_Cliff_Large_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Rock_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Rock_03.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Rock_04.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Rock_05.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Rock_06.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Rock_Cave_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Rock_Cliff_01.prefab`：GPU Instancer Pro GPUIPrefab
- `Assets/Arts/Prefabs/DaoYu1/ShiTou/SM_Env_Rock_Cliff_02.prefab`：GPU Instancer Pro GPUIPrefab
- … 另有 83 条

## 缺失 GUID（节选）

- `efa47a43a7df3bb4ab81aa22a07b5e2b` ← `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_basket_03b.prefab`
- `80940a7d533759a4798fb45eb7df6bcb` ← `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_bench_wood_03.prefab`
- `ae83e28184ca1b142877ca26896aae93` ← `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_planks_01b.prefab`
- `2726e3cacf755f549a680eb50d019984` ← `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_planks_01b.prefab`
- `d79c0f6d145077f41b290e859d9e13a1` ← `Assets/Arts/Prefabs/DaoYu1/JianZhu/rpgpp_ag_stairs_entrance_01.prefab`
- `0000000000000000e000000000000000` ← `Assets/Arts/Prefabs/DaoYu1/Other/Huodui.prefab`
- `639247ca83abc874e893eb93af2b5e44` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Materials/Plants/CropField_01.mat`
- `d0353a89b1f911e48b9e16bdc9f2e058` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Materials/Plants/CropField_01.mat`
- `40b924e2dad56384a8df2a1e111bb675` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Meadows_Post_Processing_01.asset`
- `c1cb7e9e120078f43bce4f0b1be547a7` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Meadows_Post_Processing_01.asset`
- `adb84e30e02715445aeb9959894e3b4d` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Meadows_Post_Processing_01.asset`
- `8e6292b2c06870d4495f009f912b9600` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Meadows_Post_Processing_01.asset`
- `7ad4f2347baa3284fb13c7c11359a02c` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Meadows_Post_Processing_01.asset`
- `48a79b01ea5641d4aa6daa2e23605641` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Meadows_Post_Processing_01.asset`
- `5b71ad40e238046238f9b0c6f33c3791` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Models/SM_Prop_Birdhouse_01_Convex 1.asset`
- `5485954d14dfb9a4c8ead8edb0ded5b1` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo/Global Volume Profile.asset`
- `221518ef91623a7438a71fef23660601` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo/Global Volume Profile.asset`
- `558a8e2b6826cf840aae193990ba9f2e` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo/Global Volume Profile.asset`
- `d7fd9488000d3734a9e00ee676215985` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo/Global Volume Profile.asset`
- `0b2db86121404754db890f4c8dfe81b2` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo/Global Volume Profile.asset`
- `66f335fb1ffd8684294ad653bf1c7564` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo/Global Volume Profile.asset`
- `50a156ed21fe5f448b4b4da0b5345643` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `5f65c00cc3a462845922182b4a0afbd9` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `85d4b66433595d840b0ef5a47fa79d10` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `1906dcefe76ac554a8739f25317803fe` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `bc5572ad125a1944a85c76ad20d6658c` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `ad4266017d60f904c9024d082d378839` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `5b46bf0a337111849b71e6b15b5307e0` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `94871858625b51e4e8fb27236a8b2efb` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `4a46b7ab261ca404382c218abc478438` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `cfe5427e6e4acef4bb8dc5e23f38b3d5` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `47fd309ef8b23b7459c9982af5dfc4ce` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `44d96a0b4a88a084e864b6ada4ee6d1e` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `c6a6b356e7a48974696a5b7029b7b02b` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `a1c8b732aaba82b4aa5ce99cd756d345` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `2633ad019cc834d4a815232473127d8a` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `74f6a6347338629448876f903d0d346e` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `faa232351140d09468c55b9d5a0ec242` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `3928fe46c1997be45a7e169b0eb63625` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- `3ca01142054c997498fd413b391cbfcc` ← `Assets/Arts/PolygonNatureBiomes/PNB_Meadow_Forest/Scene/Demo.unity`
- … 另有 415 条

## 路径映射样例

- `Assets/AmplifyImpostors/Plugins/EditorResources/Presets/BakePresetURP.asset` → `Assets/GameMain/Environment/Other/AmplifyImpostors/Plugins/EditorResources/Presets/BakePresetURP.asset`
- `Assets/AmplifyImpostors/Plugins/EditorResources/Shaders/Baking/BakingURP.shader` → `Assets/GameMain/Environment/Other/AmplifyImpostors/Plugins/EditorResources/Shaders/Baking/BakingURP.shader`
- `Assets/AmplifyImpostors/Plugins/EditorResources/Shaders/Runtime/OctahedronImpostorURP.shader` → `Assets/GameMain/Environment/Other/AmplifyImpostors/Plugins/EditorResources/Shaders/Runtime/OctahedronImpostorURP.shader`
- `Assets/Arts/Adjustable Weather/Atmosphere/10.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Atmosphere/10.asset`
- `Assets/Arts/Adjustable Weather/Atmosphere/3.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Atmosphere/3.asset`
- `Assets/Arts/Adjustable Weather/Atmosphere/7(1).asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Atmosphere/7(1).asset`
- `Assets/Arts/Adjustable Weather/Atmosphere/8.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Atmosphere/8.asset`
- `Assets/Arts/Adjustable Weather/Atmosphere/Camera_1.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Atmosphere/Camera_1.asset`
- `Assets/Arts/Adjustable Weather/Atmosphere/Camera_5.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Atmosphere/Camera_5.asset`
- `Assets/Arts/Adjustable Weather/Cloud_1.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Cloud_1.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Cloud 1.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Cloud 1.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Cloud 2.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Cloud 2.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Cloud.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Cloud.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Filter 1.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Filter 1.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Filter 2.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Filter 2.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Filter 3.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Filter 3.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Fog.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Fog.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Wind 1.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Wind 1.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Wind 2.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Wind 2.asset`
- `Assets/Arts/Adjustable Weather/FX/Adjustable Wind 3.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Adjustable Wind 3.asset`
- `Assets/Arts/Adjustable Weather/FX/Heavy Rain Particle FX.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Heavy Rain Particle FX.asset`
- `Assets/Arts/Adjustable Weather/FX/Light Rain Particle FX.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Light Rain Particle FX.asset`
- `Assets/Arts/Adjustable Weather/FX/Thunderstorm Particle FX.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/FX/Thunderstorm Particle FX.asset`
- `Assets/Arts/Adjustable Weather/Foggy_1.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Foggy_1.asset`
- `Assets/Arts/Adjustable Weather/Rain_1.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Rain_1.asset`
- `Assets/Arts/Adjustable Weather/Rain_2.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Rain_2.asset`
- `Assets/Arts/Adjustable Weather/Rain_3.asset` → `Assets/GameMain/Environment/Other/Arts/Adjustable Weather/Rain_3.asset`
- `Assets/Arts/Animas/View/TouchAnima.anim` → `Assets/GameMain/Environment/Other/Arts/Animas/View/TouchAnima.anim`
- `Assets/Arts/Animas/View/TouchPoint.controller` → `Assets/GameMain/Environment/Other/Arts/Animas/View/TouchPoint.controller`
- `Assets/Arts/FX/FX Mats/Blank.mat` → `Assets/GameMain/Environment/FX/FX Mats/Blank.mat`
- … 共 1017 条，完整列表见 `migration_manifest.json`

## 延后（Phase 4，本轮不实现）

- EnvStreaming / 地形 16 切片 / `AllMapItem.bytes`
- BoluoInstancing / GPUInstancer Pro 运行时
- Hybrid Bridge / 登录 / 音频 Bank

