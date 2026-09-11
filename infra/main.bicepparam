using './main.bicep'

// Example placeholders only. Replace the public values before deployment.
param appName = 'replace-with-globally-unique-adapter-name'
param location = 'westeurope'
param clientId = '00000000-0000-0000-0000-000000000000'
param sku = 'B1'

// tenantId is intentionally omitted: it defaults to tenant().tenantId.
// The existing Entra application must expose Agents.Invoke and have the necessary
// delegated Copilot Studio permissions and consent for on-behalf-of authentication.

// Deploy a ZIP with published .NET output at its root using Microsoft Entra authentication.
// Basic publishing authentication is disabled; the package is mounted read-only.

// Explicit allowlist, never inferred from either settings dictionary.
// Names preserve case and must match ^[A-Za-z][A-Za-z0-9-]{0,62}$: 1-63 ASCII
// letters, digits or hyphens, beginning with a letter; underscores are not allowed.
// Runtime rejects duplicates case-insensitively and reserved root names:
// Agents, Adapter, Authentication, Logging, AllowedHosts and CopilotStudio.
param agentNames = [
  'CoolAgent'
]

// App Service receives one Agents setting: ["CoolAgent"].
// Runtime also supports a root Agents JSON array in appsettings and, alternatively,
// indexed environment settings such as Agents__0; this template emits neither.
// Each listed name becomes the runtime route /copilot-studio/{name}/a2a.

// Supply secrets through the deployment process environment, never source control.
// No fallback: missing variables must fail rather than deploy dummy credentials.
param clientSecret = readEnvironmentVariable('A2A_CLIENT_SECRET')

// Secure environment JSON shape (placeholders only; never save actual URLs here):
// {"CoolAgent":"https://replace-with-direct-connect-url.invalid"}
// Each value becomes its own vault secret, referenced by <name>__DirectConnectUrl.
param agentDirectConnectUrls = json(readEnvironmentVariable('A2A_AGENT_DIRECT_CONNECT_URLS'))

// Descriptions are emitted independently as <name>__SkillDescription.
// Runtime startup fails when a listed agent lacks either required setting or has an
// invalid value. Partial or unlisted dictionary entries are not filtered by this template;
// unlisted agent settings produce runtime warnings and those agents are not registered.
// Listed agents require nonblank descriptions, published anonymously in agent cards,
// with no secrets.
param agentSkillDescriptions = {
  CoolAgent: 'Answers questions and helps users complete common tasks.'
}

// readEnvironmentVariable is evaluated locally, not by the running App Service.
// Use a current Bicep release. The editor can report missing environment variables.
// Generated parameter JSON contains resolved secrets: never save, log or commit it,
// and do not enable deployment debug logging when supplying secret values.
