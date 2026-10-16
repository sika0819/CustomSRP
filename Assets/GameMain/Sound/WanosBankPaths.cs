using Game.BoluoApi;
using WanosAudio;

namespace Game
{
    /// <summary>Maps bank names to <see cref="AudioEnginePaths"/> (BoluoAudioBanks).</summary>
    public sealed class WanosBankPaths : IWanosBankPaths
    {
        public static readonly WanosBankPaths Instance = new WanosBankPaths();

        public bool TryResolve(string bankName, out string absolutePath)
            => AudioEnginePaths.TryFindLocalBank(bankName, out absolutePath);
    }
}
