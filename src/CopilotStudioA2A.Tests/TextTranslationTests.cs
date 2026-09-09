using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;

namespace CopilotStudioA2A.Tests;

/// <summary>Specifies lossless text translation and rejection of unsupported Copilot responses.</summary>
public sealed class TextTranslationTests
{
    private static async IAsyncEnumerable<IActivity> ActivitiesAsync(IEnumerable<IActivity> activities,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var activity in activities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return activity;
        }
    }

    /// <summary>Requires a nonempty array of parts.</summary>
    /// <param name="json">An invalid message.</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"parts\":null}")]
    [InlineData("{\"parts\":{}}")]
    [InlineData("{\"parts\":[]}")]
    public void GivenMissingParts_WhenFromA2A_RejectsMessage(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.Throws<A2AException>(() => TextTranslation.FromA2A(document.RootElement));

        Assert.Equal(A2AErrorCode.InvalidParams, exception.ErrorCode);
    }

    /// <summary>Rejects ambiguous representations, nontext media and unsupported part properties.</summary>
    /// <param name="part">An invalid part.</param>
    /// <param name="code">The expected error.</param>
    [Theory]
    [InlineData("null", A2AErrorCode.InvalidParams)]
    [InlineData("\"text\"", A2AErrorCode.InvalidParams)]
    [InlineData("[]", A2AErrorCode.InvalidParams)]
    [InlineData("{}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":null}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":42}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":\"\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":\"  \"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"raw\":\"YQ==\"}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("{\"url\":\"https://example.invalid/file\"}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("{\"data\":{\"value\":1}}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("{\"text\":\"hello\",\"raw\":\"YQ==\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":\"hello\",\"url\":\"https://example.invalid/file\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":\"hello\",\"data\":{}}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"raw\":\"YQ==\",\"url\":\"https://example.invalid/file\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":\"hello\",\"mediaType\":\"image/png\"}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("{\"text\":\"hello\",\"mediaType\":\"text/html\"}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("{\"text\":\"hello\",\"mediaType\":\"text/plain; charset=utf-8\"}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("{\"text\":\"hello\",\"mediaType\":42}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":\"hello\",\"metadata\":{}}", A2AErrorCode.UnsupportedOperation)]
    [InlineData("{\"text\":\"hello\",\"filename\":\"file.txt\"}", A2AErrorCode.UnsupportedOperation)]
    [InlineData("{\"text\":\"hello\",\"kind\":\"text\"}", A2AErrorCode.InvalidParams)]
    public void GivenUnsupportedPart_WhenFromA2A_ReturnsSpecificError(string part, A2AErrorCode code)
    {
        using var document = JsonDocument.Parse("{\"parts\":[" + part + "]}");

        var exception = Assert.Throws<A2AException>(() => TextTranslation.FromA2A(document.RootElement));

        Assert.Equal(code, exception.ErrorCode);
    }

    /// <summary>Preserves internal whitespace and Unicode while separating distinct text parts.</summary>
    [Fact]
    public void GivenMultipleTextParts_WhenFromA2A_JoinsWithoutTrimming()
    {
        using var document = JsonDocument.Parse("""
            {"parts":[
              {"text":"  Hello\nworld  ","mediaType":"text/plain"},
              {"text":"Grüße 🌍","raw":null,"url":null,"data":null,"metadata":null,"filename":null}
            ]}
            """);

        var result = TextTranslation.FromA2A(document.RootElement);

        Assert.Equal("  Hello\nworld  \nGrüße 🌍", result);
    }

    /// <summary>Does not silently discard a nontext part following valid text.</summary>
    [Fact]
    public void GivenMixedTextAndMediaParts_WhenFromA2A_RejectsWholeMessage()
    {
        using var document = JsonDocument.Parse("""{"parts":[{"text":"hello"},{"data":{"value":1}}]}""");

        var exception = Assert.Throws<A2AException>(() => TextTranslation.FromA2A(document.RootElement));

        Assert.Equal(A2AErrorCode.ContentTypeNotSupported, exception.ErrorCode);
    }

    /// <summary>Creates one message activity without altering the caller's text.</summary>
    [Fact]
    public void GivenText_WhenToCopilotMessage_PreservesMessageContent()
    {
        const string text = "  first\nsecond 🌍  ";

        var activity = TextTranslation.ToCopilotMessage(text);

        Assert.Equal("message", activity.Type);
        Assert.Equal(text, activity.Text);
        Assert.True(activity.Attachments is null || activity.Attachments.Count == 0);
    }

    /// <summary>Ignores typing, event progress and streaming deltas preceding the committed message.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenProgressAndDeltas_WhenFromCopilot_UsesFinalAnswerOnce()
    {
        IActivity[] activities =
        [
            new Activity { Type = "typing", Text = "Hel" },
            new Activity { Type = "typing", Text = "Hello" },
            new Activity { Type = "event", Name = "progress", Text = "Working" },
            new Activity { Type = "event", Name = "delta", Text = "Hello" },
            new Activity { Type = "message", Text = "Hello" },
            new Activity { Type = "message", Text = "   " }
        ];

        var answer = await TextTranslation.FromCopilotAsync(ActivitiesAsync(activities), CancellationToken.None);

        Assert.Equal("Hello", answer);
    }

    /// <summary>Preserves the order and whitespace of multiple final messages.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenMultipleFinalMessages_WhenFromCopilot_JoinsInOrder()
    {
        IActivity[] activities =
        [
            new Activity { Type = "MESSAGE", Text = " first " },
            new Activity { Type = "message", Text = "second\nthird" }
        ];

        var answer = await TextTranslation.FromCopilotAsync(ActivitiesAsync(activities), CancellationToken.None);

        Assert.Equal(" first \nsecond\nthird", answer);
    }

    /// <summary>Requires a committed text answer even if the stream contains progress.</summary>
    /// <param name="kind">The kind of empty response stream.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("null")]
    [InlineData("progress")]
    public async Task GivenNoFinalText_WhenFromCopilot_RejectsResponse(string kind)
    {
        IActivity[] activities = kind switch
        {
            "blank" => [new Activity { Type = "message", Text = "  " }],
            "null" => [new Activity { Type = "message" }],
            "progress" => [new Activity { Type = "typing", Text = "not final" }],
            _ => []
        };

        await Assert.ThrowsAsync<CopilotResponseException>(() =>
            TextTranslation.FromCopilotAsync(ActivitiesAsync(activities), CancellationToken.None));
    }

    /// <summary>Rejects attachments, including OAuth cards, on startup activities before any send.</summary>
    /// <param name="type">The activity type encountered during startup.</param>
    /// <param name="contentType">The attachment MIME type.</param>
    [Theory]
    [InlineData("message", "application/vnd.microsoft.card.oauth")]
    [InlineData("event", "application/vnd.microsoft.card.oauth")]
    [InlineData("typing", "application/vnd.microsoft.card.adaptive")]
    [InlineData("message", "image/png")]
    public void GivenStartupAttachment_WhenValidate_RejectsUnsupportedResponse(string type, string contentType)
    {
        var activity = new Activity
        {
            Type = type,
            Text = "A greeting does not make attachments supported.",
            Attachments = [new Attachment { ContentType = contentType }]
        };

        var exception = Assert.Throws<CopilotResponseException>(() => TextTranslation.ValidateCopilotActivity(activity));

        Assert.Contains(contentType == "application/vnd.microsoft.card.oauth" ? "interactive sign-in" : "attachments", exception.Message);
    }

    /// <summary>Rejects startup error events regardless of case or accompanying text.</summary>
    [Fact]
    public void GivenStartupError_WhenValidate_RejectsActivity()
    {
        var activity = new Activity { Type = "EVENT", Name = "ERROR", Text = "sensitive downstream failure" };

        var exception = Assert.Throws<CopilotResponseException>(() => TextTranslation.ValidateCopilotActivity(activity));

        Assert.DoesNotContain(activity.Text, exception.Message);
    }

    /// <summary>Validates the entire stream instead of returning a partial answer before a late attachment.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenAttachmentAfterText_WhenFromCopilot_RejectsWholeResponse()
    {
        IActivity[] activities =
        [
            new Activity { Type = "message", Text = "partial answer" },
            new Activity { Type = "message", Attachments = [new Attachment { ContentType = "image/png" }] }
        ];

        await Assert.ThrowsAsync<CopilotResponseException>(() =>
            TextTranslation.FromCopilotAsync(ActivitiesAsync(activities), CancellationToken.None));
    }

    /// <summary>Enforces the final-text size boundary across a response stream.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenTextLimit_WhenFromCopilot_AcceptsBoundaryAndRejectsOverflow()
    {
        var boundary = new Activity { Type = "message", Text = new string('x', 262144) };

        var accepted = await TextTranslation.FromCopilotAsync(ActivitiesAsync([boundary]), CancellationToken.None);
        Assert.Equal(boundary.Text, accepted);

        await Assert.ThrowsAsync<CopilotResponseException>(() => TextTranslation.FromCopilotAsync(
            ActivitiesAsync([boundary, new Activity { Type = "message", Text = "x" }]), CancellationToken.None));
    }

    /// <summary>Counts inserted separators toward the total returned-text limit.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenJoinedTextExceedsLimit_WhenFromCopilot_RejectsIncludingSeparators()
    {
        IActivity[] activities =
        [
            new Activity { Type = "message", Text = new string('a', 131072) },
            new Activity { Type = "message", Text = new string('b', 131072) }
        ];

        // The final answer would have 262145 characters, including the separating newline.
        await Assert.ThrowsAsync<CopilotResponseException>(() =>
            TextTranslation.FromCopilotAsync(ActivitiesAsync(activities), CancellationToken.None));
    }

    /// <summary>Propagates cancellation into activity enumeration.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenCancellation_WhenFromCopilot_DoesNotReturnAnAnswer()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TextTranslation.FromCopilotAsync(
            ActivitiesAsync([new Activity { Type = "message", Text = "answer" }]), cancellation.Token));
    }

    /// <summary>Builds one assistant message with explicit response and conversation identifiers.</summary>
    [Fact]
    public void GivenReply_WhenToAgentResponse_PreservesIdentifiersAndText()
    {
        var response = TextTranslation.ToAgentResponse("answer", "downstream-conversation", "server-message");

        Assert.Equal("downstream-conversation", response.ConversationId);
        Assert.Equal("server-message", response.ResponseId);
        var message = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal("server-message", message.MessageId);
        Assert.Equal("answer", message.Text);
    }
}