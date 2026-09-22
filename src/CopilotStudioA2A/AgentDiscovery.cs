namespace CopilotStudioA2A;

/// <summary>
/// Builds public paths and discovery cards for configured agents.
/// </summary>
internal static class AgentDiscovery
{
    public static string RuntimePath(string name) => $"/copilot-studio/{name}/a2a";
    public static string CardPath(string name) => $"/copilot-studio/{name}/a2a/.well-known/agent-card.json";

    /// <summary>Selects the discovery representation using the A2A-Version request header.</summary>
    /// <param name="http">The discovery request and response context.</param>
    /// <param name="name">The configured agent name.</param>
    /// <param name="options">The adapter's public endpoint settings.</param>
    /// <param name="agentOptions">The agent's public skill settings.</param>
    /// <returns>A versioned card or an HTTP problem response for an unsupported version.</returns>
    public static IResult GetCard(HttpContext http, string name, AdapterOptions options, CopilotAgentOptions agentOptions)
    {
        http.Response.Headers.Append("Vary", "A2A-Version");
        var version = A2AProfile.SelectVersion(http.Request.Headers["A2A-Version"].ToString());

        if (version is null)
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A2A protocol version not supported",
                type: "https://a2a-protocol.org/errors/version-not-supported",
                detail: "Use A2A-Version: 0.3 or 1.0 for agent discovery. An absent or empty header selects 0.3.",
                extensions: new Dictionary<string, object?> { ["supportedVersions"] = new[] { "0.3", A2AProfile.Version } });
        }

        http.Response.Headers["A2A-Version"] = version;
        return Results.Ok(version == "0.3"
            ? CreateLegacyCard(name, options, agentOptions)
            : CreateCard(name, options, agentOptions));
    }

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

    private static object CreateLegacyCard(string name, AdapterOptions options, CopilotAgentOptions agentOptions) => new
    {
        protocolVersion = "0.3.0",
        name,
        description = "Copilot Studio specialist agent with delegated, text-only request/response access. Requires the Agents.Invoke scope.",
        version = "1.0.0",
        url = options.PublicBaseUrl.TrimEnd('/') + RuntimePath(name),
        preferredTransport = "JSONRPC",
        capabilities = new { streaming = false, pushNotifications = false, stateTransitionHistory = false },
        supportsAuthenticatedExtendedCard = false,
        defaultInputModes = new[] { A2AProfile.MediaType },
        defaultOutputModes = new[] { A2AProfile.MediaType },
        securitySchemes = new
        {
            entra = new { type = "http", scheme = "Bearer", bearerFormat = "JWT", description = "Tenant-specific Entra v2 access token with delegated Agents.Invoke scope." }
        },
        security = new[] { new { entra = Array.Empty<string>() } },
        skills = new[]
        {
            new { id = name, name, description = agentOptions.SkillDescription, tags = new[] { "copilot-studio", "text" } }
        }
    };
}