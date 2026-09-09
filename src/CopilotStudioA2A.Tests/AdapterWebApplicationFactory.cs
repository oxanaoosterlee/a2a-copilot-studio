using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace CopilotStudioA2A.Tests;

/// <summary>
/// Hosts the real adapter and JWT bearer handler with ephemeral local signing keys and an offline backend.
/// No authentication handler, signature validator, issuer check, audience check or lifetime check is bypassed.
/// </summary>
/// <remarks>
/// An empty per-factory content root and the Testing environment exclude application JSON files and user secrets.
/// Explicit settings override environment values before Program loads options, without changing process state.
/// Unknown environment keys can still appear during that early load: standard WebApplicationFactory callbacks
/// cannot remove the default environment provider before it. Tests must mask missing required values explicitly
/// and scope unlisted-agent warning assertions to their synthetic agents, rather than assume a global count.
/// </remarks>
public sealed class AdapterWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly HttpClient _backchannel = new(new DenyNetworkHandler());
    private readonly DirectoryInfo _contentRoot;
    private readonly Dictionary<string, string?> _settings;
    private readonly RsaSecurityKey _signingKey;

    /// <summary>Creates an isolated two-agent host with synthetic configuration.</summary>
    public AdapterWebApplicationFactory() : this(includeBilling: true) { }

    internal AdapterWebApplicationFactory(bool includeBilling, int maxRequestBytes = 65536, int maxConversations = 16,
        Action<IDictionary<string, string?>>? configureSettings = null)
    {
        using var rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = Guid.NewGuid().ToString("N") };
        _settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authentication:TenantId"] = TestIdentity.TenantId,
            ["Authentication:ClientId"] = TestIdentity.ClientId,
            ["Authentication:Audience"] = TestIdentity.ClientId,
            ["Authentication:RequiredScope"] = TestIdentity.Scope,
            ["Authentication:ClientSecret"] = TestIdentity.ClientSecret,
            ["Adapter:PublicBaseUrl"] = "https://localhost",
            ["Adapter:MaxRequestBytes"] = maxRequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Adapter:MaxConversations"] = maxConversations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Adapter:RequestTimeoutSeconds"] = "30",
            ["Agents"] = includeBilling ? "[\"support\",\"billing\"]" : "[\"support\"]",
            ["support:DirectConnectUrl"] =
                "https://test.environment.api.powerplatform.com/copilotstudio/dataverse-backed/authenticated/bots/support/conversations?api-version=2022-03-01-preview",
            ["support:SkillDescription"] = "Answers support questions and helps troubleshoot product issues.",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "",
            ["AllowedHosts"] = "localhost",
            ["Logging:LogLevel:Default"] = "None"
        };
        if (includeBilling)
        {
            _settings["billing:DirectConnectUrl"] =
                "https://test.environment.api.powerplatform.com/copilotstudio/dataverse-backed/authenticated/bots/billing/conversations?api-version=2022-03-01-preview";
            _settings["billing:SkillDescription"] = "Explains invoices, payment status and billing charges.";
        }

        // Apply per-test additions, replacements and removals before Program creates or validates options.
        configureSettings?.Invoke(_settings);

        // Allocate only after per-test configuration succeeds; no files or process-wide settings are changed.
        // These host keys must reach CreateBuilder, not merely the callbacks replayed at Build.
        _settings[HostDefaults.EnvironmentKey] = "Testing";
        // A validation failure before Build leaves no host to dispose the default JSON file watchers.
        _settings["hostBuilder:reloadConfigOnChange"] = "false";
        _contentRoot = Directory.CreateTempSubdirectory("CopilotStudioA2A.Tests-");
        _settings[HostDefaults.ContentRootKey] = _contentRoot.FullName;
    }

    internal RecordingBackend Backend { get; } = new();
    internal string ContentRootPath => _contentRoot.FullName;
    internal RecordingLoggerProvider Logs { get; } = new();

    internal string CreateToken(IEnumerable<Claim>? claims = null, string? issuer = null, string? audience = null,
        DateTime? expires = null, DateTime? notBefore = null, bool wrongSignature = false,
        bool unsigned = false, bool omitExpiration = false, string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var signingKey = _signingKey;
        if (wrongSignature)
        {
            using var rsa = RSA.Create(2048);
            // Keep the kid identical: rejection must come from signature verification, not key lookup alone.
            signingKey = new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = _signingKey.KeyId };
        }

        var token = new JwtSecurityToken(
            issuer: issuer ?? TestIdentity.Issuer,
            audience: audience ?? TestIdentity.ClientId,
            claims: claims ?? TestIdentity.Claims(),
            notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-20),
            expires: omitExpiration ? null : expires ?? DateTime.UtcNow.AddMinutes(10),
            signingCredentials: unsigned ? null : new SigningCredentials(signingKey, algorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal HttpClient CreateLocalClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        // TestServer stays in process: an HTTPS origin does not require a listener or a certificate.
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = false
    });

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? token,
        string? json = null, string path = "/a2a/support", string? version = "1.0", string? contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json ?? TestRequests.Create().ToJsonString(), Encoding.UTF8)
        };
        request.Content.Headers.ContentType = contentType is null ? null : new MediaTypeHeaderValue(contentType);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (version is not null) request.Headers.Add("A2A-Version", version);
        return await client.SendAsync(request);
    }

    /// <inheritdoc/>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Minimal-host entry points read options before Build. Web-host configuration callbacks alone
        // arrive too late; the deferred host forwards settings, content root and environment as arguments
        // to CreateBuilder, so file selection and early options reads already see the test overrides.
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(_settings));
        return base.CreateHost(builder);
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Match the early host settings instead of restoring WebApplicationFactory's project content root.
        builder.UseContentRoot(ContentRootPath);
        builder.UseEnvironment("Testing");
        // Limit later configuration consumers too. This cannot undo options already read before Build.
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(_settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging =>
            {
                // Capture real startup warnings in memory only, regardless of configured category filters.
                logging.ClearProviders();
                logging.AddProvider(Logs);
                logging.AddFilter<RecordingLoggerProvider>((_, level) => level >= LogLevel.Warning);
            });
            services.RemoveAll<ICopilotStudioBackend>();
            services.AddSingleton<ICopilotStudioBackend>(Backend);
            services.RemoveAll<IDelegatedTokenProvider>();
            services.AddSingleton<IDelegatedTokenProvider>(_ =>
                throw new InvalidOperationException("Live OBO authentication is forbidden in these tests."));

            // Mock only the public HTTP factory, not internal interfaces requiring DynamicProxy visibility.
            var httpClients = Substitute.For<IHttpClientFactory>();
            httpClients.CreateClient(Arg.Any<string>()).Returns(_ =>
                throw new InvalidOperationException("External HTTP clients are forbidden in these tests."));
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton(httpClients);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = TestIdentity.Issuer };
                configuration.SigningKeys.Add(new RsaSecurityKey(new RSAParameters
                {
                    Modulus = _signingKey.Parameters.Modulus,
                    Exponent = _signingKey.Parameters.Exponent
                })
                { KeyId = _signingKey.KeyId });
                options.Configuration = configuration;
                // Replace the manager already installed by JwtBearerPostConfigureOptions, not just Configuration.
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.Backchannel?.Dispose();
                options.Backchannel = _backchannel;
                options.RefreshOnIssuerKeyNotFound = false;
            });
        });
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            DisposeTestResources();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing) DisposeTestResources();
        }
    }

    private void DisposeTestResources()
    {
        try
        {
            _backchannel.Dispose();
        }
        finally
        {
            // Release the host/file providers first, including when startup failed or was never attempted.
            // WebApplicationFactory can cross-call the disposal paths, so repeated cleanup must be harmless.
            try
            {
                Directory.Delete(ContentRootPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Already removed by an earlier disposal call. Other cleanup failures remain visible.
            }
        }
    }

    private sealed class DenyNetworkHandler : HttpMessageHandler
    {
        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("OIDC network access is forbidden in these tests.");
    }
}

internal sealed record BackendCall(string AgentName, string Text, string UserAssertion,
    string? ConversationId, CancellationToken CancellationToken);

internal sealed class RecordingBackend : ICopilotStudioBackend
{
    private readonly ConcurrentQueue<BackendCall> _calls = new();

    internal IReadOnlyList<BackendCall> Calls => _calls.ToArray();
    internal Func<BackendCall, Task<CopilotReply>>? ReplyAsync { get; set; }

    /// <inheritdoc/>
    public Task<CopilotReply> SendAsync(string agentName, string text, string userAssertion,
        string? conversationId, CancellationToken cancellationToken)
    {
        var call = new BackendCall(agentName, text, userAssertion, conversationId, cancellationToken);
        _calls.Enqueue(call);
        return ReplyAsync?.Invoke(call) ?? Task.FromResult(new CopilotReply(
            $"{agentName}: {text}", conversationId ?? $"copilot-{agentName}-{Guid.NewGuid():N}"));
    }
}