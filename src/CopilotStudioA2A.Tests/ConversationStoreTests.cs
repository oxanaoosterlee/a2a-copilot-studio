using A2A;

namespace CopilotStudioA2A.Tests;

/// <summary>Specifies per-agent conversation identity, lease exclusion and capacity behavior.</summary>
public sealed class ConversationStoreTests
{
    private readonly ConversationStore _sut = new(new AdapterOptions { MaxConversations = 8 });

    /// <summary>Creates opaque server contexts rather than reusing downstream conversation IDs.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenNewConversations_WhenOpen_CreatesDistinctBusyContexts()
    {
        using var first = await _sut.OpenAsync("support", null);
        using var second = await _sut.OpenAsync("support", null);

        Assert.True(Guid.TryParse(first.Conversation.ContextId, out _));
        Assert.NotEqual(first.Conversation.ContextId, second.Conversation.ContextId);
        Assert.Null(first.Conversation.CopilotConversationId);
        Assert.True(first.Conversation.Busy);
        Assert.True(second.Conversation.Busy);
    }

    /// <summary>Continues successful conversations, including case-insensitive agent lookup.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenSuccessfulTurn_WhenReopened_PreservesConversationState()
    {
        var first = await _sut.OpenAsync("support", null);
        var conversation = first.Conversation;
        conversation.CopilotConversationId = "copilot-conversation-1";
        first.Succeeded = true;
        first.Dispose();
        Assert.False(conversation.Busy);

        using var continuation = await _sut.OpenAsync("SUPPORT", conversation.ContextId);

        Assert.Same(conversation, continuation.Conversation);
        Assert.Equal("copilot-conversation-1", continuation.Conversation.CopilotConversationId);
        Assert.True(continuation.Conversation.Busy);
    }

    /// <summary>Accepts and preserves caller contexts for new conversations.</summary>
    /// <param name="context">A caller-supplied context.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("unknown")]
    [InlineData("00000000000000000000000000000000")]
    public async Task GivenNewCallerContext_WhenOpen_CreatesConversationWithSuppliedId(string context)
    {
        using var conversation = await _sut.OpenAsync("support", context);

        Assert.Equal(context, conversation.Conversation.ContextId);
        Assert.Null(conversation.Conversation.CopilotConversationId);
    }

    /// <summary>Uses the same caller context as an independent new conversation on another agent.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenOtherAgentsContext_WhenOpen_CreatesIndependentConversation()
    {
        using var support = await _sut.OpenAsync("support", null);

        using var billing = await _sut.OpenAsync("billing", support.Conversation.ContextId);

        Assert.NotSame(support.Conversation, billing.Conversation);
        Assert.Equal(support.Conversation.ContextId, billing.Conversation.ContextId);
        Assert.Null(billing.Conversation.CopilotConversationId);
    }

    /// <summary>Queues an overlapping turn until the current turn releases the context.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenBusyContext_WhenOpen_QueuesOverlappingTurn()
    {
        var first = await _sut.OpenAsync("support", null);

        var nextTask = _sut.OpenAsync("support", first.Conversation.ContextId).AsTask();
        Assert.False(nextTask.IsCompleted);
        first.Succeeded = true;
        first.Dispose();
        using var next = await nextTask;

        Assert.Same(first.Conversation, next.Conversation);
        Assert.True(next.Conversation.Busy);
    }

    /// <summary>Queues simultaneous contenders without timing-dependent sleeps.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenConcurrentContenders_WhenOpen_GrantsEveryLeaseSequentially()
    {
        var seed = await _sut.OpenAsync("support", null);
        var contextId = seed.Conversation.ContextId;
        seed.Succeeded = true;
        seed.Dispose();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 16).Select(async _ =>
        {
            await start.Task;
            return await _sut.OpenAsync("support", contextId);
        }).ToArray();

        start.SetResult(true);
        var pending = attempts.ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            using var lease = await completed;
            Assert.Same(seed.Conversation, lease.Conversation);
        }
    }

    /// <summary>Removes an unsuccessful caller context and allows a clean retry.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenFailedFirstTurn_WhenDisposed_RemovesContextAndAllowsReplacement()
    {
        var store = new ConversationStore(new AdapterOptions { MaxConversations = 1 });
        var failed = await store.OpenAsync("support", "caller-context");
        var contextId = failed.Conversation.ContextId;
        failed.Conversation.CopilotConversationId = "partially-created-downstream-state";

        failed.Dispose();

        using var replacement = await store.OpenAsync("support", contextId);
        Assert.NotSame(failed.Conversation, replacement.Conversation);
        Assert.Equal(contextId, replacement.Conversation.ContextId);
        Assert.Null(replacement.Conversation.CopilotConversationId);
    }

    /// <summary>A failed continuation releases exclusion without silently restarting the conversation.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenFailedContinuation_WhenDisposed_PreservesCommittedContext()
    {
        var seed = await _sut.OpenAsync("support", null);
        seed.Conversation.CopilotConversationId = "existing-downstream";
        seed.Succeeded = true;
        seed.Dispose();
        var failed = await _sut.OpenAsync("support", seed.Conversation.ContextId);

        failed.Dispose();
        using var retry = await _sut.OpenAsync("support", seed.Conversation.ContextId);

        Assert.Same(seed.Conversation, retry.Conversation);
        Assert.Equal("existing-downstream", retry.Conversation.CopilotConversationId);
    }

    /// <summary>Repeated disposal must not release a different active lease on the same conversation.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenDisposedLease_WhenDisposedAgain_DoesNotUnlockCurrentTurn()
    {
        var seed = await _sut.OpenAsync("support", null);
        seed.Succeeded = true;
        seed.Dispose();
        var current = await _sut.OpenAsync("support", seed.Conversation.ContextId);

        seed.Dispose();
        var nextTask = _sut.OpenAsync("support", seed.Conversation.ContextId).AsTask();
        Assert.False(nextTask.IsCompleted);
        current.Dispose();
        using var next = await nextTask;

        Assert.Same(current.Conversation, next.Conversation);
    }

    /// <summary>Rejects new contexts when every capacity slot is busy, without evicting active work.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenCapacityAllBusy_WhenOpen_RejectsNewContextAcrossAgents()
    {
        var store = new ConversationStore(new AdapterOptions { MaxConversations = 2 });
        var support = await store.OpenAsync("support", null);
        using var billing = await store.OpenAsync("billing", null);

        var exception = await Assert.ThrowsAsync<A2AException>(
            () => store.OpenAsync("support", null).AsTask());

        Assert.Equal(A2AErrorCode.InternalError, exception.ErrorCode);
        Assert.True(support.Conversation.Busy);
        Assert.True(billing.Conversation.Busy);
        var continuationTask = store.OpenAsync("support", support.Conversation.ContextId).AsTask();
        Assert.False(continuationTask.IsCompleted);
        support.Succeeded = true;
        support.Dispose();
        using var continuation = await continuationTask;
        Assert.Same(support.Conversation, continuation.Conversation);
    }

    /// <summary>Reclaims the least recently used idle context and permits that ID to start fresh.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenFullIdleCapacity_WhenOpen_EvictsOldestAndRestartsItsContext()
    {
        var store = new ConversationStore(new AdapterOptions { MaxConversations = 2 });
        var oldest = await store.OpenAsync("support", null);
        oldest.Succeeded = true;
        oldest.Dispose();
        var recent = await store.OpenAsync("billing", null);
        recent.Succeeded = true;
        recent.Dispose();

        var replacement = await store.OpenAsync("support", null);

        Assert.Equal(("support", oldest.Conversation.ContextId), replacement.Evicted);
        replacement.Succeeded = true;
        replacement.Dispose();
        using var restarted = await store.OpenAsync("support", oldest.Conversation.ContextId);
        Assert.NotSame(oldest.Conversation, restarted.Conversation);
        Assert.Equal(oldest.Conversation.ContextId, restarted.Conversation.ContextId);
        Assert.Null(restarted.Conversation.CopilotConversationId);
    }

    /// <summary>Preserves the configured service key even after reopening a context with different casing.</summary>
    /// <param name="originalName">The original mixed-case name used for registration.</param>
    /// <param name="nextName">The agent that replaces the capacity-one entry.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("CoolAgent", "BackupAgent")]
    [InlineData("BackupAgent", "CoolAgent")]
    public async Task GivenMixedCaseConversation_WhenEvicted_ReturnsOriginalAgentName(string originalName, string nextName)
    {
        var store = new ConversationStore(new AdapterOptions { MaxConversations = 1 });
        var first = await store.OpenAsync(originalName, null);
        var conversation = first.Conversation;
        first.Succeeded = true;
        first.Dispose();
        using (var continuation = await store.OpenAsync(originalName.ToLowerInvariant(), conversation.ContextId))
        {
            Assert.Same(conversation, continuation.Conversation);
            Assert.Equal(originalName, continuation.Conversation.AgentName);
            continuation.Succeeded = true;
        }

        var replacement = await store.OpenAsync(nextName, null);

        Assert.Equal(originalName, conversation.AgentName);
        Assert.Equal((originalName, conversation.ContextId), replacement.Evicted);
        Assert.Equal(nextName, replacement.Conversation.AgentName);
        replacement.Succeeded = true;
        replacement.Dispose();
        using var restarted = await store.OpenAsync(originalName, conversation.ContextId);
        Assert.NotSame(conversation, restarted.Conversation);
        Assert.Equal(originalName, restarted.Conversation.AgentName);
        Assert.Null(restarted.Conversation.CopilotConversationId);
    }
}