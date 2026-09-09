using System.Text.Json;
using A2A;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;

namespace CopilotStudioA2A;

/// <summary>
/// Changes text messages between A2A, Agent Framework, and Copilot Studio formats.
/// </summary>
internal static class TextTranslation
{
    public static string FromA2A(JsonElement message)
    {
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
        {
            throw A2AProfile.Error(A2AErrorCode.InvalidParams, "message.parts must contain at least one text part.");
        }

        var texts = new List<string>();
        foreach (var part in parts.EnumerateArray())
        {
            A2AProfile.OnlyProperties(part, "text", "raw", "url", "data", "mediaType", "metadata", "filename");
            var representations = new[] { "text", "raw", "url", "data" }.Count(name => A2AProfile.HasValue(part, name));
            if (representations != 1)
                throw A2AProfile.Error(A2AErrorCode.InvalidParams, "Each part must contain exactly one representation.");
            if (!A2AProfile.HasValue(part, "text"))
                throw A2AProfile.Error(A2AErrorCode.ContentTypeNotSupported, "Only text parts are supported; files, URLs and data are not accepted.");
            if (A2AProfile.HasValue(part, "mediaType") && A2AProfile.RequiredString(part, "mediaType") != A2AProfile.MediaType)
                throw A2AProfile.Error(A2AErrorCode.ContentTypeNotSupported, "Only text/plain input is supported.");
            if (A2AProfile.HasValue(part, "metadata") || A2AProfile.HasValue(part, "filename"))
                throw A2AProfile.Error(A2AErrorCode.UnsupportedOperation, "Part metadata and filenames are not supported.");
            texts.Add(A2AProfile.RequiredString(part, "text"));
        }

        // Preserve whitespace within each part; separate distinct parts with a newline.
        return string.Join('\n', texts);
    }

    public static IActivity ToCopilotMessage(string text)
    {
        var activity = Activity.CreateMessageActivity();
        activity.Text = text;
        return activity;
    }

    public static async Task<string> FromCopilotAsync(IAsyncEnumerable<IActivity> activities, CancellationToken cancellationToken)
    {
        var texts = new List<string>();
        var length = 0;
        await foreach (var activity in activities.WithCancellation(cancellationToken))
        {
            ValidateCopilotActivity(activity);
            // Typing, progress and deltas are not answers. Use only committed message activities,
            // so streaming deltas followed by the final message cannot duplicate the answer.
            if (!string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(activity.Text)) continue;
            length += activity.Text.Length + (texts.Count > 0 ? 1 : 0);
            if (length > 262144)
                throw new CopilotResponseException("Copilot Studio returned an answer exceeding the adapter's text limit.");
            texts.Add(activity.Text);
        }
        if (texts.Count == 0)
            throw new CopilotResponseException("Copilot Studio returned no final text answer. Check that the agent is published and supports the text-only channel.");
        return string.Join('\n', texts);
    }

    public static void ValidateCopilotActivity(IActivity activity)
    {
        if (activity.Attachments?.Any(attachment => attachment.ContentType == "application/vnd.microsoft.card.oauth") == true)
            throw new CopilotResponseException("Copilot Studio requested interactive sign-in. This text-only adapter supports delegated transport OBO, not interactive OAuth cards.");
        if (activity.Attachments?.Count > 0)
            throw new CopilotResponseException("Copilot Studio returned attachments. Configure this agent for text-only responses.");
        if (string.Equals(activity.Type, "event", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(activity.Name, "error", StringComparison.OrdinalIgnoreCase))
            throw new CopilotResponseException("Copilot Studio reported an error activity.");
    }

    public static ChatResponse ToAgentResponse(string text, string conversationId, string messageId) =>
        new(new ChatMessage(ChatRole.Assistant, text) { MessageId = messageId })
        {
            ConversationId = conversationId,
            ResponseId = messageId
        };
}

/// <summary>
/// Reports that Copilot Studio returned a response the adapter cannot use.
/// </summary>
internal sealed class CopilotResponseException(string message) : Exception(message);