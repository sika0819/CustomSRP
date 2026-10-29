using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 岛屿场景卸完之后，在 Launch 上把引用计数归零的 Yoo bundle 卸掉。
    /// 状态放在这个组件上。静态字段会跨退登留住协程和宿主，下一趟登录卸不干净。
    /// 句柄在场景 OnDestroy 里放开，Destroy 要等帧末才落地，所以先空等再卸。
    /// 登录流程看到 <see cref="IsBusy"/> 会等这一轮结束，避免和下一趟铺装叠在一起。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IslandLeaveAssetReclaim : MonoBehaviour
    {
        const int YooUnloadLoops = 8;
        const int YooUnloadPasses = 3;

        int _generation;
        bool _busy;
        Coroutine _routine;

        /// <summary>仅旧路径拆过管线时非空。当前退登不拆设备，进岛时若有残留才装回。</summary>
        static RenderPipelineAsset _parkedPipeline;

        public bool IsBusy => _busy;

        /// <summary>挂到 Launch 的 Base 上。不缓存静态引用。</summary>
        public static IslandLeaveAssetReclaim Ensure()
        {
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
                return null;
            IslandLeaveAssetReclaim reclaim = host.GetComponent<IslandLeaveAssetReclaim>();
            if (reclaim == null)
                reclaim = host.gameObject.AddComponent<IslandLeaveAssetReclaim>();
            return reclaim;
        }

        /// <summary>岛屿场景卸载成功后调用。可重入，只保留最新一代。</summary>
        public void ScheduleAfterWorldRelease()
        {
            IslandAssetReclaimScheduler.Cancel();
            _generation++;
            int gen = _generation;
            StopRoutine();
            _busy = true;
            _routine = StartCoroutine(Run(gen));
        }

        /// <summary>进岛 Load / ChangeState 前挡住，卸完才放行，避免 Prefill 与卸载叠主线程。</summary>
        public IEnumerator WaitIfBusy()
        {
            while (_busy)
                yield return null;
        }

        /// <summary>关机 / 强杀流程作废进行中的回收。</summary>
        public void Cancel()
        {
            _generation++;
            StopRoutine();
            _busy = false;
            // 作废时把管线装回，避免下一路径（非进岛）一直 Builtin。
            RestoreParkedPipeline();
        }

        /// <summary>再进岛前装回退登拆下的 SRP。幂等。</summary>
        public static void RestoreParkedPipeline()
        {
            if (_parkedPipeline == null)
                return;
            RestorePipeline(_parkedPipeline);
            Log.Info("[LeaveMem] pipeline restored name=" + _parkedPipeline.name);
            _parkedPipeline = null;
        }

        void OnDestroy()
        {
            StopRoutine();
            _busy = false;
        }

        void StopRoutine()
        {
            if (_routine != null)
                StopCoroutine(_routine);
            _routine = null;
        }

        IEnumerator Run(int gen)
        {
            // Destroy 落到帧末再卸，避免 handle 仍短暂持有。
            yield return null;
            yield return null;
            if (gen != _generation)
            {
                Finish(gen);
                yield break;
            }

            // 场景包的引用计数要在 UnloadUnused 之前归零，依赖链才拆得掉。
            YooAssetResourceManager.Instance?.ReleaseScene(SceneAssets.Island);

            // 环境音的卸载有 2 秒宽限，服务停掉之后队列没人再扫。先等到点，再收剩余 bank。
            float bankDeadline = Time.realtimeSinceStartup + 3f;
            while (WanosAudio.WanosBankStore.HasPendingUnloads &&
                   Time.realtimeSinceStartup < bankDeadline)
            {
                if (gen != _generation)
                {
                    Finish(gen);
                    yield break;
                }

                WanosAudio.WanosBankStore.Update();
                yield return null;
            }

            int banks = IslandAssetReclaimScheduler.UnloadLooseBanks();
            Log.Info("[IslandLeaveAssetReclaim] loose banks unloaded=" + banks);

            float t0 = Time.realtimeSinceStartup;
            Log.Info("[IslandLeaveAssetReclaim] begin Yoo UnloadUnused after leave");
            for (int pass = 0; pass < YooUnloadPasses; pass++)
            {
                yield return YooResourceModule.UnloadUnusedAssetsCoroutine(YooUnloadLoops);
                if (gen != _generation)
                {
                    Finish(gen);
                    yield break;
                }
            }

            // 句柄已经归零。Resources.UnloadUnusedAssets 只走框架这一条，避免和 Update 再排一轮。
            Log.Info("[IslandLeaveAssetReclaim] begin Resource.ForceUnloadUnusedAssets");
            ResourceComponent resource = GameEntry.Resource;
            if (resource != null)
            {
                resource.ForceUnloadUnusedAssets(true);
                float unloadDeadline = Time.realtimeSinceStartup + 30f;
                while (resource.UnloadUnusedAssetsBusy && Time.realtimeSinceStartup < unloadDeadline)
                {
                    if (gen != _generation)
                    {
                        Finish(gen);
                        yield break;
                    }

                    yield return null;
                }

                if (resource.UnloadUnusedAssetsBusy)
                    Log.Warning("[IslandLeaveAssetReclaim] ForceUnloadUnusedAssets still busy");
            }

            if (gen != _generation)
            {
                Finish(gen);
                yield break;
            }

            // Cleanup 只是把销毁排进渲染线程。Surface 还在时多 Flush + 多画几帧，
            // Adreno 才会真正 vkDestroy；管线拆掉之后再 Flush 就没 SRP 实例可清。
            for (int pass = 0; pass < 3; pass++)
            {
                IslandGpuReclaim.FlushGpuPool();
                for (int frame = 0; frame < 2; frame++)
                    yield return new WaitForEndOfFrame();
                if (gen != _generation)
                {
                    Finish(gen);
                    yield break;
                }
            }

            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            Log.Info($"[IslandLeaveAssetReclaim] done ms={ms:F0} gen={gen}");
            LogFootprint("afterLeave");
            LogScudoStats("afterLeave");
            // 拆管线前再 Flush 一次：之后 currentPipeline 为空，FlushGpuPool 会直接 return。
            IslandGpuReclaim.FlushGpuPool();
            yield return new WaitForEndOfFrame();
            if (gen != _generation)
            {
                Finish(gen);
                yield break;
            }

            LogFootprint("beforeOffFlush");
            // 不拆 SRP。第一次退登在拆管线之前 kgsl 已经回到约 1100。
            // 拆掉再装回会新建 Vulkan 设备；第二次进岛这台设备上的 kgsl 映射
            // （实测 1170 → 36344）退登时 vkDestroy 不生效，scudo 64KB 档被占满，
            // 渲染线程 malloc 失败后 SIGSEGV。登录页靠 CoverOnly，不再换设备。
            LogPipelineSource("keepDevice");
            LogScudoStats("keepDevice");

            TrimNativeHeap();
            LogFootprint("afterTrim");
            Finish(gen);
        }

        static void LogPipelineSource(string reason)
        {
            string current = "null";
            try
            {
                if (GraphicsSettings.currentRenderPipeline != null)
                    current = GraphicsSettings.currentRenderPipeline.name;
            }
            catch (System.Exception)
            {
                current = "err";
            }

            bool quality = false;
            bool fallback = false;
            try
            {
                quality = QualitySettings.renderPipeline != null;
                fallback = GraphicsSettings.defaultRenderPipeline != null;
            }
            catch (System.Exception e)
            {
                Log.Warning("[LeaveMem] pipeline query failed " + e.Message);
            }

            Log.Info("[LeaveMem] " + reason
                + " current=" + current
                + " quality=" + (quality ? 1 : 0)
                + " default=" + (fallback ? 1 : 0));
        }

        /// <summary>
        /// 退登不要调用。拆掉再装回会换 Vulkan 设备，第二次进岛的 kgsl 映射退不掉。
        /// </summary>
        static void TakePipelineOffline()
        {
            try
            {
                if (_parkedPipeline != null)
                    return;
                RenderPipelineAsset asset = QualitySettings.renderPipeline;
                if (asset == null)
                    asset = GraphicsSettings.defaultRenderPipeline;
                if (asset == null)
                    return;
                QualitySettings.renderPipeline = null;
                GraphicsSettings.defaultRenderPipeline = null;
                _parkedPipeline = asset;
                Log.Info("[LeaveMem] pipeline parked name=" + asset.name);
            }
            catch (System.Exception e)
            {
                Log.Warning("[LeaveMem] pipeline off failed " + e.Message);
            }
        }

        static void RestorePipeline(RenderPipelineAsset asset)
        {
            try
            {
                if (QualitySettings.renderPipeline == null)
                    QualitySettings.renderPipeline = asset;
                if (GraphicsSettings.defaultRenderPipeline == null)
                    GraphicsSettings.defaultRenderPipeline = asset;
            }
            catch (System.Exception e)
            {
                Log.Warning("[LeaveMem] pipeline on failed " + e.Message);
            }
        }

        /// <summary>
        /// Android Bionic/scudo：malloc_trim 常不存在。mallopt(M_PURGE / M_PURGE_ALL) 只把已经在空闲链表上的页还内核，不缩小 primary mapped。
        /// </summary>
        static void TrimNativeHeap()
        {
            int purged = -1;
            int purgedAll = -1;
            int trimmed = -1;
            try
            {
                purged = Mallopt(M_PURGE, 0);
            }
            catch (System.Exception e)
            {
                Log.Warning("[LeaveMem] mallopt M_PURGE failed " + e.Message);
            }

            try
            {
                purgedAll = Mallopt(M_PURGE_ALL, 0);
            }
            catch (System.Exception e)
            {
                Log.Warning("[LeaveMem] mallopt M_PURGE_ALL failed " + e.Message);
            }

            try
            {
                trimmed = MallocTrim(0);
            }
            catch (System.Exception)
            {
                trimmed = -2; // Android 上常见缺符号
            }

            Log.Info(
                "[LeaveMem] purge=" + purged
                + " purgeAll=" + purgedAll
                + " malloc_trim=" + trimmed);
        }

        // bionic/libc/include/malloc.h。API 28 / 34 / 35。
        const int M_PURGE = -101;
        const int M_PURGE_ALL = -104;
        const int M_LOG_STATS = -205;

        /// <summary>
        /// 打一行标记后让 Scudo 把 SizeClassAllocator64 打到 logcat。API 35 以下 mallopt 返回 0。
        /// </summary>
        public static void LogScudoStats(string reason)
        {
            Log.Info("[LeaveMem] scudo stats " + reason);
            if (Application.platform != RuntimePlatform.Android)
                return;

            int logged = -1;
            try
            {
                logged = Mallopt(M_LOG_STATS, 0);
            }
            catch (System.Exception e)
            {
                Log.Warning("[LeaveMem] mallopt M_LOG_STATS failed " + e.Message);
                return;
            }

            Log.Info("[LeaveMem] scudo stats " + reason + " mallopt=" + logged);
        }

        [DllImport("libc.so", EntryPoint = "mallopt")]
        static extern int Mallopt(int param, int value);

        [DllImport("libc.so", EntryPoint = "malloc_trim")]
        static extern int MallocTrim(nuint pad);

        void Finish(int gen)
        {
            if (gen == _generation)
                _busy = false;
            _routine = null;
        }

        /// <summary>退登和再进岛各打一行，对照显存和 kgsl 映射有没有还回去。</summary>
        public static void LogFootprint(string reason)
        {
            IslandLeakProbe.LogMemory(reason);
            long texBytes = 0;
            int texCount = 0;
            Texture[] textures = Resources.FindObjectsOfTypeAll<Texture>();
            for (int i = 0; i < textures.Length; i++)
            {
                Texture texture = textures[i];
                if (texture == null)
                    continue;
                texCount++;
                texBytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);
            }

            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int kgsl = 0;
            long kgslRssKb = 0;
            long scudoRssKb = 0;
            long vmRssKb = 0;
            long vmDataKb = 0;
            try
            {
                // smaps：RSS 分桶比 VmData（虚地址）更能说明谁还占着页。
                SummarizeSmaps(out kgsl, out kgslRssKb, out scudoRssKb);

                string status = System.IO.File.ReadAllText("/proc/self/status");
                vmRssKb = ReadKb(status, "VmRSS:");
                vmDataKb = ReadKb(status, "VmData:");
            }
            catch (System.Exception e)
            {
                Log.Warning("[LeaveMem] proc read failed " + e.Message);
            }

            Log.Info(
                "[LeaveMem] " + reason
                + " tex=" + texCount
                + " texMB=" + (texBytes / (1024 * 1024))
                + " mesh=" + meshes
                + " kgslMaps=" + kgsl
                + " kgslRssMB=" + (kgslRssKb / 1024)
                + " scudoRssMB=" + (scudoRssKb / 1024)
                + " rssMB=" + (vmRssKb / 1024)
                + " dataMB=" + (vmDataKb / 1024));
            LogHeavyObjects();
        }

        static void SummarizeSmaps(out int kgslMaps, out long kgslRssKb, out long scudoRssKb)
        {
            kgslMaps = 0;
            kgslRssKb = 0;
            scudoRssKb = 0;
            if (!System.IO.File.Exists("/proc/self/smaps"))
            {
                string maps = System.IO.File.ReadAllText("/proc/self/maps");
                int index = 0;
                while ((index = maps.IndexOf("kgsl", index, System.StringComparison.Ordinal)) >= 0)
                {
                    kgslMaps++;
                    index += 4;
                }

                return;
            }

            string header = null;
            foreach (string line in System.IO.File.ReadLines("/proc/self/smaps"))
            {
                if (line.Length > 0 && IsHex(line[0]))
                {
                    header = line;
                    continue;
                }

                if (header == null || !line.StartsWith("Rss:", System.StringComparison.Ordinal))
                    continue;

                long rssKb = 0;
                string[] parts = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    long.TryParse(parts[1], out rssKb);

                bool isKgsl = header.IndexOf("kgsl", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool isScudo = header.IndexOf("scudo", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (isKgsl)
                {
                    kgslMaps++;
                    kgslRssKb += rssKb;
                }

                if (isScudo)
                    scudoRssKb += rssKb;
                header = null;
            }
        }

        static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        static void LogHeavyObjects()
        {
            var totals = new System.Collections.Generic.Dictionary<string, long>(32);
            var counts = new System.Collections.Generic.Dictionary<string, int>(32);
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            for (int i = 0; i < all.Length; i++)
            {
                UnityEngine.Object obj = all[i];
                if (obj == null)
                    continue;
                long bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(obj);
                string key = obj.GetType().Name;
                totals.TryGetValue(key, out long sum);
                totals[key] = sum + bytes;
                counts.TryGetValue(key, out int count);
                counts[key] = count + 1;
            }

            var keys = new System.Collections.Generic.List<string>(totals.Keys);
            keys.Sort((a, b) => totals[b].CompareTo(totals[a]));
            var sb = new System.Text.StringBuilder(256);
            sb.Append("[LeaveMem] heavy");
            int shown = 0;
            for (int i = 0; i < keys.Count && shown < 8; i++)
            {
                string key = keys[i];
                if (totals[key] < 1024L * 1024L)
                    break;
                shown++;
                sb.Append(' ');
                sb.Append(key);
                sb.Append('=');
                sb.Append(totals[key] / (1024 * 1024));
                sb.Append("MB/");
                sb.Append(counts[key]);
            }

            var bundles = AssetBundle.GetAllLoadedAssetBundles();
            int bundleCount = 0;
            if (bundles != null)
            {
                foreach (AssetBundle bundle in bundles)
                {
                    if (bundle == null)
                        continue;
                    bundleCount++;
                    if (bundleCount <= 6)
                    {
                        sb.Append(" bundle=");
                        sb.Append(bundle.name);
                    }
                }
            }

            sb.Append(" bundles=");
            sb.Append(bundleCount);
            Log.Info(sb.ToString());
        }

        static long ReadKb(string status, string key)
        {
            int start = status.IndexOf(key, System.StringComparison.Ordinal);
            if (start < 0)
                return 0;
            start += key.Length;
            while (start < status.Length && char.IsWhiteSpace(status[start]))
                start++;
            int end = start;
            while (end < status.Length && char.IsDigit(status[end]))
                end++;
            if (end == start)
                return 0;
            return long.Parse(status.Substring(start, end - start));
        }
    }
}
