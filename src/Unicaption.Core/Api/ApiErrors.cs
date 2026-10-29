namespace Unicaption.Core.Api;

/// <summary>Non-2xx response. <see cref="Body"/> is for diagnostics only, never shown to users (HTTP 01).</summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string body)
        : base($"Server returned HTTP {statusCode}.")
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }
    public string Body { get; }

    public bool IsUnauthorized => StatusCode == 401;
}

/// <summary>2xx response whose JSON does not match the expected contract.</summary>
public sealed class ApiSchemaException(string message) : Exception(message);

/// <summary>Maps exceptions to short, user-facing text without raw bodies, tokens or URLs (UI 06).</summary>
public static class ErrorText
{
    public static string For(Exception e) => e switch
    {
        ApiException { StatusCode: 401 or 403 } => "Your session has expired. Please sign in again.",
        ApiException { StatusCode: 402 } => "Your plan limit has been reached.",
        ApiException { StatusCode: >= 500 } => "The server is unavailable. Please try again.",
        ApiException => "The request could not be completed.",
        ApiSchemaException => "Unexpected response from the server.",
        TimeoutException or TaskCanceledException => "The request timed out. Check your connection.",
        HttpRequestException => "Could not connect. Check your connection.",
        _ => "Something went wrong. Please try again.",
    };
}
