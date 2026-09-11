using Microsoft.Extensions.DependencyInjection;

namespace CopilotStudioA2A.Tests;

/// <summary>Exercises application startup and message handling with the configured offline backend.</summary>
public sealed class HardcodedBackendIntegrationTests
{
    /// <summary>Starts without Copilot Studio secrets and returns the fixed response without external access.</summary>
    [Fact]
    public async Task GivenHardcodedBackendSetting_WhenSending_ReturnsFixedResponseWithoutLiveDependencies()
    {
        await using var factory = new AdapterWebApplicationFactory(
            includeBilling: false,
            configureSettings: settings =>
            {
                settings["Adapter:UseHardcodedBackend"] = "true";
                settings["Authentication:ClientSecret"] = "";
                settings["support:DirectConnectUrl"] = "";
            },
            replaceBackend: false);
        using var client = factory.CreateLocalClient();

        Assert.IsType<HardcodedBackend>(factory.Services.GetRequiredService<ICopilotStudioBackend>());
        using var response = await AdapterWebApplicationFactory.SendAsync(client, factory.CreateToken());
        var message = await TestRequests.AssertMessageAsync(response, HardcodedBackend.ResponseText);

        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("contextId").GetString()));
        Assert.Empty(factory.Backend.Calls);
    }
}
