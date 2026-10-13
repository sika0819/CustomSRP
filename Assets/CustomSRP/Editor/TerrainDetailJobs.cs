using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace CustomSRP.Editor
{
    /// <summary>
    /// 编辑器里一次性烘焙高度和山脊掩码。脚本在 Editor 目录，播放器不会编进这些 Job。
    /// </summary>
    public static class TerrainDetailProcessor
    {
        /// <summary>最高倍频的波长至少跨过这么多高度图像素，避免地形 LOD 为了细碎起伏把网格拆开。</summary>
        public const int MinWavelengthTexels = 4;

        /// <summary>海湾噪声的最短波长（米）。低于这档会在岸线上出碎点。</summary>
        public const float MinBayWavelengthMeters = 64f;

        const string RockLayerName = "SWS_DemoRock";
        const string GrassLayerName = "Grass";
        const string SandLayerName = "SWS_OceanFloorSmooth";

        /// <summary>
        /// 从 <paramref name="settings"/>.source 重算高度和岩石权重，写进 <paramref name="target"/>。
        /// 不改分辨率、LOD、层列表，也不打开 _RidgeAmount。
        /// </summary>
        /// <param name="settings">山脊、海岸、岩石参数。frequency 单位是周/米。</param>
        /// <param name="target">场景正在用的 TerrainData。必须已有 SWS_DemoRock 和 Grass。</param>
        /// <param name="error">失败原因。成功时为空。</param>
        public static bool TryApply(TerrainDetailSettings settings, TerrainData target, out string error)
        {
            if (!Validate(settings, target, out error, out int res, out int alphaW, out int alphaH,
                    out int sand, out int grass, out int rock))
            {
                return false;
            }

            Vector3 size = target.size;
            float texel = MinTexelMeters(size, res);
            float frequency = ClampFrequency(
                settings.frequency, settings.octaves, settings.lacunarity, texel);
            float bayFrequency = ClampBayFrequency(settings.bayFrequency);
            if (frequency < settings.frequency - 1e-6f)
            {
                Log.Info(
                    "[CustomSRP] Ridge frequency clamped from " +
                    $"{settings.frequency:0.0000} to {frequency:0.0000} /m " +
                    $"so the shortest wavelength stays at least {MinWavelengthTexels} heightmap texels " +
                    $"({MinWavelengthTexels * texel:0.0} m).");
            }

            TerrainData source = settings.source;
            float[,] baseHeights = source.GetHeights(0, 0, res, res);
            float[,,] alphamaps = source.GetAlphamaps(0, 0, alphaW, alphaH);
            var output = new NativeArray<float>(res * res, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var heights = new float[res, res];
            try
            {
                RunHeightJob(settings, baseHeights, output, size, frequency, bayFrequency);
                CopyHeights(output, heights);
                // 只把海岸误刷拨回沙子；山脊岩石从草地加权重，不回收内陆已有岩石/草地图案。
                RestoreBeachSand(settings, heights, alphamaps, sand, grass, rock, size);
                PaintRock(
                    settings, baseHeights, heights, alphamaps, sand, grass, rock, size,
                    requireRaise: true, stripNonRidge: false);
            }
            finally
            {
                if (output.IsCreated)
                {
                    output.Dispose();
                }
            }

            if (EditorUtility.IsPersistent(target))
            {
                Undo.RegisterCompleteObjectUndo(target, "Apply Terrain Detail");
            }

            target.SetHeights(0, 0, heights);
            target.SetAlphamaps(0, 0, alphamaps);
            error = null;
            return true;
        }

        /// <summary>
        /// 只把海岸带误刷的岩石拨回沙子。不改内陆草地/岩石分布，也不改高度。
        /// 山脊岩石请用 Apply（从快照重算）或手绘，避免把原有草地冲掉。
        /// </summary>
        /// <param name="settings">海岸米数。seaLevelMeters 默认 5。</param>
        /// <param name="target">三层沙/草/岩的 Island TerrainData。</param>
        /// <param name="error">失败原因。</param>
        public static bool TryFixSandRockSplat(TerrainDetailSettings settings, TerrainData target, out string error)
        {
            if (settings == null || target == null)
            {
                error = "Settings and target TerrainData are required.";
                return false;
            }

            if (!HasNoNullLayers(target, out error))
            {
                return false;
            }

            int rock = FindLayer(target, RockLayerName);
            int grass = FindLayer(target, GrassLayerName);
            int sand = FindLayer(target, SandLayerName);
            if (rock < 0 || grass < 0 || sand < 0)
            {
                error = "Need SWS_OceanFloorSmooth, Grass, and SWS_DemoRock layers.";
                return false;
            }

            int res = target.heightmapResolution;
            int alphaW = target.alphamapWidth;
            int alphaH = target.alphamapHeight;
            float[,] heights = target.GetHeights(0, 0, res, res);
            float[,,] alphamaps = target.GetAlphamaps(0, 0, alphaW, alphaH);

            if (EditorUtility.IsPersistent(target))
            {
                Undo.RegisterCompleteObjectUndo(target, "Fix Beach Sand Splat");
            }

            RestoreBeachSand(settings, heights, alphamaps, sand, grass, rock, target.size);
            target.SetAlphamaps(0, 0, alphamaps);
            error = null;
            return true;
        }

        /// <summary>
        /// 把海岸带上的岩石（以及部分草地）拨回沙层。beach 掩码覆盖
        /// [seaLevel - belowMeters, seaLevel + aboveMeters + 6m]（世界米，再除以 size.y）。
        /// 带外不改。沙层下标无效时直接返回。
        /// </summary>
        /// <param name="settings">seaLevelMeters / belowMeters / aboveMeters，单位米。</param>
        /// <param name="heights">归一化高度 [z,x]。</param>
        /// <param name="alphamaps">splat [y,x,layer]，就地改写。</param>
        /// <param name="sandLayer">SWS_OceanFloorSmooth 下标。</param>
        /// <param name="grassLayer">Grass 下标。</param>
        /// <param name="rockLayer">SWS_DemoRock 下标。</param>
        /// <param name="size">TerrainData.size，y 为高度米数。</param>
        public static void RestoreBeachSand(
            TerrainDetailSettings settings,
            float[,] heights,
            float[,,] alphamaps,
            int sandLayer,
            int grassLayer,
            int rockLayer,
            Vector3 size)
        {
            if (sandLayer < 0 || grassLayer < 0 || rockLayer < 0)
            {
                return;
            }

            int alphaH = alphamaps.GetLength(0);
            int alphaW = alphamaps.GetLength(1);
            int layers = alphamaps.GetLength(2);
            int heightRes = heights.GetLength(0);
            float invY = 1f / size.y;
            float sea = settings.seaLevelMeters * invY;
            float below = math.max(0.5f, settings.belowMeters) * invY;
            // 岸上沙滩比高度重塑带宽一点，避免涨潮线外仍留着岩石沙滩。
            float above = (math.max(0.5f, settings.aboveMeters) + 6f) * invY;
            float fade = 2f * invY;
            float bandLow = sea - below;
            float bandHigh = sea + above;

            for (int y = 0; y < alphaH; y++)
            {
                float fz = alphaH <= 1 ? 0f : y / (float)(alphaH - 1) * (heightRes - 1);
                for (int x = 0; x < alphaW; x++)
                {
                    float fx = alphaW <= 1 ? 0f : x / (float)(alphaW - 1) * (heightRes - 1);
                    float height01 = SampleBilinearManaged(heights, fx, fz);
                    float beach = math.saturate(
                        math.smoothstep(bandLow - fade, bandLow, height01) *
                        (1f - math.smoothstep(bandHigh, bandHigh + fade, height01)));
                    if (beach < 1e-4f)
                    {
                        continue;
                    }

                    float sandW = alphamaps[y, x, sandLayer];
                    float grassW = alphamaps[y, x, grassLayer];
                    float rockW = alphamaps[y, x, rockLayer];
                    // 岩石全部还沙滩；近水草地也拨一半成湿沙。
                    float fromRock = rockW * beach;
                    float fromGrass = grassW * beach * 0.55f;
                    rockW -= fromRock;
                    grassW -= fromGrass;
                    sandW += fromRock + fromGrass;
                    alphamaps[y, x, sandLayer] = sandW;
                    alphamaps[y, x, grassLayer] = grassW;
                    alphamaps[y, x, rockLayer] = rockW;
                    NormalizeLayers(alphamaps, y, x, layers);
                }
            }
        }

        static void NormalizeLayers(float[,,] alphamaps, int y, int x, int layers)
        {
            float sum = 0f;
            for (int layer = 0; layer < layers; layer++)
            {
                sum += alphamaps[y, x, layer];
            }

            if (sum <= 1e-5f)
            {
                return;
            }

            float inv = 1f / sum;
            for (int layer = 0; layer < layers; layer++)
            {
                alphamaps[y, x, layer] *= inv;
            }
        }

        /// <summary>
        /// 把基础频率压低，使最高一档的波长不短于 <see cref="MinWavelengthTexels"/> 个高度图像素。
        /// </summary>
        /// <param name="frequency">第一档频率，周/米，必须大于 0。</param>
        /// <param name="octaves">倍频数，使用时夹到 1..8。</param>
        /// <param name="lacunarity">频率倍数，使用时夹到 1.5..3.5。</param>
        /// <param name="texelMeters">一个高度图像素的世界边长，米。</param>
        public static float ClampFrequency(float frequency, int octaves, float lacunarity, float texelMeters)
        {
            octaves = math.clamp(octaves, 1, 8);
            lacunarity = math.clamp(lacunarity, 1.5f, 3.5f);
            float minWavelength = MinWavelengthTexels * math.max(texelMeters, 1e-4f);
            float maxHighest = 1f / minWavelength;
            float span = math.pow(lacunarity, octaves - 1);
            float maxBase = maxHighest / math.max(span, 1e-4f);
            return math.min(math.max(frequency, 1e-6f), maxBase);
        }

        /// <summary>
        /// 海湾频率上限，周/米。波长不会短于 <see cref="MinBayWavelengthMeters"/> 米。
        /// </summary>
        /// <param name="bayFrequency">用户填的频率，周/米。</param>
        public static float ClampBayFrequency(float bayFrequency)
        {
            float maxFrequency = 1f / MinBayWavelengthMeters;
            return math.min(math.max(bayFrequency, 1e-6f), maxFrequency);
        }

        /// <summary>
        /// 高度图像素的较短边，米。用来换算波长下限。
        /// </summary>
        /// <param name="size">TerrainData.size，xyz 是水平尺寸，y 是高度米数。</param>
        /// <param name="resolution">heightmapResolution，2^n+1。</param>
        public static float MinTexelMeters(Vector3 size, int resolution)
        {
            float denom = math.max(resolution - 1, 1);
            return math.min(size.x, size.z) / denom;
        }

        static bool Validate(
            TerrainDetailSettings settings,
            TerrainData target,
            out string error,
            out int res,
            out int alphaW,
            out int alphaH,
            out int sand,
            out int grass,
            out int rock)
        {
            res = 0;
            alphaW = 0;
            alphaH = 0;
            sand = -1;
            grass = -1;
            rock = -1;
            if (settings == null)
            {
                error = "Terrain detail settings are missing.";
                return false;
            }

            if (target == null || settings.source == null)
            {
                error = "Assign a target terrain and capture a source snapshot first.";
                return false;
            }

            if (target == settings.source)
            {
                error = "Source snapshot and target terrain are the same asset.";
                return false;
            }

            if (target.size.y < 0.01f)
            {
                error = "Terrain height size.y is too small to convert meters.";
                return false;
            }

            res = target.heightmapResolution;
            if (settings.source.heightmapResolution != res)
            {
                error = "Source heightmap resolution does not match the target. Capture again.";
                return false;
            }

            alphaW = target.alphamapWidth;
            alphaH = target.alphamapHeight;
            if (settings.source.alphamapWidth != alphaW || settings.source.alphamapHeight != alphaH)
            {
                error = "Source alphamap resolution does not match the target. Capture again.";
                return false;
            }

            if (!HasNoNullLayers(target, out error) || !HasNoNullLayers(settings.source, out error))
            {
                return false;
            }

            if (!SameLayerNames(settings.source, target))
            {
                error = "Source and target terrain layers differ. Capture again.";
                return false;
            }

            rock = FindLayer(target, RockLayerName);
            grass = FindLayer(target, GrassLayerName);
            sand = FindLayer(target, SandLayerName);
            if (rock < 0)
            {
                error = "SWS_DemoRock layer is missing. This tool only paints that existing layer.";
                return false;
            }

            if (grass < 0)
            {
                error = "Grass layer is missing. Ridge rock is taken from grass only.";
                return false;
            }

            if (sand < 0)
            {
                Log.Warning(
                    "[CustomSRP] SWS_OceanFloorSmooth is missing. Ridge rock will not be masked by sand.");
            }

            float[,,] sourceMaps = settings.source.GetAlphamaps(0, 0, alphaW, alphaH);
            if (IsAlphamapDegenerate(sourceMaps, out error))
            {
                error = "Source splat looks wiped (" + error + "). Capture again from a good terrain.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// TerrainLayers 里不能有空槽。空槽会把权重画到看不见的层上，看起来像 splat 丢了。
        /// </summary>
        /// <param name="data">要检查的 TerrainData。</param>
        /// <param name="error">失败原因。</param>
        public static bool HasNoNullLayers(TerrainData data, out string error)
        {
            TerrainLayer[] layers = data != null ? data.terrainLayers : null;
            if (layers == null || layers.Length == 0)
            {
                error = "Terrain has no terrain layers.";
                return false;
            }

            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] == null)
                {
                    error =
                        "Terrain layer slot " + i +
                        " is null. Keep exactly sand / grass / SWS_DemoRock with no empty slots.";
                    return false;
                }
            }

            if (data.alphamapLayers != layers.Length)
            {
                error =
                    "alphamapLayers (" + data.alphamapLayers +
                    ") does not match terrainLayers.Length (" + layers.Length + ").";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// 整张 splat 几乎只有一层时视为被冲掉（例如 Capture 在 CreateAsset 前写图）。
        /// 平均权重大于 0.99 的层超过一层就不算退化。
        /// </summary>
        /// <param name="alphamaps">下标 [y, x, layer]，权重 0..1。</param>
        /// <param name="error">哪一层几乎占满。</param>
        public static bool IsAlphamapDegenerate(float[,,] alphamaps, out string error)
        {
            error = null;
            if (alphamaps == null)
            {
                error = "alphamap is null";
                return true;
            }

            int h = alphamaps.GetLength(0);
            int w = alphamaps.GetLength(1);
            int layers = alphamaps.GetLength(2);
            if (h < 1 || w < 1 || layers < 1)
            {
                error = "alphamap is empty";
                return true;
            }

            var sum = new float[layers];
            int step = math.max(1, math.min(h, w) / 64);
            int samples = 0;
            for (int y = 0; y < h; y += step)
            {
                for (int x = 0; x < w; x += step)
                {
                    samples++;
                    for (int layer = 0; layer < layers; layer++)
                    {
                        sum[layer] += alphamaps[y, x, layer];
                    }
                }
            }

            float inv = 1f / math.max(1, samples);
            for (int layer = 0; layer < layers; layer++)
            {
                if (sum[layer] * inv > 0.99f)
                {
                    error = "layer " + layer + " mean weight > 0.99";
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 按层名字找下标。找不到返回 -1。名字必须与 TerrainLayer 资源名一致。
        /// </summary>
        /// <param name="data">要查的 TerrainData。</param>
        /// <param name="layerName">SWS_DemoRock、Grass 或 SWS_OceanFloorSmooth。</param>
        public static int FindLayer(TerrainData data, string layerName)
        {
            TerrainLayer[] layers = data.terrainLayers;
            if (layers == null)
            {
                return -1;
            }

            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] != null && layers[i].name == layerName)
                {
                    return i;
                }
            }

            return -1;
        }

        static bool SameLayerNames(TerrainData a, TerrainData b)
        {
            TerrainLayer[] left = a.terrainLayers;
            TerrainLayer[] right = b.terrainLayers;
            int n = left != null ? left.Length : 0;
            int m = right != null ? right.Length : 0;
            if (n != m)
            {
                return false;
            }

            for (int i = 0; i < n; i++)
            {
                string ln = left[i] != null ? left[i].name : "";
                string rn = right[i] != null ? right[i].name : "";
                if (ln != rn)
                {
                    return false;
                }
            }

            return true;
        }

        static void RunHeightJob(
            TerrainDetailSettings settings,
            float[,] baseHeights,
            NativeArray<float> output,
            Vector3 size,
            float frequency,
            float bayFrequency)
        {
            int res = baseHeights.GetLength(0);
            var source = new NativeArray<float>(res * res, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                CopyToNative(baseHeights, source);
                float invY = 1f / size.y;
                var job = new ApplyHeightJob
                {
                    source = source,
                    dest = output,
                    resolution = res,
                    sizeX = size.x,
                    sizeZ = size.z,
                    sizeY = size.y,
                    texelX = size.x / math.max(res - 1, 1),
                    texelZ = size.z / math.max(res - 1, 1),
                    octaves = math.clamp(settings.octaves, 1, 8),
                    lacunarity = math.clamp(settings.lacunarity, 1.5f, 3.5f),
                    gain = math.clamp(settings.gain, 0.3f, 0.7f),
                    frequency = frequency,
                    sharpness = math.clamp(settings.sharpness, 1f, 4f),
                    amplitude01 = math.max(0f, settings.amplitudeMeters) * invY,
                    ridgeWeight = math.saturate(settings.ridgeWeight),
                    minSlopeDegrees = math.clamp(settings.minSlopeDegrees, 0f, 60f),
                    seedOffset = SeedOffset(settings.seed),
                    sea01 = settings.seaLevelMeters * invY,
                    below01 = math.max(0.25f, settings.belowMeters) * invY,
                    above01 = math.max(0.25f, settings.aboveMeters) * invY,
                    bay01 = math.max(0f, settings.bayMeters) * invY,
                    bayFrequency = bayFrequency,
                    seabed01 = settings.seabedMeters * invY,
                };
                job.Schedule(res * res, 64).Complete();
            }
            finally
            {
                if (source.IsCreated)
                {
                    source.Dispose();
                }
            }
        }

        /// <summary>
        /// 山脊刷 SWS_DemoRock：先把非山脊上的岩石收回草地，再按掩码从草地扣到岩石。
        /// 海岸带和沙权重大的像素不刷岩石。
        /// </summary>
        /// <param name="settings">rockStrength 0..1，tpiRadiusTexels 1..8，minCrestMeters 为米。</param>
        /// <param name="baseHeights">快照高度，归一化 0..1，下标 [z, x]。</param>
        /// <param name="outHeights">烘焙后的高度，同一分辨率。</param>
        /// <param name="alphamaps">splat，下标 [y, x, layer]，会被改写。</param>
        /// <param name="sandLayer">沙层下标。小于 0 表示没有沙层，跳过沙掩码。</param>
        /// <param name="grassLayer">草层下标，权重从这里扣。</param>
        /// <param name="rockLayer">SWS_DemoRock 下标，权重加到这里。</param>
        /// <param name="size">地形尺寸。y 是高度米数，用来把归一化高度换回米。</param>
        /// <param name="requireRaise">true 时掩码还要本次抬高；false 时只用 TPI×坡度。</param>
        /// <param name="stripNonRidge">true 时把非山脊岩石收回草地（会改原有草地图案，默认 false）。</param>
        public static void PaintRock(
            TerrainDetailSettings settings,
            float[,] baseHeights,
            float[,] outHeights,
            float[,,] alphamaps,
            int sandLayer,
            int grassLayer,
            int rockLayer,
            Vector3 size,
            bool requireRaise,
            bool stripNonRidge = false)
        {
            int alphaH = alphamaps.GetLength(0);
            int alphaW = alphamaps.GetLength(1);
            int layers = alphamaps.GetLength(2);
            int heightRes = outHeights.GetLength(0);
            int count = alphaW * alphaH;
            var baseNative = new NativeArray<float>(heightRes * heightRes, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var outNative = new NativeArray<float>(heightRes * heightRes, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var raw = new NativeArray<float>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var blurred = new NativeArray<float>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                CopyToNative(baseHeights, baseNative);
                CopyToNative(outHeights, outNative);
                float invY = 1f / size.y;
                var maskJob = new RidgeMaskJob
                {
                    baseHeights = baseNative,
                    outHeights = outNative,
                    mask = raw,
                    heightRes = heightRes,
                    alphaW = alphaW,
                    alphaH = alphaH,
                    sizeY = size.y,
                    texelX = size.x / math.max(heightRes - 1, 1),
                    texelZ = size.z / math.max(heightRes - 1, 1),
                    radius = math.clamp(settings.tpiRadiusTexels, 1, 8),
                    minCrestMeters = math.max(0f, settings.minCrestMeters),
                    minSlopeDegrees = math.clamp(settings.minSlopeDegrees, 0f, 60f),
                    raiseFeatherMeters = math.max(0.2f, settings.amplitudeMeters * 0.25f),
                    requireRaise = requireRaise ? 1f : 0f,
                };
                maskJob.Schedule(count, 64).Complete();
                // 比高度重塑岸带略宽，岩石绝不压过沙滩。
                float shoreTop = (settings.seaLevelMeters + settings.aboveMeters + 6f + settings.bayMeters) * invY;
                float shoreFade = 2f * invY;
                GateCoastAndSand(raw, outHeights, alphamaps, sandLayer, heightRes, shoreTop, shoreFade);

                var blurJob = new BlurMaskJob
                {
                    source = raw,
                    dest = blurred,
                    width = alphaW,
                    height = alphaH,
                };
                blurJob.Schedule(count, 64).Complete();

                float rockScale = math.saturate(settings.rockStrength);
                for (int y = 0; y < alphaH; y++)
                {
                    for (int x = 0; x < alphaW; x++)
                    {
                        float ridge = blurred[y * alphaW + x];
                        float rockW = alphamaps[y, x, rockLayer];
                        float grassW = alphamaps[y, x, grassLayer];
                        if (stripNonRidge)
                        {
                            float stray = rockW * (1f - ridge);
                            rockW -= stray;
                            grassW += stray;
                        }

                        float take = ridge * rockScale * grassW;
                        grassW = math.max(0f, grassW - take);
                        rockW += take;
                        alphamaps[y, x, grassLayer] = grassW;
                        alphamaps[y, x, rockLayer] = rockW;
                        NormalizeLayers(alphamaps, y, x, layers);
                    }
                }
            }
            finally
            {
                if (baseNative.IsCreated)
                {
                    baseNative.Dispose();
                }

                if (outNative.IsCreated)
                {
                    outNative.Dispose();
                }

                if (raw.IsCreated)
                {
                    raw.Dispose();
                }

                if (blurred.IsCreated)
                {
                    blurred.Dispose();
                }
            }
        }

        /// <summary>
        /// 把海岸和沙地上的掩码乘掉。shoreTop / shoreFade 是归一化高度（米 / size.y）。
        /// 高于 shoreTop 两个 shoreFade 后不再衰减。sandLayer 小于 0 时不看沙。
        /// 沙权重到 0.45 时掩码为 0。
        /// </summary>
        static void GateCoastAndSand(
            NativeArray<float> mask,
            float[,] heights,
            float[,,] alphamaps,
            int sandLayer,
            int heightRes,
            float shoreTop,
            float shoreFade)
        {
            int alphaH = alphamaps.GetLength(0);
            int alphaW = alphamaps.GetLength(1);
            for (int y = 0; y < alphaH; y++)
            {
                float fz = alphaH <= 1 ? 0f : y / (float)(alphaH - 1) * (heightRes - 1);
                for (int x = 0; x < alphaW; x++)
                {
                    int index = y * alphaW + x;
                    float gate = 1f;
                    if (sandLayer >= 0)
                    {
                        gate *= math.smoothstep(0.45f, 0.2f, alphamaps[y, x, sandLayer]);
                    }

                    float fx = alphaW <= 1 ? 0f : x / (float)(alphaW - 1) * (heightRes - 1);
                    float height01 = SampleBilinearManaged(heights, fx, fz);
                    gate *= math.smoothstep(shoreTop, shoreTop + shoreFade, height01);
                    mask[index] *= gate;
                }
            }
        }

        static void CopyToNative(float[,] heights, NativeArray<float> dst)
        {
            int res = heights.GetLength(0);
            for (int z = 0; z < res; z++)
            {
                int row = z * res;
                for (int x = 0; x < res; x++)
                {
                    dst[row + x] = heights[z, x];
                }
            }
        }

        static void CopyHeights(NativeArray<float> src, float[,] dst)
        {
            int res = dst.GetLength(0);
            for (int z = 0; z < res; z++)
            {
                int row = z * res;
                for (int x = 0; x < res; x++)
                {
                    dst[z, x] = src[row + x];
                }
            }
        }

        /// <summary>
        /// 由整数种子得到固定的平面平移（米）。不用 UnityEngine.Random，同一种子永远得到同一个偏移。
        /// </summary>
        /// <param name="seed">任意整数。</param>
        public static float2 SeedOffset(int seed)
        {
            uint bits = (uint)seed * 747796405u + 2891336453u;
            float x = (bits & 0xFFFF) / 65535f * 1000f;
            float z = (bits >> 16) / 65535f * 1000f;
            return new float2(x, z);
        }

        static float SampleBilinearManaged(float[,] heights, float fx, float fz)
        {
            int resZ = heights.GetLength(0);
            int resX = heights.GetLength(1);
            fx = Mathf.Clamp(fx, 0f, resX - 1);
            fz = Mathf.Clamp(fz, 0f, resZ - 1);
            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);
            int x1 = Mathf.Min(x0 + 1, resX - 1);
            int z1 = Mathf.Min(z0 + 1, resZ - 1);
            float tx = fx - x0;
            float tz = fz - z0;
            float a = Mathf.Lerp(heights[z0, x0], heights[z0, x1], tx);
            float b = Mathf.Lerp(heights[z1, x0], heights[z1, x1], tx);
            return Mathf.Lerp(a, b, tz);
        }
    }

    /// <summary>
    /// 在快照高度上加山脊，再只重塑海岸带。每个像素只写自己的 dest。
    /// </summary>
    [BurstCompile]
    struct ApplyHeightJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> source;
        [WriteOnly] public NativeArray<float> dest;
        public int resolution;
        public float sizeX;
        public float sizeZ;
        public float sizeY;
        public float texelX;
        public float texelZ;
        public int octaves;
        public float lacunarity;
        public float gain;
        public float frequency;
        public float sharpness;
        public float amplitude01;
        public float ridgeWeight;
        public float minSlopeDegrees;
        public float2 seedOffset;
        public float sea01;
        public float below01;
        public float above01;
        public float bay01;
        public float bayFrequency;
        public float seabed01;

        public void Execute(int index)
        {
            int x = index % resolution;
            int z = index / resolution;
            float height = source[index];
            float2 localXZ = LocalMeters(x, z);
            float bay = BayOffset(localXZ);
            float slopeMask = SlopeMask(x, z);
            if (InCoastBand(height, bay))
            {
                slopeMask = 0f;
            }

            float ridge = Ridged(localXZ);
            float raised = math.saturate(height + (ridge - 0.5f) * amplitude01 * slopeMask * ridgeWeight);
            dest[index] = math.saturate(ApplyCoast(raised, bay));
        }

        /// <summary>
        /// 高度图像素对应的地形局部米。Island 放在原点时与世界米相同。
        /// 噪声绑在高度图上，移动 Terrain 物体不会改结果。
        /// </summary>
        /// <param name="x">列，0 .. resolution-1。</param>
        /// <param name="z">行，0 .. resolution-1。</param>
        float2 LocalMeters(int x, int z)
        {
            float denom = math.max(resolution - 1, 1);
            return new float2(x / denom * sizeX, z / denom * sizeZ);
        }

        /// <summary>
        /// 局部海平面偏移，归一化高度。幅度是 bay01（米 / size.y），频率是周/米。
        /// snoise 返回约 -1..1，所以偏移落在 ±bay01。
        /// </summary>
        /// <param name="localXZ">地形局部坐标，米。</param>
        float BayOffset(float2 localXZ)
        {
            float n = noise.snoise(localXZ * bayFrequency + seedOffset);
            return n * bay01;
        }

        /// <summary>
        /// 用源高度的世界坡度做掩码。低于 minSlopeDegrees（度，0..60）为 0，
        /// 再抬高 8 度后为 1。平地不加山脊。
        /// </summary>
        /// <param name="x">列。</param>
        /// <param name="z">行。</param>
        float SlopeMask(int x, int z)
        {
            float degrees = SlopeDegrees(source, x, z);
            return math.smoothstep(minSlopeDegrees, minSlopeDegrees + 8f, degrees);
        }

        /// <summary>
        /// 源高度是否落在海岸带内。带是 [sea+bay-below, sea+bay+above]，都是归一化高度。
        /// </summary>
        /// <param name="height01">该像素的归一化高度，0..1。</param>
        /// <param name="bay">该点的海平面偏移，归一化。</param>
        bool InCoastBand(float height01, float bay)
        {
            float localSea = sea01 + bay;
            return height01 >= localSea - below01 && height01 <= localSea + above01;
        }

        /// <summary>
        /// Ridged multifractal，返回 0..1。
        /// frequency 是第一档的周/米；octaves 1..8；lacunarity 1.5..3.5；gain 0.3..0.7；sharpness 1..4。
        /// 调用前频率已经按 4 像素波长夹过。
        /// </summary>
        /// <param name="localXZ">地形局部坐标，米。</param>
        float Ridged(float2 localXZ)
        {
            float sum = 0f;
            float amp = 1f;
            float norm = 0f;
            float freq = frequency;
            float2 p = localXZ + seedOffset;
            for (int i = 0; i < octaves; i++)
            {
                float n = noise.snoise(p * freq);
                float ridge = math.pow(math.saturate(1f - math.abs(n)), sharpness);
                sum += ridge * amp;
                norm += amp;
                freq *= lacunarity;
                amp *= gain;
            }

            return norm > 1e-6f ? sum / norm : 0f;
        }

        /// <summary>
        /// 只重塑 [sea-below, sea+above] 里的高度。两端输出等于输入。
        /// 水上用 smoothstep 收到海平面与带上沿之间；水下用指数靠近海床，再在带下沿贴回原高度。
        /// sea、below、above、seabed、bay 都是归一化高度（米 / size.y）。
        /// </summary>
        /// <param name="height01">山脊之后的归一化高度。</param>
        /// <param name="bay">局部海平面偏移，归一化，约 ±bayMeters/size.y。</param>
        float ApplyCoast(float height01, float bay)
        {
            float localSea = sea01 + bay;
            float bandLow = localSea - below01;
            float bandHigh = localSea + above01;
            if (height01 <= bandLow || height01 >= bandHigh)
            {
                return height01;
            }

            float span = math.max(bandHigh - bandLow, 1e-5f);
            float u = (height01 - bandLow) / span;
            float target;
            if (height01 >= localSea)
            {
                float t = (height01 - localSea) / math.max(bandHigh - localSea, 1e-5f);
                target = math.lerp(localSea, bandHigh, math.smoothstep(0f, 1f, t));
            }
            else
            {
                float t = (localSea - height01) / math.max(localSea - bandLow, 1e-5f);
                float bed = math.min(seabed01, bandLow);
                float expT = 1f - math.exp(-3f * t);
                float towardBed = math.lerp(localSea, bed, expT);
                target = math.lerp(towardBed, bandLow, t * t);
            }

            float pin = math.sin(u * math.PI);
            return math.lerp(height01, target, pin);
        }

        /// <summary>
        /// 中心差分得到的坡度，单位度。高差乘 size.y，水平距离是像素边长（米）。
        /// 边缘像素会缩短差分跨度，不会读出数组。
        /// </summary>
        /// <param name="heights">归一化高度，行主序 z * resolution + x。</param>
        /// <param name="x">列。</param>
        /// <param name="z">行。</param>
        float SlopeDegrees(NativeArray<float> heights, int x, int z)
        {
            int x0 = math.max(x - 1, 0);
            int x1 = math.min(x + 1, resolution - 1);
            int z0 = math.max(z - 1, 0);
            int z1 = math.min(z + 1, resolution - 1);
            float hL = heights[z * resolution + x0];
            float hR = heights[z * resolution + x1];
            float hD = heights[z0 * resolution + x];
            float hU = heights[z1 * resolution + x];
            float dx = (hR - hL) * sizeY / math.max((x1 - x0) * texelX, 1e-4f);
            float dz = (hU - hD) * sizeY / math.max((z1 - z0) * texelZ, 1e-4f);
            return math.degrees(math.atan(math.length(new float2(dx, dz))));
        }
    }

    /// <summary>
    /// 在烘焙后的高度上做山脊掩码：正的地形位置指数 × 坡度 × 这次实际抬高的部分。
    /// 输出与 alphamap 一一对应，范围 0..1。
    /// </summary>
    [BurstCompile]
    struct RidgeMaskJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> baseHeights;
        [ReadOnly] public NativeArray<float> outHeights;
        [WriteOnly] public NativeArray<float> mask;
        public int heightRes;
        public int alphaW;
        public int alphaH;
        public float sizeY;
        public float texelX;
        public float texelZ;
        public int radius;
        public float minCrestMeters;
        public float minSlopeDegrees;
        public float raiseFeatherMeters;
        /// <summary>1 = 掩码还乘本次抬高；0 = 只用 TPI×坡度（仅修 splat 时）。</summary>
        public float requireRaise;

        public void Execute(int index)
        {
            int x = index % alphaW;
            int y = index / alphaW;
            float fx = AlphaToHeight(x, alphaW);
            float fz = AlphaToHeight(y, alphaH);
            float raised = Sample(outHeights, fx, fz);
            float basis = Sample(baseHeights, fx, fz);
            float slopeDeg = SlopeDegrees(fx, fz);
            float tpiMeters = (raised - NeighborhoodMean(fx, fz)) * sizeY;
            float tpiW = math.smoothstep(minCrestMeters, minCrestMeters + math.max(minCrestMeters, 0.5f), tpiMeters);
            float slopeW = math.smoothstep(minSlopeDegrees, minSlopeDegrees + 8f, slopeDeg);
            // 陡崖即使 TPI 不强也露岩石；平缓山脊仍靠正 TPI。
            float cliffW = math.smoothstep(36f, 52f, slopeDeg);
            float raiseMeters = (raised - basis) * sizeY;
            float raiseW = requireRaise > 0.5f
                ? math.smoothstep(0.05f, raiseFeatherMeters, raiseMeters)
                : 1f;
            float crest = math.max(tpiW * slopeW, cliffW * math.max(tpiW, 0.35f));
            mask[index] = math.saturate(crest * raiseW);
        }

        /// <summary>
        /// alphamap 像素中心在高度图上的连续坐标。两端是 0 和 heightRes-1。
        /// </summary>
        /// <param name="pixel">alphamap 列或行。</param>
        /// <param name="alphaRes">该方向的 alphamap 分辨率。</param>
        float AlphaToHeight(int pixel, int alphaRes)
        {
            if (alphaRes <= 1)
            {
                return 0f;
            }

            return pixel / (float)(alphaRes - 1) * (heightRes - 1);
        }

        /// <summary>
        /// 邻域平均高度，不含中心。半径单位是 alphamap 像素，使用时夹到 1..8。
        /// 比邻域高的地方 TPI 为正，才是山脊。
        /// </summary>
        /// <param name="fx">高度图连续列坐标。</param>
        /// <param name="fz">高度图连续行坐标。</param>
        float NeighborhoodMean(float fx, float fz)
        {
            float stepX = (heightRes - 1) / (float)math.max(alphaW - 1, 1);
            float stepZ = (heightRes - 1) / (float)math.max(alphaH - 1, 1);
            float sum = 0f;
            int count = 0;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    sum += Sample(outHeights, fx + dx * stepX, fz + dz * stepZ);
                    count++;
                }
            }

            return count > 0 ? sum / count : Sample(outHeights, fx, fz);
        }

        /// <summary>
        /// 烘焙高度的坡度，度。差分跨一个高度图像素，水平长度是 texelX / texelZ（米）。
        /// </summary>
        /// <param name="fx">高度图连续列坐标。</param>
        /// <param name="fz">高度图连续行坐标。</param>
        float SlopeDegrees(float fx, float fz)
        {
            float hL = Sample(outHeights, fx - 1f, fz);
            float hR = Sample(outHeights, fx + 1f, fz);
            float hD = Sample(outHeights, fx, fz - 1f);
            float hU = Sample(outHeights, fx, fz + 1f);
            float dx = (hR - hL) * sizeY / math.max(2f * texelX, 1e-4f);
            float dz = (hU - hD) * sizeY / math.max(2f * texelZ, 1e-4f);
            return math.degrees(math.atan(math.length(new float2(dx, dz))));
        }

        /// <summary>
        /// 双线性采样归一化高度。坐标会夹在高度图内。
        /// </summary>
        /// <param name="heights">行主序高度，z * heightRes + x。</param>
        /// <param name="fx">连续列，0 .. heightRes-1。</param>
        /// <param name="fz">连续行，0 .. heightRes-1。</param>
        float Sample(NativeArray<float> heights, float fx, float fz)
        {
            fx = math.clamp(fx, 0f, heightRes - 1);
            fz = math.clamp(fz, 0f, heightRes - 1);
            int x0 = (int)math.floor(fx);
            int z0 = (int)math.floor(fz);
            int x1 = math.min(x0 + 1, heightRes - 1);
            int z1 = math.min(z0 + 1, heightRes - 1);
            float tx = fx - x0;
            float tz = fz - z0;
            float a = math.lerp(heights[z0 * heightRes + x0], heights[z0 * heightRes + x1], tx);
            float b = math.lerp(heights[z1 * heightRes + x0], heights[z1 * heightRes + x1], tx);
            return math.lerp(a, b, tz);
        }
    }

    /// <summary>
    /// 3×3 盒式模糊，半径 1 个 alphamap 像素。抹掉山脊掩码上的单像素噪点。
    /// </summary>
    [BurstCompile]
    struct BlurMaskJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> source;
        [WriteOnly] public NativeArray<float> dest;
        public int width;
        public int height;

        public void Execute(int index)
        {
            int x = index % width;
            int y = index / width;
            float sum = 0f;
            for (int dy = -1; dy <= 1; dy++)
            {
                int sy = math.clamp(y + dy, 0, height - 1);
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sx = math.clamp(x + dx, 0, width - 1);
                    sum += source[sy * width + sx];
                }
            }

            dest[index] = sum / 9f;
        }
    }
}
