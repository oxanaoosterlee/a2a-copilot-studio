---
title: Test the Copilot Studio A2A adapter
description: PowerShell commands for obtaining a delegated token and testing every adapter endpoint and supported message flow.
ms.date: 2026-09-09
ms.topic: how-to
---

## Prerequisites

Use PowerShell 7 or later. The commands use `Invoke-RestMethod` and keep the access
token only in the current PowerShell process.

Before requesting a token, create a separate single-tenant public client app
registration for testing:

1. Add the adapter API's delegated `Agents.Invoke` permission to the client app.
2. Grant consent for that delegated permission.
3. In **Authentication**, enable **Allow public client flows** for device code sign-in.
4. Do not create or use a client secret for the test client.

The signed-in user must have access to the published Copilot Studio specialist agent.
The adapter app registration remains a confidential client because it uses its own
secret for the on-behalf-of exchange.

Set the values for your environment:

```powershell
$BaseUrl = 'http://localhost:5180'
$TenantId = '<directory-tenant-id>'
$AdapterClientId = '<adapter-api-client-id>'
$TestClientId = '<public-test-client-id>'
$Agent = 'CoolAgent'
$Scope = "api://$AdapterClientId/Agents.Invoke"
```

Start the adapter in another PowerShell session after configuring its user secrets:

```powershell
dotnet run --project .\src\CopilotStudioA2A\CopilotStudioA2A.csproj
```

## Get a delegated access token

The following commands use the Microsoft identity platform device authorization
flow. Follow the sign-in message shown in the terminal. The script waits until sign-in
finishes and then stores the API access token in `ADAPTER_ACCESS_TOKEN` for the current
PowerShell process.

```powershell
$DeviceCodeUri = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/devicecode"
$TokenUri = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"

$DeviceAuthorization = Invoke-RestMethod -Method Post -Uri $DeviceCodeUri -Body @{
    client_id = $TestClientId
    scope = $Scope
}

Write-Host $DeviceAuthorization.message
$IntervalSeconds = [int]$DeviceAuthorization.interval
$ExpiresAt = [DateTimeOffset]::UtcNow.AddSeconds([int]$DeviceAuthorization.expires_in)
$TokenResponse = $null

while ($null -eq $TokenResponse -and [DateTimeOffset]::UtcNow -lt $ExpiresAt) {
    Start-Sleep -Seconds $IntervalSeconds

    try {
        $TokenResponse = Invoke-RestMethod -Method Post -Uri $TokenUri -Body @{
            grant_type = 'urn:ietf:params:oauth:grant-type:device_code'
            client_id = $TestClientId
            device_code = $DeviceAuthorization.device_code
        }
    }
    catch {
        $OAuthError = $_.ErrorDetails.Message | ConvertFrom-Json
        if ($OAuthError.error -eq 'authorization_pending') {
            continue
        }
        if ($OAuthError.error -eq 'slow_down') {
            $IntervalSeconds += 5
            continue
        }
        throw
    }
}

if ($null -eq $TokenResponse) {
    throw 'Device sign-in expired before an access token was returned.'
}

$env:ADAPTER_ACCESS_TOKEN = $TokenResponse.access_token
$Headers = @{ Authorization = "Bearer $env:ADAPTER_ACCESS_TOKEN" }
Write-Host "Token received for scope: $($TokenResponse.scope)"
```

> [!CAUTION]
> Do not print, commit, paste, or share the token. Close the PowerShell session when
> testing is complete. The token is for the adapter API, not Microsoft Graph or Power
> Platform.

If sign-in returns a consent or public-client error, check the test client registration,
its delegated permission to the adapter API, and tenant consent.

## Test health

The health endpoint is anonymous. It confirms that the adapter process is running. It
does not contact Microsoft Entra ID or Copilot Studio.

```powershell
Invoke-RestMethod -Method Get -Uri "$BaseUrl/health"
```

Expected result: `status` is `healthy`.

## Test the authenticated agent catalog

```powershell
$Catalog = Invoke-RestMethod -Method Get -Uri "$BaseUrl/agents" -Headers $Headers
$Catalog | Format-Table name, endpoint, agentCard
```

Expected result: each configured agent appears without its secret connection URL.

## Test a per-agent discovery card

The discovery card is anonymous.

```powershell
$Card = Invoke-RestMethod -Method Get -Uri "$BaseUrl/a2a/$Agent/.well-known/agent-card.json"
$Card | ConvertTo-Json -Depth 10
```

Expected result: the card advertises JSON-RPC version `1.0`, text input and output,
and the configured skill description.

## Test the standard root discovery card

This route exists only when exactly one agent is configured.

```powershell
$RootCard = Invoke-RestMethod -Method Get -Uri "$BaseUrl/.well-known/agent-card.json"
$RootCard | ConvertTo-Json -Depth 10
```

Expected result: the root card matches the only configured agent. With multiple agents,
an HTTP 404 response is expected.

## Start a conversation

This request tests the complete path: API authentication, A2A validation, delegated
on-behalf-of token exchange, Copilot Studio invocation, and A2A response translation.
It can perform actions allowed by the published agent.

```powershell
$MessageHeaders = @{
    Authorization = "Bearer $env:ADAPTER_ACCESS_TOKEN"
    'A2A-Version' = '1.0'
}

$FirstRequest = @{
    jsonrpc = '2.0'
    id = [guid]::NewGuid().ToString()
    method = 'SendMessage'
    params = @{
        message = @{
            messageId = [guid]::NewGuid().ToString()
            role = 'ROLE_USER'
            parts = @(
                @{
                    text = 'Reply with a short greeting.'
                    mediaType = 'text/plain'
                }
            )
        }
        configuration = @{
            acceptedOutputModes = @('text/plain')
            returnImmediately = $false
        }
    }
}

$FirstTurn = Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl/a2a/$Agent" `
    -Headers $MessageHeaders `
    -ContentType 'application/json' `
    -Body ($FirstRequest | ConvertTo-Json -Depth 10)

$FirstTurn | ConvertTo-Json -Depth 10
$ContextId = $FirstTurn.result.message.contextId
```

Expected result: `result.message.role` is `ROLE_AGENT`, the response contains a text
part, and `contextId` is not empty.

## Continue a conversation

Run this command in the same PowerShell session after starting a conversation.

```powershell
$NextRequest = @{
    jsonrpc = '2.0'
    id = [guid]::NewGuid().ToString()
    method = 'SendMessage'
    params = @{
        message = @{
            messageId = [guid]::NewGuid().ToString()
            contextId = $ContextId
            role = 'ROLE_USER'
            parts = @(
                @{ text = 'What was my previous message?' }
            )
        }
    }
}

$NextTurn = Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl/a2a/$Agent" `
    -Headers $MessageHeaders `
    -ContentType 'application/json' `
    -Body ($NextRequest | ConvertTo-Json -Depth 10)

$NextTurn | ConvertTo-Json -Depth 10
```

Expected result: the response uses the same public `contextId`, and the agent can use
its conversation history. Context state is lost when the adapter restarts.

## Test multiple text parts

The adapter joins separate text parts with a newline before sending one message to
Copilot Studio.

```powershell
$PartsRequest = @{
    jsonrpc = '2.0'
    id = [guid]::NewGuid().ToString()
    method = 'SendMessage'
    params = @{
        message = @{
            messageId = [guid]::NewGuid().ToString()
            role = 'ROLE_USER'
            parts = @(
                @{ text = 'First line.' }
                @{ text = 'Second line.' }
            )
        }
    }
}

Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl/a2a/$Agent" `
    -Headers $MessageHeaders `
    -ContentType 'application/json' `
    -Body ($PartsRequest | ConvertTo-Json -Depth 10) |
    ConvertTo-Json -Depth 10
```

## Test authentication errors

A request without a bearer token must return HTTP 401.

```powershell
$Response = Invoke-WebRequest `
    -Method Get `
    -Uri "$BaseUrl/agents" `
    -SkipHttpErrorCheck

$Response.StatusCode
```

Expected result: `401`.

A valid token without the exact delegated `Agents.Invoke` scope must return HTTP 403.
Use a token issued to the adapter API without that scope, if your test tenant provides
one. Do not use an app-only token as a successful test token.

## Test routing and protocol errors

An unknown agent must return HTTP 404 and a JSON-RPC error.

```powershell
$UnknownAgentResponse = Invoke-WebRequest `
    -Method Post `
    -Uri "$BaseUrl/a2a/not-configured" `
    -Headers $MessageHeaders `
    -ContentType 'application/json' `
    -Body ($FirstRequest | ConvertTo-Json -Depth 10) `
    -SkipHttpErrorCheck

$UnknownAgentResponse.StatusCode
$UnknownAgentResponse.Content | ConvertFrom-Json | ConvertTo-Json -Depth 10
```

Expected result: HTTP `404`.

A request without `A2A-Version` must return JSON-RPC error `-32009`.

```powershell
$NoVersionHeaders = @{ Authorization = "Bearer $env:ADAPTER_ACCESS_TOKEN" }
$NoVersion = Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl/a2a/$Agent" `
    -Headers $NoVersionHeaders `
    -ContentType 'application/json' `
    -Body ($FirstRequest | ConvertTo-Json -Depth 10)

$NoVersion.error
```

A streaming request must return unsupported-operation error `-32004`.

```powershell
$StreamingRequest = $FirstRequest.Clone()
$StreamingRequest.method = 'SendStreamingMessage'

$Streaming = Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl/a2a/$Agent" `
    -Headers $MessageHeaders `
    -ContentType 'application/json' `
    -Body ($StreamingRequest | ConvertTo-Json -Depth 10)

$Streaming.error
```

An unsupported content type must return HTTP 415.

```powershell
$MediaResponse = Invoke-WebRequest `
    -Method Post `
    -Uri "$BaseUrl/a2a/$Agent" `
    -Headers $MessageHeaders `
    -ContentType 'text/plain' `
    -Body 'not JSON' `
    -SkipHttpErrorCheck

$MediaResponse.StatusCode
```

Expected result: `415`.

An unknown conversation context must return JSON-RPC error `-32602`.

```powershell
$UnknownContextRequest = @{
    jsonrpc = '2.0'
    id = [guid]::NewGuid().ToString()
    method = 'SendMessage'
    params = @{
        message = @{
            messageId = [guid]::NewGuid().ToString()
            contextId = 'unknown-context'
            role = 'ROLE_USER'
            parts = @(@{ text = 'Continue this conversation.' })
        }
    }
}

$UnknownContext = Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl/a2a/$Agent" `
    -Headers $MessageHeaders `
    -ContentType 'application/json' `
    -Body ($UnknownContextRequest | ConvertTo-Json -Depth 10)

$UnknownContext.error
```

## Clear the token

Remove the token from the current PowerShell process when testing is complete:

```powershell
Remove-Item Env:ADAPTER_ACCESS_TOKEN -ErrorAction SilentlyContinue
Remove-Variable TokenResponse, Headers, MessageHeaders -ErrorAction SilentlyContinue
```

## References

* [Microsoft identity platform device authorization flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-device-code)
* [Configure client access to a web API](https://learn.microsoft.com/entra/identity-platform/quickstart-configure-app-access-web-apis)
