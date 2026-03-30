using System.Net;

namespace SqDbAiAgent.ConsoleApp.Services;

internal static class LlmRetryPolicy
{
    public static bool ShouldRetry(Exception exception)
    {
        if (exception is HttpRequestException httpRequestException
            && httpRequestException.StatusCode is { } statusCode)
        {
            return IsRetryableStatusCode(statusCode);
        }

        return true;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        var statusCodeNumber = (int)statusCode;

        if (statusCodeNumber >= 500)
        {
            return true;
        }

        return statusCode is HttpStatusCode.RequestTimeout;
    }
}
