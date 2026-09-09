using System.Security.Claims;

namespace CopilotStudioA2A.Tests;

/// <summary>Specifies exact delegated-scope matching and required Entra v2 caller claims.</summary>
public sealed class DelegatedAuthenticationTests
{
    /// <summary>Matches scope tokens exactly, case-sensitively, with space delimiters.</summary>
    /// <param name="scope">The scp claim value.</param>
    /// <param name="expected">Whether the delegated policy accepts the scope.</param>
    [Theory]
    [InlineData("Agents.Invoke", true)]
    [InlineData("User.Read Agents.Invoke Other.Scope", true)]
    [InlineData("  Agents.Invoke  ", true)]
    [InlineData("Agents.Invoke Agents.Invoke", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("agents.invoke", false)]
    [InlineData("Agents.Invoke.All", false)]
    [InlineData("PrefixAgents.Invoke", false)]
    [InlineData("Other.Scope,Agents.Invoke", false)]
    [InlineData("Other.Scope\tAgents.Invoke", false)]
    [InlineData("User.Read", false)]
    public void GivenScopeClaim_WhenCheckingDelegation_RequiresExactScope(string? scope, bool expected)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(TestIdentity.WithClaim("scp", scope), "Bearer"));

        var result = DelegatedAuthentication.IsDelegatedCaller(user, TestIdentity.Settings());

        Assert.Equal(expected, result);
    }

    /// <summary>Requires tenant, token version, object ID and a non-application identity.</summary>
    /// <param name="type">The claim to replace.</param>
    /// <param name="value">The invalid value, or null to remove it.</param>
    [Theory]
    [InlineData("tid", null)]
    [InlineData("tid", TestIdentity.OtherTenantId)]
    [InlineData("ver", null)]
    [InlineData("ver", "1.0")]
    [InlineData("ver", "2")]
    [InlineData("oid", null)]
    [InlineData("oid", "")]
    [InlineData("oid", "not-a-guid")]
    [InlineData("idtyp", "app")]
    [InlineData("idtyp", "APP")]
    [InlineData("idtyp", "App")]
    public void GivenInvalidCallerClaim_WhenCheckingDelegation_RejectsCaller(string type, string? value)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(TestIdentity.WithClaim(type, value), "Bearer"));

        var result = DelegatedAuthentication.IsDelegatedCaller(user, TestIdentity.Settings());

        Assert.False(result);
    }

    /// <summary>Checks all scp claims rather than only the first occurrence.</summary>
    [Fact]
    public void GivenMultipleScopeClaims_WhenCheckingDelegation_AcceptsExactScopeInLaterClaim()
    {
        var claims = TestIdentity.WithClaim("scp", "User.Read");
        claims.Add(new Claim("scp", "Other.Scope Agents.Invoke"));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var result = DelegatedAuthentication.IsDelegatedCaller(user, TestIdentity.Settings());

        Assert.True(result);
    }

    /// <summary>Application roles cannot substitute for a delegated scope.</summary>
    [Fact]
    public void GivenRolesWithoutScope_WhenCheckingDelegation_RejectsCaller()
    {
        var claims = TestIdentity.WithClaim("scp", null);
        claims.Add(new Claim("roles", "Agents.Invoke"));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var result = DelegatedAuthentication.IsDelegatedCaller(user, TestIdentity.Settings());

        Assert.False(result);
    }

    /// <summary>Additional roles do not invalidate an otherwise valid delegated user token.</summary>
    [Fact]
    public void GivenDelegatedScopeAndRoles_WhenCheckingDelegation_AcceptsUser()
    {
        var claims = TestIdentity.Claims();
        claims.Add(new Claim("roles", "Unrelated.Role"));
        claims.Add(new Claim("idtyp", "user"));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var result = DelegatedAuthentication.IsDelegatedCaller(user, TestIdentity.Settings());

        Assert.True(result);
    }
}