namespace CopilotStudioA2A.Tests;

/// <summary>Provides an independent offline host and HTTP client for each integration test case.</summary>
public abstract class AdapterIntegrationTestsBase : IAsyncLifetime
{
    private HttpClient? _client;

    /// <summary>Gets the test server factory with its per-test recorded backend.</summary>
    protected AdapterWebApplicationFactory Factory { get; } = new();

    /// <summary>Gets the in-process client after initialization.</summary>
    protected HttpClient Client => _client ?? throw new InvalidOperationException("The test has not been initialized.");

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _client = Factory.CreateLocalClient();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        try
        {
            _client?.Dispose();
        }
        finally
        {
            await Factory.DisposeAsync();
        }
    }
}