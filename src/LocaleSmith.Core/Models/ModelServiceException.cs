using System.Net;

namespace LocaleSmith.Core.Models;

public sealed class ModelServiceException : Exception
{
    public ModelServiceException(
        string message,
        HttpStatusCode? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null,
        string? requestId = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        RequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>A bounded, sanitized provider error summary; never the raw response body.</summary>
    public string? ResponseBody { get; }

    /// <summary>A provider request identifier safe to surface for support diagnostics.</summary>
    public string? RequestId { get; }
}
