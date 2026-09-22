using System.Net;
using System.Text.Json.Nodes;
using A2A;

namespace CopilotStudioA2A.Tests;

/// <summary>Exercises legacy JSON-RPC through the actual middleware and Agent Framework pipeline.</summary>
public sealed class LegacyA2AIntegrationTests : AdapterIntegrationTestsBase
{
    /// <summary>Defaults headerless requests to 0.3 and keeps all supported configurations synchronous.</summary>
    /// <param name="version">The request header, or null to omit it.</param>
    /// <param name="configuration">The legacy configuration JSON.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "{}")]
    [InlineData("0.3", "{\"blocking\":true,\"acceptedOutputModes\":[\"text/plain\"],\"historyLength\":0}")]
    [InlineData("0.3", "{\"blocking\":false}")]
    [InlineData("0.3", "{\"blocking\":null,\"pushNotificationConfig\":null}")]
    public async Task GivenLegacyRequest_WhenSending_ReturnsDirectLegacyMessage(string? version, string configuration)
    {
        var token = Factory.CreateToken();
        var request = TestRequests.CreateLegacy("  Hallo 🌍  ");
        request["params"]!["configuration"] = JsonNode.Parse(configuration);
        request["params"]!["message"]!["parts"]!.AsArray().Add(new JsonObject { ["kind"] = "text", ["text"] = "second\nline" });

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, token, request.ToJsonString(), version: version);

        var reply = await TestRequests.AssertMessageAsync(response, "support:   Hallo 🌍  \nsecond\nline", "0.3");
        Assert.NotEqual("caller-message-1", reply.GetProperty("messageId").GetString());
        var call = Assert.Single(Factory.Backend.Calls);
        Assert.Equal("  Hallo 🌍  \nsecond\nline", call.Text);
        Assert.Equal(token, call.UserAssertion);
        Assert.Null(call.ConversationId);
    }

    /// <summary>Rejects methods from the wrong version and unsupported legacy operations without invoking the backend.</summary>
    /// <param name="method">The requested method.</param>
    /// <param name="version">The requested protocol.</param>
    /// <param name="legacyBody">Whether to construct legacy message fields.</param>
    /// <param name="code">The expected error code.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("SendMessage", null, false, A2AErrorCode.MethodNotFound)]
    [InlineData("SendMessage", "0.3", true, A2AErrorCode.MethodNotFound)]
    [InlineData("message/send", "1.0", true, A2AErrorCode.MethodNotFound)]
    [InlineData("message/stream", "0.3", true, A2AErrorCode.UnsupportedOperation)]
    [InlineData("tasks/get", "0.3", true, A2AErrorCode.UnsupportedOperation)]
    [InlineData("tasks/cancel", "0.3", true, A2AErrorCode.UnsupportedOperation)]
    [InlineData("tasks/resubscribe", "0.3", true, A2AErrorCode.UnsupportedOperation)]
    [InlineData("tasks/pushNotificationConfig/set", "0.3", true, A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("tasks/pushNotificationConfig/get", "0.3", true, A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("tasks/pushNotificationConfig/list", "0.3", true, A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("tasks/pushNotificationConfig/delete", "0.3", true, A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("agent/getAuthenticatedExtendedCard", "0.3", true, A2AErrorCode.ExtendedAgentCardNotConfigured)]
    [InlineData("GetExtendedAgentCard", "0.3", true, A2AErrorCode.MethodNotFound)]
    [InlineData("unknown", "0.3", true, A2AErrorCode.MethodNotFound)]
    public async Task GivenUnsupportedMethod_WhenSending_ReturnsVersionSpecificError(string method, string? version, bool legacyBody, A2AErrorCode code)
    {
        var request = legacyBody ? TestRequests.CreateLegacy() : TestRequests.Create();
        request["method"] = method;

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), request.ToJsonString(), version: version);

        await TestRequests.AssertErrorAsync(response, code, version: version ?? "0.3");
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Continues to enforce context, content and task restrictions for legacy clients.</summary>
    /// <param name="path">The property to replace.</param>
    /// <param name="json">The replacement value.</param>
    /// <param name="code">The expected protocol error.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("params.message.kind", "\"task\"", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.role", "\"ROLE_USER\"", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.parts", "[{\"text\":\"hello\"}]", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.parts", "[]", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.parts", "[{\"kind\":\"text\",\"text\":\"hello\"},{\"kind\":\"data\",\"data\":{}}]", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("params.message.taskId", "\"task-id\"", A2AErrorCode.TaskNotFound)]
    [InlineData("params.configuration", "{\"blocking\":\"true\"}", A2AErrorCode.InvalidParams)]
    [InlineData("params.configuration", "{\"historyLength\":1}", A2AErrorCode.UnsupportedOperation)]
    [InlineData("params.configuration", "{\"pushNotificationConfig\":{}}", A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("params.configuration", "{\"acceptedOutputModes\":[\"image/png\"]}", A2AErrorCode.ContentTypeNotSupported)]
    public async Task GivenUnsupportedLegacyPayload_WhenSending_RejectsBeforeBackend(string path, string json, A2AErrorCode code)
    {
        var request = TestRequests.CreateLegacy();
        var segments = path.Split('.');
        JsonNode parent = request;
        foreach (var segment in segments[..^1]) parent = parent[segment]!;
        parent[segments[^1]] = JsonNode.Parse(json);

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), request.ToJsonString(), version: null);

        await TestRequests.AssertErrorAsync(response, code, version: "0.3");
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Reuses the same public context across legacy and modern turns without replaying history.</summary>
    /// <param name="firstVersion">The first turn's protocol.</param>
    /// <param name="secondVersion">The continuation's protocol.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("0.3", "0.3")]
    [InlineData("0.3", "1.0")]
    [InlineData("1.0", "0.3")]
    public async Task GivenTwoTurns_WhenContinuingAcrossVersions_PreservesConversation(string firstVersion, string secondVersion)
    {
        var token = Factory.CreateToken();
        var firstRequest = firstVersion == "0.3" ? TestRequests.CreateLegacy("first") : TestRequests.Create("first");
        using var firstResponse = await AdapterWebApplicationFactory.SendAsync(Client, token, firstRequest.ToJsonString(), version: firstVersion);
        var first = await TestRequests.AssertMessageAsync(firstResponse, "support: first", firstVersion);
        var contextId = first.GetProperty("contextId").GetString()!;
        var secondRequest = secondVersion == "0.3" ? TestRequests.CreateLegacy("next", contextId) : TestRequests.Create("next", contextId);

        using var secondResponse = await AdapterWebApplicationFactory.SendAsync(Client, token, secondRequest.ToJsonString(), version: secondVersion);

        var second = await TestRequests.AssertMessageAsync(secondResponse, "support: next", secondVersion);
        Assert.Equal(contextId, second.GetProperty("contextId").GetString());
        Assert.NotEqual(first.GetProperty("messageId").GetString(), second.GetProperty("messageId").GetString());
        Assert.Equal(2, Factory.Backend.Calls.Count);
        Assert.Null(Factory.Backend.Calls[0].ConversationId);
        Assert.Equal("next", Factory.Backend.Calls[1].Text);
        Assert.StartsWith("copilot-support-", Factory.Backend.Calls[1].ConversationId);
        Assert.NotEqual(contextId, Factory.Backend.Calls[1].ConversationId);
    }

    /// <summary>Preserves numeric JSON-RPC IDs on both success and validation failure.</summary>
    /// <param name="version">The protocol version.</param>
    /// <param name="fail">Whether to provoke a validation error.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("0.3", false)]
    [InlineData("0.3", true)]
    [InlineData("1.0", false)]
    [InlineData("1.0", true)]
    public async Task GivenNumericId_WhenSending_PreservesCorrelation(string version, bool fail)
    {
        var request = version == "0.3" ? TestRequests.CreateLegacy() : TestRequests.Create();
        request["id"] = long.MaxValue;
        if (fail) request["params"]!["message"]!["messageId"] = "";

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), request.ToJsonString(), version: version);

        var body = await TestRequests.ReadResponseAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(long.MaxValue, body.GetProperty("id").GetInt64());
        Assert.Equal(version, Assert.Single(response.Headers.GetValues("A2A-Version")));
        Assert.Equal(fail, body.TryGetProperty("error", out _));
        Assert.Equal(!fail, body.TryGetProperty("result", out _));
        Assert.Equal(fail ? 0 : 1, Factory.Backend.Calls.Count);
    }

    /// <summary>Returns legacy headers for parse failures and unsupported envelopes.</summary>
    /// <param name="json">The invalid JSON-RPC input.</param>
    /// <param name="code">The expected error.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("{", A2AErrorCode.ParseError)]
    [InlineData("[]", A2AErrorCode.InvalidRequest)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"message/send\"}", A2AErrorCode.InvalidRequest)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"message/send\"}", A2AErrorCode.InvalidRequest)]
    public async Task GivenMalformedLegacyRequest_WhenSending_ReturnsLegacyError(string json, A2AErrorCode code)
    {
        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), json, version: null);

        await TestRequests.AssertErrorAsync(response, code, id: null, version: "0.3");
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Applies the duplicate-property guard before legacy translation.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenDuplicateLegacyProperty_WhenSending_ReturnsInvalidRequest()
    {
        var json = TestRequests.CreateLegacy().ToJsonString().Replace("\"kind\":\"text\"", "\"kind\":\"text\",\"kind\":\"text\"", StringComparison.Ordinal);

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), json, version: "0.3");

        await TestRequests.AssertErrorAsync(response, A2AErrorCode.InvalidRequest, version: "0.3");
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Restores the legacy response version after a backend failure and never exposes sensitive details.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenBackendFailure_WhenSendingLegacyMessage_ReturnsSanitizedLegacyError()
    {
        var token = Factory.CreateToken();
        Factory.Backend.ReplyAsync = call => Task.FromException<CopilotReply>(new InvalidOperationException($"secret-failure {call.UserAssertion}"));

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, token, TestRequests.CreateLegacy().ToJsonString(), version: "0.3");

        var body = await TestRequests.AssertErrorAsync(response, A2AErrorCode.InternalError, version: "0.3");
        Assert.DoesNotContain("secret-failure", body.GetRawText());
        Assert.DoesNotContain(token, body.GetRawText());
        Assert.Single(Factory.Backend.Calls);
    }

    /// <summary>Preserves the selected version on errors raised before framework dispatch.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenLegacyTransportErrors_WhenSending_PreservesHeadersAndStatus()
    {
        var token = Factory.CreateToken();
        using var unknown = await AdapterWebApplicationFactory.SendAsync(Client, token, TestRequests.CreateLegacy().ToJsonString(),
            path: "/copilot-studio/unknown/a2a", version: null);
        await TestRequests.AssertErrorAsync(unknown, A2AErrorCode.InvalidParams, HttpStatusCode.NotFound, version: "0.3");
        using var wrongType = await AdapterWebApplicationFactory.SendAsync(Client, token, TestRequests.CreateLegacy().ToJsonString(),
            version: null, contentType: "text/plain");
        await TestRequests.AssertErrorAsync(wrongType, A2AErrorCode.ContentTypeNotSupported, HttpStatusCode.UnsupportedMediaType, id: null, version: "0.3");
        Assert.Empty(Factory.Backend.Calls);
    }
}