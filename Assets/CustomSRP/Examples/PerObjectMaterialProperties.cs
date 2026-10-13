using UnityEngine;

namespace CustomSRP.Examples
{
    /// <summary>
    /// 用 MaterialPropertyBlock 覆盖单物体 Lit/Unlit 属性。
    /// 会打断 SRP Batcher，但可与 GPU Instancing 同用。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    public class PerObjectMaterialProperties : MonoBehaviour
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
        static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        static MaterialPropertyBlock block;

        [SerializeField] Color baseColor = Color.white;
        [SerializeField, Range(0f, 1f), UnityEngine.Serialization.FormerlySerializedAs("cutoff")]
        float alphaCutoff = 0.5f;
        [SerializeField, Range(0f, 1f)] float metallic = 0f, smoothness = 0.5f;
        [SerializeField, ColorUsage(false, true)] Color emissionColor = Color.black;

        void Awake()
        {
            OnValidate();
        }

        void OnValidate()
        {
            if (block == null)
            {
                block = new MaterialPropertyBlock();
            }

            block.SetColor(BaseColorId, baseColor);
            block.SetFloat(CutoffId, alphaCutoff);
            block.SetFloat(MetallicId, metallic);
            block.SetFloat(SmoothnessId, smoothness);
            block.SetColor(EmissionColorId, emissionColor);
            GetComponent<Renderer>().SetPropertyBlock(block);
        }
    }
}
