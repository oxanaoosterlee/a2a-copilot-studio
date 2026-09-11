---
title: Microsoft Entra setup for testing
description: App registration and permission setup for each Copilot Studio A2A adapter test path.
ms.date: 2026-09-11
ms.topic: how-to
---

## Choose the setup for your test

The test paths do not all need the same Microsoft Entra setup.

| Test path                   | Adapter API | Public test client | Sign-in method        | Redirect URI       | Power Platform permission |
|-----------------------------|-------------|--------------------|-----------------------|--------------------|---------------------------|
| HTTP client through adapter | Required    | Required           | Device code           | Not used           | On adapter registration   |
| A2A .NET console client     | Required    | Required           | System browser + PKCE | `http://localhost` | On adapter registration   |
| .NET direct client          | Not used    | Required           | System browser + PKCE | `http://localhost` | On test client            |
| Hardcoded adapter           | Required    | Required           | System browser + PKCE | `http://localhost` | Not required              |

Use single-tenant registrations unless your solution has a reviewed multi-tenant
design. The signed-in user must be in the configured tenant, or be a guest in that
tenant.

## Create the adapter API registration

Use this registration for test options 1 and 2. It represents the protected A2A API
and the confidential client that performs OBO.

1. Open **Microsoft Entra admin center** > **App registrations**.
2. Select **New registration**.
3. Enter a clear name, such as `Copilot Studio A2A adapter`.
4. Select **Accounts in this organizational directory only**.
5. Create the registration.
6. Copy the **Application (client) ID** and **Directory (tenant) ID**.

Use these values in the adapter:

| Adapter setting                       | Value                           |
|---------------------------------------|---------------------------------|
| `Authentication:TenantId`             | Directory tenant ID             |
| `Authentication:ClientId`             | Adapter application client ID   |
| `Authentication:Audience`             | The same bare adapter client ID |
| `Authentication:RequiredScope`        | `Agents.Invoke`                 |

### Expose the adapter API

1. Open **Expose an API**.
2. Set the Application ID URI to `api://<adapter-client-id>`.
3. Add a delegated scope named `Agents.Invoke`.
4. Enable the scope.
5. Select **Admins only** for consent unless your tenant policy allows user consent.
6. Save the scope.

The full scope is:

```text
api://<adapter-client-id>/Agents.Invoke
```

### Request version 2 access tokens

1. Open **Manifest**.
2. Set `api.requestedAccessTokenVersion` to `2`.
3. Save the manifest.

The adapter accepts Microsoft identity platform version 2 user access tokens. It does
not accept ID tokens, app-only tokens, Microsoft Graph tokens, or Power Platform
tokens at the A2A endpoint.

### Add the Power Platform delegated permission

This step is required when the adapter calls a real Copilot Studio specialist agent.
It is not required for the hardcoded backend.

1. Open **API permissions**.
2. Select **Add a permission**.
3. Select **APIs my organization uses**.
4. Find **Power Platform API**.
5. Select **Delegated permissions**.
6. Add `CopilotStudio.Copilots.Invoke`.
7. Grant tenant admin consent.

The adapter uses this permission during OBO. The calling test client requests the
adapter's `Agents.Invoke` scope, not the Power Platform scope.

### Create the adapter client secret

This step is required when the adapter calls a real Copilot Studio specialist agent.
It is not required for the hardcoded backend.

1. Open **Certificates & secrets**.
2. Create a client secret with the shortest practical lifetime.
3. Copy the secret **value** at once.
4. Store it as `Authentication:ClientSecret` in .NET user secrets or a secure host
  setting.

Do not use the secret ID. Do not put the secret in source files, scripts, screenshots,
or chat.

Do not enable public client flows on the adapter registration. The adapter is a
confidential client.

Do not add the test client's `http://localhost` redirect URI to the adapter API
registration. Interactive sign-in happens in the separate public test client. The
adapter only validates the resulting bearer token and, for a real backend, performs
the confidential OBO exchange.

## Create the public test client registration

Use this registration for all interactive test paths. It represents a local console or
HTTP test client. It must not have a client secret.

1. Open **Microsoft Entra admin center** > **App registrations**.
2. Select **New registration**.
3. Enter a clear name, such as `Copilot Studio A2A test client`.
4. Select **Accounts in this organizational directory only**.
5. Create the registration.
6. Copy its **Application (client) ID**. This is the test client ID.
7. Open **Authentication**.
8. Select **Add a platform** > **Mobile and desktop applications**.
9. Add `http://localhost` as the redirect URI for system-browser login.
10. Save the platform configuration.

The .NET clients open the system browser and use authorization code flow with PKCE.
MSAL selects an available loopback port when `http://localhost` has no explicit port.
Do not use `https://localhost`, and do not create a client secret or certificate for
this registration.

Use the application IDs for their distinct purposes:

* Set `A2A_ADAPTER_CLIENT_ID` to the confidential adapter API application ID.
* Set `A2A_TEST_CLIENT_ID` to this public test client application ID.
* Set `A2A_REDIRECT_URI` to `http://localhost`, matching this registration.

The public test client and adapter API must not share an application ID. The redirect
URI must be registered on the application identified by `A2A_TEST_CLIENT_ID`.

The optional PowerShell client still uses device code sign-in. If you use that client,
open **Authentication** > **Advanced settings**, set **Allow public client flows** to
**Yes**, and save the change.

## Grant access for adapter tests

Complete these steps for test options 1 and 2, including the hardcoded backend
variant.

1. Open the public test client registration.
2. Open **API permissions**.
3. Select **Add a permission** > **My APIs**.
4. Select the adapter API registration.
5. Select **Delegated permissions**.
6. Add `Agents.Invoke`.
7. Grant consent according to your tenant policy.

If the scope allows only admin consent, a tenant administrator must grant it before a
user can sign in through the test client.

The resulting access token must have:

* The adapter's bare client ID in the `aud` claim
* The configured tenant ID in the `tid` claim
* `2.0` in the `ver` claim
* The signed-in user's object ID in the `oid` claim
* The exact, case-sensitive `Agents.Invoke` value in the `scp` claim

## Grant access for the direct Copilot Studio test

Complete these steps for test option 3. The direct console application does not call
the adapter.

1. Open the public test client registration.
2. Open **API permissions**.
3. Select **Add a permission**.
4. Select **APIs my organization uses**.
5. Find **Power Platform API**.
6. Select **Delegated permissions**.
7. Add `CopilotStudio.Copilots.Invoke`.
8. Grant tenant admin consent.

The signed-in user must also have access to the Power Platform environment and the
published Copilot Studio specialist agent.

You can use one public test client registration for both adapter tests and the direct
test. In that case, it has both delegated permissions:

* The adapter API's `Agents.Invoke` permission
* Power Platform API's `CopilotStudio.Copilots.Invoke` permission

Keeping one public test client is practical for local development. Use separate public
clients when different teams, owners, or consent policies apply.

## Setup for the hardcoded backend

The hardcoded backend still protects the A2A endpoint. Keep these items:

* Adapter API registration
* Exposed `Agents.Invoke` scope
* Version 2 access tokens
* Public test client registration
* `http://localhost` registered as a mobile and desktop redirect URI
* Test client delegated access to `Agents.Invoke`

These items are not needed for the hardcoded backend:

* Adapter client secret
* Power Platform delegated permission
* Copilot Studio direct connection URL
* Copilot Studio specialist agent access

The direct console application bypasses the adapter. It therefore cannot use the
adapter's hardcoded backend. Use the A2A .NET or PowerShell console client against the
hardcoded adapter when you need an offline console test.

## Check the setup

Use this checklist before testing through the adapter:

* The tenant ID is the same in both registrations and the adapter configuration.
* The adapter audience is the bare adapter client ID.
* The adapter and public test client have different application IDs.
* The public test client requests `api://<adapter-client-id>/Agents.Invoke`.
* `http://localhost` is registered as a mobile and desktop redirect URI.
* `A2A_REDIRECT_URI` exactly matches `http://localhost`.
* A real adapter test has the Power Platform permission and adapter secret.
* The signed-in user can access the published Copilot Studio specialist agent.

Use this checklist before direct testing:

* The public test client has `CopilotStudio.Copilots.Invoke`.
* `http://localhost` is registered as a mobile and desktop redirect URI.
* The signed-in user can access the Power Platform environment and agent.
* The direct connection URL comes from the published agent's standard channel.

Continue with [the testing guide](TESTING.md).

## References

* [MSAL.NET system browser authentication](https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/using-web-browsers#system-web-browser)
* [Microsoft identity platform device authorization flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-device-code)
* [Configure client access to a web API](https://learn.microsoft.com/entra/identity-platform/quickstart-configure-app-access-web-apis)
* [OAuth 2.0 on-behalf-of flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow)
* [Connect to Copilot Studio with the Microsoft 365 Agents SDK](https://learn.microsoft.com/microsoft-copilot-studio/publication-integrate-web-or-native-app-m365-agents-sdk)
