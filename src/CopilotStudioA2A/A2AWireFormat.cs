using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotStudioA2A;

/// <summary>Translates validated wire messages at the Agent Framework 1.0 boundary.</summary>
internal static class A2AWireFormat
{
    internal static JsonObject CreateFrameworkRequest(JsonElement root, UserMessage input, string contextId, string version)
    {
        var parameters = root.GetProperty("params");
        var parts = new JsonArray();
        foreach (var part in parameters.GetProperty("message").GetProperty("parts").EnumerateArray())
            parts.Add(new JsonObject { ["text"] = part.GetProperty("text").GetString() });

        var normalizedParameters = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["messageId"] = input.MessageId,
                ["contextId"] = contextId,
                ["role"] = "ROLE_USER",
                ["parts"] = parts
            }
        };
        if (A2AProfile.HasValue(parameters, "configuration"))
        {
            var configuration = parameters.GetProperty("configuration");
            var normalizedConfiguration = new JsonObject();
            foreach (var field in new[] { "acceptedOutputModes", "historyLength" })
            {
                if (A2AProfile.HasValue(configuration, field))
                    normalizedConfiguration[field] = JsonNode.Parse(configuration.GetProperty(field).GetRawText());
            }
            var legacy = version == A2AProfile.LegacyVersion;
            var executionMode = legacy ? "blocking" : "returnImmediately";
            if (A2AProfile.HasValue(configuration, executionMode))
            {
                var mode = configuration.GetProperty(executionMode).GetBoolean();
                normalizedConfiguration["returnImmediately"] = legacy ? !mode : mode;
            }
            // DisallowBackground still governs execution. Empty unsupported fields are not forwarded.
            normalizedParameters["configuration"] = normalizedConfiguration;
        }
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(root.GetProperty("id").GetRawText()),
            ["method"] = "SendMessage",
            ["params"] = normalizedParameters
        };
    }

    internal static JsonObject CreateResponse(JsonElement id, JsonObject message, string contextId, string version)
    {
        JsonObject result;
        if (version == A2AProfile.LegacyVersion)
        {
            var parts = new JsonArray();
            foreach (var part in message["parts"]!.AsArray())
                parts.Add(new JsonObject { ["kind"] = "text", ["text"] = part!["text"]!.GetValue<string>() });
            result = new JsonObject
            {
                ["kind"] = "message",
                ["messageId"] = message["messageId"]!.GetValue<string>(),
                ["contextId"] = contextId,
                ["role"] = "agent",
                ["parts"] = parts
            };
        }
        else
        {
            var reply = message.DeepClone().AsObject();
            reply["contextId"] = contextId;
            result = new JsonObject { ["message"] = reply };
        }
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = result
        };
    }
}