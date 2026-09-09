using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CopilotStudioA2A.Tests;

/// <summary>Specifies per-factory configuration isolation and temporary-directory ownership without global mutation.</summary>
public sealed class FactoryIsolationIntegrationTests
{
    /// <summary>Uses independent empty content roots and a non-Development environment with HTTPS test clients.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact]
    public async Task GivenIndependentFactories_WhenCreatingHosts_UsesEmptyTestingRootsAndSyntheticOptions()
    {
        await using var first = new AdapterWebApplicationFactory(includeBilling: false);
        await using var second = new AdapterWebApplicationFactory(includeBilling: true);
        Assert.NotEqual(first.ContentRootPath, second.ContentRootPath);

        foreach (var factory in new[] { first, second })
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(factory.ContentRootPath));
            using var client = factory.CreateLocalClient();
            var environment = factory.Services.GetRequiredService<IHostEnvironment>();
            var webEnvironment = factory.Services.GetRequiredService<IWebHostEnvironment>();

            Assert.Equal("Testing", environment.EnvironmentName);
            Assert.Equal("Testing", webEnvironment.EnvironmentName);
            Assert.Equal(factory.ContentRootPath, environment.ContentRootPath);
            Assert.Equal(factory.ContentRootPath, webEnvironment.ContentRootPath);
            Assert.Equal(new Uri("https://localhost"), client.BaseAddress);
            Assert.Equal("https://localhost", factory.Services.GetRequiredService<AdapterOptions>().PublicBaseUrl);
            Assert.Empty(Directory.EnumerateFileSystemEntries(factory.ContentRootPath));
            Assert.Empty(factory.Backend.Calls);
        }

        Assert.Equal(new[] { "support" }, first.Services.GetRequiredService<CopilotStudioOptions>().Agents.Keys);
        Assert.Equal(new[] { "support", "billing" }, second.Services.GetRequiredService<CopilotStudioOptions>().Agents.Keys);
    }

    /// <summary>Deletes only the factory's own directory after normal, failed or unattempted startup, even on repeated disposal.</summary>
    /// <param name="startup">Whether startup succeeds, fails validation or is never attempted.</param>
    /// <param name="disposeAsync">Whether the explicit cleanup uses asynchronous disposal.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory]
    [InlineData("not-started", false)]
    [InlineData("not-started", true)]
    [InlineData("started", false)]
    [InlineData("started", true)]
    [InlineData("startup-failed", false)]
    [InlineData("startup-failed", true)]
    public async Task GivenFactoryLifecycle_WhenDisposing_RemovesOnlyOwnedRootIdempotently(string startup, bool disposeAsync)
    {
        await using var other = new AdapterWebApplicationFactory(includeBilling: false);
        await using var factory = new AdapterWebApplicationFactory(includeBilling: false, configureSettings: settings =>
        {
            if (startup == "startup-failed") settings["Agents"] = "[]";
        });
        var contentRoot = factory.ContentRootPath;
        Assert.True(Directory.Exists(contentRoot));

        if (startup == "started")
        {
            using var client = factory.CreateLocalClient();
        }
        else if (startup == "startup-failed")
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                using var client = factory.CreateLocalClient();
            });
            Assert.Contains("Agents", error.Message);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (disposeAsync) await factory.DisposeAsync();
            else factory.Dispose();

            Assert.False(Directory.Exists(contentRoot));
            Assert.True(Directory.Exists(other.ContentRootPath));
        }
        Assert.Empty(factory.Backend.Calls);
    }
}