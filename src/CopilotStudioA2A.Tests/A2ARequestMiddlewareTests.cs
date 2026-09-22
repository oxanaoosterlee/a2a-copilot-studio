using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotStudioA2A.Tests;

/// <summary>Checks canonical framework dispatch and restoration of the original HTTP request.</summary>
public sealed class A2ARequestMiddlewareTests
{
    /// <summary>Normalizes the internal request and restores headers and streams on success and failure.</summary>
    /// <param name="version">The caller's version header, or null for legacy default.</param>
    /// <param name="fail">Whether the downstream delegate throws.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("0.3", false)]
    [InlineData("0.3", true)]
    [InlineData("1.0", false)]
    [InlineData("1.0", true)]
    public async Task GivenVersionedMessage_WhenDispatching_UsesV1InternallyAndRestoresCallerState(string? version, bool fail)
    {
        var legacy = version != "1.0";
        var request = legacy ? TestRequests.CreateLegacy(" hello ") : TestRequests.Create(" hello ");
        request["params"]!["configuration"] = legacy
            ? new JsonObject { ["blocking"] = true }
            : new JsonObject { ["returnImmediately"] = false };
        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(request.ToJsonString()));
        using var outputStream = new MemoryStream();
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new A2ARoute("support")), "test"));
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Headers.Authorization = "Bearer test-assertion";
        if (version is not null) http.Request.Headers["A2A-Version"] = version;
        http.Request.Body = inputStream;
        http.Request.ContentLength = inputStream.Length;
        http.Response.Body = outputStream;
        var agents = new CopilotStudioOptions();
        agents.Agents.Add("support", new CopilotAgentOptions());
        var options = new AdapterOptions();
        JsonElement dispatched = default;
        string? dispatchedVersion = null;
        string? invocationText = null;
        var dispatchCount = 0;
        var middleware = new A2ARequestMiddleware(async context =>
        {
            dispatchCount++;
            dispatchedVersion = context.Request.Headers["A2A-Version"].ToString();
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            dispatched = document.RootElement.Clone();
            invocationText = InvocationContext.Current.Text;
            context.Response.Headers["A2A-Version"] = "1.0";
            if (fail) throw new InvalidOperationException("private framework detail");
            await context.Response.WriteAsJsonAsync(new
            {
                jsonrpc = "2.0",
                id = "framework-id-not-caller-id",
                result = new
                {
                    message = new
                    {
                        messageId = "reply-id",
                        contextId = "framework-context",
                        role = "ROLE_AGENT",
                        parts = new[] { new { text = "answer" } }
                    }
                }
            });
        }, NullLogger<A2ARequestMiddleware>.Instance);

        await middleware.InvokeAsync(http, options, agents, new ConversationStore(options));

        Assert.Equal(1, dispatchCount);
        Assert.Equal("1.0", dispatchedVersion);
        Assert.Equal("SendMessage", dispatched.GetProperty("method").GetString());
        var canonical = A2AProfile.Read(dispatched, "1.0");
        Assert.Equal(" hello ", canonical.Text);
        Assert.Equal(canonical.Text, invocationText);
        Assert.False(string.IsNullOrWhiteSpace(canonical.ContextId));
        Assert.False(dispatched.GetProperty("params").GetProperty("configuration").GetProperty("returnImmediately").GetBoolean());
        Assert.Equal("rpc-1", dispatched.GetProperty("id").GetString());
        Assert.Same(inputStream, http.Request.Body);
        Assert.Same(outputStream, http.Response.Body);
        Assert.Equal((long?)inputStream.Length, http.Request.ContentLength);
        Assert.Equal(version is not null, http.Request.Headers.ContainsKey("A2A-Version"));
        Assert.Equal(version ?? "", http.Request.Headers["A2A-Version"].ToString());
        Assert.Equal(legacy ? "0.3" : "1.0", http.Response.Headers["A2A-Version"].ToString());
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        outputStream.Position = 0;
        using var outgoing = await JsonDocument.ParseAsync(outputStream);
        Assert.Equal("rpc-1", outgoing.RootElement.GetProperty("id").GetString());
        if (fail)
        {
            Assert.Equal((int)A2AErrorCode.InternalError, outgoing.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.DoesNotContain("private framework detail", outgoing.RootElement.GetRawText());
        }
        else
        {
            var result = outgoing.RootElement.GetProperty("result");
            var message = legacy ? result : result.GetProperty("message");
            Assert.Equal(legacy ? "agent" : "ROLE_AGENT", message.GetProperty("role").GetString());
            Assert.Equal(canonical.ContextId, message.GetProperty("contextId").GetString());
        }
    }
}