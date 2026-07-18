using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

public static class PoleFunctionsEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions ProtocolJsonOptions = PoleFunctionProtocolJson.CreateOptions();

    public static IEndpointRouteBuilder MapPoleFunctionsAgent(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(PoleFunctionProtocol.MetadataPath, GetMetadata);
        endpoints.MapGet(PoleFunctionProtocol.LivenessPath, () => Results.Ok());
        endpoints.MapGet(PoleFunctionProtocol.ReadinessPath, GetReadiness);
        endpoints.MapPost(PoleFunctionProtocol.ReserveAttemptPath, ReserveAttempt);
        endpoints.MapPost(PoleFunctionProtocol.BasePath + "/attempts/{attemptId}:execute", ExecuteAttempt);
        endpoints.MapGet(PoleFunctionProtocol.BasePath + "/attempts/{attemptId}", GetAttemptStatus);
        endpoints.MapGet(PoleFunctionProtocol.AgentStatusPath, GetAgentStatus);
        return endpoints;
    }

    private static async Task<IResult> GetMetadata(
        HttpContext httpContext,
        IPoleFunctionAgentAuthenticator authenticator,
        FunctionAgentIdentity identity,
        FunctionAgentLifecycle lifecycle,
        FunctionHandlerRegistry handlers,
        IOptions<PoleFunctionsOptions> options)
    {
        if (!await IsAuthorizedAsync(
                httpContext,
                authenticator,
                new PoleFunctionAgentAuthorizationContext(PoleFunctionAgentOperation.Metadata)))
            return Results.Unauthorized();

        var metadata = new FunctionAgentMetadataResponse(
            PoleFunctionProtocol.Version,
            typeof(PoleFunctionsEndpointRouteBuilderExtensions).Assembly.GetName().Version?.ToString() ?? "unknown",
            identity.InstanceId,
            options.Value.FunctionAppId,
            options.Value.RevisionId,
            lifecycle.IsDraining
                ? FunctionAgentLifecycleState.Draining
                : FunctionAgentLifecycleState.Accepting,
            handlers.All
                .Select(handler => new FunctionHandlerMetadata(
                    handler.Descriptor.Name,
                    handler.Descriptor.TriggerKind,
                    handler.MaxConcurrencyPerInstance))
                .ToArray());
        return Results.Ok(metadata);
    }

    private static IResult GetReadiness(FunctionAgentLifecycle lifecycle) =>
        lifecycle.IsDraining
            ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Results.Ok();

    private static async Task<IResult> ReserveAttempt(
        HttpContext httpContext,
        IPoleFunctionAgentAuthenticator authenticator,
        FunctionAttemptStore store,
        IOptions<PoleFunctionsOptions> options)
    {
        if (!await IsAuthorizedAsync(
                httpContext,
                authenticator,
                new PoleFunctionAgentAuthorizationContext(
                    PoleFunctionAgentOperation.ReserveAttempt,
                    IsPreflight: true)))
            return Results.Unauthorized();

        var requestRead = await ReadRequestAsync<ReserveFunctionAttemptRequest>(
            httpContext,
            options.Value.MaxProtocolRequestBytes);
        if (requestRead.IsTooLarge)
            return RequestTooLarge();
        if (requestRead.Value is not { Attempt: not null } request)
            return InvalidRequest();
        if (!await IsAuthorizedAsync(
                httpContext,
                authenticator,
                new PoleFunctionAgentAuthorizationContext(
                    PoleFunctionAgentOperation.ReserveAttempt,
                    request.Attempt.AttemptId,
                    request.Attempt)))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var result = store.Reserve(request);
        if (!result.IsSuccess)
            return ToError(result.Error!, httpContext);

        return Results.Ok(result.Value!.Response);
    }

    private static async Task<IResult> ExecuteAttempt(
        string attemptId,
        HttpContext httpContext,
        IPoleFunctionAgentAuthenticator authenticator,
        FunctionAttemptStore store,
        FunctionAttemptExecutor executor,
        FunctionExecutionTracker executionTracker,
        IOptions<PoleFunctionsOptions> options)
    {
        if (!await IsAuthorizedAsync(
                httpContext,
                authenticator,
                new PoleFunctionAgentAuthorizationContext(
                    PoleFunctionAgentOperation.ExecuteAttempt,
                    attemptId)))
            return Results.Unauthorized();

        var requestRead = await ReadRequestAsync<ExecuteFunctionAttemptRequest>(
            httpContext,
            options.Value.MaxProtocolRequestBytes);
        if (requestRead.IsTooLarge)
            return RequestTooLarge();
        if (requestRead.Value is not { } request)
            return InvalidRequest();

        if (!executionTracker.TryBegin(out var registration))
        {
            return ToError(
                new AttemptOperationError(
                    PoleFunctionProtocolErrors.AgentDraining,
                    "The Function Agent is draining.",
                    StatusCodes.Status503ServiceUnavailable,
                    Retryable: true),
                httpContext);
        }

        var result = store.Start(attemptId, request);
        if (!result.IsSuccess)
        {
            registration.Dispose();
            return ToError(result.Error!, httpContext);
        }

        var lease = result.Value!;
        executor.Start(lease, registration);
        var statusUrl = PoleFunctionProtocol.AttemptStatusPath(attemptId);
        httpContext.Response.Headers.Location = statusUrl;
        return Results.Accepted(
            statusUrl,
            new StartFunctionAttemptResponse(
                attemptId,
                lease.StartedAtUtc,
                IsReplay: !lease.ShouldExecute,
                statusUrl));
    }

    private static async Task<IResult> GetAttemptStatus(
        string attemptId,
        int? waitUpToMs,
        HttpContext httpContext,
        IPoleFunctionAgentAuthenticator authenticator,
        FunctionAttemptStore store,
        FunctionAgentIdentity identity,
        IOptions<PoleFunctionsOptions> options,
        TimeProvider timeProvider)
    {
        if (!await IsAuthorizedAsync(
                httpContext,
                authenticator,
                new PoleFunctionAgentAuthorizationContext(
                    PoleFunctionAgentOperation.ObserveAttempt,
                    attemptId)))
            return Results.Unauthorized();

        var observation = store.Observe(attemptId);
        if (observation.Completion is not null && waitUpToMs is > 0)
        {
            var requestedWait = TimeSpan.FromMilliseconds(waitUpToMs.Value);
            var wait = requestedWait <= options.Value.MaxLongPoll
                ? requestedWait
                : options.Value.MaxLongPoll;
            try
            {
                await observation.Completion.WaitAsync(wait, timeProvider, httpContext.RequestAborted);
            }
            catch (TimeoutException)
            {
                // A timed-out poll does not affect the running handler.
            }

            observation = store.Observe(attemptId);
        }

        return Results.Ok(new FunctionAttemptStatusResponse(
            identity.InstanceId,
            timeProvider.GetUtcNow(),
            observation.Status));
    }

    private static async Task<IResult> GetAgentStatus(
        HttpContext httpContext,
        IPoleFunctionAgentAuthenticator authenticator,
        FunctionAttemptStore store) =>
        await IsAuthorizedAsync(
            httpContext,
            authenticator,
            new PoleFunctionAgentAuthorizationContext(PoleFunctionAgentOperation.AgentStatus))
            ? Results.Ok(store.GetAgentStatus())
            : Results.Unauthorized();

    private static ValueTask<bool> IsAuthorizedAsync(
        HttpContext httpContext,
        IPoleFunctionAgentAuthenticator authenticator,
        PoleFunctionAgentAuthorizationContext authorizationContext) =>
        authenticator.AuthorizeAsync(
            httpContext,
            authorizationContext,
            httpContext.RequestAborted);

    private static async Task<ProtocolRequestReadResult<TRequest>> ReadRequestAsync<TRequest>(
        HttpContext httpContext,
        int maxBytes)
        where TRequest : class
    {
        if (httpContext.Request.ContentLength > maxBytes)
            return new(null, IsTooLarge: true);

        try
        {
            await using var boundedBody = new BoundedReadStream(httpContext.Request.Body, maxBytes);
            var value = await JsonSerializer.DeserializeAsync<TRequest>(
                boundedBody,
                ProtocolJsonOptions,
                httpContext.RequestAborted);
            return new(value, IsTooLarge: false);
        }
        catch (ProtocolRequestTooLargeException)
        {
            return new(null, IsTooLarge: true);
        }
        catch (JsonException)
        {
            return new(null, IsTooLarge: false);
        }
        catch (BadHttpRequestException)
        {
            return new(null, IsTooLarge: false);
        }
    }

    private static IResult InvalidRequest() =>
        Results.BadRequest(new FunctionProtocolError(
            PoleFunctionProtocolErrors.InvalidRequest,
            "The request body is missing or invalid."));

    private static IResult RequestTooLarge() =>
        Results.Json(
            new FunctionProtocolError(
                PoleFunctionProtocolErrors.RequestTooLarge,
                "The protocol request exceeds the configured limit."),
            statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult ToError(AttemptOperationError error, HttpContext httpContext)
    {
        if (error.RetryAfter is { } retryAfter)
            httpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

        return Results.Json(
            new FunctionProtocolError(error.Code, error.Message, error.Retryable),
            statusCode: error.StatusCode);
    }
}

internal readonly record struct ProtocolRequestReadResult<TRequest>(TRequest? Value, bool IsTooLarge)
    where TRequest : class;

internal sealed class ProtocolRequestTooLargeException : Exception;

internal sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
{
    private long bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Count(read);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The HTTP request owns the inner stream.
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Count(int read)
    {
        bytesRead += read;
        if (bytesRead > maxBytes)
            throw new ProtocolRequestTooLargeException();
    }
}
