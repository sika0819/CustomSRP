using UnityEngine;

namespace Game
{
    /// <summary>
    /// 岛屿服务只在流程里创建。热重载后静态被清空、物体还在时，编辑器才找回一次。
    /// </summary>
    static class IslandServiceHost
    {
        public static T Ensure<T>(T current, string objectName) where T : Component
        {
            if (current != null)
                return current;

#if UNITY_EDITOR
            T existing = Object.FindAnyObjectByType<T>();
            if (existing != null)
                return existing;
#endif
            var go = new GameObject(objectName);
            return go.AddComponent<T>();
        }
    }
}
