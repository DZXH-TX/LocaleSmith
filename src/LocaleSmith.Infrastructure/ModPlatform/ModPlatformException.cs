using System.Net;
using LocaleSmith.Core.Abstractions;

namespace LocaleSmith.Infrastructure.ModPlatform;

public sealed class ModPlatformException : Exception, IModPlatformServiceError
{
    public ModPlatformException(HttpStatusCode statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }

    public string Code { get; }
}
