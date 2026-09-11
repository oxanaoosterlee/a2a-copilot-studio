---
title: Test the Copilot Studio A2A adapter
description: Practical steps for using the HTTP requests, console clients, and automated tests included in this repository.
ms.date: 2026-09-11
ms.topic: how-to
---

## Choose a test option

Use one of these three options.

| Option | Test path                                                                    | Best use                                            |
|--------|------------------------------------------------------------------------------|-----------------------------------------------------|
| 1      | HTTP client -> A2A adapter -> Copilot Studio specialist agent                | Test the full flow and inspect each request         |
| 2      | .NET console client -> A2A adapter -> Copilot Studio specialist agent        | Run a quick full-flow test with one command         |
| 3      | .NET console application -> Copilot Studio specialist agent                  | Test Copilot Studio without the A2A adapter         |

Options 1 and 2 can use the hardcoded adapter backend. This keeps the A2A endpoint,
authentication, and request translation, but it does not call Copilot Studio. Option 3
bypasses the adapter, so the adapter's hardcoded backend does not apply to it.

Complete the app registration steps in [Microsoft Entra setup](ENTRA-SETUP.md) before
you use a real signed-in user. Keep access tokens, client secrets, and direct connection
URLs out of source control.

## Test code layout

All test code is outside the main application project:

* [tools/A2ATestClient](../tools/A2ATestClient) contains the .NET console client for
    the A2A endpoint.
* [tools/CopilotStudioDirectTest](../tools/CopilotStudioDirectTest) contains the small
    .NET client that calls Copilot Studio directly.
* [scripts/Invoke-A2ATestClient.ps1](../scripts/Invoke-A2ATestClient.ps1) is an optional
    PowerShell client for the A2A endpoint.
* [src/CopilotStudioA2A.Tests](../src/CopilotStudioA2A.Tests) contains the automated
    unit and integration tests.

The production application does not reference these projects or scripts. The two
console projects are grouped under `tools` in the solution.

## Common preparation

Install the .NET 10 SDK. Use PowerShell 7 or later for the PowerShell examples.

The examples use these values:

```powershell
$BaseUrl = 'http://localhost:5180'
$TenantId = '<directory-tenant-id>'
$AdapterClientId = '<adapter-api-client-id>'
$TestClientId = '<public-test-client-id>'
$Agent = 'CoolAgent'
$RedirectUri = 'http://localhost'
```

For real Copilot Studio tests, configure the adapter with:

* `Authentication:TenantId`
* `Authentication:ClientId`
* `Authentication:Audience`, using the same bare client ID
* `Authentication:RequiredScope`, using `Agents.Invoke`
* `Authentication:ClientSecret`
* `Agents`
* `<agent-name>:SkillDescription`
* `<agent-name>:DirectConnectUrl`

Use .NET user secrets for the client secret and direct connection URL. Do not put
these values in tracked configuration files.

## Option 1: Test the complete flow with HTTP requests

This option starts the adapter and lets you send each HTTP request yourself. It tests
the full path from the A2A endpoint to the Copilot Studio specialist agent.

### Start the adapter

Start it in one PowerShell window:

```powershell
dotnet run --project .\src\CopilotStudioA2A\CopilotStudioA2A.csproj
```

### Get an adapter access token

Open a second PowerShell window. Dot-source the included test client so that its token
function is available, then sign in:

```powershell
. .\scripts\Invoke-A2ATestClient.ps1

$env:ADAPTER_ACCESS_TOKEN = Get-DeviceCodeAccessToken `
    -DirectoryId $TenantId `
    -ClientId $TestClientId `
    -Scope "api://$AdapterClientId/Agents.Invoke"
```

Follow the device sign-in message. The token stays in the current PowerShell process.

> [!CAUTION]
> Do not print, commit, paste, or share the token.

### Send requests

Open [the REST Client request file](../requests/adapter.http). Set `baseUrl` and
`agent` at the top. Run these requests in order:

1. `Health` checks that the adapter process is running.
2. `Configured agents` returns the authenticated agent catalog.
3. `Discover this agent` returns the public agent card.
4. `End-to-end connection test` sends the first message.
5. `Continue the context` sends a second message in the same conversation.

A successful message response contains:

* `result.message.role` set to `ROLE_AGENT`
* At least one text value in `result.message.parts`
* A nonempty `result.message.contextId`

The health request does not test Microsoft Entra, OBO, or Copilot Studio. Only a
successful message request tests the complete flow.

### Send a request without the REST Client extension

You can also send a first message from PowerShell:

```powershell
$Headers = @{
    Authorization = "Bearer $env:ADAPTER_ACCESS_TOKEN"
    'A2A-Version' = '1.0'
}

$Request = @{
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
    }
}

$Response = Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl/copilot-studio/$Agent/a2a" `
    -Headers $Headers `
    -ContentType 'application/json' `
    -Body ($Request | ConvertTo-Json -Depth 10)

$Response | ConvertTo-Json -Depth 10
```

## Option 2: Test the A2A endpoint with the .NET console client

The separate .NET console client opens the system browser, signs in a user through
authorization code flow with PKCE, gets an adapter access token, sends one A2A
message, and prints the JSON response. It does not reference the main application
project.

Start the adapter as shown in option 1. In another PowerShell window, set:

```powershell
$env:A2A_TENANT_ID = '<directory-tenant-id>'
$env:A2A_ADAPTER_CLIENT_ID = '<adapter-api-client-id>'
$env:A2A_TEST_CLIENT_ID = '<public-test-client-id>'
$env:A2A_AGENT = 'CoolAgent'
$env:A2A_BASE_URL = 'http://localhost:5180'
$env:A2A_REDIRECT_URI = 'http://localhost'
```

Run the client:

```powershell
dotnet run --project .\tools\A2ATestClient -- 'Reply with a short greeting.'
```

Complete sign-in in the browser. Microsoft Entra redirects to the local MSAL listener,
which finishes the console login. A successful result contains an A2A agent message,
a text part, and a context ID. The client does not print or save the access token.

This option tests the same live path as option 1. It is faster, but it does not provide
separate commands for health, discovery, or conversation continuation.

The PowerShell client remains available as a device-code alternative. Its test client
registration must have public client flows enabled:

```powershell
.\scripts\Invoke-A2ATestClient.ps1 `
    -TenantId '<directory-tenant-id>' `
    -AdapterClientId '<adapter-api-client-id>' `
    -TestClientId '<public-test-client-id>' `
    -Agent 'CoolAgent' `
    -Message 'Reply with a short greeting.'
```

## Option 3: Test Copilot Studio directly

Use the .NET console application included in this repository when you want to test the
Copilot Studio specialist agent without the A2A adapter. It opens the system browser
for authorization code flow with PKCE, does not save its token, and bypasses:

* The A2A endpoint and protocol validation
* Adapter token validation
* The OBO exchange
* A2A text translation
* The adapter conversation store

Set the values in the current PowerShell window:

```powershell
$env:COPILOT_STUDIO_TENANT_ID = '<directory-tenant-id>'
$env:COPILOT_STUDIO_CLIENT_ID = '<public-test-client-id>'
$env:COPILOT_STUDIO_DIRECT_CONNECT_URL = '<copilot-studio-direct-connect-url>'
$env:COPILOT_STUDIO_REDIRECT_URI = 'http://localhost'
```

Run the application:

```powershell
dotnet run --project .\tools\CopilotStudioDirectTest -- 'Reply with a short greeting.'
```

Complete sign-in in the browser. A successful test prints the text returned by the
Copilot Studio specialist agent.

Clear the secret connection URL when the test is complete:

```powershell
Remove-Item Env:COPILOT_STUDIO_DIRECT_CONNECT_URL -ErrorAction SilentlyContinue
```

Do not use this option to prove that A2A or OBO works. Use option 1 or 2 for that.

## Use the hardcoded adapter backend

The hardcoded backend returns `This is a hardcoded response.` It does not need an
adapter client secret or an agent direct connection URL. The adapter still validates
the caller's bearer token, so the adapter API and public test client registrations are
still required.

Set the hardcoded option in the PowerShell window that starts the adapter:

```powershell
$env:Adapter__UseHardcodedBackend = 'true'
dotnet run --project .\src\CopilotStudioA2A\CopilotStudioA2A.csproj
```

Then use option 1 or option 2 without changing the client command. A successful A2A
response contains the fixed text.

Option 3 cannot use this setting because it does not run or call the adapter. For an
offline console-to-A2A check, use option 2 with the hardcoded adapter backend.

Clear the setting before a real Copilot Studio test:

```powershell
Remove-Item Env:Adapter__UseHardcodedBackend -ErrorAction SilentlyContinue
```

## Run the automated tests

The automated tests use local test servers and fake backends. They do not contact
Microsoft Entra or Copilot Studio.

```powershell
dotnet test .\CopilotStudioA2A.sln
```

The tests cover the A2A profile, text translation, authentication rules, conversation
handling, configuration, the HTTP boundary, and the hardcoded backend. They do not
replace a live option 1 or option 2 test.

## Expected errors

| Result            | Meaning                                                     | Check                                           |
|-------------------|-------------------------------------------------------------|-------------------------------------------------|
| HTTP `401`        | The token is missing or invalid                             | Token audience, issuer, signature, and expiry   |
| HTTP `403`        | The caller does not have the required delegated scope       | The `Agents.Invoke` scope and consent            |
| HTTP `404`        | The agent route is not configured                           | The `Agents` list and route name                 |
| JSON-RPC `-32009` | The A2A version is missing or invalid                       | The `A2A-Version: 1.0` header                    |
| JSON-RPC `-32004` | The requested A2A operation is not supported                | Use synchronous `SendMessage`                   |
| JSON-RPC `-32602` | The message or context is invalid                           | Required message fields and current context ID  |
| HTTP `415`        | The request content type is not supported                   | Use `application/json`                          |

For OBO errors, check the adapter secret, Power Platform delegated permission, and
consent. For direct test errors, check the direct connection URL, publication state,
and the signed-in user's access to the Copilot Studio specialist agent.

## Clean up local values

Close the PowerShell windows after testing, or remove the values:

```powershell
Remove-Item Env:ADAPTER_ACCESS_TOKEN -ErrorAction SilentlyContinue
Remove-Item Env:COPILOT_STUDIO_DIRECT_CONNECT_URL -ErrorAction SilentlyContinue
Remove-Item Env:Adapter__UseHardcodedBackend -ErrorAction SilentlyContinue
Remove-Item Env:A2A_TENANT_ID -ErrorAction SilentlyContinue
Remove-Item Env:A2A_ADAPTER_CLIENT_ID -ErrorAction SilentlyContinue
Remove-Item Env:A2A_TEST_CLIENT_ID -ErrorAction SilentlyContinue
Remove-Item Env:A2A_AGENT -ErrorAction SilentlyContinue
Remove-Item Env:A2A_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:A2A_REDIRECT_URI -ErrorAction SilentlyContinue
Remove-Item Env:COPILOT_STUDIO_REDIRECT_URI -ErrorAction SilentlyContinue
```

## Verification status

The source and editor diagnostics were reviewed. Restore, compilation, automated test
execution, infrastructure deployment, and live Copilot Studio tests were not run as
part of the implementation. Run the checks that match your environment before you
use the adapter.
