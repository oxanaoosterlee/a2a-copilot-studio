using System.Security.Claims;

namespace CopilotStudioA2A.Tests;

internal static class TestIdentity
{
    internal const string TenantId = "11111111-1111-4111-8111-111111111111";
    internal const string ClientId = "22222222-2222-4222-8222-222222222222";
    internal const string ObjectId = "33333333-3333-4333-8333-333333333333";
    internal const string OtherTenantId = "44444444-4444-4444-8444-444444444444";
    internal const string OtherObjectId = "55555555-5555-4555-8555-555555555555";
    internal const string ClientSecret = "dummy-client-secret-for-offline-tests-only";
    internal const string Scope = "Agents.Invoke";
    internal const string Issuer = "https://login.microsoftonline.com/" + TenantId + "/v2.0";

    internal static AuthenticationOptions Settings() => new()
    {
        TenantId = TenantId,
        ClientId = ClientId,
        Audience = ClientId,
        RequiredScope = Scope,
        ClientSecret = ClientSecret
    };

    internal static List<Claim> Claims() =>
    [
        new("tid", TenantId),
        new("ver", "2.0"),
        new("oid", ObjectId),
        new("scp", Scope)
    ];

    internal static List<Claim> WithClaim(string type, string? value)
    {
        var claims = Claims();
        claims.RemoveAll(claim => claim.Type == type);
        if (value is not null) claims.Add(new Claim(type, value));
        return claims;
    }
}