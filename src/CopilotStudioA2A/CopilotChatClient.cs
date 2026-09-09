using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A;
using Microsoft.Extensions.AI;
using Microsoft.Identity.Client;

namespace CopilotStudioA2A;

/// <summary>
/// Connects Agent Framework chat requests to a Copilot Studio agent.
/// </summary>
internal sealed class CopilotChatClient(string agentName, ICopilotStudioBackend backend, ILogger<CopilotChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var context = InvocationContext.Current;
        using var activity = AdapterTelemetry.Source.StartActivity("copilot_studio.invoke", ActivityKind.Client);
        activity?.SetTag("agent.name", agentName);
        activity?.SetTag("conversation.continued", context.Conversation.CopilotConversationId is not null);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken);
        try
        {
            if (context.AgentName != agentName)
                throw new InvalidOperationException("The registered agent does not match the validated route.");

            var reply = await backend.SendAsync(agentName, context.Text, context.BearerToken,
                context.Conversation.CopilotConversationId, linked.Token);
            context.Conversation.CopilotConversationId = reply.ConversationId;
            activity?.SetStatus(ActivityStatusCode.Ok);
            logger.LogInformation("Copilot Studio turn completed for agent {AgentName}.", agentName);
            return TextTranslation.ToAgentResponse(reply.Text, reply.ConversationId, Guid.NewGuid().ToString("N"));
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            AdapterTelemetry.LogFailure(logger, exception, agentName);
            context.Failure = exception switch
            {
                OperationCanceledException => A2AProfile.Error(A2AErrorCode.InternalError, "The Copilot Studio turn was canceled or exceeded the configured timeout. Its completion is unknown; do not automatically replay it."),
                MsalUiRequiredException => A2AProfile.Error(A2AErrorCode.UnsupportedOperation, "Delegated consent or interactive sign-in is required. Sign in again and verify the adapter's delegated Copilot Studio permission."),
                CopilotResponseException => A2AProfile.Error(A2AErrorCode.InvalidAgentResponse, exception.Message),
                _ => A2AProfile.Error(A2AErrorCode.InternalError, "The Copilot Studio invocation failed. Use the X-Correlation-ID response header to locate the server log.")
            };
            // The hosting layer may aggregate failures. The transport guard uses this shared,
            // request-local failure to return the same sanitized error, never the SDK exception.
            throw context.Failure;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Agent Framework may internally enumerate updates in message mode. This yields exactly
        // one COMPLETE reply; the public SendStreamingMessage operation is rejected at the edge.
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text)
        {
            ConversationId = response.ConversationId,
            ResponseId = response.ResponseId,
            MessageId = response.ResponseId
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}