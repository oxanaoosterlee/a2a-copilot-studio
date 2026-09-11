using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Identity.Client;

const string tenantVariable = "A2A_TENANT_ID";
const string adapterClientVariable = "A2A_ADAPTER_CLIENT_ID";
const string testClientVariable = "A2A_TEST_CLIENT_ID";
const string agentVariable = "A2A_AGENT";
const string baseUrlVariable = "A2A_BASE_URL";
const string redirectUriVariable = "A2A_REDIRECT_URI";

try
{
    var tenantId = RequiredGuidEnvironmentVariable(tenantVariable);
    var adapterClientId = RequiredGuidEnvironmentVariable(adapterClientVariable);
    var testClientId = RequiredGuidEnvironmentVariable(testClientVariable);
    var agent = OptionalEnvironmentVariable(agentVariable, "CoolAgent");
    var baseUrl = RequiredBaseUrl(OptionalEnvironmentVariable(baseUrlVariable, "http://localhost:5180"));
    var redirectUri = RequiredRedirectUri(OptionalEnvironmentVariable(redirectUriVariable, "http://localhost"));
    var prompt = args.Length == 0 ? "Reply with a short greeting." : string.Join(' ', args);

    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var application = PublicClientApplicationBuilder
        .Create(testClientId)
        .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
        .WithRedirectUri(redirectUri.GetLeftPart(UriPartial.Authority))
        .Build();
    var scope = $"api://{adapterClientId}/Agents.Invoke";
    var authentication = await application
        .AcquireTokenInteractive([scope])
        .WithUseEmbeddedWebView(false)
        .ExecuteAsync(cancellation.Token);

    using var client = new HttpClient
    {
        BaseAddress = baseUrl,
        Timeout = TimeSpan.FromMinutes(2)
    };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.AccessToken);
    client.DefaultRequestHeaders.Add("A2A-Version", "1.0");

    var request = new
    {
        jsonrpc = "2.0",
        id = Guid.NewGuid().ToString(),
        method = "SendMessage",
        @params = new
        {
            message = new
            {
                messageId = Guid.NewGuid().ToString(),
                role = "ROLE_USER",
                parts = new[]
                {
                    new { text = prompt, mediaType = "text/plain" }
                }
            },
            configuration = new
            {
                acceptedOutputModes = new[] { "text/plain" },
                returnImmediately = false
            }
        }
    };

    var path = $"copilot-studio/{Uri.EscapeDataString(agent)}/a2a";
    using var response = await client.PostAsJsonAsync(path, request, cancellation.Token);
    var responseText = await response.Content.ReadAsStringAsync(cancellation.Token);

    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"The adapter returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");

    using var responseJson = JsonDocument.Parse(responseText);
    Console.WriteLine(JsonSerializer.Serialize(responseJson, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The A2A test was canceled or timed out.");
    return 1;
}
catch (MsalException exception)
{
    Console.Error.WriteLine($"Microsoft Entra authentication failed ({exception.ErrorCode}).");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"A2A test failed: {exception.Message}");
    return 1;
}

static string RequiredGuidEnvironmentVariable(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (!Guid.TryParse(value, out _))
        throw new InvalidOperationException($"Set {name} to a valid GUID.");
    return value!;
}

static string OptionalEnvironmentVariable(string name, string defaultValue)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
}

static Uri RequiredBaseUrl(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        !string.IsNullOrEmpty(uri.UserInfo) ||
        !string.IsNullOrEmpty(uri.Query) ||
        !string.IsNullOrEmpty(uri.Fragment) ||
        (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)))
    {
        throw new InvalidOperationException(
            "Set A2A_BASE_URL to an HTTPS URL or a loopback HTTP URL without a query or fragment.");
    }

    return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
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
