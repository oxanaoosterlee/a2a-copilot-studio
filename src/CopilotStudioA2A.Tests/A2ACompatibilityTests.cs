using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;

namespace CopilotStudioA2A.Tests;

/// <summary>Specifies version selection, strict legacy validation and canonical wire translation.</summary>
public sealed class A2ACompatibilityTests
{
    /// <summary>Selects the same protocol for discovery and messages without body-based guessing.</summary>
    /// <param name="header">The raw version header.</param>
    /// <param name="expected">The selected protocol, or null when unsupported.</param>
    [Theory]
    [InlineData(null, "0.3")]
    [InlineData("", "0.3")]
    [InlineData("  ", "0.3")]
    [InlineData("0.3", "0.3")]
    [InlineData(" 1.0 ", "1.0")]
    [InlineData("0.3.0", null)]
    [InlineData("1.0.0", null)]
    [InlineData("0.3,1.0", null)]
    [InlineData("0.3,0.3", null)]
    [InlineData("2.0", null)]
    public void GivenVersionHeader_WhenSelecting_ReturnsExactSupportedVersion(string? header, string? expected)
    {
        Assert.Equal(expected, A2AProfile.SelectVersion(header));
    }

    /// <summary>Rejects legacy fields with incorrect types and mixed-version messages.</summary>
    /// <param name="path">The dotted property path to replace.</param>
    /// <param name="json">The replacement JSON, or null to remove the field.</param>
    /// <param name="code">The expected protocol error.</param>
    [Theory]
    [InlineData("params.message.kind", null, A2AErrorCode.InvalidParams)]
    [InlineData("params.message.kind", "null", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.kind", "42", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.kind", "\"task\"", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.role", "\"ROLE_USER\"", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.role", "\"agent\"", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.role", null, A2AErrorCode.InvalidParams)]
    [InlineData("params.message.messageId", "\"\"", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.messageId", null, A2AErrorCode.InvalidParams)]
    [InlineData("params.message.contextId", "42", A2AErrorCode.InvalidParams)]
    [InlineData("params.message.taskId", "\"task-1\"", A2AErrorCode.TaskNotFound)]
    [InlineData("params.configuration", "[]", A2AErrorCode.InvalidParams)]
    [InlineData("params.configuration", "{\"blocking\":\"true\"}", A2AErrorCode.InvalidParams)]
    [InlineData("params.configuration", "{\"blocking\":0}", A2AErrorCode.InvalidParams)]
    [InlineData("params.configuration", "{\"pushNotificationConfig\":{}}", A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("params.configuration", "{\"historyLength\":1}", A2AErrorCode.UnsupportedOperation)]
    [InlineData("params.configuration", "{\"historyLength\":-1}", A2AErrorCode.InvalidParams)]
    [InlineData("params.configuration", "{\"historyLength\":\"0\"}", A2AErrorCode.InvalidParams)]
    [InlineData("params.configuration", "{\"acceptedOutputModes\":[42]}", A2AErrorCode.InvalidParams)]
    [InlineData("params.configuration", "{\"acceptedOutputModes\":[\"image/png\"]}", A2AErrorCode.ContentTypeNotSupported)]
    public void GivenInvalidLegacyField_WhenReading_RejectsBeforeTranslation(string path, string? json, A2AErrorCode code)
    {
        var request = TestRequests.CreateLegacy();
        var segments = path.Split('.');
        JsonNode parent = request;
        foreach (var segment in segments[..^1]) parent = parent[segment]!;
        if (json is null) parent.AsObject().Remove(segments[^1]);
        else parent[segments[^1]] = JsonNode.Parse(json);

        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request, "0.3"));

        Assert.Equal(code, exception.ErrorCode);
    }

    /// <summary>Requires discriminated, text-only legacy parts without 1.0-only fields.</summary>
    /// <param name="part">The invalid legacy part JSON.</param>
    /// <param name="code">The expected protocol error.</param>
    [Theory]
    [InlineData("null", A2AErrorCode.InvalidParams)]
    [InlineData("[]", A2AErrorCode.InvalidParams)]
    [InlineData("{\"text\":\"hello\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"kind\":null,\"text\":\"hello\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"kind\":42,\"text\":\"hello\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"kind\":\"Text\",\"text\":\"hello\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"kind\":\"text\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"kind\":\"text\",\"text\":42}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"kind\":\"text\",\"text\":\" \"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"kind\":\"file\",\"file\":{\"uri\":\"https://example.invalid/file\"}}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("{\"kind\":\"data\",\"data\":{}}", A2AErrorCode.ContentTypeNotSupported)]
    public void GivenInvalidLegacyPart_WhenReading_ReturnsProtocolError(string part, A2AErrorCode code)
    {
        var request = TestRequests.CreateLegacy();
        request["params"]!["message"]!["parts"] = new JsonArray(JsonNode.Parse(part));

        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request, "0.3"));

        Assert.Equal(code, exception.ErrorCode);
    }

    /// <summary>Reads Copilot Studio legacy messages while ignoring extension data.</summary>
    [Fact]
    public void GivenCopilotStudioMetadata_WhenReadingLegacy_PreservesSupportedData()
    {
        var request = TestRequests.CreateLegacy("De gebruiker vraagt om de orchestrator te benaderen.");
        request["params"]!["unexpected"] = true;
        request["params"]!["message"]!["metadata"] = JsonNode.Parse("""
            {
              "copilotstudio.microsoft.com/a2a/chathistory":[{"HasValue":true,"Value":[{"From":"ssai_SaraOrchestrator","Text":"Hello"}]}],
              "copilotstudio.microsoft.com/a2a/extensions/activity/v1":{"channelId":"pva-studio","locale":"en-US"}
            }
            """);
        request["params"]!["message"]!["parts"]![0]!["metadata"] = new JsonObject { ["ignored"] = true };
        using var document = JsonDocument.Parse(request.ToJsonString());

        var input = A2AProfile.Read(document.RootElement, "0.3");
        var normalized = A2AWireFormat.CreateFrameworkRequest(document.RootElement, input, "server-context", "0.3");

        Assert.Equal("De gebruiker vraagt om de orchestrator te benaderen.", input.Text);
        Assert.DoesNotContain("metadata", normalized.ToJsonString());
        Assert.DoesNotContain("unexpected", normalized.ToJsonString());
    }

    /// <summary>Translates legacy configuration into a canonical request and preserves identifiers and text.</summary>
    /// <param name="configuration">A supported legacy configuration.</param>
    /// <param name="returnImmediately">The expected canonical execution option.</param>
    [Theory]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    [InlineData("{\"blocking\":true}", false)]
    [InlineData("{\"blocking\":false}", true)]
    [InlineData("{\"blocking\":null,\"pushNotificationConfig\":null,\"historyLength\":0}", null)]
    [InlineData("{\"acceptedOutputModes\":[\"text/*\"],\"historyLength\":0,\"blocking\":true}", false)]
    [InlineData("{\"acceptedOutputModes\":[]}", null)]
    public void GivenLegacyRequest_WhenNormalizing_ProducesValidV1WithoutMutatingInput(string configuration, bool? returnImmediately)
    {
        var request = TestRequests.CreateLegacy("  Hello\nworld  ", "caller-context");
        request["id"] = long.MaxValue;
        request["params"]!["configuration"] = JsonNode.Parse(configuration);
        request["params"]!["metadata"] = new JsonObject();
        request["params"]!["message"]!["metadata"] = new JsonObject();
        request["params"]!["message"]!["parts"]!.AsArray().Add(new JsonObject { ["kind"] = "text", ["text"] = "Grüße 🌍" });
        var original = request.ToJsonString();
        using var document = JsonDocument.Parse(original);
        var input = A2AProfile.Read(document.RootElement, "0.3");

        var normalized = A2AWireFormat.CreateFrameworkRequest(document.RootElement, input, "server-context", "0.3");

        Assert.Equal(original, request.ToJsonString());
        Assert.Equal(long.MaxValue, normalized["id"]!.GetValue<long>());
        Assert.Equal("SendMessage", normalized["method"]!.GetValue<string>());
        Assert.Equal(returnImmediately, normalized["params"]?["configuration"]?["returnImmediately"]?.GetValue<bool>());
        Assert.DoesNotContain("blocking", normalized.ToJsonString());
        Assert.DoesNotContain("kind", normalized.ToJsonString());
        Assert.DoesNotContain("metadata", normalized.ToJsonString());
        Assert.Equal(2, normalized["params"]!["message"]!["parts"]!.AsArray().Count);
        foreach (var field in new[] { "acceptedOutputModes", "historyLength" })
            Assert.True(JsonNode.DeepEquals(request["params"]?["configuration"]?[field], normalized["params"]?["configuration"]?[field]));
        var canonical = TestRequests.Read(normalized);
        Assert.Equal("caller-message-1", canonical.MessageId);
        Assert.Equal("server-context", canonical.ContextId);
        Assert.Equal("  Hello\nworld  \nGrüße 🌍", canonical.Text);
    }

    /// <summary>Leaves supported 1.0 configuration semantics intact during canonical dispatch.</summary>
    /// <param name="returnImmediately">The configured direct-response execution flag.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GivenV1Configuration_WhenNormalizing_PreservesConfiguration(bool returnImmediately)
    {
        var request = TestRequests.Create();
        var configuration = new JsonObject
        {
            ["returnImmediately"] = returnImmediately,
            ["acceptedOutputModes"] = new JsonArray("text/plain"),
            ["historyLength"] = 0
        };
        request["params"]!["configuration"] = configuration;
        using var document = JsonDocument.Parse(request.ToJsonString());
        var input = A2AProfile.Read(document.RootElement, "1.0");

        var normalized = A2AWireFormat.CreateFrameworkRequest(document.RootElement, input, "server-context", "1.0");

        Assert.True(JsonNode.DeepEquals(configuration, normalized["params"]!["configuration"]));
        Assert.Equal("rpc-1", normalized["id"]!.GetValue<string>());
        Assert.Equal("Hello", TestRequests.Read(normalized).Text);
    }

    /// <summary>Produces the selected response shape without changing the framework message.</summary>
    /// <param name="version">The response protocol version.</param>
    [Theory]
    [InlineData("0.3")]
    [InlineData("1.0")]
    public void GivenFrameworkMessage_WhenSerializing_PreservesIdsAndTextInSelectedShape(string version)
    {
        using var id = JsonDocument.Parse("-9223372036854775808");
        var message = JsonNode.Parse("""
            {"messageId":"reply-id","contextId":"framework-context","role":"ROLE_AGENT",
             "parts":[{"text":"  answer 🌍  ","mediaType":"text/plain"},{"text":"second"}]}
            """)!.AsObject();
        var original = message.ToJsonString();

        var response = A2AWireFormat.CreateResponse(id.RootElement, message, "public-context", version);

        Assert.Equal(original, message.ToJsonString());
        Assert.Equal(long.MinValue, response["id"]!.GetValue<long>());
        var reply = version == "0.3" ? response["result"]! : response["result"]!["message"]!;
        Assert.Equal("reply-id", reply["messageId"]!.GetValue<string>());
        Assert.Equal("public-context", reply["contextId"]!.GetValue<string>());
        Assert.Equal(version == "0.3" ? "agent" : "ROLE_AGENT", reply["role"]!.GetValue<string>());
        Assert.Equal("  answer 🌍  ", reply["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("second", reply["parts"]![1]!["text"]!.GetValue<string>());
        Assert.Equal(version == "0.3" ? "message" : null, reply["kind"]?.GetValue<string>());
        foreach (var part in reply["parts"]!.AsArray())
            Assert.Equal(version == "0.3" ? "text" : null, part!["kind"]?.GetValue<string>());
        if (version == "0.3") Assert.DoesNotContain("mediaType", response.ToJsonString());
    }
}