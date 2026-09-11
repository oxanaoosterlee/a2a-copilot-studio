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
        using var response = await Client.GetAsync($"/copilot-studio/{name}/a2a/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = await TestRequests.ReadResponseAsync(response);
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
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenSingleAgent_WhenGettingRootCard_MatchesAgentSpecificCard()
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: false);
        using var client = factory.CreateLocalClient();

        using var rootResponse = await client.GetAsync("/.well-known/agent-card.json");
        using var agentResponse = await client.GetAsync("/copilot-studio/support/a2a/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, agentResponse.StatusCode);
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