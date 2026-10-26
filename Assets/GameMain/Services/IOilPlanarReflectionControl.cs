namespace Game
{
    /// <summary>
    /// Implemented by Assembly-CSharp <c>OilPlanarReflection</c> so Game can
    /// suspend the mirror RT during idle immersive without an asmdef cycle.
    /// </summary>
    public interface IOilPlanarReflectionControl
    {
        void SuspendForImmersive();
        void ResumeFromImmersive();
    }

    public static class OilPlanarReflectionControl
    {
        public static IOilPlanarReflectionControl Driver { get; set; }

        public static void SuspendForImmersive() => Driver?.SuspendForImmersive();

        public static void ResumeFromImmersive() => Driver?.ResumeFromImmersive();
    }
}
