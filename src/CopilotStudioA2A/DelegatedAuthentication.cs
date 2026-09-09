using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Tokens;

namespace CopilotStudioA2A;

/// <summary>
/// Sets up user authentication and checks that callers have the required access.
/// </summary>
internal static class DelegatedAuthentication
{
    internal const string Policy = "Agents.Invoke";

    public static void AddDelegatedAuthentication(this IServiceCollection services, AuthenticationOptions settings)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = settings.Authority;
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Authority,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromMinutes(1)
            };
        });
        services.AddAuthorization(options => options.AddPolicy(Policy, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => IsDelegatedCaller(context.User, settings))));
    }

    public static bool IsDelegatedCaller(ClaimsPrincipal user, AuthenticationOptions settings) =>
        user.FindFirst("tid")?.Value == settings.TenantId &&
        user.FindFirst("ver")?.Value == "2.0" &&
        Guid.TryParse(user.FindFirst("oid")?.Value, out _) &&
        !string.Equals(user.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase) &&
        user.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(settings.RequiredScope, StringComparer.Ordinal);
}

/// <summary>
/// Defines how to get a Copilot Studio access token for the current user.
/// </summary>
internal interface IDelegatedTokenProvider
{
    Task<string> AcquireAsync(string scope, string userAssertion, CancellationToken cancellationToken);
}

/// <summary>
/// Gets a Copilot Studio access token by using the current user's token.
/// </summary>
internal sealed class DelegatedTokenProvider(AuthenticationOptions settings) : IDelegatedTokenProvider
{
    private readonly IConfidentialClientApplication _application = ConfidentialClientApplicationBuilder
        .Create(settings.ClientId)
        .WithAuthority(AzureCloudInstance.AzurePublic, settings.TenantId)
        .WithClientSecret(settings.ClientSecret)
        .Build();

    public async Task<string> AcquireAsync(string scope, string userAssertion, CancellationToken cancellationToken)
    {
        // Deliberately no client-credentials fallback: every turn uses the current user's assertion.
        var result = await _application.AcquireTokenOnBehalfOf([scope], new UserAssertion(userAssertion))
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }
}