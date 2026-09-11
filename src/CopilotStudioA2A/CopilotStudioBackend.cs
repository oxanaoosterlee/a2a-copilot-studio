using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Agents.CopilotStudio.Client.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotStudioA2A;

/// <summary>
/// Defines how the adapter sends a message to Copilot Studio.
/// </summary>
internal interface ICopilotStudioBackend
{
    Task<CopilotReply> SendAsync(string agentName, string text, string userAssertion,
        string? conversationId, CancellationToken cancellationToken);
}

/// <summary>
/// Stores the text and conversation ID returned by Copilot Studio.
/// </summary>
internal sealed record CopilotReply(string Text, string ConversationId);

/// <summary>
/// Returns a fixed response without acquiring a token or contacting Copilot Studio.
/// </summary>
internal sealed class HardcodedBackend : ICopilotStudioBackend
{
    internal const string ResponseText = "This is a hardcoded response.";

    /// <inheritdoc/>
    public Task<CopilotReply> SendAsync(string agentName, string text, string userAssertion,
        string? conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CopilotReply(
            ResponseText,
            conversationId ?? $"hardcoded-{agentName}-{Guid.NewGuid():N}"));
    }
}

/// <summary>
/// Sends user messages to Copilot Studio and returns the final text answer.
/// </summary>
internal sealed class CopilotStudioBackend(
    CopilotStudioOptions options,
    IDelegatedTokenProvider tokens,
    IHttpClientFactory httpClients) : ICopilotStudioBackend
{
    internal const string HttpClientName = "copilot-studio";

    public async Task<CopilotReply> SendAsync(string agentName, string text, string userAssertion,
        string? conversationId, CancellationToken cancellationToken)
    {
        var settings = new ConnectionSettings
        {
            DirectConnectUrl = options.Agents[agentName].DirectConnectUrl,
            Cloud = PowerPlatformCloud.Prod,
            CopilotAgentType = AgentType.Published
        };
        // The SDK callback argument is a URL, NOT an OAuth scope.
        var scope = CopilotClient.ScopeFromSettings(settings);
        var accessToken = await tokens.AcquireAsync(scope, userAssertion, cancellationToken);
        var client = new CopilotClient(settings, httpClients,
            (string _) => Task.FromResult(accessToken), NullLogger<CopilotClient>.Instance, HttpClientName);

        if (conversationId is null)
        {
            await foreach (var activity in client.StartConversationAsync(
                emitStartConversationEvent: true, cancellationToken: cancellationToken))
            {
                TextTranslation.ValidateCopilotActivity(activity);
                conversationId ??= activity.Conversation?.Id;
                // Startup greetings are not the answer to the user's message.
            }
        }
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new CopilotResponseException("Copilot Studio did not provide a conversation ID. Check the published agent's DirectConnectUrl.");

        // Copilot Studio owns the history. Never prepend or replay an A2A transcript.
        // Do not retry sends or silently restart a rejected conversation: either can duplicate actions.
        var answer = await TextTranslation.FromCopilotAsync(
            client.ExecuteAsync(conversationId, TextTranslation.ToCopilotMessage(text), cancellationToken), cancellationToken);
        return new CopilotReply(answer, conversationId);
    }
}