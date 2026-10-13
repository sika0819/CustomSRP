using YooAsset;

namespace Game
{
    /// <summary>
    /// 远端资源地址查询。对齐 BoluoUnity RemoteServices。
    /// </summary>
    public sealed class YooRemoteServices : IRemoteServices
    {
        readonly string _defaultHostServer;
        readonly string _fallbackHostServer;

        public YooRemoteServices(string defaultHostServer, string fallbackHostServer)
        {
            _defaultHostServer = defaultHostServer;
            _fallbackHostServer = fallbackHostServer;
        }

        string IRemoteServices.GetRemoteMainURL(string fileName)
        {
            return $"{_defaultHostServer}/{fileName}";
        }

        string IRemoteServices.GetRemoteFallbackURL(string fileName)
        {
            return $"{_fallbackHostServer}/{fileName}";
        }
    }
}
