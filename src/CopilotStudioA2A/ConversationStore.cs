using A2A;

namespace CopilotStudioA2A;

/// <summary>
/// Keeps active conversations in memory and limits how many conversations can be stored.
/// </summary>
internal sealed class ConversationStore(AdapterOptions options)
{
    private readonly Dictionary<(string Agent, string Context), Conversation> _conversations = new();
    private readonly Lock _sync = new();
    private long _sequence;

    public async ValueTask<ConversationLease> OpenAsync(
        string agentName,
        string? contextId,
        CancellationToken cancellationToken = default)
    {
        Conversation conversation;
        (string Agent, string Context)? evicted;
        (string Agent, string Context) key;
        bool isNew;
        using (_sync.EnterScope())
        {
            evicted = null;
            key = (agentName.ToLowerInvariant(), contextId ?? Guid.NewGuid().ToString("N"));
            if (!_conversations.TryGetValue(key, out conversation))
            {
                isNew = true;
                if (_conversations.Count >= options.MaxConversations)
                {
                    var candidate = _conversations.Where(entry => !entry.Value.Busy)
                        .OrderBy(entry => entry.Value.LastUsed).FirstOrDefault();
                    if (candidate.Value is null)
                        throw A2AProfile.Error(A2AErrorCode.InternalError, "The in-memory conversation capacity is busy; try a new request after active turns finish.");
                    // Dictionary lookups are normalized, but keyed framework services use the
                    // case-preserving configured name (for example CoolAgent).
                    evicted = (candidate.Value.AgentName, candidate.Key.Context);
                    _conversations.Remove(candidate.Key);
                }
                conversation = new Conversation(key.Item2, agentName);
                _conversations.Add(key, conversation);
            }
            else
            {
                isNew = false;
            }
            conversation.ActiveTurns++;
        }

        try
        {
            await conversation.TurnGate.WaitAsync(cancellationToken);
        }
        catch
        {
            using (_sync.EnterScope())
            {
                conversation.ActiveTurns--;
                if (isNew) _conversations.Remove(key);
            }
            throw;
        }

        return new ConversationLease(conversation, success =>
        {
            using (_sync.EnterScope())
            {
                conversation.ActiveTurns--;
                conversation.LastUsed = ++_sequence;
                if (isNew && !success) _conversations.Remove(key);
            }
            conversation.TurnGate.Release();
        }, evicted);
    }
}

/// <summary>
/// Stores the state of one conversation with one configured agent.
/// </summary>
internal sealed class Conversation(string contextId, string agentName)
{
    public int ActiveTurns { get; set; }
    public string ContextId { get; } = contextId;
    public string AgentName { get; } = agentName;
    public string? CopilotConversationId { get; set; }
    public bool Busy => ActiveTurns > 0;
    public long LastUsed { get; set; }
    public SemaphoreSlim TurnGate { get; } = new(1, 1);
}

/// <summary>
/// Gives a request temporary access to a conversation and releases it after use.
/// </summary>
internal sealed class ConversationLease(Conversation conversation, Action<bool> release,
    (string Agent, string Context)? evicted = null) : IDisposable
{
    private bool _disposed;
    public Conversation Conversation { get; } = conversation;
    public (string Agent, string Context)? Evicted { get; } = evicted;
    public bool Succeeded { get; set; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        release(Succeeded);
    }
}

/// <summary>
/// Stores the validated request data used during one agent call.
/// </summary>
internal sealed class InvocationContext
{
    private static readonly AsyncLocal<InvocationContext?> CurrentContext = new();
    public static InvocationContext Current
    {
        get => CurrentContext.Value ?? throw new InvalidOperationException("No validated A2A request is active.");
        set => CurrentContext.Value = value;
    }
    public required string AgentName { get; init; }
    public required string Text { get; init; }
    public required string BearerToken { get; init; }
    public required Conversation Conversation { get; init; }
    public required CancellationToken CancellationToken { get; init; }
    public A2AException? Failure { get; set; }
    public static void Clear() => CurrentContext.Value = null;
}