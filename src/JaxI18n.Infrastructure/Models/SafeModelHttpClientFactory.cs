using System.Net;

namespace JaxI18n.Infrastructure.Models;

/// <summary>
/// Creates model clients that never automatically forward a provider request across redirects.
/// </summary>
public static class SafeModelHttpClientFactory
{
    public static HttpClient Create(TimeSpan? requestTimeout = null)
    {
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(2);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The model request timeout must be greater than zero and no more than ten minutes.");
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
            MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ResponseDrainTimeout = TimeSpan.FromSeconds(5)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout
        };
    }
}
