using System;

namespace Game
{
    /// <summary>
    /// Logical sound asset: bank + event. Asset name format: "BankName/EventName".
    /// </summary>
    public sealed class WanosSoundAsset
    {
        public string BankName { get; }
        public string EventName { get; }
        public string AssetName { get; }

        public WanosSoundAsset(string bankName, string eventName)
        {
            BankName = bankName ?? "";
            EventName = eventName ?? "";
            AssetName = string.IsNullOrEmpty(BankName)
                ? EventName
                : BankName + "/" + EventName;
        }

        public static bool TryParse(string assetName, out WanosSoundAsset asset)
        {
            asset = null;
            if (string.IsNullOrEmpty(assetName))
                return false;

            int slash = assetName.IndexOf('/');
            if (slash <= 0 || slash >= assetName.Length - 1)
                return false;

            string bank = assetName.Substring(0, slash);
            string evt = assetName.Substring(slash + 1);
            if (string.IsNullOrEmpty(bank) || string.IsNullOrEmpty(evt))
                return false;

            asset = new WanosSoundAsset(bank, evt);
            return true;
        }

        public static string Format(string bankName, string eventName)
            => (bankName ?? "") + "/" + (eventName ?? "");
    }
}
