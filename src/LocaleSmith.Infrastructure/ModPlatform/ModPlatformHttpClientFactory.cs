using System.Net;

namespace LocaleSmith.Infrastructure.ModPlatform;

public static class ModPlatformHttpClientFactory
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultTransferTimeout = TimeSpan.FromMinutes(30);

    public static HttpClient Create(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultRequestTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The Mod platform request timeout must be greater than zero and no more than one hour.");
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
            MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            MaxResponseContentBufferSize = 8 * 1024 * 1024,
            Timeout = effectiveTimeout
        };
    }

    public static HttpClient CreateForTransfer() => Create(DefaultTransferTimeout);
}
