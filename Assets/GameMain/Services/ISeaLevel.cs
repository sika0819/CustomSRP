namespace Game
{
    /// <summary>
    /// 海面高度。管线类型留在 Assembly-CSharp，Game 只读这份。
    /// </summary>
    public interface ISeaLevel
    {
        float Level { get; }
        float GridScale { get; }
    }
}
