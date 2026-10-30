using UnityEngine;

namespace Game
{
    /// <summary>
    /// 游戏侧只处理前 8 个触摸。引擎原生回调仍可能收到更多指，这里挡不住那一层。
    /// </summary>
    public static class TouchInputLimit
    {
        public const int MaxPointers = 8;

        public static int Count => Mathf.Min(Input.touchCount, MaxPointers);
    }
}
