---
title: Implementation limitations
description: Known functional, security, scalability, reliability, deployment, and verification limitations of the Copilot Studio A2A adapter.
ms.date: 2026-09-08
ms.topic: reference
---

## Support boundary

> [!IMPORTANT]
> This implementation is a minimal adapter and has not been validated for production-scale or high-availability workloads. The configured limit of 1,000 conversations is a state-capacity limit, not evidence that 1,000 simultaneous requests are supported.

The adapter provides a delegated, text-only path from an authenticated A2A caller to a Copilot Studio specialist agent. It does not provide a general-purpose or complete A2A hosting platform.

## Protocol and content limitations

* Only the synchronous A2A `SendMessage` operation is supported.
* Streaming, tasks, background execution, push notifications, request batches, and JSON-RPC notifications are rejected.
* Input and output are limited to plain text.
* Files, URLs, binary data, attachments, adaptive cards, and caller-supplied metadata are not forwarded.
* Interactive OAuth cards returned by a Copilot Studio specialist agent are rejected. Delegated transport authentication does not complete an agent-specific interactive sign-in flow.
* The adapter returns one final text response and does not expose typing, progress, or streaming-delta activities.
* Message IDs are validated but are not persisted for durable deduplication or exactly-once execution.

See [src/CopilotStudioA2A/A2AProfile.cs](src/CopilotStudioA2A/A2AProfile.cs) and [src/CopilotStudioA2A/TextTranslation.cs](src/CopilotStudioA2A/TextTranslation.cs) for the enforced profile.

## Conversation state limitations

* Conversation mappings and Agent Framework sessions are held in process memory.
* A process restart, deployment, slot swap, or instance replacement invalidates every existing context.
* The default limit is 1,000 stored contexts across all configured agents, not 1,000 contexts per agent.
* Idle contexts have no age-based expiration. They remain until capacity pressure evicts them or the process restarts.
* At capacity, a new conversation evicts the least recently used idle context and its framework session.
* A new conversation is rejected when every context has an active or queued turn.
* Turns for the same context execute sequentially. Queue time consumes the request deadline.
* A context that was evicted or lost cannot be reconstructed from its public context ID.

See [src/CopilotStudioA2A/ConversationStore.cs](src/CopilotStudioA2A/ConversationStore.cs) for the current state and turn-queue behavior.

## Context ownership limitation

> [!WARNING]
> Context ownership is not bound to the authenticated user. Any authorized caller who obtains another caller's context ID can attempt to continue that conversation.

Context IDs must be treated as sensitive bearer-like values. The adapter should not be exposed to mutually untrusted users until each context is bound to stable caller identity claims and ownership is checked on every continuation.

## Scalability limitations

* The supplied infrastructure deploys one B1 or S1 Linux App Service instance with fixed capacity of one.
* Horizontal scale-out is unsafe because conversation state and Agent Framework sessions are not shared between instances.
* The 1,000-context setting does not reserve CPU, memory, sockets, downstream capacity, or request throughput.
* Each request is buffered in memory before and after Agent Framework processing.
* Requests can remain open for up to 120 seconds by default, including time spent waiting behind another turn in the same context.
* There is no application-wide concurrency limiter or load-shedding policy for downstream Copilot Studio calls.
* Throughput depends on Microsoft Entra token acquisition, Power Platform, the selected Copilot Studio specialist agent, connectors, actions, and tenant-specific service limits.
* No supported requests-per-second, simultaneous-user, p95 latency, or p99 latency target has been established.

The supplied deployment should be treated as a development or controlled-pilot topology. See [infra/main.bicep](infra/main.bicep) for its fixed single-instance configuration.

## OBO token-cache limitations

MSAL checks its token cache when `AcquireTokenOnBehalfOf` is called. The adapter registers one singleton token provider and retains one confidential client application, so repeated requests can use MSAL's process-local cache.

The current cache has these limitations:

* Cache contents are lost when the process restarts.
* Cache contents are not shared across instances.
* Cold requests for distinct users or sessions still require Microsoft Entra exchanges.
* Default OBO cache lookup identifies a session from the incoming assertion. A renewed upstream access token can produce a different cache key.
* Cache hit rate, token source, size, and eviction are not exposed as adapter metrics.
* No explicit bounded or distributed cache provider is configured.

A distributed MSAL cache becomes necessary before enabling multiple application instances. Cache entries must be encrypted in transit and at rest, access must be restricted, and retention and eviction policies must be defined.

See [src/CopilotStudioA2A/DelegatedAuthentication.cs](src/CopilotStudioA2A/DelegatedAuthentication.cs) for the current OBO implementation.

## Reliability limitations

* Downstream message sends are not automatically retried because replay can execute a Copilot Studio action more than once.
* A timeout or caller disconnect can occur after a downstream action has started or completed, leaving the final outcome unknown.
* There is no durable inbox, outbox, idempotency record, or recovery queue.
* A failed continuation preserves the last committed context but does not prove whether the failed downstream operation executed.
* The health endpoint reports that the web host is running. It does not verify Microsoft Entra permissions, Key Vault resolution, Copilot Studio availability, agent publication, or connector health.
* There is no zone redundancy, regional failover, disaster-recovery state replication, or zero-downtime conversation migration.

Clients must not automatically replay an uncertain request. Safe retries require durable idempotency semantics that include downstream actions.

## Authentication and deployment limitations

* Only a tenant-specific Microsoft Entra v2 delegated token with the exact `Agents.Invoke` scope is accepted.
* Application-only tokens and client-credentials invocation are not supported.
* OBO uses a configured confidential-client secret. Certificate credentials and workload identity federation are not implemented.
* Power Platform delegated permission and tenant consent must be configured outside the deployment template.
* Copilot Studio DirectConnect URLs and the authentication client secret must be supplied externally.
* The supplied Key Vault uses a public endpoint protected by RBAC. Private endpoints and network isolation are not configured.
* The template does not create the Entra application registration, API scope, delegated consent grant, custom domain, private networking, or edge protection.

## Observability limitations

* Message content, bearer tokens, DirectConnect URLs, SDK response bodies, and other sensitive values are deliberately excluded from telemetry.
* Raw outbound HTTP dependency instrumentation is suppressed to prevent secret URL disclosure.
* The adapter does not publish metrics for conversation count, active turns, queued turns, queue duration, context eviction, rejected capacity, OBO cache hits, or downstream throttling.
* Correlation telemetry can identify a failed invocation but may not contain enough downstream detail to diagnose a tenant or agent configuration problem without separate service-side evidence.

## Verification limitations

* Unit and integration tests use a fake downstream backend and block network access.
* Existing concurrency tests verify sequencing and capacity behavior, not production throughput.
* No sustained load, spike, soak, failover, restart, chaos, or memory-pressure test has established operational limits.
* No test demonstrates 1,000 simultaneous users or 1,000 concurrent in-flight turns.
* No live test has established end-to-end capacity across Microsoft Entra, Power Platform, Copilot Studio, connectors, and agent actions.
* Compilation, automated tests, infrastructure validation, deployment, and live Copilot Studio verification remain environment-dependent checks.

## Production-scale prerequisites

Before making a production-scale or 1,000-simultaneous-user claim, complete these changes and validations:

1. Bind every context to authenticated tenant and user identity claims.
2. Replace process-local conversation and framework session state with a distributed store that supports atomic leases and expiration.
3. Add multi-instance deployment, autoscaling, health-based routing, and failure recovery.
4. Add bounded downstream concurrency, queue limits, overload responses, and capacity metrics.
5. Configure an explicit distributed OBO token cache for multi-instance operation and monitor its hit rate.
6. Define durable idempotency behavior before adding retries or asynchronous recovery.
7. Confirm applicable Microsoft Entra, Power Platform, Copilot Studio, connector, and action quotas.
8. Run realistic cold-cache and warm-cache load tests with representative agent latency and user distribution.
9. Establish service-level objectives for throughput, availability, error rate, and latency.
10. Validate restart, scale-out, throttling, timeout, and partial-failure behavior before production exposure.
