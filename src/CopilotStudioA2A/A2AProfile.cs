using System.Text.Json;
using A2A;

namespace CopilotStudioA2A;

/// <summary>
/// Checks A2A requests and allows only direct text messages that this adapter supports.
/// </summary>
internal static class A2AProfile
{
    internal const string Version = "1.0";
    internal const string MediaType = "text/plain";

    public static UserMessage Read(JsonElement root, string version)
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
        if (version != Version)
        {
            throw Error(A2AErrorCode.VersionNotSupported, "Use the A2A-Version: 1.0 HTTP header. Only version 1.0 is supported.");
        }

        var methodName = method.GetString();
        if (methodName != "SendMessage")
        {
            var code = methodName switch
            {
                "CreateTaskPushNotificationConfig" or "GetTaskPushNotificationConfig" or
                "ListTaskPushNotificationConfigs" or "DeleteTaskPushNotificationConfig" => A2AErrorCode.PushNotificationNotSupported,
                "SendStreamingMessage" or "SubscribeToTask" or "GetTask" or "ListTasks" or "CancelTask" => A2AErrorCode.UnsupportedOperation,
                "GetExtendedAgentCard" => A2AErrorCode.ExtendedAgentCardNotConfigured,
                _ => A2AErrorCode.MethodNotFound
            };
            throw Error(code, "Only SendMessage is available; tasks, streaming and push notifications are not supported.");
        }

        var parameters = RequiredObject(root, "params");
        OnlyProperties(parameters, "message", "configuration", "metadata", "tenant");
        RejectNonempty(parameters, "metadata");
        RejectNonempty(parameters, "tenant");
        var message = RequiredObject(parameters, "message");
        OnlyProperties(message, "messageId", "contextId", "role", "parts", "taskId", "metadata", "extensions", "referenceTaskIds");
        var messageId = RequiredString(message, "messageId");
        if (RequiredString(message, "role") != "ROLE_USER")
        {
            throw Error(A2AErrorCode.InvalidParams, "message.role must be ROLE_USER.");
        }
        if (HasValue(message, "taskId"))
        {
            throw Error(A2AErrorCode.TaskNotFound, "This adapter does not create or resume tasks.");
        }
        foreach (var field in new[] { "metadata", "extensions", "referenceTaskIds" })
        {
            RejectNonempty(message, field);
        }

        var contextId = HasValue(message, "contextId") ? RequiredString(message, "contextId") : null;
        if (messageId.Length > 256 || contextId?.Length > 256)
        {
            throw Error(A2AErrorCode.InvalidParams, "Message and context identifiers must be at most 256 characters.");
        }

        if (parameters.TryGetProperty("configuration", out var configuration) && configuration.ValueKind != JsonValueKind.Null)
        {
            ValidateConfiguration(configuration);
        }

        return new UserMessage(messageId, contextId, TextTranslation.FromA2A(message));
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

    internal static void OnlyProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error(A2AErrorCode.InvalidParams, "An object was expected.");
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Error(A2AErrorCode.InvalidParams, "The request contains a field outside the supported A2A 1.0 text profile.");
            }
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

    private static void ValidateConfiguration(JsonElement configuration)
    {
        OnlyProperties(configuration, "acceptedOutputModes", "returnImmediately", "historyLength", "taskPushNotificationConfig");
        if (HasValue(configuration, "taskPushNotificationConfig"))
        {
            throw Error(A2AErrorCode.PushNotificationNotSupported, "Push notifications are not supported.");
        }
        if (HasValue(configuration, "returnImmediately") &&
            configuration.GetProperty("returnImmediately").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Error(A2AErrorCode.InvalidParams, "returnImmediately must be a boolean; it has no effect for direct message responses.");
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

    private static void RejectNonempty(JsonElement element, string name)
    {
        if (!HasValue(element, name)) return;
        var value = element.GetProperty(name);
        if ((value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) ||
            (value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()) ||
            (value.ValueKind == JsonValueKind.String && value.GetString() == "")) return;
        throw Error(A2AErrorCode.UnsupportedOperation, $"{name} is not supported by this text-only adapter.");
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