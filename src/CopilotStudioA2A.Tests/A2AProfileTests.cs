using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;

namespace CopilotStudioA2A.Tests;

/// <summary>Specifies the supported JSON-RPC envelope and A2A 1.0 text request profile.</summary>
public sealed class A2AProfileTests
{
    /// <summary>Rejects batches, notifications, invalid identifiers and malformed envelopes.</summary>
    /// <param name="json">A syntactically valid JSON value that is not a supported envelope.</param>
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"request\"")]
    [InlineData("[]")]
    [InlineData("[ {\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SendMessage\"} ]")]
    [InlineData("{}")]
    [InlineData("{\"id\":1,\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":2,\"id\":1,\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":[],\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":9223372036854775808,\"method\":\"SendMessage\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":42}")]
    public void GivenInvalidEnvelope_WhenRead_RejectsRequest(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.Throws<A2AException>(() => A2AProfile.Read(document.RootElement, "1.0"));

        Assert.Equal(A2AErrorCode.InvalidRequest, exception.ErrorCode);
    }

    /// <summary>Accepts integer and string correlation identifiers.</summary>
    /// <param name="id">The JSON representation of the correlation identifier.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("-9223372036854775808")]
    [InlineData("9223372036854775807")]
    [InlineData("\"caller-id\"")]
    public void GivenSupportedIdentifier_WhenRead_AcceptsEnvelope(string id)
    {
        var request = TestRequests.Create();
        request["id"] = JsonNode.Parse(id);

        var result = TestRequests.Read(request);

        Assert.Equal("caller-message-1", result.MessageId);
        Assert.Null(result.ContextId);
        Assert.Equal("Hello", result.Text);
    }

    /// <summary>Rejects duplicate properties at the root, message and text-part levels.</summary>
    /// <param name="original">The unique property to replace.</param>
    /// <param name="duplicate">Two occurrences of the property.</param>
    [Theory]
    [InlineData("\"jsonrpc\":\"2.0\"", "\"jsonrpc\":\"2.0\",\"jsonrpc\":\"2.0\"")]
    [InlineData("\"role\":\"ROLE_USER\"", "\"role\":\"ROLE_USER\",\"role\":\"ROLE_USER\"")]
    [InlineData("\"text\":\"Hello\"", "\"text\":\"Hello\",\"text\":\"Hello\"")]
    public void GivenDuplicateProperty_WhenRead_RejectsRequest(string original, string duplicate)
    {
        using var document = JsonDocument.Parse(TestRequests.Create().ToJsonString().Replace(original, duplicate, StringComparison.Ordinal));

        var exception = Assert.Throws<A2AException>(() => A2AProfile.Read(document.RootElement, "1.0"));

        Assert.Equal(A2AErrorCode.InvalidRequest, exception.ErrorCode);
    }

    /// <summary>Requires an exact version header value rather than a compatible-looking value.</summary>
    /// <param name="version">The supplied header value, empty when missing.</param>
    [Theory]
    [InlineData("")]
    [InlineData("0.3")]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("2.0")]
    [InlineData("1.0,1.0")]
    public void GivenUnsupportedVersion_WhenRead_RejectsVersion(string version)
    {
        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(TestRequests.Create(), version));

        Assert.Equal(A2AErrorCode.VersionNotSupported, exception.ErrorCode);
    }

    /// <summary>Distinguishes unsupported task, push, discovery and unknown operations.</summary>
    /// <param name="method">The JSON-RPC method.</param>
    /// <param name="code">The required protocol error.</param>
    [Theory]
    [InlineData("SendStreamingMessage", A2AErrorCode.UnsupportedOperation)]
    [InlineData("SubscribeToTask", A2AErrorCode.UnsupportedOperation)]
    [InlineData("GetTask", A2AErrorCode.UnsupportedOperation)]
    [InlineData("ListTasks", A2AErrorCode.UnsupportedOperation)]
    [InlineData("CancelTask", A2AErrorCode.UnsupportedOperation)]
    [InlineData("CreateTaskPushNotificationConfig", A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("GetTaskPushNotificationConfig", A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("ListTaskPushNotificationConfigs", A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("DeleteTaskPushNotificationConfig", A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("GetExtendedAgentCard", A2AErrorCode.ExtendedAgentCardNotConfigured)]
    [InlineData("message/send", A2AErrorCode.MethodNotFound)]
    [InlineData("sendmessage", A2AErrorCode.MethodNotFound)]
    [InlineData("Unknown", A2AErrorCode.MethodNotFound)]
    public void GivenUnsupportedMethod_WhenRead_ReturnsSpecificError(string method, A2AErrorCode code)
    {
        var request = TestRequests.Create();
        request["method"] = method;

        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request));

        Assert.Equal(code, exception.ErrorCode);
    }

    /// <summary>Requires params and message objects.</summary>
    /// <param name="field">The object to replace or remove.</param>
    /// <param name="json">Its invalid JSON value, or null to remove the property.</param>
    [Theory]
    [InlineData("params", null)]
    [InlineData("params", "null")]
    [InlineData("params", "[]")]
    [InlineData("params", "42")]
    [InlineData("message", null)]
    [InlineData("message", "null")]
    [InlineData("message", "[]")]
    public void GivenMissingObject_WhenRead_RejectsParameters(string field, string? json)
    {
        var request = TestRequests.Create();
        var parent = field == "params" ? request : request["params"]!.AsObject();
        if (json is null) parent.Remove(field);
        else parent[field] = JsonNode.Parse(json);

        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request));

        Assert.Equal(A2AErrorCode.InvalidParams, exception.ErrorCode);
    }

    /// <summary>Requires caller message IDs, user roles and nonempty context IDs when supplied.</summary>
    /// <param name="field">The message property.</param>
    /// <param name="json">The replacement value, or null to omit the property.</param>
    [Theory]
    [InlineData("messageId", null)]
    [InlineData("messageId", "null")]
    [InlineData("messageId", "42")]
    [InlineData("messageId", "\"\"")]
    [InlineData("messageId", "\"  \"")]
    [InlineData("role", null)]
    [InlineData("role", "null")]
    [InlineData("role", "42")]
    [InlineData("role", "\"user\"")]
    [InlineData("role", "\"ROLE_AGENT\"")]
    [InlineData("role", "\"role_user\"")]
    [InlineData("contextId", "\"\"")]
    [InlineData("contextId", "\"  \"")]
    [InlineData("contextId", "42")]
    public void GivenInvalidMessageField_WhenRead_RejectsParameters(string field, string? json)
    {
        var request = TestRequests.Create();
        var message = request["params"]!["message"]!.AsObject();
        if (json is null) message.Remove(field);
        else message[field] = JsonNode.Parse(json);

        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request));

        Assert.Equal(A2AErrorCode.InvalidParams, exception.ErrorCode);
    }

    /// <summary>Bounds message and context identifiers without shortening accepted identifiers.</summary>
    /// <param name="field">The identifier field.</param>
    [Theory]
    [InlineData("messageId")]
    [InlineData("contextId")]
    public void GivenIdentifierBoundary_WhenRead_Accepts256AndRejects257(string field)
    {
        var request = TestRequests.Create();
        request["params"]!["message"]![field] = new string('a', 256);
        var accepted = TestRequests.Read(request);
        Assert.Equal(new string('a', 256), field == "messageId" ? accepted.MessageId : accepted.ContextId);

        request["params"]!["message"]![field] = new string('a', 257);
        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request));

        Assert.Equal(A2AErrorCode.InvalidParams, exception.ErrorCode);
    }

    /// <summary>Rejects unsupported metadata, task associations and unknown fields.</summary>
    /// <param name="location">The object containing the field.</param>
    /// <param name="field">The field to add.</param>
    /// <param name="json">Its nonempty value.</param>
    /// <param name="code">The expected protocol error.</param>
    [Theory]
    [InlineData("params", "metadata", "{\"key\":1}", A2AErrorCode.UnsupportedOperation)]
    [InlineData("params", "tenant", "\"tenant\"", A2AErrorCode.UnsupportedOperation)]
    [InlineData("params", "unexpected", "true", A2AErrorCode.InvalidParams)]
    [InlineData("message", "taskId", "\"task-1\"", A2AErrorCode.TaskNotFound)]
    [InlineData("message", "metadata", "{\"key\":1}", A2AErrorCode.UnsupportedOperation)]
    [InlineData("message", "extensions", "[\"extension\"]", A2AErrorCode.UnsupportedOperation)]
    [InlineData("message", "referenceTaskIds", "[\"task-1\"]", A2AErrorCode.UnsupportedOperation)]
    [InlineData("message", "kind", "\"message\"", A2AErrorCode.InvalidParams)]
    public void GivenUnsupportedField_WhenRead_ReturnsSpecificError(string location, string field, string json, A2AErrorCode code)
    {
        var request = TestRequests.Create();
        var parent = location == "params" ? request["params"]! : request["params"]!["message"]!;
        parent[field] = JsonNode.Parse(json);

        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request));

        Assert.Equal(code, exception.ErrorCode);
    }

    /// <summary>Permits absent semantics expressed as null or empty optional fields.</summary>
    [Fact]
    public void GivenEmptyOptionalFields_WhenRead_PreservesText()
    {
        var request = TestRequests.Create();
        var parameters = request["params"]!;
        parameters["metadata"] = new JsonObject();
        parameters["tenant"] = "";
        parameters["configuration"] = null;
        var message = parameters["message"]!;
        message["contextId"] = null;
        message["taskId"] = null;
        message["metadata"] = new JsonObject();
        message["extensions"] = new JsonArray();
        message["referenceTaskIds"] = new JsonArray();

        var result = TestRequests.Read(request);

        Assert.Null(result.ContextId);
        Assert.Equal("Hello", result.Text);
    }

    /// <summary>Rejects configuration outside the direct-text response profile.</summary>
    /// <param name="json">The configuration JSON.</param>
    /// <param name="code">The expected protocol error.</param>
    [Theory]
    [InlineData("[]", A2AErrorCode.InvalidParams)]
    [InlineData("42", A2AErrorCode.InvalidParams)]
    [InlineData("{\"unexpected\":true}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"returnImmediately\":\"false\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"returnImmediately\":0}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"taskPushNotificationConfig\":{}}", A2AErrorCode.PushNotificationNotSupported)]
    [InlineData("{\"historyLength\":1}", A2AErrorCode.UnsupportedOperation)]
    [InlineData("{\"historyLength\":-1}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"historyLength\":0.5}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"historyLength\":2147483648}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"acceptedOutputModes\":\"text/plain\"}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"acceptedOutputModes\":[42]}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"acceptedOutputModes\":[null]}", A2AErrorCode.InvalidParams)]
    [InlineData("{\"acceptedOutputModes\":[\"image/png\"]}", A2AErrorCode.ContentTypeNotSupported)]
    [InlineData("{\"acceptedOutputModes\":[\"text/html\"]}", A2AErrorCode.ContentTypeNotSupported)]
    public void GivenUnsupportedConfiguration_WhenRead_ReturnsSpecificError(string json, A2AErrorCode code)
    {
        var request = TestRequests.Create();
        request["params"]!["configuration"] = JsonNode.Parse(json);

        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request));

        Assert.Equal(code, exception.ErrorCode);
    }

    /// <summary>Malformed history types must yield protocol errors, not CLR accessor exceptions.</summary>
    /// <param name="json">A nonnumeric history value.</param>
    [Theory]
    [InlineData("\"0\"")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void GivenNonnumericHistory_WhenRead_ReturnsProtocolError(string json)
    {
        var request = TestRequests.Create();
        request["params"]!["configuration"] = new JsonObject { ["historyLength"] = JsonNode.Parse(json) };

        var exception = Assert.Throws<A2AException>(() => TestRequests.Read(request));

        Assert.Equal(A2AErrorCode.InvalidParams, exception.ErrorCode);
    }

    /// <summary>Accepts supported output negotiation and direct-response configuration.</summary>
    /// <param name="json">A supported configuration.</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"acceptedOutputModes\":[]}")]
    [InlineData("{\"acceptedOutputModes\":[\"text/plain\"]}")]
    [InlineData("{\"acceptedOutputModes\":[\"text/*\"]}")]
    [InlineData("{\"acceptedOutputModes\":[\"*/*\"]}")]
    [InlineData("{\"acceptedOutputModes\":[\"image/png\",\"text/plain\"]}")]
    [InlineData("{\"returnImmediately\":true,\"historyLength\":0}")]
    [InlineData("{\"returnImmediately\":false,\"historyLength\":null,\"taskPushNotificationConfig\":null}")]
    public void GivenSupportedConfiguration_WhenRead_AcceptsText(string json)
    {
        var request = TestRequests.Create();
        request["params"]!["configuration"] = JsonNode.Parse(json);

        var result = TestRequests.Read(request);

        Assert.Equal("Hello", result.Text);
    }
}