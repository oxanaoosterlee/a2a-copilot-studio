using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CopilotStudioA2A.Tests;

/// <summary>Exercises real bearer-token validation and delegated authorization through HTTP.</summary>
public sealed class AuthenticationIntegrationTests : AdapterIntegrationTestsBase
{
    /// <summary>Requires authentication on the directory, known runtimes and unknown-agent fallback.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The protected path.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("GET", "/agents")]
    [InlineData("POST", "/copilot-studio/support/a2a")]
    [InlineData("POST", "/copilot-studio/billing/a2a")]
    [InlineData("POST", "/copilot-studio/unknown/a2a")]
    public async Task GivenNoToken_WhenCallingProtectedRoute_ReturnsUnauthorized(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Runs cryptographic, issuer, audience and lifetime rejection through the production handler.</summary>
    /// <param name="failure">The token validation failure to introduce.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("signature")]
    [InlineData("unsigned")]
    [InlineData("algorithm")]
    [InlineData("no-expiration")]
    [InlineData("malformed")]
    public async Task GivenInvalidJwt_WhenCallingKnownRoutes_ReturnsUnauthorized(string failure)
    {
        var token = failure switch
        {
            "audience" => Factory.CreateToken(audience: "another-api"),
            "issuer" => Factory.CreateToken(issuer: "https://issuer.invalid/v2.0"),
            "expired" => Factory.CreateToken(expires: DateTime.UtcNow.AddMinutes(-10)),
            "future" => Factory.CreateToken(notBefore: DateTime.UtcNow.AddMinutes(5)),
            "signature" => Factory.CreateToken(wrongSignature: true),
            "unsigned" => Factory.CreateToken(unsigned: true),
            "algorithm" => Factory.CreateToken(algorithm: SecurityAlgorithms.RsaSha384),
            "no-expiration" => Factory.CreateToken(omitExpiration: true),
            "malformed" => "not-a-jwt",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        foreach (var path in new[] { "/agents", "/copilot-studio/support/a2a", "/copilot-studio/billing/a2a" })
        {
            using var request = new HttpRequestMessage(path == "/agents" ? HttpMethod.Get : HttpMethod.Post, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
            Assert.All(response.Headers.WwwAuthenticate, header => Assert.Null(header.Parameter));
            Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>A valid signature does not bypass tenant, delegated scope, object ID or token-kind checks.</summary>
    /// <param name="type">The claim to replace.</param>
    /// <param name="value">The replacement value, or null to omit the claim.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("scp", null)]
    [InlineData("scp", "Agents.Invoke.All")]
    [InlineData("scp", "PrefixAgents.Invoke")]
    [InlineData("scp", "agents.invoke")]
    [InlineData("scp", "User.Read")]
    [InlineData("tid", null)]
    [InlineData("tid", TestIdentity.OtherTenantId)]
    [InlineData("ver", "1.0")]
    [InlineData("ver", null)]
    [InlineData("oid", "not-a-guid")]
    [InlineData("oid", null)]
    [InlineData("idtyp", "app")]
    [InlineData("idtyp", "APP")]
    public async Task GivenInvalidDelegation_WhenCallingKnownRoutes_ReturnsForbidden(string type, string? value)
    {
        var token = Factory.CreateToken(TestIdentity.WithClaim(type, value));
        foreach (var path in new[] { "/agents", "/copilot-studio/support/a2a", "/copilot-studio/billing/a2a" })
        {
            using var request = new HttpRequestMessage(path == "/agents" ? HttpMethod.Get : HttpMethod.Post, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Does not authorize application roles as delegated scopes.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenRolesOnlyToken_WhenSendingMessage_ReturnsForbidden()
    {
        var claims = TestIdentity.WithClaim("scp", null);
        claims.Add(new Claim("roles", "Agents.Invoke"));

        using var response = await AdapterWebApplicationFactory.SendAsync(Client, Factory.CreateToken(claims));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Accepts a locally signed delegated JWT with the required scope among other scopes.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenValidDelegatedToken_WhenListingAgents_ReturnsConfiguredDirectory()
    {
        var claims = TestIdentity.WithClaim("scp", "User.Read Agents.Invoke Other.Scope");
        claims.Add(new Claim("roles", "Unrelated.Role"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/agents");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Factory.CreateToken(claims));

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestRequests.ReadResponseAsync(response);
        Assert.Equal(new[] { "billing", "support" }, body.EnumerateArray()
            .Select(agent => agent.GetProperty("name").GetString()).OrderBy(name => name).ToArray());
        foreach (var agent in body.EnumerateArray())
        {
            var name = agent.GetProperty("name").GetString();
            Assert.Equal($"https://localhost/copilot-studio/{name}/a2a", agent.GetProperty("endpoint").GetString());
            Assert.Equal($"https://localhost/copilot-studio/{name}/a2a/.well-known/agent-card.json", agent.GetProperty("agentCard").GetString());
        }
        Assert.Empty(Factory.Backend.Calls);
    }

    /// <summary>Documents that only OIDC metadata retrieval is replaced in the test host.</summary>
    [Fact]
    public void GivenOfflineHost_WhenInspectingJwtOptions_RetainsProductionValidation()
    {
        var options = Factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.IsType<StaticConfigurationManager<OpenIdConnectConfiguration>>(options.ConfigurationManager);
        Assert.False(options.MapInboundClaims);
        Assert.True(options.RequireHttpsMetadata);
        Assert.False(options.IncludeErrorDetails);
        var validation = options.TokenValidationParameters;
        Assert.True(validation.ValidateAudience);
        Assert.True(validation.ValidateIssuer);
        Assert.True(validation.ValidateLifetime);
        Assert.True(validation.ValidateIssuerSigningKey);
        Assert.True(validation.RequireSignedTokens);
        Assert.True(validation.RequireExpirationTime);
        Assert.Equal(TestIdentity.ClientId, validation.ValidAudience);
        Assert.Equal(TestIdentity.Issuer, validation.ValidIssuer);
        Assert.Equal(SecurityAlgorithms.RsaSha256, Assert.Single(validation.ValidAlgorithms));
        Assert.Null(validation.SignatureValidator);
        Assert.Null(validation.SignatureValidatorUsingConfiguration);
    }
}