using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CopilotStudioA2A.Tests;

/// <summary>Specifies the root agent allowlist, required settings and sanitized diagnostics.</summary>
public sealed class CopilotStudioOptionsTests
{
    private const string DirectConnectUrl = "https://test.environment.api.powerplatform.com/synthetic-private-path?secret=synthetic-url-marker";

    private static Dictionary<string, string?> CreateSettings(params string[] names)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            settings[$"Agents:{index}"] = name;
            settings[$"{name}:DirectConnectUrl"] = DirectConnectUrl;
            settings[$"{name}:SkillDescription"] = $"Skill for {name}.";
        }
        return settings;
    }

    private static ConfigurationRoot BuildJson(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private static void AssertSanitized(InvalidOperationException error, params string[] sensitiveValues)
    {
        Assert.Null(error.InnerException);
        foreach (var value in sensitiveValues)
            Assert.DoesNotContain(value, error.ToString());
    }

    /// <summary>Resolves native nested JSON and preserves configured name casing independently of key casing.</summary>
    [Fact]
    public void GivenNativeJson_WhenLoad_PreservesNamesAndDistinctSettings()
    {
        using var configuration = BuildJson($$"""
            {
                            "aGeNtS": ["CoolAgent", "BackupAgent"],
                            "coolagent": { "directconnecturl": "{{DirectConnectUrl}}", "skilldescription": "Answers general questions." },
                            "BACKUPAGENT": { "DIRECTCONNECTURL": "{{DirectConnectUrl}}", "SKILLDESCRIPTION": "Handles backup requests." }
            }
            """);

        var options = CopilotStudioOptions.Load(configuration);

        Assert.Equal(new[] { "CoolAgent", "BackupAgent" }, options.Agents.Keys);
        Assert.Equal("Answers general questions.", options.Agents["COOLAGENT"].SkillDescription);
        Assert.Equal("Handles backup requests.", options.Agents["backupagent"].SkillDescription);
        Assert.All(options.Agents.Values, agent => Assert.Equal(DirectConnectUrl, agent.DirectConnectUrl));
        Assert.Empty(options.UnlistedAgents);
    }

    /// <summary>Uses case-insensitive configuration keys and agent lookups for both supported flattened forms.</summary>
    /// <param name="scalar">Whether the allowlist is a scalar JSON array instead of indexed children.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GivenFlattenedSettings_WhenLoad_ResolvesCaseInsensitiveKeys(bool scalar)
    {
        var settings = CreateSettings("CoolAgent", "BackupAgent");
        if (scalar)
        {
            settings.Remove("Agents:0");
            settings.Remove("Agents:1");
            settings["Agents"] = "[\"CoolAgent\",\"BackupAgent\"]";
        }
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(
            settings.ToDictionary(pair => pair.Key.ToUpperInvariant(), pair => pair.Value)).Build();

        var options = CopilotStudioOptions.Load(configuration);

        Assert.Equal(new[] { "CoolAgent", "BackupAgent" }, options.Agents.Keys);
        Assert.Equal("Skill for CoolAgent.", options.Agents["coolagent"].SkillDescription);
        Assert.Equal("Skill for BackupAgent.", options.Agents["BACKUPAGENT"].SkillDescription);
        Assert.All(options.Agents.Values, agent => Assert.Equal(DirectConnectUrl, agent.DirectConnectUrl));
        Assert.Empty(options.UnlistedAgents);
    }

    /// <summary>Delegates double-underscore normalization to the environment provider, using an isolated prefix.</summary>
    /// <param name="scalar">Whether the environment supplies a scalar array or indexed entries.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GivenEnvironmentSettings_WhenLoad_UsesProviderNormalizedSections(bool scalar)
    {
        var prefix = $"A2A_OPTIONS_TEST_{Guid.NewGuid():N}_";
        var settings = CreateSettings("CoolAgent", "BackupAgent");
        if (scalar)
        {
            settings.Remove("Agents:0");
            settings.Remove("Agents:1");
            settings["Agents"] = "[\"CoolAgent\",\"BackupAgent\"]";
        }
        var variables = settings.ToDictionary(pair => prefix + pair.Key.Replace(":", "__"), pair => pair.Value);
        try
        {
            foreach (var (key, value) in variables) Environment.SetEnvironmentVariable(key, value);
            using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();

            var options = CopilotStudioOptions.Load(configuration);

            Assert.Equal(new[] { "CoolAgent", "BackupAgent" }, options.Agents.Keys);
            Assert.All(options.Agents.Values, agent => Assert.Equal(DirectConnectUrl, agent.DirectConnectUrl));
            Assert.Empty(options.UnlistedAgents);
        }
        finally
        {
            foreach (var key in variables.Keys) Environment.SetEnvironmentVariable(key, null);
        }
    }

    /// <summary>A scalar replaces inherited indexed names rather than merging or validating them.</summary>
    [Fact]
    public void GivenScalarOverride_WhenLoad_ReplacesPreexistingNativeArray()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            { "Agents": ["support", "billing", "invalid/name"],
              "support": { "DirectConnectUrl": "invalid-synthetic-old-url" },
              "billing": { "SkillDescription": "" } }
            """));
        var replacement = CreateSettings("CoolAgent", "BackupAgent");
        replacement.Remove("Agents:0");
        replacement.Remove("Agents:1");
        replacement["Agents"] = "[\"CoolAgent\",\"BackupAgent\"]";
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(stream)
            .AddInMemoryCollection(replacement).Build();
        Assert.Equal("invalid/name", configuration["Agents:2"]);

        var options = CopilotStudioOptions.Load(configuration);

        Assert.Equal(new[] { "CoolAgent", "BackupAgent" }, options.Agents.Keys);
        Assert.Equal(new[] { "billing", "support" }, options.UnlistedAgents.OrderBy(name => name));
    }

    /// <summary>An invalid or empty scalar never falls back to a valid lower-priority indexed list.</summary>
    /// <param name="scalar">The overriding scalar value.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not-json")]
    public void GivenInvalidScalarOverride_WhenLoad_RejectsWithoutIndexedFallback(string scalar)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(CreateSettings("CoolAgent"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Agents"] = scalar }).Build();

        Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));
    }

    /// <summary>Requires a nonempty array of quoted strings rather than malformed JSON or coerced values.</summary>
    /// <param name="scalar">The scalar array, or null to omit the allowlist altogether.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"CoolAgent\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("[")]
    [InlineData("[CoolAgent]")]
    [InlineData("[\"CoolAgent\",]")]
    [InlineData("[\"CoolAgent\",123]")]
    [InlineData("[true]")]
    [InlineData("[false]")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[[]]")]
    public void GivenInvalidOrMissingScalarList_WhenLoad_RejectsConfiguration(string? scalar)
    {
        var settings = CreateSettings("CoolAgent");
        settings.Remove("Agents:0");
        if (scalar is not null) settings["Agents"] = scalar;
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains("Agents", error.Message);
        AssertSanitized(error, DirectConnectUrl);
    }

    /// <summary>Rejects empty native arrays, nested entries, numeric names and nonconsecutive indexes.</summary>
    /// <param name="json">The native JSON configuration with an invalid list shape.</param>
    [Theory]
    [InlineData("{\"Agents\":[]}")]
    [InlineData("{\"Agents\":null}")]
    [InlineData("{\"Agents\":[null]}")]
    [InlineData("{\"Agents\":[123]}")]
    [InlineData("{\"Agents\":[[\"CoolAgent\"]]}")]
    [InlineData("{\"Agents\":[{\"name\":\"CoolAgent\"}]}")]
    [InlineData("{\"Agents\":{\"CoolAgent\":{\"SkillDescription\":\"ignored\"}}}")]
    [InlineData("{\"Agents\":{\"1\":\"CoolAgent\"}}")]
    [InlineData("{\"Agents\":{\"0\":\"CoolAgent\",\"2\":\"BackupAgent\"}}")]
    [InlineData("{\"Agents\":{\"00\":\"CoolAgent\"}}")]
    public void GivenInvalidNativeList_WhenLoad_RejectsConfiguration(string json)
    {
        using var configuration = BuildJson(json);

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains("Agents", error.Message);
        Assert.DoesNotContain("required settings are missing", error.Message);
    }

    /// <summary>Orders flattened indexes numerically when the list extends beyond a single digit.</summary>
    [Fact]
    public void GivenDoubleDigitIndexes_WhenLoad_PreservesArrayOrder()
    {
        var names = Enumerable.Range(0, 12).Select(index => $"Agent{index}").ToArray();
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(CreateSettings(names)).Build();

        Assert.Equal(names, CopilotStudioOptions.Load(configuration).Agents.Keys);
    }

    /// <summary>Supports the inclusive slug length boundaries and uppercase ASCII letters.</summary>
    /// <param name="length">The valid slug length.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    public void GivenBoundaryLengthSlug_WhenLoad_AcceptsName(int length)
    {
        var name = "A" + new string('z', length - 1);
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(CreateSettings(name)).Build();

        var options = CopilotStudioOptions.Load(configuration);

        Assert.Equal(name, Assert.Single(options.Agents).Key);
    }

    /// <summary>Accepts digits and hyphens after an initial ASCII letter.</summary>
    [Fact]
    public void GivenAsciiSlug_WhenLoad_AcceptsMixedCaseDigitsAndHyphens()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(CreateSettings("Cool-Agent9-")).Build();

        Assert.Equal("Cool-Agent9-", Assert.Single(CopilotStudioOptions.Load(configuration).Agents).Key);
    }

    /// <summary>Rejects invalid names before exposing their contents or validating settings.</summary>
    /// <param name="name">The invalid agent name.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("9Agent")]
    [InlineData("-Agent")]
    [InlineData("Cool_Agent")]
    [InlineData("Cool.Agent")]
    [InlineData("Cool/Agent")]
    [InlineData("Cool:Agent")]
    [InlineData(" Cool")]
    [InlineData("Cool ")]
    [InlineData("Cool\n")]
    [InlineData("CöolAgent")]
    [InlineData("ＣoolAgent")]
    [InlineData("Cool😀")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("https://test.environment.api.powerplatform.com/synthetic-private-path?secret=synthetic-url-marker")]
    public void GivenInvalidSlug_WhenLoad_RejectsName(string name)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agents:0"] = name
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains("1-63 ASCII", error.Message);
        if (!string.IsNullOrWhiteSpace(name)) AssertSanitized(error, name);
    }

    /// <summary>Rejects duplicate names independently of provider form or case.</summary>
    /// <param name="secondName">A duplicate of the first name.</param>
    /// <param name="scalar">Whether the list is encoded as a scalar.</param>
    [Theory]
    [InlineData("CoolAgent", false)]
    [InlineData("coolagent", false)]
    [InlineData("CoolAgent", true)]
    [InlineData("COOLAGENT", true)]
    public void GivenDuplicateNames_WhenLoad_RejectsCaseInsensitiveDuplicates(string secondName, bool scalar)
    {
        var settings = CreateSettings("CoolAgent", secondName);
        if (scalar) settings["Agents"] = JsonSerializer.Serialize(new[] { "CoolAgent", secondName });
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains("unique names (case-insensitive)", error.Message);
    }

    /// <summary>Reserves core root configuration names, including alternate casing.</summary>
    /// <param name="name">The reserved configuration root name.</param>
    [Theory]
    [InlineData("Agents")]
    [InlineData("Adapter")]
    [InlineData("Authentication")]
    [InlineData("Logging")]
    [InlineData("AllowedHosts")]
    [InlineData("CopilotStudio")]
    [InlineData("aGeNtS")]
    [InlineData("ADAPTER")]
    [InlineData("authentication")]
    [InlineData("lOgGiNg")]
    [InlineData("allowedhosts")]
    [InlineData("COPILOTSTUDIO")]
    public void GivenReservedName_WhenLoad_RejectsBeforeReadingSettings(string name)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agents"] = JsonSerializer.Serialize(new[] { name })
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains("Names cannot be", error.Message);
    }

    /// <summary>Validates each required setting on each listed agent, not just the first entry.</summary>
    /// <param name="name">The first or second listed agent.</param>
    /// <param name="field">The required field to omit or blank.</param>
    /// <param name="value">The missing or blank value.</param>
    [Theory]
    [InlineData("CoolAgent", "DirectConnectUrl", null)]
    [InlineData("CoolAgent", "DirectConnectUrl", "")]
    [InlineData("CoolAgent", "DirectConnectUrl", " \t\r\n")]
    [InlineData("CoolAgent", "SkillDescription", null)]
    [InlineData("CoolAgent", "SkillDescription", "")]
    [InlineData("CoolAgent", "SkillDescription", " \t\r\n")]
    [InlineData("BackupAgent", "DirectConnectUrl", null)]
    [InlineData("BackupAgent", "DirectConnectUrl", "")]
    [InlineData("BackupAgent", "DirectConnectUrl", " \t\r\n")]
    [InlineData("BackupAgent", "SkillDescription", null)]
    [InlineData("BackupAgent", "SkillDescription", "")]
    [InlineData("BackupAgent", "SkillDescription", " \t\r\n")]
    public void GivenMissingRequiredSetting_WhenLoad_RejectsEveryListedAgent(string name, string field, string? value)
    {
        var settings = CreateSettings("CoolAgent", "BackupAgent");
        if (value is null) settings.Remove($"{name}:{field}");
        else settings[$"{name}:{field}"] = value;
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains($"{name}:{field}", error.Message);
        var presentField = field == "DirectConnectUrl" ? "SkillDescription" : "DirectConnectUrl";
        Assert.DoesNotContain($"{name}:{presentField}", error.Message);
        AssertSanitized(error, DirectConnectUrl, "synthetic-url-marker", "Skill for CoolAgent.", "Skill for BackupAgent.");
    }

    /// <summary>Allows the unused connection URL to be absent when the hardcoded backend is selected.</summary>
    [Fact]
    public void GivenHardcodedBackend_WhenLoad_AllowsMissingDirectConnectUrl()
    {
        var settings = CreateSettings("CoolAgent");
        settings.Remove("CoolAgent:DirectConnectUrl");
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var options = CopilotStudioOptions.Load(configuration, requireDirectConnectUrl: false);

        Assert.Equal("", options.Agents["CoolAgent"].DirectConnectUrl);
        Assert.Equal("Skill for CoolAgent.", options.Agents["CoolAgent"].SkillDescription);
    }

    /// <summary>Reports both required keys when an entire listed section is absent or blank.</summary>
    /// <param name="value">Null omits the section; otherwise both required fields receive this blank value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void GivenBothRequiredSettingsMissing_WhenLoad_ReportsBothKeys(string? value)
    {
        var settings = CreateSettings("CoolAgent", "BackupAgent");
        foreach (var field in new[] { "DirectConnectUrl", "SkillDescription" })
        {
            if (value is null) settings.Remove($"BackupAgent:{field}");
            else settings[$"BackupAgent:{field}"] = value;
        }
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains("BackupAgent:DirectConnectUrl", error.Message);
        Assert.Contains("BackupAgent:SkillDescription", error.Message);
        Assert.DoesNotContain("CoolAgent:", error.Message);
        AssertSanitized(error, DirectConnectUrl);
    }

    /// <summary>Reports both missing keys together and does not fall back to the legacy prefix.</summary>
    [Fact]
    public void GivenLegacySettingsOnly_WhenLoad_ReportsBothMissingRootSettings()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agents:0"] = "CoolAgent",
            ["CopilotStudio:Agents:CoolAgent:DirectConnectUrl"] = DirectConnectUrl,
            ["CopilotStudio:Agents:CoolAgent:SkillDescription"] = "Legacy description."
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains("CoolAgent:DirectConnectUrl", error.Message);
        Assert.Contains("CoolAgent:SkillDescription", error.Message);
        AssertSanitized(error, DirectConnectUrl, "Legacy description.", "CopilotStudio:Agents");
    }

    /// <summary>JSON keys containing literal double underscores are not environment-style nested settings.</summary>
    [Fact]
    public void GivenDoubleUnderscoreJsonKeys_WhenLoad_ReportsMissingNestedSettings()
    {
        using var configuration = BuildJson($$"""
                        { "Agents": ["CoolAgent"], "CoolAgent__DirectConnectUrl": "{{DirectConnectUrl}}",
                            "CoolAgent__SkillDescription": "Synthetic description." }
            """);

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Contains("CoolAgent:DirectConnectUrl", error.Message);
        Assert.Contains("CoolAgent:SkillDescription", error.Message);
        AssertSanitized(error, DirectConnectUrl);
    }

    /// <summary>Rejects unsafe connection URLs without exposing URL, path, query or credential details.</summary>
    /// <param name="url">The synthetic invalid connection URL.</param>
    [Theory]
    [InlineData("not-a-url/synthetic-private-path?secret=synthetic-url-marker")]
    [InlineData("/synthetic-private-path?secret=synthetic-url-marker")]
    [InlineData("http://test.environment.api.powerplatform.com/synthetic-private-path?secret=synthetic-url-marker")]
    [InlineData("https://synthetic-user:synthetic-password@test.environment.api.powerplatform.com/synthetic-private-path?secret=synthetic-url-marker")]
    [InlineData("https://test.environment.api.powerplatform.com/synthetic-private-path?secret=synthetic-url-marker#fragment")]
    [InlineData("https://example.invalid/synthetic-private-path?secret=synthetic-url-marker")]
    [InlineData("https://test.environment.api.powerplatform.com.evil.invalid/synthetic-private-path?secret=synthetic-url-marker")]
    [InlineData("https://environment.api.powerplatform.com/synthetic-private-path?secret=synthetic-url-marker")]
    public void GivenInvalidDirectConnectUrl_WhenLoad_ThrowsSanitizedError(string url)
    {
        var settings = CreateSettings("CoolAgent", "BackupAgent");
        settings["BackupAgent:DirectConnectUrl"] = url;
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var error = Assert.Throws<InvalidOperationException>(() => CopilotStudioOptions.Load(configuration));

        Assert.Equal("BackupAgent:DirectConnectUrl must be an HTTPS public-cloud Power Platform URL.", error.Message);
        AssertSanitized(error, url, "synthetic-private-path", "synthetic-url-marker", "synthetic-user", "synthetic-password");
    }

    /// <summary>Records sections with either known child even if partial, blank, invalid or not a valid slug.</summary>
    /// <param name="field">The single field to provide, or both.</param>
    /// <param name="value">The unvalidated field value.</param>
    [Theory]
    [InlineData("DirectConnectUrl", null)]
    [InlineData("DirectConnectUrl", "")]
    [InlineData("DirectConnectUrl", " \t")]
    [InlineData("DirectConnectUrl", "synthetic-invalid-unlisted-url")]
    [InlineData("SkillDescription", null)]
    [InlineData("SkillDescription", "")]
    [InlineData("SkillDescription", " \t")]
    [InlineData("SkillDescription", "synthetic-unlisted-description")]
    [InlineData("both", "")]
    [InlineData("both", " \t")]
    public void GivenUnlistedPartialSettings_WhenLoad_RecordsWithoutValidatingOrRegistering(string field, string? value)
    {
        const string unlisted = "not_a_valid_slug";
        var settings = CreateSettings("CoolAgent");
        if (field is "DirectConnectUrl" or "both") settings[$"{unlisted}:directconnecturl"] = value;
        if (field is "SkillDescription" or "both") settings[$"{unlisted}:SKILLDESCRIPTION"] = value;
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var options = CopilotStudioOptions.Load(configuration);

        Assert.Equal("CoolAgent", Assert.Single(options.Agents).Key);
        Assert.Equal(unlisted, Assert.Single(options.UnlistedAgents));
    }

    /// <summary>Warns once per ignored section through real logging, without any configured values.</summary>
    [Fact]
    public void GivenUnlistedSettings_WhenLoggingWarnings_EmitsOnlySanitizedNames()
    {
        var settings = CreateSettings("CoolAgent");
        settings["IgnoredUrl:DirectConnectUrl"] = DirectConnectUrl;
        settings["IgnoredDescription:SkillDescription"] = "synthetic-unlisted-description";
        settings["IgnoredBoth:DirectConnectUrl"] = "synthetic-invalid-unlisted-url";
        settings["IgnoredBoth:SkillDescription"] = "synthetic-unlisted-description";
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = CopilotStudioOptions.Load(configuration);
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(provider));

        options.LogUnlistedAgentWarnings(loggerFactory.CreateLogger("OptionsTests"));

        Assert.Equal(3, provider.Entries.Count);
        Assert.Equal(options.UnlistedAgents.OrderBy(name => name), provider.Entries
            .Select(entry => Assert.IsType<string>(entry.Properties["AgentName"])).OrderBy(name => name));
        Assert.All(provider.Entries, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Contains("not in Agents", entry.Message);
            Assert.Contains("no endpoint is registered", entry.Message);
            var captured = entry.Message + JsonSerializer.Serialize(entry.Properties);
            foreach (var value in new[] { DirectConnectUrl, "synthetic-url-marker", "synthetic-unlisted-description", "synthetic-invalid-unlisted-url" })
                Assert.DoesNotContain(value, captured);
            Assert.Equal(new[] { "AgentName", "{OriginalFormat}" }, entry.Properties.Keys.OrderBy(key => key, StringComparer.Ordinal));
        });
    }

    /// <summary>Ignores unrelated roots, deeper lookalike fields and already listed sections.</summary>
    [Fact]
    public void GivenUnrelatedSections_WhenLoggingWarnings_EmitsNothing()
    {
        var settings = CreateSettings("CoolAgent");
        settings["Logging:LogLevel:Default"] = "Warning";
        settings["Authentication:ClientSecret"] = "synthetic-auth-marker";
        settings["Adapter:PublicBaseUrl"] = "https://localhost";
        settings["AllowedHosts"] = "localhost";
        settings["Other:Description"] = "Not an agent setting.";
        settings["Other:Nested:DirectConnectUrl"] = DirectConnectUrl;
        settings["Other:DirectConnectUrlSuffix"] = DirectConnectUrl;
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = CopilotStudioOptions.Load(configuration);
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(provider));

        options.LogUnlistedAgentWarnings(loggerFactory.CreateLogger("OptionsTests"));

        Assert.Empty(options.UnlistedAgents);
        Assert.Empty(provider.Entries);
    }
}