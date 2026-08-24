using System.Net;

namespace LocaleSmith.Infrastructure.ModPlatform;

internal static class AcceleratedDownloadHttpClientFactory
{
    internal static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            Credentials = null,
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }
}
