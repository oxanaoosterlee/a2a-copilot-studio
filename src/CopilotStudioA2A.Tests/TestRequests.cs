using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;

namespace CopilotStudioA2A.Tests;

internal static class TestRequests
{
    internal static JsonObject Create(string text = "Hello", string? contextId = null)
    {
        var message = new JsonObject
        {
            ["messageId"] = "caller-message-1",
            ["role"] = "ROLE_USER",
            ["parts"] = new JsonArray(new JsonObject { ["text"] = text })
        };
        if (contextId is not null) message["contextId"] = contextId;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "rpc-1",
            ["method"] = "SendMessage",
            ["params"] = new JsonObject { ["message"] = message }
        };
    }

    internal static UserMessage Read(JsonNode request, string version = "1.0")
    {
        using var document = JsonDocument.Parse(request.ToJsonString());
        return A2AProfile.Read(document.RootElement, version);
    }

    internal static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    internal static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response,
        A2AErrorCode code, HttpStatusCode status = HttpStatusCode.OK, string? id = "rpc-1")
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadResponseAsync(response);
        Assert.Equal("2.0", body.GetProperty("jsonrpc").GetString());
        if (id is null) Assert.Equal(JsonValueKind.Null, body.GetProperty("id").ValueKind);
        else Assert.Equal(id, body.GetProperty("id").GetString());
        Assert.Equal((int)code, body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetProperty("message").GetString()));
        Assert.False(body.TryGetProperty("result", out _));
        Assert.Equal("1.0", Assert.Single(response.Headers.GetValues("A2A-Version")));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(response.Headers.GetValues("X-Correlation-ID"))));
        return body;
    }

    internal static async Task<JsonElement> AssertMessageAsync(HttpResponseMessage response, string text)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadResponseAsync(response);
        Assert.Equal("2.0", body.GetProperty("jsonrpc").GetString());
        Assert.Equal("rpc-1", body.GetProperty("id").GetString());
        Assert.False(body.TryGetProperty("error", out _));
        var result = body.GetProperty("result");
        Assert.False(result.TryGetProperty("task", out _));
        var message = result.GetProperty("message");
        Assert.Equal("ROLE_AGENT", message.GetProperty("role").GetString());
        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("messageId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("contextId").GetString()));
        Assert.Equal(text, Assert.Single(message.GetProperty("parts").EnumerateArray()).GetProperty("text").GetString());
        return message;
    }
}