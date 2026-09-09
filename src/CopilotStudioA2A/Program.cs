using Azure.Monitor.OpenTelemetry.AspNetCore;
using CopilotStudioA2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var adapter = builder.Configuration.GetSection("Adapter").Get<AdapterOptions>() ?? new();
var authentication = builder.Configuration.GetSection("Authentication").Get<AuthenticationOptions>() ?? new();
var copilot = CopilotStudioOptions.Load(builder.Configuration);
adapter.Validate(builder.Environment.IsDevelopment());
authentication.Validate();

builder.Services.AddSingleton(adapter);
builder.Services.AddSingleton(authentication);
builder.Services.AddSingleton(copilot);
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<IDelegatedTokenProvider, DelegatedTokenProvider>();
builder.Services.AddSingleton<ICopilotStudioBackend, CopilotStudioBackend>();
builder.Services.AddDelegatedAuthentication(authentication);
builder.Services.AddHttpClient(CopilotStudioBackend.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(adapter.RequestTimeoutSeconds))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .RemoveAllLoggers(); // URLs and SDK payloads are secret; use sanitized invocation telemetry.

// No global retry handler: a retried message can execute an agent action twice.
var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("copilot-studio-a2a", serviceVersion: "1.0.0"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    telemetry.UseAzureMonitor(options => options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);
    // Automatic dependency URLs can reveal secret DirectConnectUrl paths/query strings.
    // Keep request telemetry plus our content-free outbound invocation span instead.
    telemetry.WithTracing(tracing => tracing.AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = _ => false));
}
telemetry.WithTracing(tracing => tracing.AddSource(AdapterTelemetry.SourceName));

foreach (var name in copilot.Agents.Keys)
{
    var agent = builder.AddAIAgent(name, (services, _) =>
        new CopilotChatClient(name, services.GetRequiredService<ICopilotStudioBackend>(),
            services.GetRequiredService<ILogger<CopilotChatClient>>())
            .AsAIAgent(name: name, description: copilot.Agents[name].SkillDescription));
    // In-memory, no user ownership checks, as explicitly requested. Each registered agent has
    // its own framework session store. The edge bounds contexts and serializes each context.
    agent.WithInMemorySessionStore(withIsolation: false);
#pragma warning disable MEAI001 // AgentRunMode is experimental in the pinned hosting package.
    agent.AddA2AServer(options => options.AgentRunMode = AgentRunMode.DisallowBackground);
#pragma warning restore MEAI001
}

var app = builder.Build();
copilot.LogUnlistedAgentWarnings(app.Logger);
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<A2ARequestMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/agents", () => Results.Ok(copilot.Agents.Keys.Select(name => new
{
    name,
    endpoint = adapter.PublicBaseUrl.TrimEnd('/') + AgentDiscovery.RuntimePath(name),
    agentCard = adapter.PublicBaseUrl.TrimEnd('/') + AgentDiscovery.CardPath(name)
}))).RequireAuthorization(DelegatedAuthentication.Policy);

foreach (var name in copilot.Agents.Keys)
{
    app.MapA2AJsonRpc(name, AgentDiscovery.RuntimePath(name))
        .WithMetadata(new A2ARoute(name))
        .RequireAuthorization(DelegatedAuthentication.Policy);
    app.MapGet(AgentDiscovery.CardPath(name), () => Results.Ok(AgentDiscovery.CreateCard(name, adapter, copilot.Agents[name]))).AllowAnonymous();
}
if (copilot.Agents.Count == 1)
{
    var name = copilot.Agents.Keys.Single();
    app.MapGet("/.well-known/agent-card.json", () => Results.Ok(AgentDiscovery.CreateCard(name, adapter, copilot.Agents[name]))).AllowAnonymous();
}

// Unknown agent calls still authenticate and return a structured JSON-RPC error with HTTP 404.
app.MapPost("/a2a/{agentName}", () => Results.NotFound())
    .WithMetadata(new A2ARoute(null)).RequireAuthorization(DelegatedAuthentication.Policy);
app.MapGet("/a2a/{agentName}/.well-known/agent-card.json", () => Results.NotFound()).AllowAnonymous();
app.Run();

/// <summary>
/// Gives integration tests access to the ASP.NET Core application entry point.
/// </summary>
public partial class Program { }