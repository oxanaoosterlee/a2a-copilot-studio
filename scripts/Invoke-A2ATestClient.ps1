#!/usr/bin/env pwsh
# Copyright (c) Microsoft Corporation.
# SPDX-License-Identifier: MIT
#Requires -Version 7.0

<#
.SYNOPSIS
    Calls the local Copilot Studio A2A adapter as a signed-in user.
.DESCRIPTION
    Uses the Microsoft identity platform device authorization flow to acquire a
    delegated access token for the adapter API, then sends one A2A SendMessage
    request. The token remains in this process and is never written to output.
.PARAMETER TenantId
    Directory (tenant) ID containing both app registrations and the user.
.PARAMETER AdapterClientId
    Application (client) ID of the confidential adapter API registration.
.PARAMETER TestClientId
    Application (client) ID of the public PowerShell test registration.
.PARAMETER Agent
    Configured adapter agent route name.
.PARAMETER Message
    Text to send to the Copilot Studio specialist agent.
.PARAMETER BaseUrl
    Local adapter origin.
.EXAMPLE
    ./scripts/Invoke-A2ATestClient.ps1 -TenantId '<tenant-id>' -AdapterClientId '<adapter-client-id>' -TestClientId '<test-client-id>' -Agent 'CoolAgent' -Message 'Reply with a short greeting.'
.NOTES
    The test registration must allow public client flows and have delegated
    permission to api://<adapter-client-id>/Agents.Invoke.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({
        $ParsedGuid = [guid]::Empty
        [guid]::TryParse($_, [ref]$ParsedGuid)
    })]
    [string]$TenantId,

    [Parameter(Mandatory = $true)]
    [ValidateScript({
        $ParsedGuid = [guid]::Empty
        [guid]::TryParse($_, [ref]$ParsedGuid)
    })]
    [string]$AdapterClientId,

    [Parameter(Mandatory = $true)]
    [ValidateScript({
        $ParsedGuid = [guid]::Empty
        [guid]::TryParse($_, [ref]$ParsedGuid)
    })]
    [string]$TestClientId,

    [Parameter(Mandatory = $false)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9-]{0,62}$')]
    [string]$Agent = 'CoolAgent',

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$Message = 'Reply with a short greeting.',

    [Parameter(Mandatory = $false)]
    [ValidateScript({
        $Uri = $_ -as [uri]
        $null -ne $Uri -and $Uri.IsAbsoluteUri -and
            (($Uri.Scheme -eq 'http' -and $Uri.IsLoopback) -or $Uri.Scheme -eq 'https')
    })]
    [string]$BaseUrl = 'http://localhost:5180'
)

$ErrorActionPreference = 'Stop'

#region Functions

function Get-DeviceCodeAccessToken {
    <#
    .SYNOPSIS
        Acquires a delegated adapter token through device authorization.
    .PARAMETER DirectoryId
        Microsoft Entra directory tenant ID.
    .PARAMETER ClientId
        Application ID of the public test client.
    .PARAMETER Scope
        Delegated adapter API scope to request.
    .OUTPUTS
        [string] Access token.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$DirectoryId,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$ClientId,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Scope
    )

    $Authority = "https://login.microsoftonline.com/$DirectoryId/oauth2/v2.0"
    $DeviceAuthorization = Invoke-RestMethod -Method Post -Uri "$Authority/devicecode" -Body @{
        client_id = $ClientId
        scope = $Scope
    }

    Write-Host $DeviceAuthorization.message -ForegroundColor Cyan
    $IntervalSeconds = [int]$DeviceAuthorization.interval
    $ExpiresAt = [DateTimeOffset]::UtcNow.AddSeconds([int]$DeviceAuthorization.expires_in)

    while ([DateTimeOffset]::UtcNow -lt $ExpiresAt) {
        Start-Sleep -Seconds $IntervalSeconds

        try {
            $TokenResponse = Invoke-RestMethod -Method Post -Uri "$Authority/token" -Body @{
                grant_type = 'urn:ietf:params:oauth:grant-type:device_code'
                client_id = $ClientId
                device_code = $DeviceAuthorization.device_code
            }
            return $TokenResponse.access_token
        }
        catch {
            $OAuthError = $null
            if (-not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
                $OAuthError = $_.ErrorDetails.Message | ConvertFrom-Json
            }

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

    throw 'Device sign-in expired before an access token was returned.'
}

function Invoke-A2AMessage {
    <#
    .SYNOPSIS
        Sends one authenticated A2A message.
    .PARAMETER Endpoint
        Adapter A2A JSON-RPC endpoint.
    .PARAMETER AccessToken
        Delegated access token issued for the adapter API.
    .PARAMETER Text
        User message sent to the configured agent.
    .OUTPUTS
        [pscustomobject] Parsed A2A response.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Endpoint,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$AccessToken,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Text
    )

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
                        text = $Text
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

    return Invoke-RestMethod `
        -Method Post `
        -Uri $Endpoint `
        -Headers @{
            Authorization = "Bearer $AccessToken"
            'A2A-Version' = '1.0'
        } `
        -ContentType 'application/json' `
        -Body ($Request | ConvertTo-Json -Depth 10)
}

#endregion Functions

#region Main Execution

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $Scope = "api://$AdapterClientId/Agents.Invoke"
        $AccessToken = Get-DeviceCodeAccessToken `
            -DirectoryId $TenantId `
            -ClientId $TestClientId `
            -Scope $Scope

        $EscapedAgent = [uri]::EscapeDataString($Agent)
        $Endpoint = "$($BaseUrl.TrimEnd('/'))/copilot-studio/$EscapedAgent/a2a"
        $Response = Invoke-A2AMessage -Endpoint $Endpoint -AccessToken $AccessToken -Text $Message
        $Response | ConvertTo-Json -Depth 10
        exit 0
    }
    catch {
        Write-Error -ErrorAction Continue "A2A test client failed: $($_.Exception.Message)"
        exit 1
    }
    finally {
        Remove-Variable AccessToken -ErrorAction SilentlyContinue
    }
}

#endregion Main Execution
