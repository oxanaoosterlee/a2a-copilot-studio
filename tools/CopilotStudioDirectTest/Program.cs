using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Agents.CopilotStudio.Client.Discovery;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;

const string tenantVariable = "COPILOT_STUDIO_TENANT_ID";
const string clientVariable = "COPILOT_STUDIO_CLIENT_ID";
const string directConnectVariable = "COPILOT_STUDIO_DIRECT_CONNECT_URL";
const string redirectUriVariable = "COPILOT_STUDIO_REDIRECT_URI";

try
{
    var tenantId = RequiredGuidEnvironmentVariable(tenantVariable);
    var clientId = RequiredGuidEnvironmentVariable(clientVariable);
    var directConnectUrl = RequiredDirectConnectUrl(directConnectVariable);
    var redirectUri = RequiredRedirectUri(
        Environment.GetEnvironmentVariable(redirectUriVariable) ?? "http://localhost");
    var prompt = args.Length == 0 ? "Reply with a short greeting." : string.Join(' ', args);

    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var settings = new ConnectionSettings
    {
        DirectConnectUrl = directConnectUrl,
        Cloud = PowerPlatformCloud.Prod,
        CopilotAgentType = AgentType.Published
    };
    var scope = CopilotClient.ScopeFromSettings(settings);
    var application = PublicClientApplicationBuilder
        .Create(clientId)
        .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
        .WithRedirectUri(redirectUri.GetLeftPart(UriPartial.Authority))
        .Build();

    var authentication = await application
        .AcquireTokenInteractive([scope])
        .WithUseEmbeddedWebView(false)
        .ExecuteAsync(cancellation.Token);

    var client = new CopilotClient(
        settings,
        new TestHttpClientFactory(),
        _ => Task.FromResult(authentication.AccessToken),
        NullLogger<CopilotClient>.Instance,
        TestHttpClientFactory.ClientName);

    string? conversationId = null;
    await foreach (var activity in client.StartConversationAsync(
        emitStartConversationEvent: true,
        cancellationToken: cancellation.Token))
    {
        conversationId ??= activity.Conversation?.Id;
    }

    if (string.IsNullOrWhiteSpace(conversationId))
        throw new InvalidOperationException("Copilot Studio did not return a conversation ID.");

    var message = Activity.CreateMessageActivity();
    message.Text = prompt;
    var responseText = new List<string>();

    await foreach (var activity in client.ExecuteAsync(
        conversationId,
        message,
        cancellation.Token))
    {
        if (activity.Attachments?.Any() == true)
            Console.Error.WriteLine("Warning: the response included an attachment that this simple test does not render.");

        if (string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(activity.Text))
        {
            responseText.Add(activity.Text);
        }
    }

    if (responseText.Count == 0)
        throw new InvalidOperationException("Copilot Studio returned no final text message.");

    Console.WriteLine(string.Join(Environment.NewLine, responseText));
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The direct Copilot Studio test was canceled or timed out.");
    return 1;
}
catch (MsalException exception)
{
    Console.Error.WriteLine($"Microsoft Entra authentication failed ({exception.ErrorCode}).");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Direct Copilot Studio test failed: {exception.Message}");
    return 1;
}

static string RequiredGuidEnvironmentVariable(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (!Guid.TryParse(value, out _))
        throw new InvalidOperationException($"Set {name} to a valid GUID.");
    return value!;
}

static string RequiredDirectConnectUrl(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        uri.Scheme != Uri.UriSchemeHttps ||
        !string.IsNullOrEmpty(uri.UserInfo) ||
        !string.IsNullOrEmpty(uri.Fragment) ||
        !uri.Host.EndsWith(".environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Set {name} to the HTTPS DirectConnectUrl copied from Copilot Studio.");
    }

    return value!;
}

static Uri RequiredRedirectUri(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        uri.Scheme != Uri.UriSchemeHttp ||
        !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrEmpty(uri.UserInfo) ||
        !string.IsNullOrEmpty(uri.Query) ||
        !string.IsNullOrEmpty(uri.Fragment) ||
        uri.AbsolutePath != "/")
    {
        throw new InvalidOperationException(
            $"Set {redirectUriVariable} to http://localhost or an explicitly registered localhost port.");
    }

    return uri;
}

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    internal const string ClientName = "copilot-studio-direct-test";

    /// <inheritdoc/>
    public HttpClient CreateClient(string name)
    {
        if (!string.Equals(name, ClientName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected HTTP client name: {name}.");

        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
    }
}
