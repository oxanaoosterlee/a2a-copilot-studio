namespace CopilotStudioA2A.Tests;

/// <summary>Specifies the offline backend's fixed response and conversation behavior.</summary>
public sealed class HardcodedBackendTests
{
    private readonly HardcodedBackend _sut = new();

    /// <summary>Returns the fixed response and creates an opaque conversation ID for a first turn.</summary>
    [Fact]
    public async Task GivenFirstTurn_WhenSending_ReturnsHardcodedResponseAndNewConversationId()
    {
        var reply = await _sut.SendAsync("CoolAgent", "ignored", "ignored", null, CancellationToken.None);

        Assert.Equal(HardcodedBackend.ResponseText, reply.Text);
        Assert.StartsWith("hardcoded-CoolAgent-", reply.ConversationId);
    }

    /// <summary>Preserves the existing downstream conversation ID for a continuation.</summary>
    [Fact]
    public async Task GivenContinuation_WhenSending_PreservesConversationId()
    {
        var reply = await _sut.SendAsync("CoolAgent", "ignored", "ignored", "existing-conversation", CancellationToken.None);

        Assert.Equal(HardcodedBackend.ResponseText, reply.Text);
        Assert.Equal("existing-conversation", reply.ConversationId);
    }

    /// <summary>Honors cancellation without producing a response.</summary>
    [Fact]
    public async Task GivenCancellation_WhenSending_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _sut.SendAsync("CoolAgent", "ignored", "ignored", null, cancellation.Token));
    }
}
