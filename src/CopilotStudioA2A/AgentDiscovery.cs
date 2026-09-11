namespace CopilotStudioA2A;

/// <summary>
/// Builds public paths and discovery cards for configured agents.
/// </summary>
internal static class AgentDiscovery
{
    public static string RuntimePath(string name) => $"/copilot-studio/{name}/a2a";
    public static string CardPath(string name) => $"/copilot-studio/{name}/a2a/.well-known/agent-card.json";

    public static object CreateCard(string name, AdapterOptions options, CopilotAgentOptions agentOptions) => new
    {
        name,
        description = "Copilot Studio specialist agent with delegated, text-only request/response access. Requires the Agents.Invoke scope.",
        version = "1.0.0",
        supportedInterfaces = new[]
        {
            new { url = options.PublicBaseUrl.TrimEnd('/') + RuntimePath(name), protocolBinding = "JSONRPC", protocolVersion = A2AProfile.Version }
        },
        capabilities = new { streaming = false, pushNotifications = false, extendedAgentCard = false },
        defaultInputModes = new[] { A2AProfile.MediaType },
        defaultOutputModes = new[] { A2AProfile.MediaType },
        securitySchemes = new
        {
            entra = new { httpAuthSecurityScheme = new { scheme = "Bearer", bearerFormat = "JWT", description = "Tenant-specific Entra v2 access token with delegated Agents.Invoke scope." } }
        },
        securityRequirements = new[] { new { schemes = new { entra = new { list = Array.Empty<string>() } } } },
        skills = new[]
        {
            new { id = name, name, description = agentOptions.SkillDescription, tags = new[] { "copilot-studio", "text" } }
        }
    };
}