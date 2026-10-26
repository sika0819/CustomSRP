using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static CustomSRP.PostFXStack;

namespace CustomSRP
{
    /// <summary>
    /// Screen-space water surface ripple post-process.
    /// Recorded AFTER OilFilter, BEFORE ColourLUT (FXAA path),
    /// or folded into RainFinal when !FXAA.
    /// Cheap: ~2 multiply-adds per pixel + a depth sample.
    /// </summary>
    public class OilRainPostPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Oil Rain Post");
        static readonly ProfilerMarker MarkerRender = new ProfilerMarker("OilRainPost.Render");

        static readonly int
            TexelSizeId = Shader.PropertyToID("_OilFilterTexelSize"),
            DepthCopyId = Shader.PropertyToID("_CameraDepthTexture_ForRain"),
            InverseVPId = Shader.PropertyToID("_InverseViewProjectionMatrix"),
            VPId = Shader.PropertyToID("_ViewProjectionMatrix"),
            RipplePositionsId = Shader.PropertyToID("_OilRainRipplePositions"),
            RippleWorldSizeId = Shader.PropertyToID("_OilRainRippleWorldSize"),
            RippleExpandId = Shader.PropertyToID("_OilRainRippleExpandSpeed"),
            RippleOpacityId = Shader.PropertyToID("_OilRainRippleOpacity"),
            RippleCountId = Shader.PropertyToID("_OilRainRippleCount");

        const int kMaxRipples = 8;
        // Persistent across RG pass instances so rings can live ~3s.
        static readonly Vector4[] s_ripplePositions = new Vector4[kMaxRipples];
        static int s_rippleWriteIndex;

        static readonly Queue<Vector3> s_pendingRipples = new Queue<Vector3>(64);
        static readonly object s_pendingLock = new object();

        TextureHandle _source;
        TextureHandle _result;
        TextureHandle _depth;
        PostFXStack _stack;
        OilRainSettings _rainSettings;
        Vector2Int _bufferSize;

        public static bool IsActive(OilRainSettings rainSettings) =>
            rainSettings != null && rainSettings.postRippleEnabled;

        public static TextureHandle ResolveDepth(in CameraRendererTextures textures)
        {
            return textures.depthCopy.IsValid()
                ? textures.depthCopy
                : textures.depthAttachment;
        }

        public static void BindGlobals(
            UnsafeCommandBuffer buffer,
            Vector2Int bufferSize,
            TextureHandle depth,
            OilRainSettings rainSettings)
        {
            int w = Mathf.Max(1, bufferSize.x);
            int h = Mathf.Max(1, bufferSize.y);
            buffer.SetGlobalVector(TexelSizeId, new Vector4(1f / w, 1f / h, w, h));
            if (depth.IsValid())
                buffer.SetGlobalTexture(DepthCopyId, depth);

            Camera cam = Camera.main;
            Matrix4x4 vp = cam != null
                ? GL.GetGPUProjectionMatrix(cam.projectionMatrix, cam.allowMSAA)
                  * cam.worldToCameraMatrix
                : Matrix4x4.identity;
            buffer.SetGlobalMatrix(VPId, vp);
            buffer.SetGlobalMatrix(InverseVPId, vp.inverse);

            DrainPendingRipples();

            float worldSize = rainSettings != null ? rainSettings.postRippleWorldSize : 3f;
            float expandSpeed = rainSettings != null ? rainSettings.postRippleExpandSpeed : 2f;
            float opacity = rainSettings != null ? rainSettings.postRippleOpacity : 0.4f;
            const float lifetimeSec = 3f;

            int count = 0;
            float now = Time.time;
            for (int i = 0; i < kMaxRipples; i++)
            {
                if (now - s_ripplePositions[i].w < lifetimeSec)
                    count++;
            }

            buffer.SetGlobalFloat(RippleCountId, count);
            buffer.SetGlobalFloat(RippleWorldSizeId, worldSize);
            buffer.SetGlobalFloat(RippleExpandId, expandSpeed);
            buffer.SetGlobalFloat(RippleOpacityId, opacity);
            buffer.SetGlobalVectorArray(RipplePositionsId, s_ripplePositions);
        }

        void Render(UnsafeGraphContext context)
        {
            using (MarkerRender.Auto())
            {
                UnsafeCommandBuffer buffer = context.cmd;
                BindGlobals(buffer, _bufferSize, _depth, _rainSettings);
                _stack.Draw(buffer, _source, _result, Pass.OilRain);
            }
        }

        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            TextureHandle colorSource,
            in CameraRendererTextures textures,
            OilRainSettings rainSettings)
        {
            if (!IsActive(rainSettings))
                return colorSource;

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out OilRainPostPass pass, Sampler);
            pass._stack = stack;
            pass._rainSettings = rainSettings;
            pass._source = colorSource;
            pass._bufferSize = stack.BufferSize;
            builder.UseTexture(colorSource);

            TextureHandle depth = ResolveDepth(textures);
            if (depth.IsValid())
            {
                pass._depth = depth;
                builder.UseTexture(depth);
            }

            var desc = new TextureDesc(stack.BufferSize.x, stack.BufferSize.y)
            {
                colorFormat = SystemInfo.GetGraphicsFormat(
                    stack.BufferSettings.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR),
                name = "Oil Rain Post",
                filterMode = FilterMode.Bilinear
            };
            pass._result = renderGraph.CreateTexture(desc);
            builder.UseTexture(pass._result, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<OilRainPostPass>(
                static (p, context) => p.Render(context));
            return pass._result;
        }

        static void AddRipple(Vector3 worldPos)
        {
            s_ripplePositions[s_rippleWriteIndex] = new Vector4(
                worldPos.x, worldPos.y, worldPos.z, Time.time);
            s_rippleWriteIndex = (s_rippleWriteIndex + 1) % kMaxRipples;
        }

        /// <summary>
        /// Main-thread producer API: enqueue a screen-space ripple to be picked
        /// up by the next OilRainPostPass render. Bounded by kMaxRipples so the
        /// static queue stays small.
        /// </summary>
        public static void EnqueueRipple(Vector3 worldPos)
        {
            lock (s_pendingLock)
            {
                if (s_pendingRipples.Count >= 64)
                    s_pendingRipples.Dequeue();
                s_pendingRipples.Enqueue(worldPos);
            }
        }

        static void DrainPendingRipples()
        {
            lock (s_pendingLock)
            {
                while (s_pendingRipples.Count > 0)
                    AddRipple(s_pendingRipples.Dequeue());
            }
        }
    }
}
