using UnityEngine;

namespace Game
{
    [CreateAssetMenu(menuName = "Game/Island Preset", fileName = "IslandPreset")]
    public sealed class IslandPresetAsset : ScriptableObject
    {
        public int Id = 1;
        public string DisplayName;
        [TextArea] public string Description;
        public Texture2D Cover;
        public Vector3 TargetPos;
        public float TargetRotateY;
        public Vector3 CameraOffset;
        public float CameraToTargetDis = 3f;
        public float PlayerHeight = 6f;
        [Range(0f, 24f)] public float TimeHours = 12f;
        public IslandWeatherKind WeatherKind = IslandWeatherKind.Cloud;
        [Range(0, 3)] public int WeatherLevel = 1;
        [Tooltip("Exponential Squared fog density for this preset (Custom SRP / RenderSettings).")]
        [Min(0f)] public float FogDensity = 0.001f;
        [Tooltip("Optional directional-light color override. Alpha=0 means use the time-of-day table; alpha>0 forces this color (RGB) on the main light and fog tint.")]
        public Color LightColorOverride = new Color(0f, 0f, 0f, 0f);
    }
}
