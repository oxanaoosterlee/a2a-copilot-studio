using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotStudioA2A;

/// <summary>
/// Stores and checks the main settings for the A2A adapter.
/// </summary>
internal sealed class AdapterOptions
{
    public string PublicBaseUrl { get; set; } = "http://localhost:5180";
    public bool UseHardcodedBackend { get; set; }
    public int MaxConversations { get; set; } = 1000;
    public int MaxRequestBytes { get; set; } = 65536;
    public int RequestTimeoutSeconds { get; set; } = 120;

    public void Validate(bool isDevelopment)
    {
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var url) ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) ||
            !string.IsNullOrEmpty(url.Fragment) || url.AbsolutePath != "/" ||
            (url.Scheme != "https" && !(isDevelopment && url.IsLoopback && url.Scheme == "http")))
        {
            throw new InvalidOperationException("Adapter:PublicBaseUrl must be an HTTPS origin (HTTP loopback is allowed in Development).");
        }

        if (MaxConversations is < 1 or > 100000 || MaxRequestBytes is < 1024 or > 1048576 ||
            RequestTimeoutSeconds is < 1 or > 180)
        {
            throw new InvalidOperationException("Adapter limits are invalid; request timeout must be between 1 and 180 seconds.");
        }
    }
}

/// <summary>
/// Stores and checks the settings used to confirm a caller's identity.
/// </summary>
internal sealed class AuthenticationOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Audience { get; set; } = "";
    public string RequiredScope { get; set; } = "Agents.Invoke";
    public string ClientSecret { get; set; } = "";
    public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public void Validate(bool requireClientSecret = true)
    {
        if (!Guid.TryParse(TenantId, out _) || !Guid.TryParse(ClientId, out _) ||
            Audience != ClientId || RequiredScope != "Agents.Invoke" ||
            (requireClientSecret && string.IsNullOrWhiteSpace(ClientSecret)))
        {
            throw new InvalidOperationException(
                $"Configure Authentication:TenantId, ClientId, Audience (the same client ID), " +
                $"{(requireClientSecret ? "ClientSecret and " : "")}RequiredScope=Agents.Invoke. Only tenant-specific Entra v2 delegated access tokens are accepted.");
        }
    }
}

/// <summary>
/// Loads and checks the list of configured Copilot Studio agents.
/// </summary>
internal sealed class CopilotStudioOptions
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Agents", "Adapter", "Authentication", "Logging", "AllowedHosts", "CopilotStudio"
    };

    public Dictionary<string, CopilotAgentOptions> Agents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> UnlistedAgents { get; private set; } = [];

    public static CopilotStudioOptions Load(IConfiguration configuration, bool requireDirectConnectUrl = true)
    {
        var names = ReadAgentNames(configuration.GetSection("Agents"));
        var listedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !Regex.IsMatch(name, "\\A[A-Za-z][A-Za-z0-9-]{0,62}\\z", RegexOptions.CultureInvariant) ||
                ReservedNames.Contains(name) || !listedNames.Add(name))
            {
                throw new InvalidOperationException(
                    "Agents must contain unique names (case-insensitive), each 1-63 ASCII letters, digits or hyphens, starting with a letter. Names cannot be Agents, Adapter, Authentication, Logging, AllowedHosts or CopilotStudio.");
            }
        }

        var options = new CopilotStudioOptions();
        foreach (var name in names)
        {
            // Environment variables such as CoolAgent__DirectConnectUrl become CoolAgent:DirectConnectUrl.
            // Read known string values directly; never bind or log an unlisted agent's configuration.
            var agent = new CopilotAgentOptions
            {
                DirectConnectUrl = configuration[$"{name}:DirectConnectUrl"] ?? "",
                SkillDescription = configuration[$"{name}:SkillDescription"] ?? ""
            };
            ValidateAgent(name, agent, requireDirectConnectUrl);
            options.Agents.Add(name, agent);
        }

        options.UnlistedAgents = configuration.GetChildren()
            .Where(section => !listedNames.Contains(section.Key) && section.GetChildren().Any(setting =>
                string.Equals(setting.Key, "DirectConnectUrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(setting.Key, "SkillDescription", StringComparison.OrdinalIgnoreCase)))
            .Select(section => section.Key)
            .ToArray();
        return options;
    }

    public void LogUnlistedAgentWarnings(ILogger logger)
    {
        foreach (var name in UnlistedAgents)
        {
            logger.LogWarning(
                "Agent settings exist for {AgentName}, but the name is not in Agents. These settings are ignored and no endpoint is registered.", name);
        }
    }

    private static string[] ReadAgentNames(IConfigurationSection section)
    {
        string[]? names;
        if (section.Value is { } json)
        {
            // App Service settings are strings. A single Agents='["CoolAgent"]' replaces the list,
            // including any lower-priority indexed entries inherited from configuration files.
            try
            {
                names = JsonSerializer.Deserialize<string[]>(json);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Agents must be a JSON array of quoted agent names, for example [\"CoolAgent\"].");
            }
        }
        else
        {
            var entries = section.GetChildren().ToArray();
            // Accept the native appsettings array / Agents__0 form, but not a name-to-settings map.
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index].Key != index.ToString(CultureInfo.InvariantCulture) || entries[index].GetChildren().Any())
                    throw new InvalidOperationException("Agents must be an array of names with consecutive indexes starting at zero.");
            }
            names = entries.Select(entry => entry.Value ?? "").ToArray();
        }

        if (names is null || names.Length == 0)
            throw new InvalidOperationException("Agents must list at least one agent. Configure <name>:DirectConnectUrl and <name>:SkillDescription for every listed name.");
        return names;
    }

    private static void ValidateAgent(string name, CopilotAgentOptions agent, bool requireDirectConnectUrl)
    {
        var missing = new List<string>();
        if (requireDirectConnectUrl && string.IsNullOrWhiteSpace(agent.DirectConnectUrl)) missing.Add($"{name}:DirectConnectUrl");
        if (string.IsNullOrWhiteSpace(agent.SkillDescription)) missing.Add($"{name}:SkillDescription");
        if (missing.Count > 0)
            throw new InvalidOperationException($"Agent '{name}' is listed in Agents but required settings are missing or blank: {string.Join(", ", missing)}.");

        if (requireDirectConnectUrl && (!Uri.TryCreate(agent.DirectConnectUrl, UriKind.Absolute, out var url) ||
            url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Fragment) || !url.Host.EndsWith(".environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase)))
        {
            // Never include the configured value in errors: connection URLs are secret.
            throw new InvalidOperationException($"{name}:DirectConnectUrl must be an HTTPS public-cloud Power Platform URL.");
        }
    }
}

/// <summary>
/// Stores the connection URL and public skill description for one agent.
/// </summary>
internal sealed class CopilotAgentOptions
{
    public string DirectConnectUrl { get; set; } = "";

    /// <summary>Describes this agent's skill in its public A2A card; must not contain secrets.</summary>
    public string SkillDescription { get; set; } = "";
}