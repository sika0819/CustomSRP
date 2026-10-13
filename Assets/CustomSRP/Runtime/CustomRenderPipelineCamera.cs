using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public class CustomRenderPipelineCamera : MonoBehaviour
    {
        [SerializeField]
        CameraSettings settings = default;

        ProfilingSampler _sampler;

        public ProfilingSampler Sampler =>
            _sampler ??= new ProfilingSampler(GetComponent<Camera>().name);

        public CameraSettings Settings => settings ??= new CameraSettings();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void OnEnable() => _sampler = null;
#endif
    }
}
