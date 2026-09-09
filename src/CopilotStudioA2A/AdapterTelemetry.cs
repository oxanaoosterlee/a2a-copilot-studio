using System.Diagnostics;
using Microsoft.Identity.Client;

namespace CopilotStudioA2A;

/// <summary>
/// Records adapter activity and writes error details without exposing secrets.
/// </summary>
internal static class AdapterTelemetry
{
    internal const string SourceName = "CopilotStudioA2A";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");

    public static void LogFailure(ILogger logger, Exception exception, string agentName)
    {
        // SDK exception messages, URLs, response bodies and inner exceptions can contain secrets.
        // Retain the exception type, stack and safe status/code, not the original exception payload.
        logger.LogError(new SanitizedException(exception),
            "Agent {AgentName} failed. ExceptionType={ExceptionType}; HttpStatus={HttpStatus}; IdentityError={IdentityError}.",
            agentName, exception.GetType().Name, (exception as HttpRequestException)?.StatusCode,
            (exception as MsalException)?.ErrorCode);
    }

    /// <summary>
    /// Keeps a safe stack trace while hiding exception details that may contain secrets.
    /// </summary>
    private sealed class SanitizedException(Exception original) : Exception("Adapter operation failed; sensitive exception detail suppressed.")
    {
        private readonly string? _stackTrace = original.StackTrace;
        public override string? StackTrace => _stackTrace;
    }
}