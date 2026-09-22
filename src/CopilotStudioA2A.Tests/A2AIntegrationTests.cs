using System.Net;
using System.Text.Json.Nodes;
using A2A;

namespace CopilotStudioA2A.Tests;

/// <summary>Exercises the actual A2A routing, middleware and Agent Framework direct-message boundary.</summary>
public sealed class A2AIntegrationTests : AdapterIntegrationTestsBase
{
    /// <summary>Returns direct A2A messages and maps stable public contexts to downstream conversations.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenTwoTurns_WhenSendingMessages_PreservesContextWithoutReplayingHistory()
    {
        var firstToken = Factory.CreateToken();
        var firstRequest = TestRequests.Create("first question");
        firstRequest["params"]!["message"]!["parts"]!.AsArray().Add(new JsonObject { ["text"] = "more detail" });

        using var firstResponse = await AdapterWebApplicationFactory.SendAsync(Client, firstToken, firstRequest.ToJsonString());
        var firstMessage = await TestRequests.AssertMessageAsync(firstResponse, "support: first question\nmore detail");
        var contextId = firstMessage.GetProperty("contextId").GetString()!;

        var secondClaims = TestIdentity.Claims();
        secondClaims.Add(new System.Security.Claims.Claim("jti", "second-turn-token"));
        var secondToken = Factory.CreateToken(secondClaims);
        var secondRequest = TestRequests.Create("follow-up only", contextId);
        secondRequest["params"]!["message"]!["messageId"] = "caller-message-2";
        using var secondResponse = await AdapterWebApplicationFactory.SendAsync(Client, secondToken, secondRequest.ToJsonString());
        var secondMessage = await TestRequests.AssertMessageAsync(secondResponse, "support: follow-up only");

        Assert.Equal(contextId, secondMessage.GetProperty("contextId").GetString());
        Assert.NotEqual(firstMessage.GetProperty("messageId").GetString(), secondMessage.GetProperty("messageId").GetString());
        Assert.NotEqual("caller-message-2", secondMessage.GetProperty("messageId").GetString());
        Assert.Collection(Factory.Backend.Calls,
            first =>
            {
                Assert.Equal("support", first.AgentName);
                Assert.Equal("first question\nmore detail", first.Text);
                Assert.Equal(firstToken, first.UserAssertion);
                Assert.Null(first.ConversationId);
                Assert.True(first.CancellationToken.CanBeCanceled);
            },
            second =>
            {
                Assert.Equal("support", second.AgentName);
                Assert.Equal("follow-up only", second.Text);
                Assert.Equal(secondToken, second.UserAssertion);
                Assert.StartsWith("copilot-support-", second.ConversationId);
                Assert.NotEqual(contextId, second.ConversationId);
            });
        Assert.DoesNotContain(firstToken, firstMessage.GetRawText());
        Assert.DoesNotContain(secondToken, secondMessage.GetRawText());
    }

    /// <summary>Keeps support and billing conversations independent during interleaved turns.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenSeparateAgents_WhenInterleavingTurns_KeepsDownstreamStateSeparate()
    {
        var token = Factory.CreateToken();
        using var supportResponse = await AdapterWebApplicationFactory.SendAsync(Client, token);
        var support = await TestRequests.AssertMessageAsync(supportResponse, "support: Hello");
        using var billingResponse = await AdapterWebApplicationFactory.SendAsync(Client, token, path: "/copilot-studio/billing/a2a");
        var billing = await TestRequests.AssertMessageAsync(billingResponse, "billing: Hello");
        var supportContext = support.GetProperty("contextId").GetString()!;
        var billingContext = billing.GetProperty("contextId").GetString()!;
        Assert.NotEqual(supportContext, billingContext);

        using var billingNext = await AdapterWebApplicationFactory.SendAsync(Client, token,
            TestRequests.Create("invoice", billingContext).ToJsonString(), "/copilot-studio/billing/a2a");
        var billingMessage = await TestRequests.AssertMessageAsync(billingNext, "billing: invoice");
        using var supportNext = await AdapterWebApplicationFactory.SendAsync(Client, token,
            TestRequests.Create("ticket", supportContext).ToJsonString());
        var supportMessage = await TestRequests.AssertMessageAsync(supportNext, "support: ticket");

        Assert.Equal(billingContext, billingMessage.GetProperty("contextId").GetString());
        Assert.Equal(supportContext, supportMessage.GetProperty("contextId").GetString());
        var calls = Factory.Backend.Calls;
        Assert.Equal(4, calls.Count);
        Assert.Null(calls[0].ConversationId);
        Assert.Null(calls[1].ConversationId);
        Assert.StartsWith("copilot-billing-", calls[2].ConversationId);
        Assert.StartsWith("copilot-support-", calls[3].ConversationId);
        Assert.NotEqual(calls[2].ConversationId, calls[3].ConversationId);
    }

    /// <summary>Records the intentional absence of context ownership checks while forwarding the current assertion.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenDifferentDelegatedUser_WhenContinuingContext_UsesCurrentUserAssertion()
    {
        using var firstResponse = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken());
        var first = await TestRequests.AssertMessageAsync(firstResponse, "support: Hello");
        var contextId = first.GetProperty("contextId").GetString()!;
        var otherToken = Factory.CreateToken(TestIdentity.WithClaim("oid", TestIdentity.OtherObjectId));

        using var nextResponse = await AdapterWebApplicationFactory.SendAsync(Client, otherToken,
            TestRequests.Create("continued by another delegated user", contextId).ToJsonString());

        var next = await TestRequests.AssertMessageAsync(nextResponse, "support: continued by another delegated user");
        Assert.Equal(contextId, next.GetProperty("contextId").GetString());
        Assert.Equal(2, Factory.Backend.Calls.Count);
        Assert.Equal(otherToken, Factory.Backend.Calls[1].UserAssertion);
    }

    /// <summary>Returns structured not-found errors rather than invoking an unconfigured agent.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenUnknownAgent_WhenSendingMessage_Returns404ProtocolError()
    {
        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), path: "/copilot-studio/unknown/a2a");

        var body = await TestRequests.AssertErrorAsync(response, A2AErrorCode.InvalidParams, HttpStatusCode.NotFound);
        Assert.Equal("Unknown agent.", body.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Accepts and preserves a caller-supplied context on its first request.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenCallerContextOnFirstRequest_WhenSendingMessage_PreservesContext()
    {
        const string contextId = "caller-selected-context";
        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(),
            TestRequests.Create(contextId: contextId).ToJsonString());

        var message = await TestRequests.AssertMessageAsync(response, "support: Hello");
        Assert.Equal(contextId, message.GetProperty("contextId").GetString());
        Assert.Null(Assert.Single(Factory.Backend.Calls).ConversationId);
    }

    /// <summary>Uses a caller context as an independent first conversation on another agent.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenCrossAgentContext_WhenSendingMessage_StartsIndependentConversation()
    {
        var token = Factory.CreateToken();
        using var firstResponse = await AdapterWebApplicationFactory.SendAsync(Client, token);
        var first = await TestRequests.AssertMessageAsync(firstResponse, "support: Hello");

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, token,
            TestRequests.Create(contextId: first.GetProperty("contextId").GetString()).ToJsonString(), "/copilot-studio/billing/a2a");

        var billing = await TestRequests.AssertMessageAsync(response, "billing: Hello");
        Assert.Equal(first.GetProperty("contextId").GetString(), billing.GetProperty("contextId").GetString());
        Assert.Equal(2, Factory.Backend.Calls.Count);
        Assert.Null(Factory.Backend.Calls[1].ConversationId);
    }

    /// <summary>Rejects unsupported version headers without selecting a response version.</summary>
    /// <param name="version">The unsupported version value.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("0.3.0")]
    [InlineData("1.0.0")]
    [InlineData("2.0")]
    [InlineData("0.3,1.0")]
    public async Task GivenInvalidVersion_WhenSendingMessage_RejectsBeforeBackend(string? version)
    {
        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), version: version);

        var error = await TestRequests.AssertErrorAsync(response, A2AErrorCode.VersionNotSupported, version: null);
        Assert.Equal(new[] { "0.3", "1.0" }, error.GetProperty("error").GetProperty("data")
            .GetProperty("supportedVersions").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Rejects unsupported HTTP content types before JSON parsing or invocation.</summary>
    /// <param name="contentType">The content type, or null for no Content-Type header.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    public async Task GivenWrongHttpMediaType_WhenSendingMessage_Returns415(string? contentType)
    {
        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), contentType: contentType);

        await TestRequests.AssertErrorAsync(response, A2AErrorCode.ContentTypeNotSupported, HttpStatusCode.UnsupportedMediaType, id: null);
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Does not dispatch non-POST HTTP methods to the A2A backend.</summary>
    /// <param name="method">The unsupported HTTP method.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task GivenWrongHttpMethod_WhenCallingRuntime_ReturnsMethodNotAllowed(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/copilot-studio/support/a2a");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Factory.CreateToken());
        request.Headers.Add("A2A-Version", "1.0");

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Turns invalid JSON and unsupported envelopes into protocol errors, never downstream sends.</summary>
    /// <param name="json">The invalid request body.</param>
    /// <param name="code">The expected protocol error.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("", A2AErrorCode.ParseError)]
    [InlineData("{", A2AErrorCode.ParseError)]
    [InlineData("{\"jsonrpc\":\"2.0\",}", A2AErrorCode.ParseError)]
    [InlineData("null", A2AErrorCode.InvalidRequest)]
    [InlineData("[]", A2AErrorCode.InvalidRequest)]
    [InlineData("[ {\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SendMessage\"} ]", A2AErrorCode.InvalidRequest)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"SendMessage\"}", A2AErrorCode.InvalidRequest)]
    public async Task GivenMalformedRequest_WhenSendingMessage_ReturnsProtocolError(string json, A2AErrorCode code)
    {
        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), json);

        await TestRequests.AssertErrorAsync(response, code, id: null);
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Enforces profile restrictions before Agent Framework or the backend receives a request.</summary>
    /// <param name="path">The dotted JSON property path.</param>
    /// <param name="json">The replacement JSON, or null to remove the field.</param>
    /// <param name="code">The expected protocol error.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("method", "\"SendStreamingMessage\"", A2AErrorCode.UnsupportedOperation)]
    [InlineData("method", "\"GetTask\"", A2AErrorCode.UnsupportedOperation)]
    [InlineData("method", "\"CreateTaskPushNotificationConfig\"", A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("method", "\"GetExtendedAgentCard\"", A2AErrorCode.ExtendedAgentCardNotConfigured)]
    [InlineData("method", "\"message/send\"", A2AErrorCode.MethodNotFound)]
    [InlineData("params.message.messageId", null, A2AErrorCode.InvalidParams)]
    [InlineData("params.message.role", "\"user\"", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.parts", "[]", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.parts", "[{\"raw\":\"YQ==\"}]", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("params.message.parts", "[{\"url\":\"https://example.invalid/file\"}]", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("params.message.parts", "[{\"text\":\"hello\",\"data\":{}}]", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.parts", "[{\"text\":\"hello\",\"mediaType\":\"text/html\"}]", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("params.message.parts", "[{\"text\":\"hello\"},{\"data\":{}}]", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("params.configuration", "{\"acceptedOutputModes\":[\"image/png\"]}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("params.message.taskId", "\"task-1\"", A2AErrorCode.TaskNotFound)]
    public async Task GivenUnsupportedProfile_WhenSendingMessage_RejectsBeforeBackend(string path, string? json, A2AErrorCode code)
    {
        var request = TestRequests.Create();
        var segments = path.Split('.');
        JsonNode parent = request;
        foreach (var segment in segments[..^1]) parent = parent[segment]!;
        if (json is null) parent.AsObject().Remove(segments[^1]);
        else parent[segments[^1]] = JsonNode.Parse(json);

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), request.ToJsonString());

        await TestRequests.AssertErrorAsync(response, code);
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Rejects duplicate JSON properties before dispatching to the backend.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenDuplicateTextProperty_WhenSendingMessage_RejectsBeforeBackend()
    {
        var json = TestRequests.Create().ToJsonString().Replace("\"text\":\"Hello\"",
            "\"text\":\"Hello\",\"text\":\"different\"", StringComparison.Ordinal);

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(), json);

        await TestRequests.AssertErrorAsync(response, A2AErrorCode.InvalidRequest);
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Rejects bodies exceeding the configured transport limit without invoking Copilot.</summary>
    /// <param name="version">The request protocol version.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("0.3")]
    [InlineData("1.0")]
    public async Task GivenOversizedBody_WhenSendingMessage_Returns413(string version)
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: true, maxRequestBytes: 1024);
        using var client = factory.CreateLocalClient();
        var request = version == "0.3" ? TestRequests.CreateLegacy(new string('x', 2048)) : TestRequests.Create(new string('x', 2048));

        using var response = await AdapterWebApplicationFactory.SendAsync(client, factory.CreateToken(),
            request.ToJsonString(), version: version);

        await TestRequests.AssertErrorAsync(response, A2AErrorCode.InvalidRequest, HttpStatusCode.RequestEntityTooLarge, id: null, version: version);
        Assert.Empty(factory.Backend.Calls);
    }

    /// <summary>Sanitizes backend exception detail, tokens and configuration while avoiding automatic retries.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenBackendException_WhenSendingMessage_ReturnsSanitizedErrorWithoutRetry()
    {
        const string privateText = "private-user-text-marker";
        const string privateUrl = "https://test.environment.api.powerplatform.com/private-test-path?secret=dummy";
        var token = Factory.CreateToken();
        Factory.Backend.ReplyAsync = call => Task.FromException<CopilotReply>(new InvalidOperationException(
            $"Sensitive backend failure: {call.UserAssertion} {call.Text} {TestIdentity.ClientSecret} {privateUrl}"));

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, token, TestRequests.Create(privateText).ToJsonString());

        var error = await TestRequests.AssertErrorAsync(response, A2AErrorCode.InternalError);
        var body = error.GetRawText();
        foreach (var sensitive in new[] { token, privateText, privateUrl, TestIdentity.ClientSecret, "Sensitive backend failure", "InvalidOperationException" })
            Assert.DoesNotContain(sensitive, body);
        Assert.Contains("X-Correlation-ID", error.GetProperty("error").GetProperty("message").GetString());
        Assert.Single(Factory.Backend.Calls);
        Assert.Equal(token, Factory.Backend.Calls[0].UserAssertion);
    }

    /// <summary>Queues overlapping turns but rejects new contexts when the single capacity slot is active.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenAllCapacityBusy_WhenSendingAnotherTurn_QueuesSameContextWithoutEviction()
    {
        using var factory = new AdapterWebApplicationFactory(includeBilling: true, maxConversations: 1);
        using var client = factory.CreateLocalClient();
        var token = factory.CreateToken();
        using var seedResponse = await AdapterWebApplicationFactory.SendAsync(client, token);
        var seed = await TestRequests.AssertMessageAsync(seedResponse, "support: Hello");
        var contextId = seed.GetProperty("contextId").GetString()!;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CopilotReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Backend.ReplyAsync = call =>
        {
            entered.TrySetResult(true);
            return release.Task.WaitAsync(call.CancellationToken);
        };

        var pending = AdapterWebApplicationFactory.SendAsync(client, token, TestRequests.Create("active turn", contextId).ToJsonString());
        Task<HttpResponseMessage>? overlap = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            overlap = AdapterWebApplicationFactory.SendAsync(client, token,
                TestRequests.Create("overlapping turn", contextId).ToJsonString());
            Assert.False(overlap.IsCompleted);
            using var newContext = await AdapterWebApplicationFactory.SendAsync(client, token, path: "/copilot-studio/billing/a2a");
            await TestRequests.AssertErrorAsync(newContext, A2AErrorCode.InternalError);
            Assert.Equal(2, factory.Backend.Calls.Count);
        }
        finally
        {
            release.TrySetResult(new CopilotReply("completed turn", "copilot-support-completed"));
            using var completed = await pending;
            var message = await TestRequests.AssertMessageAsync(completed, "completed turn");
            Assert.Equal(contextId, message.GetProperty("contextId").GetString());
        }

        using var queued = await overlap;
        var queuedMessage = await TestRequests.AssertMessageAsync(queued, "completed turn");
        Assert.Equal(contextId, queuedMessage.GetProperty("contextId").GetString());
        Assert.Equal(3, factory.Backend.Calls.Count);
    }
}