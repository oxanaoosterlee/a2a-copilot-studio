using System.Net;

namespace CopilotStudioA2A.Tests;

/// <summary>Specifies anonymous discovery cards and single-agent root discovery.</summary>
public sealed class AgentDiscoveryIntegrationTests : AdapterIntegrationTestsBase
{
    /// <summary>Serves each configured card anonymously without disclosing the downstream connection URL.</summary>
    /// <param name="name">The configured agent name.</param>
    /// <param name="skillDescription">The description configured for this agent's skill.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("support", "Answers support questions and helps troubleshoot product issues.")]
    [InlineData("billing", "Explains invoices, payment status and billing charges.")]
    public async Task GivenConfiguredAgent_WhenGettingAnonymousCard_ReturnsTextOnlyV1Capabilities(string name, string skillDescription)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/copilot-studio/{name}/a2a/.well-known/agent-card.json");
        request.Headers.Add("A2A-Version", "1.0");
        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1.0", Assert.Single(response.Headers.GetValues("A2A-Version")));
        Assert.Contains("A2A-Version", response.Headers.Vary);
        var card = await TestRequests.ReadResponseAsync(response);
        Assert.False(card.TryGetProperty("protocolVersion", out _));
        Assert.False(card.TryGetProperty("url", out _));
        Assert.False(card.TryGetProperty("security", out _));
        Assert.Equal(name, card.GetProperty("name").GetString());
        Assert.Equal("1.0.0", card.GetProperty("version").GetString());
        var endpoint = Assert.Single(card.GetProperty("supportedInterfaces").EnumerateArray());
        Assert.Equal($"https://localhost/copilot-studio/{name}/a2a", endpoint.GetProperty("url").GetString());
        Assert.Equal("JSONRPC", endpoint.GetProperty("protocolBinding").GetString());
        Assert.Equal("1.0", endpoint.GetProperty("protocolVersion").GetString());
        var capabilities = card.GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("streaming").GetBoolean());
        Assert.False(capabilities.GetProperty("pushNotifications").GetBoolean());
        Assert.False(capabilities.GetProperty("extendedAgentCard").GetBoolean());
        Assert.Equal("text/plain", Assert.Single(card.GetProperty("defaultInputModes").EnumerateArray()).GetString());
        Assert.Equal("text/plain", Assert.Single(card.GetProperty("defaultOutputModes").EnumerateArray()).GetString());
        var scheme = card.GetProperty("securitySchemes").GetProperty("entra").GetProperty("httpAuthSecurityScheme");
        Assert.Equal("Bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
        Assert.Contains("Agents.Invoke", scheme.GetProperty("description").GetString());
        var requirement = Assert.Single(card.GetProperty("securityRequirements").EnumerateArray());
        Assert.Empty(requirement.GetProperty("schemes").GetProperty("entra").GetProperty("list").EnumerateArray());
        var skill = Assert.Single(card.GetProperty("skills").EnumerateArray());
        Assert.Equal(name, skill.GetProperty("id").GetString());
        Assert.Equal(skillDescription, skill.GetProperty("description").GetString());
        Assert.DoesNotContain("powerplatform.com", card.GetRawText());
        Assert.DoesNotContain(TestIdentity.ClientSecret, card.GetRawText());
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Defaults to the legacy card shape without changing the shared runtime URL or disclosing secrets.</summary>
    /// <param name="name">The configured agent name.</param>
    /// <param name="version">The version header, or null to omit it.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("support", null)]
    [InlineData("support", "")]
    [InlineData("support", "0.3")]
    [InlineData("billing", null)]
    [InlineData("billing", "0.3")]
    public async Task GivenLegacyOrMissingVersion_WhenGettingAnonymousCard_ReturnsTextOnlyV03Capabilities(string name, string? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/copilot-studio/{name}/a2a/.well-known/agent-card.json");
        if (version is not null) request.Headers.Add("A2A-Version", version);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("0.3", Assert.Single(response.Headers.GetValues("A2A-Version")));
        Assert.Contains("A2A-Version", response.Headers.Vary);
        var card = await TestRequests.ReadResponseAsync(response);
        Assert.Equal("0.3.0", card.GetProperty("protocolVersion").GetString());
        Assert.Equal("1.0.0", card.GetProperty("version").GetString());
        Assert.Equal(name, card.GetProperty("name").GetString());
        Assert.Equal($"https://localhost/copilot-studio/{name}/a2a", card.GetProperty("url").GetString());
        Assert.Equal("JSONRPC", card.GetProperty("preferredTransport").GetString());
        Assert.False(card.GetProperty("supportsAuthenticatedExtendedCard").GetBoolean());
        Assert.False(card.TryGetProperty("supportedInterfaces", out _));
        Assert.False(card.TryGetProperty("securityRequirements", out _));
        var capabilities = card.GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("streaming").GetBoolean());
        Assert.False(capabilities.GetProperty("pushNotifications").GetBoolean());
        Assert.False(capabilities.GetProperty("stateTransitionHistory").GetBoolean());
        Assert.False(capabilities.TryGetProperty("extendedAgentCard", out _));
        Assert.Equal("text/plain", Assert.Single(card.GetProperty("defaultInputModes").EnumerateArray()).GetString());
        Assert.Equal("text/plain", Assert.Single(card.GetProperty("defaultOutputModes").EnumerateArray()).GetString());
        var scheme = card.GetProperty("securitySchemes").GetProperty("entra");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("Bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
        Assert.Contains("Agents.Invoke", scheme.GetProperty("description").GetString());
        Assert.False(scheme.TryGetProperty("httpAuthSecurityScheme", out _));
        Assert.Empty(Assert.Single(card.GetProperty("security").EnumerateArray()).GetProperty("entra").EnumerateArray());
        var skill = Assert.Single(card.GetProperty("skills").EnumerateArray());
        Assert.Equal(name, skill.GetProperty("id").GetString());
        Assert.Equal(name, skill.GetProperty("name").GetString());
        Assert.Equal(name == "support"
            ? "Answers support questions and helps troubleshoot product issues."
            : "Explains invoices, payment status and billing charges.", skill.GetProperty("description").GetString());
        Assert.DoesNotContain("powerplatform.com", card.GetRawText());
        Assert.DoesNotContain(TestIdentity.ClientSecret, card.GetRawText());
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Rejects unknown, malformed, patch-level and ambiguous version headers without falling back.</summary>
    /// <param name="version">The unsupported version header.</param>
    /// <param name="rootAlias">Whether to request the single-agent root alias.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("2.0", false)]
    [InlineData("2.0", true)]
    [InlineData("0.3.0", false)]
    [InlineData("1.0.0", false)]
    [InlineData("invalid", true)]
    [InlineData("0.3, 1.0", false)]
    public async Task GivenUnsupportedVersion_WhenGettingCard_ReturnsVersionProblem(string version, bool rootAlias)
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: false);
        using var client = factory.CreateLocalClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, rootAlias
            ? "/.well-known/agent-card.json"
            : "/copilot-studio/support/a2a/.well-known/agent-card.json");
        request.Headers.Add("A2A-Version", version);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("A2A-Version", response.Headers.Vary);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("A2A-Version"));
        var problem = await TestRequests.ReadResponseAsync(response);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("https://a2a-protocol.org/errors/version-not-supported", problem.GetProperty("type").GetString());
        Assert.Equal(new[] { "0.3", "1.0" }, problem.GetProperty("supportedVersions").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.False(problem.TryGetProperty("name", out _));
        Assert.Empty(factory.Backend.Calls);
    }

    /// <summary>Does not choose an arbitrary default card when several agents are configured.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenMultipleAgents_WhenGettingRootCard_ReturnsNotFound()
    {
        using var response = await Client.GetAsync("/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Exposes the configured card at the root only for a single-agent host.</summary>
    /// <param name="version">The version header, or null to omit it.</param>
    /// <param name="expectedVersion">The selected response version.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData(null, "0.3")]
    [InlineData("", "0.3")]
    [InlineData("0.3", "0.3")]
    [InlineData("1.0", "1.0")]
    public async Task GivenSingleAgent_WhenGettingRootCard_MatchesAgentSpecificCard(string? version, string expectedVersion)
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: false);
        using var client = factory.CreateLocalClient();
        if (version is not null) client.DefaultRequestHeaders.Add("A2A-Version", version);

        using var rootResponse = await client.GetAsync("/.well-known/agent-card.json");
        using var agentResponse = await client.GetAsync("/copilot-studio/support/a2a/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, agentResponse.StatusCode);
        Assert.Equal(expectedVersion, Assert.Single(rootResponse.Headers.GetValues("A2A-Version")));
        Assert.Equal(expectedVersion, Assert.Single(agentResponse.Headers.GetValues("A2A-Version")));
        Assert.Contains("A2A-Version", rootResponse.Headers.Vary);
        Assert.Contains("A2A-Version", agentResponse.Headers.Vary);
        var root = await TestRequests.ReadResponseAsync(rootResponse);
        var agent = await TestRequests.ReadResponseAsync(agentResponse);
        Assert.Equal(agent.GetRawText(), root.GetRawText());
        Assert.Equal("support", root.GetProperty("name").GetString());
        Assert.Equal("Answers support questions and helps troubleshoot product issues.",
            Assert.Single(root.GetProperty("skills").EnumerateArray()).GetProperty("description").GetString());
        Assert.Empty(factory.Backend.Calls);
    }

    /// <summary>Returns an anonymous 404 for an unconfigured agent's card.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenUnknownAgent_WhenGettingCard_ReturnsNotFound()
    {
        using var response = await Client.GetAsync("/copilot-studio/unknown/a2a/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Allows unauthenticated health checks without a downstream invocation.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenAnonymousCaller_WhenGettingHealth_ReturnsHealthy()
    {
        using var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestRequests.ReadResponseAsync(response);
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Empty(Factory.Backend.Calls);
    }
}