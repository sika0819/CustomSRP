using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    /// <summary>
    /// Advanced Mesh API upload (Stylized Water 2 WaterMesh-style layout, no legacy SetVertices).
    /// </summary>
    public static class OilOceanAdvancedMesh
    {
        public static readonly Vector4 DefaultTangent = new Vector4(-1f, 0f, 0f, -1f);

        static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2)
        };

        const MeshUpdateFlags UploadFlags =
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

        public static Mesh CreateShell(string meshName)
        {
            return new Mesh
            {
                name = meshName,
                hideFlags = HideFlags.DontSave,
                indexFormat = IndexFormat.UInt32
            };
        }

        /// <summary>
        /// Boluo WaterMesh-style bounds: flat surface with vertical padding for GPU displacement.
        /// </summary>
        public static Bounds BoundsForOcean(float sizeX, float sizeZ, float waveHeight, float boundsPadding)
        {
            float pad = Mathf.Max(0f, boundsPadding);
            float yExtent = Mathf.Max(waveHeight * 4f + 40f, pad);
            return new Bounds(
                new Vector3(sizeX * 0.5f, waveHeight, sizeZ * 0.5f),
                new Vector3(sizeX, yExtent, sizeZ));
        }

        public static void Apply(
            Mesh mesh,
            NativeArray<OilOceanVertex> vertices,
            NativeArray<int> indices,
            int vertexCount,
            int indexCount,
            Bounds bounds)
        {
            if (mesh == null || vertexCount <= 0 || indexCount < 3)
            {
                return;
            }

            Mesh.MeshDataArray dataArray = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData data = dataArray[0];
            data.SetVertexBufferParams(vertexCount, VertexLayout);
            data.SetIndexBufferParams(indexCount, IndexFormat.UInt32);

            data.GetVertexData<OilOceanVertex>().CopyFrom(vertices);
            data.GetIndexData<int>().CopyFrom(indices);

            data.subMeshCount = 1;
            data.SetSubMesh(
                0,
                new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles),
                UploadFlags);

            Mesh.ApplyAndDisposeWritableMeshData(dataArray, mesh, UploadFlags);
            mesh.MarkDynamic();
            mesh.bounds = bounds;
        }

        /// <summary>
        /// First call builds topology. Later calls write vertices only — indices do not change.
        /// </summary>
        public static void Upload(
            Mesh mesh,
            NativeArray<OilOceanVertex> vertices,
            NativeArray<int> indices,
            int vertexCount,
            int indexCount,
            Bounds bounds)
        {
            bool sameTopology = mesh.vertexCount == vertexCount
                && mesh.subMeshCount > 0
                && mesh.GetIndexCount(0) == (uint)indexCount;
            if (!sameTopology)
            {
                Apply(mesh, vertices, indices, vertexCount, indexCount, bounds);
                return;
            }

            mesh.SetVertexBufferData(vertices, 0, 0, vertexCount, 0, UploadFlags);
            mesh.bounds = bounds;
        }
    }
}
