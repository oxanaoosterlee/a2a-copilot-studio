metadata name = 'A2A Copilot Studio adapter infrastructure'
metadata description = 'Single-instance .NET 10 Linux App Service with Key Vault references and workspace-based Application Insights.'

targetScope = 'resourceGroup'

/* Common parameters */

@description('Globally unique App Service name, using letters, digits and hyphens, with an alphanumeric first and last character.')
@minLength(2)
@maxLength(60)
param appName string

@description('Azure public-cloud region supporting Linux App Service .NET 10 and the monitoring resources.')
param location string = resourceGroup().location

/* Authentication and agent parameters */

@description('Existing Entra application client ID, also used as the bare incoming-token audience; not an api:// URI.')
@minLength(36)
@maxLength(36)
param clientId string

@description('Client secret for the existing Entra application used for on-behalf-of authentication to Copilot Studio.')
@secure()
@minLength(1)
param clientSecret string

@description('Authentication tenant ID. No Entra application registrations or consent grants are created.')
param tenantId string = tenant().tenantId

@description('Explicit nonsecret allowlist of agent names, independent of the settings dictionaries. Names preserve case and must match ^[A-Za-z][A-Za-z0-9-]{0,62}$: 1-63 ASCII letters, digits or hyphens, beginning with a letter.')
@minLength(1)
param agentNames array

// Runtime rejects invalid names, case-insensitive duplicates and reserved root names:
// Agents, Adapter, Authentication, Logging, AllowedHosts and CopilotStudio.
// Listed agents require both settings at startup. Unlisted agent settings produce
// runtime warnings and do not register agents; neither dictionary defines the allowlist.
@description('Map of nonsecret agent names to secret HTTPS DirectConnectUrl strings for the public-cloud SDK. Entries are emitted independently of agentNames and agentSkillDescriptions so runtime can diagnose missing or unlisted settings.')
@secure()
param agentDirectConnectUrls object

@description('Map of nonsecret agent names to SkillDescription strings, emitted independently of agentNames and agentDirectConnectUrls. Runtime requires a nonblank description for each listed agent. Descriptions are published anonymously in agent cards and must contain no secrets.')
param agentSkillDescriptions object

/* Compute parameters */

@description('Linux App Service plan SKU. Capacity is fixed at one because conversation state is held in memory.')
@allowed([
  'B1'
  'S1'
])
param sku string = 'B1'

/* Variables */

var resourceSuffix = uniqueString(resourceGroup().id, appName)
var agents = items(agentDirectConnectUrls)
var secretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

/* Resources */

// AVM options were reviewed. Direct definitions keep this two-file template
// self-contained and make identity/RBAC/settings ordering and SDK-only telemetry explicit.
resource workspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: 'law-${resourceSuffix}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${resourceSuffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
  }
}

resource plan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: 'asp-${resourceSuffix}'
  location: location
  kind: 'linux'
  sku: {
    name: sku
    tier: sku == 'B1' ? 'Basic' : 'Standard'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2025-03-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet CopilotStudioA2A.dll'
      alwaysOn: true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      // The deployed application must expose this endpoint without bearer authentication.
      healthCheckPath: '/health'
    }
  }
}

// ZIP deployments use Microsoft Entra authentication, not publishing credentials.
resource scmPublishingPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: app
  name: 'scm'
  properties: {
    allow: false
  }
}

resource ftpPublishingPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: app
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource vault 'Microsoft.KeyVault/vaults@2026-02-01' = {
  name: 'kv-${resourceSuffix}'
  location: location
  properties: {
    // The vault belongs to the deployment tenant, like the app's managed identity.
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    // No VNet/private endpoints in this minimal deployment; secret access requires RBAC.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Allow'
    }
  }
}

resource authenticationSecret 'Microsoft.KeyVault/vaults/secrets@2026-02-01' = {
  parent: vault
  name: 'authentication-client-secret'
  properties: {
    value: clientSecret
  }
}

resource agentSecrets 'Microsoft.KeyVault/vaults/secrets@2026-02-01' = [for agent in agents: {
  parent: vault
  // Hash only the public agent name, never the URL.
  name: 'agent-${uniqueString(agent.key)}-direct-connect-url'
  properties: {
    value: agent.value
  }
}]

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, app.id, secretsUserRoleId)
  properties: {
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

// Separate settings avoid a dependency cycle with the system-assigned identity.
// Versionless references follow secret rotations (App Service may cache them for 24 hours).
// Emit both dictionaries independently, including partial and unlisted agent settings,
// so runtime validation can report missing settings and warn about unlisted agents.
resource appSettings 'Microsoft.Web/sites/config@2025-03-01' = {
  parent: app
  name: 'appsettings'
  properties: union({
    ASPNETCORE_ENVIRONMENT: 'Production'
    AllowedHosts: app.properties.defaultHostName
    WEBSITE_RUN_FROM_PACKAGE: '1'
    Authentication__TenantId: tenantId
    Authentication__ClientId: clientId
    Authentication__Audience: clientId
    Authentication__RequiredScope: 'Agents.Invoke'
    Authentication__ClientSecret: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/${authenticationSecret.name})'
    Adapter__PublicBaseUrl: 'https://${app.properties.defaultHostName}'
    Adapter__MaxConversations: '1000'
    Adapter__RequestTimeoutSeconds: '120'
    Adapter__MaxRequestBytes: '65536'
    // One root JSON-array scalar, not a comma-separated list or indexed settings.
    Agents: string(agentNames)
    // The application must enable Azure Monitor OpenTelemetry in code and redact secrets.
    // Deliberately no ApplicationInsightsAgent_EXTENSION_VERSION or site extension.
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
  }, toObject(
    agents,
    agent => '${agent.key}__DirectConnectUrl',
    agent => '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/agent-${uniqueString(agent.key)}-direct-connect-url)'
  ), toObject(
    items(agentSkillDescriptions),
    agent => '${agent.key}__SkillDescription',
    agent => agent.value
  ))
  // The client secret dependency is inferred; computed agent URIs need an explicit dependency.
  dependsOn: [
    agentSecrets
    secretsUser
  ]
}

/* Outputs */

@description('Application base URL. The deployed runtime serves each configured agent at /a2a/{name}; no secrets are output.')
output appUrl string = 'https://${app.properties.defaultHostName}'