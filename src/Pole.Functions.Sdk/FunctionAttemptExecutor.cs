using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

internal sealed class FunctionAttemptExecutor(
    FunctionAttemptStore store,
    FunctionHandlerRegistry handlers,
    IServiceScopeFactory scopeFactory,
    IOptions<PoleFunctionsOptions> options,
    TimeProvider timeProvider,
    ILogger<FunctionAttemptExecutor> logger)
{
    public void Start(StartedAttemptLease lease, FunctionExecutionRegistration registration)
    {
        if (!lease.ShouldExecute)
        {
            registration.Dispose();
            return;
        }

        var execution = Task.Run(() => ExecuteAsync(lease), CancellationToken.None);
        registration.Attach(execution);
    }

    private async Task ExecuteAsync(StartedAttemptLease lease)
    {
        if (!handlers.TryGet(lease.Identity.HandlerName, out var handler))
        {
            store.Complete(
                lease.Identity.AttemptId,
                FunctionAttemptOutcome.PlatformFailure,
                result: null,
                new FunctionAttemptFailure(
                    PoleFunctionProtocolErrors.UnknownHandler,
                    "The registered handler is no longer available.",
                    FunctionFailureOrigin.Runtime));
            return;
        }

        var now = timeProvider.GetUtcNow();
        var remaining = lease.DeadlineUtc - now;
        if (remaining <= TimeSpan.Zero)
        {
            store.Complete(
                lease.Identity.AttemptId,
                FunctionAttemptOutcome.TimedOut,
                result: null,
                new FunctionAttemptFailure(
                    "deadline_exceeded",
                    "The invocation deadline elapsed before execution.",
                    FunctionFailureOrigin.Runtime));
            return;
        }

        using var deadline = new CancellationTokenSource(remaining, timeProvider);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = new PoleFunctionContext(
                lease.Identity,
                lease.DeadlineUtc,
                lease.TraceContext,
                scope.ServiceProvider);
            var result = await handler.Descriptor.InvokeAsync(
                scope.ServiceProvider,
                lease.Payload,
                context,
                deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();

            if (result is HttpFunctionInvocationResult http)
            {
                if (http.StatusCode is < 100 or > 599)
                    throw new PoleFunctionException("invalid_http_status", "The HTTP status code is invalid.", retryable: false);
                if (http.Body is { Length: var length } && length > options.Value.MaxResponseBodyBytes)
                    throw new PoleFunctionException("response_body_too_large", "The response body exceeds the configured limit.", retryable: false);
                if (!FunctionPayloadLimits.AreHeadersValid(http.Headers, options.Value))
                    throw new PoleFunctionException("response_headers_too_large", "The response headers exceed the configured limit.", retryable: false);
            }

            store.Complete(
                lease.Identity.AttemptId,
                FunctionAttemptOutcome.Succeeded,
                result,
                failure: null);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            store.Complete(
                lease.Identity.AttemptId,
                FunctionAttemptOutcome.TimedOut,
                result: null,
                new FunctionAttemptFailure(
                    "deadline_exceeded",
                    "The invocation deadline elapsed.",
                    FunctionFailureOrigin.Runtime));
        }
        catch (PoleFunctionException exception)
        {
            store.Complete(
                lease.Identity.AttemptId,
                exception.Retryable
                    ? FunctionAttemptOutcome.RetryableFailure
                    : FunctionAttemptOutcome.PermanentFailure,
                result: null,
                new FunctionAttemptFailure(
                    exception.Code,
                    SanitizeMessage(exception.Message),
                    FunctionFailureOrigin.User));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[pole-function-handler-failed] Handler {HandlerName} failed for attempt {AttemptId}",
                lease.Identity.HandlerName,
                lease.Identity.AttemptId);
            store.Complete(
                lease.Identity.AttemptId,
                FunctionAttemptOutcome.RetryableFailure,
                result: null,
                new FunctionAttemptFailure(
                    "handler_exception",
                    "The Function handler failed.",
                    FunctionFailureOrigin.User));
        }
    }

    private static string SanitizeMessage(string message) =>
        message.Length <= 512 ? message : message[..512];
}
