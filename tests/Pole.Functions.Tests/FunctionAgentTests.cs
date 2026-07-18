using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pole.Functions.Protocol;
using Pole.Functions.Sdk;
using Xunit;

namespace Pole.Functions.Tests;

public sealed class FunctionAgentTests
{
    private static readonly JsonSerializerOptions JsonOptions = PoleFunctionProtocolJson.CreateOptions();

    [Fact]
    public async Task Metadata_describes_handlers_in_stable_order()
    {
        await using var app = await StartAgentAsync(functions => functions
            .AddTimerHandler("z-timer", (_, _, _) => ValueTask.CompletedTask)
            .AddHttpHandler("a-http", (_, _, _) =>
                ValueTask.FromResult(PoleHttpResponse.Empty())));

        var metadata = await app.GetTestClient().GetFromJsonAsync<FunctionAgentMetadataResponse>(
            PoleFunctionProtocol.MetadataPath,
            JsonOptions);

        Assert.NotNull(metadata);
        Assert.Equal("app-1", metadata.FunctionAppId);
        Assert.Equal("revision-1", metadata.RevisionId);
        Assert.Equal(["a-http", "z-timer"], metadata.Handlers.Select(handler => handler.Name));
    }

    [Fact]
    public async Task Reserve_is_idempotent_and_does_not_invoke_handler()
    {
        var invocationCount = 0;
        await using var app = await StartAgentAsync(functions => functions.AddHttpHandler(
            "hello",
            (_, _, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                return ValueTask.FromResult(PoleHttpResponse.Text("ok"));
            }));
        var client = app.GetTestClient();
        var request = CreateReserveRequest("attempt-1");

        var first = await PostAsync<ReserveFunctionAttemptRequest, ReserveFunctionAttemptResponse>(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            request);
        var replay = await PostAsync<ReserveFunctionAttemptRequest, ReserveFunctionAttemptResponse>(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            request);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.ReservationToken, replay.ReservationToken);
        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task Execute_returns_accepted_and_duplicate_executes_only_once()
    {
        var invocationCount = 0;
        await using var app = await StartAgentAsync(functions => functions.AddHttpHandler(
            "hello",
            (_, _, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                return ValueTask.FromResult(PoleHttpResponse.Text("done"));
            }));
        var client = app.GetTestClient();
        var reserveRequest = CreateReserveRequest("attempt-1");
        var reservation = await PostAsync<ReserveFunctionAttemptRequest, ReserveFunctionAttemptResponse>(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            reserveRequest);
        var execute = CreateExecuteRequest(reservation.ReservationToken);

        using var firstResponse = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ExecuteAttemptPath("attempt-1"),
            execute);
        using var replayResponse = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ExecuteAttemptPath("attempt-1"),
            execute);
        var replay = await replayResponse.Content.ReadFromJsonAsync<StartFunctionAttemptResponse>(JsonOptions);
        var status = await client.GetFromJsonAsync<FunctionAttemptStatusResponse>(
            PoleFunctionProtocol.AttemptStatusPath("attempt-1") + "?waitUpToMs=5000",
            JsonOptions);

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        Assert.NotNull(replay);
        Assert.True(replay.IsReplay);
        Assert.Equal(1, Volatile.Read(ref invocationCount));
        var completed = Assert.IsType<CompletedFunctionAttemptStatus>(status!.Status);
        Assert.Equal(FunctionAttemptOutcome.Succeeded, completed.Outcome);
        var result = Assert.IsType<HttpFunctionInvocationResult>(completed.Result);
        Assert.Equal("done", System.Text.Encoding.UTF8.GetString(result.Body!));
        var reserveReplay = await PostAsync<ReserveFunctionAttemptRequest, ReserveFunctionAttemptResponse>(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            reserveRequest);
        Assert.True(reserveReplay.IsReplay);
        Assert.Equal(reservation.ReservationToken, reserveReplay.ReservationToken);
    }

    [Fact]
    public async Task Capacity_is_held_by_reservation_and_released_after_completion()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAgentAsync(
            functions => functions.AddHttpHandler(
                "hello",
                async (_, _, cancellationToken) =>
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return PoleHttpResponse.Empty();
                }),
            maxConcurrency: 1);
        var client = app.GetTestClient();
        var firstReservation = await ReserveAsync(client, "attempt-1");

        using var capacityResponse = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            CreateReserveRequest("attempt-2"));
        var capacityError = await capacityResponse.Content.ReadFromJsonAsync<FunctionProtocolError>(JsonOptions);

        Assert.Equal(HttpStatusCode.TooManyRequests, capacityResponse.StatusCode);
        Assert.Equal("1", capacityResponse.Headers.RetryAfter?.ToString());
        Assert.Equal(PoleFunctionProtocolErrors.CapacityExhausted, capacityError!.Code);

        using var executeResponse = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ExecuteAttemptPath("attempt-1"),
            CreateExecuteRequest(firstReservation.ReservationToken));
        release.SetResult();
        _ = await client.GetFromJsonAsync<FunctionAttemptStatusResponse>(
            PoleFunctionProtocol.AttemptStatusPath("attempt-1") + "?waitUpToMs=5000",
            JsonOptions);
        var secondReservation = await ReserveAsync(client, "attempt-2");

        Assert.False(secondReservation.IsReplay);
    }

    [Fact]
    public async Task Reusing_attempt_id_with_different_identity_is_rejected()
    {
        await using var app = await StartAgentAsync(functions => functions.AddHttpHandler(
            "hello",
            (_, _, _) => ValueTask.FromResult(PoleHttpResponse.Empty())));
        var client = app.GetTestClient();
        _ = await ReserveAsync(client, "attempt-1");
        var conflicting = CreateReserveRequest("attempt-1") with
        {
            Attempt = CreateReserveRequest("attempt-1").Attempt with { InvocationId = "invocation-2" }
        };

        using var response = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            conflicting);
        var error = await response.Content.ReadFromJsonAsync<FunctionProtocolError>(JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(PoleFunctionProtocolErrors.AttemptConflict, error!.Code);
    }

    [Fact]
    public async Task Expired_reservation_releases_its_capacity_slot()
    {
        await using var app = await StartAgentAsync(
            functions => functions.AddHttpHandler(
                "hello",
                (_, _, _) => ValueTask.FromResult(PoleHttpResponse.Empty())),
            maxConcurrency: 1,
            reservationLease: TimeSpan.FromMilliseconds(30));
        var client = app.GetTestClient();
        _ = await ReserveAsync(client, "attempt-1");

        await Task.Delay(80);
        var second = await ReserveAsync(client, "attempt-2");
        var expired = await client.GetFromJsonAsync<FunctionAttemptStatusResponse>(
            PoleFunctionProtocol.AttemptStatusPath("attempt-1"),
            JsonOptions);

        Assert.False(second.IsReplay);
        Assert.IsType<ExpiredFunctionAttemptStatus>(expired!.Status);
    }

    [Fact]
    public async Task Cancelling_long_poll_does_not_cancel_handler_execution()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAgentAsync(functions => functions.AddHttpHandler(
            "hello",
            async (_, _, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return PoleHttpResponse.Text("completed");
            }));
        var client = app.GetTestClient();
        var reservation = await ReserveAsync(client, "attempt-1");
        using var executeResponse = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ExecuteAttemptPath("attempt-1"),
            CreateExecuteRequest(reservation.ReservationToken));
        using var pollCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync(
                PoleFunctionProtocol.AttemptStatusPath("attempt-1") + "?waitUpToMs=5000",
                pollCancellation.Token));
        release.SetResult();
        var completed = await client.GetFromJsonAsync<FunctionAttemptStatusResponse>(
            PoleFunctionProtocol.AttemptStatusPath("attempt-1") + "?waitUpToMs=5000",
            JsonOptions);

        var status = Assert.IsType<CompletedFunctionAttemptStatus>(completed!.Status);
        Assert.Equal(FunctionAttemptOutcome.Succeeded, status.Outcome);
    }

    [Fact]
    public async Task Agent_protocol_is_denied_by_default_while_health_remains_available()
    {
        await using var app = await StartAgentAsync(
            functions => functions.AddHttpHandler(
                "hello",
                (_, _, _) => ValueTask.FromResult(PoleHttpResponse.Empty())),
            allowUnauthenticatedRequests: false);
        var client = app.GetTestClient();

        using var metadata = await client.GetAsync(PoleFunctionProtocol.MetadataPath);
        using var reserve = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            CreateReserveRequest("attempt-1"));
        using var live = await client.GetAsync(PoleFunctionProtocol.LivenessPath);

        Assert.Equal(HttpStatusCode.Unauthorized, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reserve.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Jwt_authentication_binds_calls_to_operation_revision_pod_and_attempt()
    {
        using var rsa = RSA.Create(2048);
        var issuer = new TestAgentTokenIssuer(
            "test-runtime",
            "test-agent",
            "test-key",
            rsa,
            TimeProvider.System);
        await using var app = await StartAgentAsync(
            functions => functions.AddHttpHandler(
                "hello",
                (_, _, _) => ValueTask.FromResult(PoleHttpResponse.Empty())),
            allowUnauthenticatedRequests: false,
            configureOptions: options =>
            {
                options.TenantId = "tenant-1";
                options.AgentAuthentication.Issuer = issuer.Issuer;
                options.AgentAuthentication.Audience = issuer.Audience;
                options.AgentAuthentication.TargetUid = "pod-1";
                options.AgentAuthentication.TrustedKeys.Add(new PoleFunctionAgentTrustedKeyOptions
                {
                    KeyId = issuer.KeyId,
                    SubjectPublicKeyInfoBase64 = issuer.SubjectPublicKeyInfoBase64
                });
            });
        var request = CreateReserveRequest("attempt-1");
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            issuer.Issue(
                "tenant-1",
                "app-1",
                "revision-1",
                "pod-1",
                PoleFunctionAgentToken.Operations.ReserveAttempt,
                request.Attempt));

        using var accepted = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            request);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            issuer.Issue(
                "tenant-1",
                "app-1",
                "revision-1",
                "pod-1",
                PoleFunctionAgentToken.Operations.Metadata));
        using var wrongOperation = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            CreateReserveRequest("attempt-2"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongOperation.StatusCode);

        var attemptBoundRequest = CreateReserveRequest("attempt-3");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            issuer.Issue(
                "tenant-1",
                "app-1",
                "revision-1",
                "pod-1",
                PoleFunctionAgentToken.Operations.ReserveAttempt,
                attemptBoundRequest.Attempt));
        using var mismatchedAttempt = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            attemptBoundRequest with
            {
                Attempt = attemptBoundRequest.Attempt with { TenantId = "other-tenant" }
            });
        Assert.Equal(HttpStatusCode.Forbidden, mismatchedAttempt.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            issuer.Issue(
                "tenant-1",
                "app-1",
                "revision-1",
                "other-pod",
                PoleFunctionAgentToken.Operations.ReserveAttempt,
                CreateReserveRequest("attempt-4").Attempt));
        using var wrongPod = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            CreateReserveRequest("attempt-4"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPod.StatusCode);
    }

    [Fact]
    public async Task Draining_agent_rejects_start_of_a_reserved_attempt()
    {
        await using var app = await StartAgentAsync(functions => functions.AddHttpHandler(
            "hello",
            (_, _, _) => ValueTask.FromResult(PoleHttpResponse.Empty())));
        var client = app.GetTestClient();
        var reservation = await ReserveAsync(client, "attempt-1");
        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        using var execute = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ExecuteAttemptPath("attempt-1"),
            CreateExecuteRequest(reservation.ReservationToken));
        using var ready = await client.GetAsync(PoleFunctionProtocol.ReadinessPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, execute.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Oversized_protocol_request_is_rejected_before_handler_start()
    {
        var invocationCount = 0;
        await using var app = await StartAgentAsync(
            functions => functions.AddHttpHandler(
                "hello",
                (_, _, _) =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return ValueTask.FromResult(PoleHttpResponse.Empty());
                }),
            maxRequestBodyBytes: 16,
            maxProtocolRequestBytes: 65_557);
        var client = app.GetTestClient();
        var reservation = await ReserveAsync(client, "attempt-1");
        var oversized = new ExecuteFunctionAttemptRequest(
            PoleFunctionProtocol.Version,
            reservation.ReservationToken,
            new HttpFunctionInvocationPayload("POST", "/", string.Empty, [], new byte[100_000]));

        using var response = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ExecuteAttemptPath("attempt-1"),
            oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task Shutdown_waits_for_an_already_started_handler()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAgentAsync(functions => functions.AddHttpHandler(
            "hello",
            async (_, _, _) =>
            {
                started.SetResult();
                await release.Task;
                return PoleHttpResponse.Empty();
            }));
        var client = app.GetTestClient();
        var reservation = await ReserveAsync(client, "attempt-1");
        using var execute = await PostResponseAsync(
            client,
            PoleFunctionProtocol.ExecuteAttemptPath("attempt-1"),
            CreateExecuteRequest(reservation.ReservationToken));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = app.StopAsync();
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);

        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<WebApplication> StartAgentAsync(
        Action<PoleFunctionsBuilder> configureHandlers,
        int maxConcurrency = 4,
        TimeSpan? reservationLease = null,
        bool allowUnauthenticatedRequests = true,
        int maxRequestBodyBytes = 1_048_576,
        int maxProtocolRequestBytes = 2_097_152,
        Action<PoleFunctionsOptions>? configureOptions = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var functions = builder.Services.AddPoleFunctions(options =>
        {
            options.FunctionAppId = "app-1";
            options.RevisionId = "revision-1";
            options.MaxConcurrencyPerInstance = maxConcurrency;
            options.DefaultHandlerConcurrencyPerInstance = maxConcurrency;
            options.AllowUnauthenticatedAgentRequests = allowUnauthenticatedRequests;
            options.MaxRequestBodyBytes = maxRequestBodyBytes;
            options.MaxProtocolRequestBytes = maxProtocolRequestBytes;
            configureOptions?.Invoke(options);
            if (reservationLease is not null)
                options.ReservationLease = reservationLease.Value;
        });
        configureHandlers(functions);
        var app = builder.Build();
        app.MapPoleFunctionsAgent();
        await app.StartAsync();
        return app;
    }

    private static ReserveFunctionAttemptRequest CreateReserveRequest(string attemptId) =>
        new(
            PoleFunctionProtocol.Version,
            new FunctionAttemptIdentity(
                "tenant-1",
                "app-1",
                "revision-1",
                "hello",
                FunctionTriggerKind.Http,
                "invocation-1",
                attemptId,
                1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            new FunctionInvocationTraceContext(null, null, "correlation-1"));

    private static ExecuteFunctionAttemptRequest CreateExecuteRequest(string token) =>
        new(
            PoleFunctionProtocol.Version,
            token,
            new HttpFunctionInvocationPayload("GET", "/hello", string.Empty, [], null));

    private static async Task<ReserveFunctionAttemptResponse> ReserveAsync(
        HttpClient client,
        string attemptId) =>
        await PostAsync<ReserveFunctionAttemptRequest, ReserveFunctionAttemptResponse>(
            client,
            PoleFunctionProtocol.ReserveAttemptPath,
            CreateReserveRequest(attemptId));

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(
        HttpClient client,
        string path,
        TRequest request)
    {
        using var response = await PostResponseAsync(client, path, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions))!;
    }

    private static Task<HttpResponseMessage> PostResponseAsync<TRequest>(
        HttpClient client,
        string path,
        TRequest request) =>
        client.PostAsync(path, JsonContent.Create(request, options: JsonOptions));

}
