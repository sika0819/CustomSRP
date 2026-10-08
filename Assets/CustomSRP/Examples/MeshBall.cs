using UnityEngine;

namespace CustomSRP.Examples
{
    /// <summary>
    /// Draw Calls / Directional Lights：Graphics.DrawMeshInstanced 一次画最多 1023 个实例。
    /// 材质需开启 GPU Instancing。
    /// </summary>
    public class MeshBall : MonoBehaviour
    {
        const int MaxInstances = 1023;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");

        [SerializeField] Mesh mesh;
        [SerializeField] Material material;

        readonly Matrix4x4[] matrices = new Matrix4x4[MaxInstances];
        readonly Vector4[] baseColors = new Vector4[MaxInstances];
        readonly float[] metallic = new float[MaxInstances];
        readonly float[] smoothness = new float[MaxInstances];

        MaterialPropertyBlock block;

        void Awake()
        {
            for (int i = 0; i < MaxInstances; i++)
            {
                matrices[i] = Matrix4x4.TRS(
                    Random.insideUnitSphere * 10f,
                    Quaternion.Euler(
                        Random.value * 360f,
                        Random.value * 360f,
                        Random.value * 360f),
                    Vector3.one * Mathf.Lerp(0.5f, 1.5f, Random.value));
                baseColors[i] = new Vector4(
                    Random.value,
                    Random.value,
                    Random.value,
                    Mathf.Lerp(0.5f, 1f, Random.value));
                metallic[i] = Random.value < 0.25f ? 1f : 0f;
                smoothness[i] = Random.Range(0.05f, 0.95f);
            }
        }

        void Update()
        {
            if (mesh == null || material == null)
            {
                return;
            }

            if (block == null)
            {
                block = new MaterialPropertyBlock();
                block.SetVectorArray(BaseColorId, baseColors);
                block.SetFloatArray(MetallicId, metallic);
                block.SetFloatArray(SmoothnessId, smoothness);
            }

            Graphics.DrawMeshInstanced(mesh, 0, material, matrices, MaxInstances, block);
        }
    }
}
