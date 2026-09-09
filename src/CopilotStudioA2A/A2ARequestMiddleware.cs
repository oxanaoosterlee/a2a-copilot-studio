using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

namespace CopilotStudioA2A;

/// <summary>
/// Stores the agent name that belongs to an A2A route.
/// </summary>
internal sealed record A2ARoute(string? AgentName);

/// <summary>
/// Handles A2A HTTP requests and returns safe A2A responses or errors.
/// </summary>
internal sealed class A2ARequestMiddleware(RequestDelegate next, ILogger<A2ARequestMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http, AdapterOptions options, CopilotStudioOptions agents, ConversationStore conversations)
    {
        var route = http.GetEndpoint()?.Metadata.GetMetadata<A2ARoute>();
        if (route is null)
        {
            await next(http);
            return;
        }

        var agentName = route.AgentName ?? http.Request.RouteValues["agentName"]?.ToString() ?? "";
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        http.Response.Headers["X-Correlation-ID"] = correlationId;
        http.Response.Headers["A2A-Version"] = A2AProfile.Version;
        http.Response.Headers.CacheControl = "no-store";
        JsonElement? requestId = null;
        var originalBody = http.Request.Body;
        var originalResponse = http.Response.Body;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        try
        {
            if (!http.Request.HasJsonContentType())
            {
                await WriteErrorAsync(http, null, A2AProfile.Error(A2AErrorCode.ContentTypeNotSupported, "Use Content-Type: application/json."), 415);
                return;
            }

            using var body = await ReadBodyAsync(http.Request, options.MaxRequestBytes, timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 32 }, timeout.Token);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) &&
                id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                requestId = id.Clone();

            if (!agents.Agents.ContainsKey(agentName))
            {
                await WriteErrorAsync(http, requestId, A2AProfile.Error(A2AErrorCode.InvalidParams, "Unknown agent."), 404);
                return;
            }

            var input = A2AProfile.Read(root, http.Request.Headers["A2A-Version"].ToString());
            // TODO: Link each context ID and conversation ID to the authenticated user. Reject the request if the IDs belong to another user.
            using var lease = await conversations.OpenAsync(agentName, input.ContextId, timeout.Token);
            if (lease.Evicted is { } evicted)
            {
                var store = http.RequestServices.GetRequiredKeyedService<AgentSessionStore>(evicted.Agent);
                var agent = http.RequestServices.GetRequiredKeyedService<AIAgent>(evicted.Agent);
                await store.DeleteSessionAsync(agent, evicted.Context, timeout.Token);
                logger.LogInformation("Evicted an idle in-memory conversation for agent {AgentName} at capacity.", evicted.Agent);
            }
            var invocation = new InvocationContext
            {
                AgentName = agentName,
                Text = input.Text,
                BearerToken = http.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim(),
                Conversation = lease.Conversation,
                CancellationToken = timeout.Token
            };

            // Give the framework the server-owned A2A context, distinct from the downstream ID.
            var normalized = JsonNode.Parse(root.GetRawText())!;
            normalized["params"]!["message"]!["contextId"] = lease.Conversation.ContextId;
            using var requestBuffer = new MemoryStream(Encoding.UTF8.GetBytes(normalized.ToJsonString()));
            using var responseBuffer = new MemoryStream();
            http.Request.Body = requestBuffer;
            http.Request.ContentLength = requestBuffer.Length;
            http.Response.Body = responseBuffer;
            InvocationContext.Current = invocation;
            await next(http);
            http.Response.Body = originalResponse;

            if (invocation.Failure is not null)
            {
                await WriteErrorAsync(http, requestId, invocation.Failure);
                return;
            }

            responseBuffer.Position = 0;
            var response = await JsonNode.ParseAsync(responseBuffer, cancellationToken: timeout.Token);
            if (response?["result"]?["message"] is JsonObject message)
            {
                message["contextId"] = lease.Conversation.ContextId;
                lease.Succeeded = true;
                http.Response.ContentLength = null;
                await http.Response.WriteAsJsonAsync(response, http.RequestAborted);
            }
            else
            {
                // No tasks, framework exception text, or incomplete protocol responses escape.
                logger.LogError("Agent Framework returned no direct message for agent {AgentName}; correlation {CorrelationId}.", agentName, correlationId);
                await WriteErrorAsync(http, requestId, A2AProfile.Error(A2AErrorCode.InternalError, "Agent Framework did not return a direct message. Inspect the correlated server log."));
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Caller disconnected during agent {AgentName} invocation; correlation {CorrelationId}.", agentName, correlationId);
        }
        catch (Exception exception)
        {
            http.Response.Body = originalResponse;
            var error = exception switch
            {
                A2AException protocol => protocol,
                JsonException => A2AProfile.Error(A2AErrorCode.ParseError, "Invalid JSON request."),
                BadHttpRequestException => A2AProfile.Error(A2AErrorCode.InvalidRequest, "Request body exceeds the configured limit."),
                OperationCanceledException => A2AProfile.Error(A2AErrorCode.InternalError, "The request exceeded the configured timeout. Completion is unknown; do not automatically replay it."),
                _ => A2AProfile.Error(A2AErrorCode.InternalError, "The request failed. Inspect the correlated server log.")
            };
            if (exception is A2AException or JsonException or BadHttpRequestException)
                logger.LogWarning("Rejected request for agent {AgentName}; protocol error {ErrorCode}.", agentName, error.ErrorCode);
            else
                AdapterTelemetry.LogFailure(logger, exception, agentName);
            await WriteErrorAsync(http, requestId, error, exception is BadHttpRequestException ? 413 : 200);
        }
        finally
        {
            http.Request.Body = originalBody;
            http.Response.Body = originalResponse;
            InvocationContext.Clear();
        }
    }

    private static async Task<MemoryStream> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit) throw new BadHttpRequestException("Request too large.", 413);
        var stream = new MemoryStream();
        try
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (stream.Length + read > limit) throw new BadHttpRequestException("Request too large.", 413);
                stream.Write(buffer, 0, read);
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static Task WriteErrorAsync(HttpContext http, JsonElement? id, A2AException exception, int statusCode = 200)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentLength = null;
        return http.Response.WriteAsJsonAsync(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code = (int)exception.ErrorCode, message = exception.Message }
        }, http.RequestAborted);
    }
}