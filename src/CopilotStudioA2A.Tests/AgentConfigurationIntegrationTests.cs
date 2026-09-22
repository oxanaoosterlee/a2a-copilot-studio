using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CopilotStudioA2A.Tests;

/// <summary>Exercises root configuration, startup diagnostics and case-preserving routing entirely in process.</summary>
public sealed class AgentConfigurationIntegrationTests
{
    private const string DirectConnectUrl = "https://test.environment.api.powerplatform.com/synthetic-agent-path?secret=synthetic-url-marker";

    private static void ConfigureMixedCaseAgents(IDictionary<string, string?> settings)
    {
        settings["Agents"] = "[\"CoolAgent\",\"BackupAgent\"]";
        foreach (var name in new[] { "support", "billing" })
        {
            settings.Remove($"{name}:DirectConnectUrl");
            settings.Remove($"{name}:SkillDescription");
        }
        foreach (var name in new[] { "CoolAgent", "BackupAgent" })
        {
            settings[$"{name}:DirectConnectUrl"] = DirectConnectUrl;
            settings[$"{name}:SkillDescription"] = $"Skill for {name}.";
        }
    }

    private static async Task<JsonElement> GetCatalogAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/agents");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestRequests.ReadResponseAsync(response);
    }

    private static bool IsUnlistedAgentWarning(RecordedLog entry, string name) =>
        entry.Message.Contains("not in Agents", StringComparison.Ordinal) &&
        entry.Properties.TryGetValue("AgentName", out var value) && value is string agentName &&
        string.Equals(name, agentName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fails during startup when the allowlist is explicitly empty or blank, without backend access.</summary>
    /// <param name="agents">The scalar override that prevents fallback to inherited indexed names.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("[]")]
    public void GivenEmptyOrBlankAgentList_WhenCreatingHost_ThrowsBeforeServingRequests(string agents)
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: true, configureSettings: settings =>
        {
            // Removing the key cannot hide an environment scalar or indexed list before Program's Load.
            // Use [] for the no-agents case; CopilotStudioOptionsTests covers a truly absent allowlist.
            settings["Agents"] = agents;
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var client = factory.CreateLocalClient();
        });

        Assert.Contains("Agents", error.Message);
        Assert.DoesNotContain(TestIdentity.ClientSecret, error.ToString());
        Assert.Empty(factory.Backend.Calls);
    }

    /// <summary>Applies missing configuration overrides before Program loads and validates every listed agent.</summary>
    /// <param name="name">The first or second mixed-case agent.</param>
    /// <param name="field">The required setting to mask with a blank value.</param>
    /// <param name="value">The blank value, or null to represent absence with an explicit empty override.</param>
    [Theory]
    [InlineData("CoolAgent", "DirectConnectUrl", null)]
    [InlineData("CoolAgent", "SkillDescription", null)]
    [InlineData("CoolAgent", "SkillDescription", "")]
    [InlineData("BackupAgent", "DirectConnectUrl", " \t")]
    [InlineData("BackupAgent", "SkillDescription", null)]
    public void GivenMissingListedSetting_WhenCreatingHost_ThrowsSanitizedError(string name, string field, string? value)
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: true, configureSettings: settings =>
        {
            ConfigureMixedCaseAgents(settings);
            // A removed key can inherit an environment value, including differently cased agent settings.
            // A blank argument wins provider precedence; true absence is covered by the options unit tests.
            settings[$"{name}:{field}"] = value ?? "";
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var client = factory.CreateLocalClient();
        });

        Assert.Contains($"{name}:{field}", error.Message);
        var presentField = field == "DirectConnectUrl" ? "SkillDescription" : "DirectConnectUrl";
        Assert.DoesNotContain($"{name}:{presentField}", error.Message);
        Assert.DoesNotContain("CopilotStudio:Agents", error.Message);
        foreach (var sensitive in new[] { DirectConnectUrl, "synthetic-agent-path", "synthetic-url-marker", TestIdentity.ClientSecret })
            Assert.DoesNotContain(sensitive, error.ToString());
        Assert.Empty(factory.Backend.Calls);
    }

    /// <summary>Warns during startup about ignored partial settings without exposing or registering the agent.</summary>
    /// <param name="field">The single unlisted field, or both fields.</param>
    /// <param name="value">A blank or deliberately invalid value that must not be validated.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("DirectConnectUrl", "synthetic-invalid-unlisted-url")]
    [InlineData("SkillDescription", "synthetic-unlisted-description")]
    [InlineData("DirectConnectUrl", "")]
    [InlineData("SkillDescription", " \t")]
    [InlineData("both", "")]
    [InlineData("both", "synthetic-unlisted-value")]
    public async Task GivenUnlistedAgent_WhenDiscoveringAndInvoking_WarnsAndReturns404WithoutBackend(string field, string value)
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: false, configureSettings: settings =>
        {
            if (field is "DirectConnectUrl" or "both") settings["UnlistedAgent:DirectConnectUrl"] = value;
            if (field is "SkillDescription" or "both") settings["UnlistedAgent:SkillDescription"] = value;
            settings["Unrelated:Description"] = "Not an agent.";
        });
        using var client = factory.CreateLocalClient();
        var options = factory.Services.GetRequiredService<CopilotStudioOptions>();
        Assert.Equal("support", Assert.Single(options.Agents).Key);
        Assert.Contains(options.UnlistedAgents, name => string.Equals(name, "UnlistedAgent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(options.UnlistedAgents, name => string.Equals(name, "support", StringComparison.OrdinalIgnoreCase));

        // This snapshot precedes any request: Program must emit the warning after building the host.
        // Unknown process environment sections may yield additional ignored-agent warnings before Build.
        var warning = Assert.Single(factory.Logs.Entries.Where(entry => IsUnlistedAgentWarning(entry, "UnlistedAgent")));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.True(string.Equals("UnlistedAgent", Assert.IsType<string>(warning.Properties["AgentName"]), StringComparison.OrdinalIgnoreCase));
        Assert.Null(warning.Exception);
        var captured = warning.Message + JsonSerializer.Serialize(warning.Properties);
        foreach (var sensitive in new[] { "synthetic-invalid-unlisted-url", "synthetic-unlisted-description", "synthetic-unlisted-value", TestIdentity.ClientSecret })
            Assert.DoesNotContain(sensitive, captured);

        var token = factory.CreateToken();
        var catalog = await GetCatalogAsync(client, token);
        Assert.Equal("support", Assert.Single(catalog.EnumerateArray()).GetProperty("name").GetString());
        Assert.DoesNotContain("UnlistedAgent", catalog.GetRawText());
        using var rootCard = await client.GetAsync("/.well-known/agent-card.json");
        Assert.Equal(HttpStatusCode.OK, rootCard.StatusCode);
        Assert.Equal("support", (await TestRequests.ReadResponseAsync(rootCard)).GetProperty("name").GetString());
        using var card = await client.GetAsync("/copilot-studio/UnlistedAgent/a2a/.well-known/agent-card.json");
        Assert.Equal(HttpStatusCode.NotFound, card.StatusCode);
        using var response = await AdapterWebApplicationFactory.SendAsync(client, token, path: "/copilot-studio/UnlistedAgent/a2a");
        var error = await TestRequests.AssertErrorAsync(response, A2AErrorCode.InvalidParams, HttpStatusCode.NotFound);
        Assert.Equal("Unknown agent.", error.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(factory.Backend.Calls);
    }

    /// <summary>Publishes and invokes both mixed-case names, preserving state while forwarding each turn's assertion.</summary>
    /// <param name="name">The configured mixed-case agent name.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("CoolAgent")]
    [InlineData("BackupAgent")]
    public async Task GivenMixedCaseAgent_WhenDiscoveringAndSendingTwoTurns_PreservesNameContextAndCurrentAssertion(string name)
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: true, configureSettings: ConfigureMixedCaseAgents);
        using var client = factory.CreateLocalClient();
        var firstToken = factory.CreateToken();
        var catalog = await GetCatalogAsync(client, firstToken);
        Assert.Equal(new[] { "CoolAgent", "BackupAgent" }, catalog.EnumerateArray()
            .Select(agent => agent.GetProperty("name").GetString()).ToArray());
        var entry = Assert.Single(catalog.EnumerateArray().Where(agent => agent.GetProperty("name").GetString() == name));
        Assert.Equal($"https://localhost/copilot-studio/{name}/a2a", entry.GetProperty("endpoint").GetString());
        Assert.Equal($"https://localhost/copilot-studio/{name}/a2a/.well-known/agent-card.json", entry.GetProperty("agentCard").GetString());

        using var cardRequest = new HttpRequestMessage(HttpMethod.Get, $"/copilot-studio/{name}/a2a/.well-known/agent-card.json");
        cardRequest.Headers.Add("A2A-Version", "1.0");
        using var cardResponse = await client.SendAsync(cardRequest);
        Assert.Equal(HttpStatusCode.OK, cardResponse.StatusCode);
        var card = await TestRequests.ReadResponseAsync(cardResponse);
        Assert.Equal(name, card.GetProperty("name").GetString());
        Assert.Equal($"https://localhost/copilot-studio/{name}/a2a", Assert.Single(card.GetProperty("supportedInterfaces").EnumerateArray())
            .GetProperty("url").GetString());
        var skill = Assert.Single(card.GetProperty("skills").EnumerateArray());
        Assert.Equal(name, skill.GetProperty("id").GetString());
        Assert.Equal($"Skill for {name}.", skill.GetProperty("description").GetString());
        Assert.DoesNotContain(DirectConnectUrl, card.GetRawText());
        Assert.Empty(factory.Backend.Calls);

        using var firstResponse = await AdapterWebApplicationFactory.SendAsync(client, firstToken,
            TestRequests.Create("first turn").ToJsonString(), $"/copilot-studio/{name}/a2a");
        var first = await TestRequests.AssertMessageAsync(firstResponse, $"{name}: first turn");
        var contextId = first.GetProperty("contextId").GetString()!;
        var secondToken = factory.CreateToken(TestIdentity.WithClaim("oid", TestIdentity.OtherObjectId));
        Assert.NotEqual(firstToken, secondToken);
        // Route matching may be insensitive, but keyed services and the advertised identity retain configured case.
        using var secondResponse = await AdapterWebApplicationFactory.SendAsync(client, secondToken,
            TestRequests.Create("follow-up only", contextId).ToJsonString(), $"/copilot-studio/{name.ToLowerInvariant()}/a2a");
        var second = await TestRequests.AssertMessageAsync(secondResponse, $"{name}: follow-up only");

        Assert.Equal(contextId, second.GetProperty("contextId").GetString());
        Assert.NotEqual(first.GetProperty("messageId").GetString(), second.GetProperty("messageId").GetString());
        Assert.Collection(factory.Backend.Calls,
            initial =>
            {
                Assert.Equal(name, initial.AgentName);
                Assert.Equal("first turn", initial.Text);
                Assert.Equal(firstToken, initial.UserAssertion);
                Assert.Null(initial.ConversationId);
            },
            continuation =>
            {
                Assert.Equal(name, continuation.AgentName);
                Assert.Equal("follow-up only", continuation.Text);
                Assert.Equal(secondToken, continuation.UserAssertion);
                Assert.StartsWith($"copilot-{name}-", continuation.ConversationId);
                Assert.NotEqual(contextId, continuation.ConversationId);
            });
        Assert.DoesNotContain(firstToken, first.GetRawText());
        Assert.DoesNotContain(secondToken, second.GetRawText());
        // Listed agents must never be warned about; unrelated environment sections may legitimately be ignored.
        Assert.DoesNotContain(factory.Logs.Entries, log => log.Level >= LogLevel.Error ||
            IsUnlistedAgentWarning(log, "CoolAgent") || IsUnlistedAgentWarning(log, "BackupAgent"));
    }

    /// <summary>Evicts framework sessions and restarts supplied contexts when capacity one switches agents.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenMixedCaseAgentsAtCapacityOne_WhenSwitchingAgents_CleansUpSessionsWithoutLowercaseKeyFailure()
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: true, maxConversations: 1,
            configureSettings: ConfigureMixedCaseAgents);
        using var client = factory.CreateLocalClient();
        var token = factory.CreateToken();
        using var firstResponse = await AdapterWebApplicationFactory.SendAsync(client, token, path: "/copilot-studio/CoolAgent/a2a");
        var first = await TestRequests.AssertMessageAsync(firstResponse, "CoolAgent: Hello");
        var firstContext = first.GetProperty("contextId").GetString()!;

        // Both transitions execute the real middleware's keyed AgentSessionStore/AIAgent resolution and deletion.
        using var backupResponse = await AdapterWebApplicationFactory.SendAsync(client, token, path: "/copilot-studio/BackupAgent/a2a");
        var backup = await TestRequests.AssertMessageAsync(backupResponse, "BackupAgent: Hello");
        var backupContext = backup.GetProperty("contextId").GetString()!;
        using var staleCool = await AdapterWebApplicationFactory.SendAsync(client, token,
            TestRequests.Create(contextId: firstContext).ToJsonString(), "/copilot-studio/CoolAgent/a2a");
        var restartedCool = await TestRequests.AssertMessageAsync(staleCool, "CoolAgent: Hello");
        Assert.Equal(firstContext, restartedCool.GetProperty("contextId").GetString());

        using var backupNextResponse = await AdapterWebApplicationFactory.SendAsync(client, token,
            TestRequests.Create("still retained", backupContext).ToJsonString(), "/copilot-studio/BackupAgent/a2a");
        var backupNext = await TestRequests.AssertMessageAsync(backupNextResponse, "BackupAgent: still retained");
        Assert.Equal(backupContext, backupNext.GetProperty("contextId").GetString());
        using var newCoolResponse = await AdapterWebApplicationFactory.SendAsync(client, token, path: "/copilot-studio/CoolAgent/a2a");
        var newCool = await TestRequests.AssertMessageAsync(newCoolResponse, "CoolAgent: Hello");
        Assert.NotEqual(firstContext, newCool.GetProperty("contextId").GetString());
        using var staleBackup = await AdapterWebApplicationFactory.SendAsync(client, token,
            TestRequests.Create(contextId: backupContext).ToJsonString(), "/copilot-studio/BackupAgent/a2a");
        var restartedBackup = await TestRequests.AssertMessageAsync(staleBackup, "BackupAgent: Hello");
        Assert.Equal(backupContext, restartedBackup.GetProperty("contextId").GetString());

        Assert.Collection(factory.Backend.Calls,
            initial => { Assert.Equal("CoolAgent", initial.AgentName); Assert.Null(initial.ConversationId); },
            switched => { Assert.Equal("BackupAgent", switched.AgentName); Assert.Null(switched.ConversationId); },
            restarted => { Assert.Equal("CoolAgent", restarted.AgentName); Assert.Null(restarted.ConversationId); },
            restarted => { Assert.Equal("BackupAgent", restarted.AgentName); Assert.Null(restarted.ConversationId); },
            restarted => { Assert.Equal("CoolAgent", restarted.AgentName); Assert.Null(restarted.ConversationId); },
            restarted => { Assert.Equal("BackupAgent", restarted.AgentName); Assert.Null(restarted.ConversationId); });
        Assert.DoesNotContain(factory.Logs.Entries, log => log.Level >= LogLevel.Error);
    }
}