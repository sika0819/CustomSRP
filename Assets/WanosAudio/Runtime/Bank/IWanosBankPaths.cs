namespace WanosAudio
{
    /// <summary>Injected by the game layer (e.g. BoluoAudioBanks).</summary>
    public interface IWanosBankPaths
    {
        /// <summary>Resolve a bank stem/name to an existing file path, or null.</summary>
        bool TryResolve(string bankName, out string absolutePath);
    }
}
