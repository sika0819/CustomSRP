using System.Collections.Generic;
using Game;
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
    /// Recorded AFTER OilFilter, BEFORE ColourLUT.
    /// Cheap: ~2 multiply-adds per pixel + a depth sample.
    /// </summary>
    public class OilRainPostPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Oil Rain Post");
        static readonly ProfilerMarker MarkerRender = new ProfilerMarker("OilRainPost.Render");

        static readonly int
            TexelSizeId = Shader.PropertyToID("_OilFilterTexelSize"),
            DepthCopyId = Shader.PropertyToID("_CameraDepthTexture_ForRain"),
            InverseVPId  = Shader.PropertyToID("_InverseViewProjectionMatrix"),
            VPId         = Shader.PropertyToID("_ViewProjectionMatrix"),
            RipplePositionsId = Shader.PropertyToID("_OilRainRipplePositions"),
            RippleWorldSizeId = Shader.PropertyToID("_OilRainRippleWorldSize"),
            RippleExpandId    = Shader.PropertyToID("_OilRainRippleExpandSpeed"),
            RippleOpacityId   = Shader.PropertyToID("_OilRainRippleOpacity"),
            RippleCountId     = Shader.PropertyToID("_OilRainRippleCount");

        const int kMaxRipples = 8;
        readonly Vector4[] _ripplePositions = new Vector4[kMaxRipples];
        int _rippleWriteIndex;

        // Pending ripples submitted from main thread (OilRainRenderHook) —
        // we drain into _ripplePositions in Render() each pass. Static so
        // producer lifetime is independent of pass instance lifetime.
        static readonly Queue<Vector3> s_pendingRipples = new Queue<Vector3>(64);
        static readonly object s_pendingLock = new object();

        TextureHandle _source;
        TextureHandle _result;
        TextureHandle _depth;
        PostFXStack _stack;
        OilRainSettings _rainSettings;
        Vector2Int _bufferSize;

        /// <summary>Public count (visible to the shader pass) of active ripples.</summary>
        int _activeRippleCount;

        void Render(UnsafeGraphContext context)
        {
            using (MarkerRender.Auto())
            {
                UnsafeCommandBuffer buffer = context.cmd;

                // --- shared globals ---
                int w = Mathf.Max(1, _bufferSize.x);
                int h = Mathf.Max(1, _bufferSize.y);
                buffer.SetGlobalVector(TexelSizeId, new Vector4(1f / w, 1f / h, w, h));
                buffer.SetGlobalTexture(DepthCopyId, _depth);

                // --- rain globals ---
                // Island camera when the island is up; Camera.main only before that.
                Camera cam = IslandJoystickCamera.ActiveView;
                Matrix4x4 vp = (cam != null)
                    ? GL.GetGPUProjectionMatrix(cam.projectionMatrix, cam.allowMSAA) * cam.worldToCameraMatrix
                    : Matrix4x4.identity;
                buffer.SetGlobalMatrix(VPId, vp);
                buffer.SetGlobalMatrix(InverseVPId, vp.inverse);

                // Drain pending ripples from main-thread producers.
                DrainPendingRipples();

                // Read settings ONCE into locals (was: redundant dead-branch)
                float worldSize   = _rainSettings != null ? _rainSettings.postRippleWorldSize   : 3f;
                float expandSpeed = _rainSettings != null ? _rainSettings.postRippleExpandSpeed : 2f;
                float opacity     = _rainSettings != null ? _rainSettings.postRippleOpacity     : 0.4f;
                float lifetimeSec = 3f;

                int count = 0;
                float now = Time.time;
                for (int i = 0; i < kMaxRipples; i++)
                {
                    if (now - _ripplePositions[i].w < lifetimeSec) count++;
                }
                _activeRippleCount = count;

                buffer.SetGlobalFloat(RippleCountId, _activeRippleCount);
                buffer.SetGlobalFloat(RippleWorldSizeId, worldSize);
                buffer.SetGlobalFloat(RippleExpandId, expandSpeed);
                buffer.SetGlobalFloat(RippleOpacityId, opacity);
                buffer.SetGlobalVectorArray(RipplePositionsId, _ripplePositions);

                // --- blit ---
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
            if (rainSettings == null || !rainSettings.postRippleEnabled)
                return colorSource;

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out OilRainPostPass pass, Sampler);
            pass._stack = stack;
            pass._rainSettings = rainSettings;
            pass._source = colorSource;
            pass._bufferSize = stack.BufferSize;
            builder.UseTexture(colorSource);

            // Need depth for water detection (sky pixels must skip)
            TextureHandle depth = textures.depthCopy.IsValid()
                ? textures.depthCopy
                : textures.depthAttachment;
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

        /// <summary>Submit a new ripple at world position.</summary>
        public void AddRipple(Vector3 worldPos)
        {
            _ripplePositions[_rippleWriteIndex] = new Vector4(
                worldPos.x, worldPos.y, worldPos.z, Time.time);
            _rippleWriteIndex = (_rippleWriteIndex + 1) % kMaxRipples;
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
                // Cap to avoid runaway if the post pass is disabled.
                if (s_pendingRipples.Count >= 64)
                    s_pendingRipples.Dequeue();
                s_pendingRipples.Enqueue(worldPos);
            }
        }

        void DrainPendingRipples()
        {
            lock (s_pendingLock)
            {
                while (s_pendingRipples.Count > 0)
                {
                    Vector3 pos = s_pendingRipples.Dequeue();
                    AddRipple(pos);
                }
            }
        }
    }
}
