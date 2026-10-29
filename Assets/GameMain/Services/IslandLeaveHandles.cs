using System.Collections.Generic;
using YooAsset;

namespace Game
{
    /// <summary>
    /// 一轮进岛的 Yoo 句柄。跟着这次 <see cref="ProcedureIsland"/> 走，不留静态列表。
    /// 场景还在时不 Release；卸掉 Island 之后由 <see cref="ReleaseAll"/> 一次放开，然后丢掉这一轮。
    /// 场景仍在、实例已经 DestroyImmediate 的路径用 <see cref="ReleaseNow"/>，避免成功回调再放一次。
    /// </summary>
    public sealed class IslandLeaveHandles
    {
        readonly List<AssetHandle> _handles = new List<AssetHandle>(64);

        public void Track(AssetHandle handle)
        {
            if (handle == null || _handles.Contains(handle))
                return;
            _handles.Add(handle);
        }

        /// <summary>实例已经拆掉、或这次加载失败：当场 Release，并从登记摘掉。</summary>
        public void ReleaseNow(AssetHandle handle)
        {
            if (handle == null)
                return;
            _handles.Remove(handle);
            if (handle.IsValid)
                handle.Release();
        }

        /// <summary>只在 Island 场景已经不在时调用。</summary>
        public void ReleaseAll()
        {
            if (_handles.Count == 0)
                return;
            AssetHandle[] pending = _handles.ToArray();
            _handles.Clear();
            for (int i = 0; i < pending.Length; i++)
            {
                AssetHandle handle = pending[i];
                if (handle != null && handle.IsValid)
                    handle.Release();
            }
        }
    }
}
