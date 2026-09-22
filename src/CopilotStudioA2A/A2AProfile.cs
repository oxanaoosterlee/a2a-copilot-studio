using System.Text.Json;
using A2A;

namespace CopilotStudioA2A;

/// <summary>
/// Checks A2A requests and allows only direct text messages that this adapter supports.
/// </summary>
internal static class A2AProfile
{
    internal const string Version = "1.0";
    internal const string LegacyVersion = "0.3";
    internal const string MediaType = "text/plain";

    internal static string? SelectVersion(string? header) => header?.Trim() switch
    {
        null or "" or LegacyVersion => LegacyVersion,
        Version => Version,
        _ => null
    };

    public static UserMessage Read(JsonElement root, string? version)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0" ||
            !root.TryGetProperty("id", out var id) ||
            !(id.ValueKind == JsonValueKind.String || (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _))) ||
            !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
        {
            throw Error(A2AErrorCode.InvalidRequest, "Supply a single JSON-RPC 2.0 request with a string/integer id. Batches and notifications are not supported.");
        }

        RejectDuplicateProperties(root);
        version = SelectVersion(version) ?? throw Error(A2AErrorCode.VersionNotSupported,
            "Use A2A-Version: 0.3 or 1.0. An absent or empty header selects 0.3.");
        var legacy = version == LegacyVersion;

        var methodName = method.GetString();
        var sendMethod = legacy ? "message/send" : "SendMessage";
        if (methodName != sendMethod)
        {
            var code = (legacy, methodName) switch
            {
                (true, "tasks/pushNotificationConfig/set" or "tasks/pushNotificationConfig/get" or
                    "tasks/pushNotificationConfig/list" or "tasks/pushNotificationConfig/delete") => A2AErrorCode.PushNotificationNotSupported,
                (true, "message/stream" or "tasks/resubscribe" or "tasks/get" or "tasks/cancel") => A2AErrorCode.UnsupportedOperation,
                (true, "agent/getAuthenticatedExtendedCard") => A2AErrorCode.ExtendedAgentCardNotConfigured,
                (false, "CreateTaskPushNotificationConfig" or "GetTaskPushNotificationConfig" or
                    "ListTaskPushNotificationConfigs" or "DeleteTaskPushNotificationConfig") => A2AErrorCode.PushNotificationNotSupported,
                (false, "SendStreamingMessage" or "SubscribeToTask" or "GetTask" or "ListTasks" or "CancelTask") => A2AErrorCode.UnsupportedOperation,
                (false, "GetExtendedAgentCard") => A2AErrorCode.ExtendedAgentCardNotConfigured,
                _ => A2AErrorCode.MethodNotFound
            };
            throw Error(code, $"Only {sendMethod} is available for A2A {version}; tasks, streaming and push notifications are not supported.");
        }

        var parameters = RequiredObject(root, "params");
        var message = RequiredObject(parameters, "message");
        if (legacy)
        {
            if (RequiredString(message, "kind") != "message")
                throw Error(A2AErrorCode.InvalidParams, "message.kind must be message for A2A 0.3.");
        }
        var messageId = RequiredString(message, "messageId");
        var userRole = legacy ? "user" : "ROLE_USER";
        if (RequiredString(message, "role") != userRole)
        {
            throw Error(A2AErrorCode.InvalidParams, $"message.role must be {userRole} for A2A {version}.");
        }
        if (HasValue(message, "taskId"))
        {
            throw Error(A2AErrorCode.TaskNotFound, "This adapter does not create or resume tasks.");
        }

        string? contextId = null;
        if (HasValue(message, "contextId"))
        {
            var context = message.GetProperty("contextId");
            if (context.ValueKind != JsonValueKind.String)
                throw Error(A2AErrorCode.InvalidParams, "contextId must be a string or null.");
            contextId = string.IsNullOrWhiteSpace(context.GetString()) ? null : context.GetString();
        }
        if (messageId.Length > 256 || contextId?.Length > 256)
        {
            throw Error(A2AErrorCode.InvalidParams, "Message and context identifiers must be at most 256 characters.");
        }

        if (parameters.TryGetProperty("configuration", out var configuration) && configuration.ValueKind != JsonValueKind.Null)
        {
            ValidateConfiguration(configuration, legacy);
        }

        return new UserMessage(messageId, contextId, TextTranslation.FromA2A(message, version));
    }

    public static A2AException Error(A2AErrorCode code, string message) => new(message, code);

    internal static bool HasValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    internal static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Error(A2AErrorCode.InvalidParams, $"{name} must be a nonempty string.");
        }
        return value.GetString()!;
    }

    internal static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error(A2AErrorCode.InvalidParams, "An object was expected.");
        }
    }

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw Error(A2AErrorCode.InvalidParams, $"{name} must be an object.");
        }
        return value;
    }

    private static void ValidateConfiguration(JsonElement configuration, bool legacy)
    {
        var executionMode = legacy ? "blocking" : "returnImmediately";
        var pushConfig = legacy ? "pushNotificationConfig" : "taskPushNotificationConfig";
        RequireObject(configuration);
        if (HasValue(configuration, pushConfig))
        {
            throw Error(A2AErrorCode.PushNotificationNotSupported, "Push notifications are not supported.");
        }
        if (HasValue(configuration, executionMode) &&
            configuration.GetProperty(executionMode).ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Error(A2AErrorCode.InvalidParams, $"{executionMode} must be a boolean; it has no effect for direct message responses.");
        }
        if (HasValue(configuration, "historyLength"))
        {
            var length = configuration.GetProperty("historyLength");
            if (length.ValueKind != JsonValueKind.Number || !length.TryGetInt32(out var history) || history < 0)
                throw Error(A2AErrorCode.InvalidParams, "historyLength must be a nonnegative integer.");
            if (history != 0)
                throw Error(A2AErrorCode.UnsupportedOperation, "Task history is unavailable; omit historyLength or use zero.");
        }
        if (HasValue(configuration, "acceptedOutputModes"))
        {
            var modes = configuration.GetProperty("acceptedOutputModes");
            if (modes.ValueKind != JsonValueKind.Array || modes.EnumerateArray().Any(mode => mode.ValueKind != JsonValueKind.String))
            {
                throw Error(A2AErrorCode.InvalidParams, "acceptedOutputModes must be an array of strings.");
            }
            if (modes.GetArrayLength() > 0 && !modes.EnumerateArray().Any(mode => mode.GetString() is MediaType or "text/*" or "*/*"))
            {
                throw Error(A2AErrorCode.ContentTypeNotSupported, "Only text/plain output is supported.");
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Error(A2AErrorCode.InvalidRequest, "Duplicate JSON property names are not supported.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}

/// <summary>
/// Stores the message data that the adapter reads from an A2A request.
/// </summary>
internal sealed record UserMessage(string MessageId, string? ContextId, string Text);