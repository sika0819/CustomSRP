using System;

namespace Game
{
    /// <summary>
    /// 桥/码头读档贴地纯策略：命名甲板识别、高度图改贴甲板、甲板未进场时保住存档眼位。
    /// </summary>
    public static class NamedDeckSpawnPolicy
    {
        /// <summary>存档眼位相对 Teleport 后落点掉落超过该值 → 暂留眼位等 Replant。</summary>
        public const float KeepSavedEyeDropMeters = 1.5f;

        /// <summary>
        /// 甲板相对脚下高度图至少高出这么多才认作桥/码头。
        /// 低于此值的命名碰撞（贴地石阶）不把人从地面抬上去。
        /// </summary>
        public const float MinDeckAboveTerrainMeters = 1f;

        /// <summary>提示高度允许低于甲板的余量（脚在甲板略下）。</summary>
        public const float HintBelowDeckSlopMeters = 0.5f;

        /// <summary>提示高度允许高于甲板的余量（眼位 = 甲板 + eyeHeight + 本值）。</summary>
        public const float HintAboveDeckExtraMeters = 2.5f;

        /// <summary>
        /// 甲板顶比海平面低过这个值就当水下结构，不放行。
        /// 贴着水面的桥面（摆放差几十厘米）仍然可走。
        /// </summary>
        public const float OceanDeckBelowSeaSlackMeters = 0.25f;

        /// <summary>
        /// 脚可以比桥面低这么多，仍然算在往桥上走。
        /// 海边码头大约高出贴水岸 1m，只放行「脚已经在板子上」会在岸边被海水挡住。
        /// </summary>
        public const float OceanDeckClimbMeters = 1.75f;

        /// <summary>
        /// 沿移动方向提前认出桥面的距离。
        /// 须盖住贴水岸到码头边的湿沙带，以及 CharacterController 半径（约 1.1m）。
        /// </summary>
        public const float OceanDeckClimbReachMeters = 3.5f;

        public static bool MatchesObjectName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.IndexOf("Dock", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("Pier", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("Bridge", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("Qiao", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("Deck", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        /// <summary>
        /// 流式生成会把实例根改成 <c>envN</c>（见 EnvItemStreamerLite.SealSpawnedNames），
        /// Dock/Pier 只留在子节点或分类父节点（Qiao）上。自/父/子任一命中即算甲板。
        /// </summary>
        public static bool MatchesHierarchy(
            string selfName, string[] parentNames, string[] childNames)
        {
            if (MatchesObjectName(selfName))
                return true;
            if (parentNames != null)
            {
                for (int i = 0; i < parentNames.Length; i++)
                {
                    if (MatchesObjectName(parentNames[i]))
                        return true;
                }
            }

            if (childNames != null)
            {
                for (int i = 0; i < childNames.Length; i++)
                {
                    if (MatchesObjectName(childNames[i]))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 命中命名甲板、甲板明显高于脚下高度图、且提示高度在甲板带内 → 改贴甲板。
        /// 不要求高度图被判成禁水：桥下河床经常超出河流竖直容差，或桥就跨在可走地面上。
        /// 提示点在甲板下方（人已经在桥底）仍然拒绝，避免走路被拽上桥。
        /// </summary>
        public static bool ShouldPreferDeckOverWater(
            float hintY,
            float terrainY,
            float deckY,
            float eyeHeight)
        {
            if (deckY < terrainY + MinDeckAboveTerrainMeters)
                return false;
            if (hintY < deckY - HintBelowDeckSlopMeters)
                return false;
            if (hintY > deckY + eyeHeight + HintAboveDeckExtraMeters)
                return false;
            return true;
        }

        /// <summary>桥面在海面上（含贴水误差），不是沉在水里的柱身。</summary>
        public static bool IsOceanDeckSurface(float deckY, float oceanY)
        {
            return deckY >= oceanY - OceanDeckBelowSeaSlackMeters;
        }

        /// <summary>
        /// 脚在桥面附近、桥面在海水之上 → 这个格子不要当海水禁行。
        /// 人可以站在板上，也可以从低一截的岸往上走。
        /// </summary>
        public static bool IsFootNearOceanDeck(float footY, float deckY, float oceanY)
        {
            if (!IsOceanDeckSurface(deckY, oceanY))
                return false;
            return footY >= deckY - OceanDeckClimbMeters;
        }

        /// <summary>
        /// 这一步的落点已经压到海水上的桥面，且高差在可上台阶内 → 把脚抬到桥面。
        /// 高差太大（人在高桥底下）不抬，避免从桥底被吸上去。
        /// </summary>
        public static bool ShouldLiftOntoOceanDeck(
            float footY, float deckY, float oceanY, bool targetsDeck)
        {
            if (!targetsDeck || !IsOceanDeckSurface(deckY, oceanY))
                return false;
            float rise = deckY - footY;
            // 小于一步的下沉交给 CharacterController，避免每帧把人从板子上弹一下。
            return rise > 0.25f && rise <= OceanDeckClimbMeters;
        }

        /// <summary>Teleport 掉到桥底时是否暂留存档眼位。</summary>
        public static bool ShouldKeepSavedEye(float savedEyeY, float afterTeleportY)
        {
            return savedEyeY - afterTeleportY > KeepSavedEyeDropMeters;
        }
    }
}
