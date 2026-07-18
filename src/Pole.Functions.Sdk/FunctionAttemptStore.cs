using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

internal sealed record AttemptOperationError(
    string Code,
    string Message,
    int StatusCode,
    bool Retryable = false,
    TimeSpan? RetryAfter = null);

internal readonly record struct AttemptOperationResult<T>(T? Value, AttemptOperationError? Error)
    where T : class
{
    public bool IsSuccess => Error is null;

    public static AttemptOperationResult<T> Success(T value) => new(value, null);
    public static AttemptOperationResult<T> Failure(AttemptOperationError error) => new(null, error);
}

internal sealed record AttemptReservation(ReserveFunctionAttemptResponse Response);

internal sealed record StartedAttemptLease(
    FunctionAttemptIdentity Identity,
    DateTimeOffset DeadlineUtc,
    FunctionInvocationTraceContext TraceContext,
    FunctionInvocationPayload Payload,
    DateTimeOffset StartedAtUtc,
    bool ShouldExecute,
    Task<CompletedFunctionAttemptStatus> Completion);

internal sealed record AttemptObservation(
    FunctionAttemptStatus Status,
    Task<CompletedFunctionAttemptStatus>? Completion);

internal enum StoredAttemptState
{
    Reserved,
    Started,
    Completed,
    Expired
}

internal sealed class StoredFunctionAttempt(
    ReserveFunctionAttemptRequest request,
    string reservationToken,
    DateTimeOffset reservedAtUtc,
    DateTimeOffset expiresAtUtc)
{
    public ReserveFunctionAttemptRequest Request { get; } = request;
    public string ReservationToken { get; } = reservationToken;
    public DateTimeOffset ReservedAtUtc { get; } = reservedAtUtc;
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
    public StoredAttemptState State { get; set; } = StoredAttemptState.Reserved;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? PayloadFingerprint { get; set; }
    public CompletedFunctionAttemptStatus? Completion { get; set; }
    public TaskCompletionSource<CompletedFunctionAttemptStatus> CompletionSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class HandlerCapacityCounter(int limit)
{
    public int Limit { get; } = limit;
    public int Reserved { get; set; }
    public int Started { get; set; }
}

internal sealed class FunctionAttemptStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, StoredFunctionAttempt> attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandlerCapacityCounter> handlerCounters;
    private readonly FunctionHandlerRegistry handlers;
    private readonly PoleFunctionsOptions options;
    private readonly TimeProvider timeProvider;
    private readonly FunctionAgentIdentity identity;
    private readonly FunctionAgentLifecycle lifecycle;
    private readonly JsonSerializerOptions jsonOptions = PoleFunctionProtocolJson.CreateOptions();
    private int appReserved;
    private int appStarted;

    public FunctionAttemptStore(
        FunctionHandlerRegistry handlers,
        IOptions<PoleFunctionsOptions> options,
        TimeProvider timeProvider,
        FunctionAgentIdentity identity,
        FunctionAgentLifecycle lifecycle)
    {
        this.handlers = handlers;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.identity = identity;
        this.lifecycle = lifecycle;
        handlerCounters = handlers.All.ToDictionary(
            handler => handler.Descriptor.Name,
            handler => new HandlerCapacityCounter(handler.MaxConcurrencyPerInstance),
            StringComparer.Ordinal);
    }

    public AttemptOperationResult<AttemptReservation> Reserve(ReserveFunctionAttemptRequest request)
    {
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            CleanupLocked(now);

            if (lifecycle.IsDraining)
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.AgentDraining,
                    "The Function Agent is draining.",
                    StatusCodes.Status503ServiceUnavailable,
                    retryable: true);
            }

            if (!string.Equals(request.ProtocolVersion, PoleFunctionProtocol.Version, StringComparison.Ordinal))
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.UnsupportedProtocol,
                    $"Protocol version '{request.ProtocolVersion}' is not supported.",
                    StatusCodes.Status426UpgradeRequired);
            }

            if (request.Attempt is null || request.TraceContext is null ||
                string.IsNullOrWhiteSpace(request.Attempt.AttemptId) ||
                string.IsNullOrWhiteSpace(request.Attempt.InvocationId) ||
                request.Attempt.AttemptNumber <= 0 ||
                string.IsNullOrWhiteSpace(request.TraceContext.CorrelationId))
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.InvalidRequest,
                    "Attempt identity and trace context are required.",
                    StatusCodes.Status400BadRequest);
            }

            if (!string.Equals(request.Attempt.FunctionAppId, options.FunctionAppId, StringComparison.Ordinal) ||
                !string.Equals(request.Attempt.RevisionId, options.RevisionId, StringComparison.Ordinal))
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.AttemptConflict,
                    "The attempt targets a different Function App revision.",
                    StatusCodes.Status409Conflict);
            }

            if (!handlers.TryGet(request.Attempt.HandlerName, out var handler))
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.UnknownHandler,
                    $"Function handler '{request.Attempt.HandlerName}' is not registered.",
                    StatusCodes.Status404NotFound);
            }

            if (handler.Descriptor.TriggerKind != request.Attempt.TriggerKind)
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.AttemptConflict,
                    "The trigger kind does not match the registered handler.",
                    StatusCodes.Status409Conflict);
            }

            if (attempts.TryGetValue(request.Attempt.AttemptId, out var existing))
            {
                if (!ReservationMatches(existing.Request, request))
                {
                    return Failure<AttemptReservation>(
                        PoleFunctionProtocolErrors.AttemptConflict,
                        "The attempt id is already associated with a different invocation.",
                        StatusCodes.Status409Conflict);
                }

                if (existing.State != StoredAttemptState.Expired)
                {
                    return AttemptOperationResult<AttemptReservation>.Success(
                        new AttemptReservation(new ReserveFunctionAttemptResponse(
                            request.Attempt.AttemptId,
                            existing.ReservationToken,
                            existing.ReservedAtUtc,
                            existing.ExpiresAtUtc,
                            IsReplay: true)));
                }

                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.ReservationExpired,
                    "The attempt can no longer be reserved.",
                    StatusCodes.Status410Gone);
            }

            if (request.DeadlineUtc <= now)
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.InvalidRequest,
                    "The invocation deadline has already elapsed.",
                    StatusCodes.Status400BadRequest);
            }
            if (request.DeadlineUtc - now > options.MaxInvocationDuration)
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.InvalidRequest,
                    "The invocation deadline exceeds the configured maximum duration.",
                    StatusCodes.Status400BadRequest);
            }

            var handlerCounter = handlerCounters[handler.Descriptor.Name];
            if (appReserved + appStarted >= options.MaxConcurrencyPerInstance ||
                handlerCounter.Reserved + handlerCounter.Started >= handlerCounter.Limit)
            {
                return Failure<AttemptReservation>(
                    PoleFunctionProtocolErrors.CapacityExhausted,
                    "The Function Agent has no available concurrency slot.",
                    StatusCodes.Status429TooManyRequests,
                    retryable: true,
                    retryAfter: TimeSpan.FromSeconds(1));
            }

            var expiresAt = Min(now + options.ReservationLease, request.DeadlineUtc);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var attempt = new StoredFunctionAttempt(request, token, now, expiresAt);
            attempts.Add(request.Attempt.AttemptId, attempt);
            appReserved++;
            handlerCounter.Reserved++;

            return AttemptOperationResult<AttemptReservation>.Success(
                new AttemptReservation(new ReserveFunctionAttemptResponse(
                    request.Attempt.AttemptId,
                    token,
                    now,
                    expiresAt,
                    IsReplay: false)));
        }
    }

    public AttemptOperationResult<StartedAttemptLease> Start(
        string attemptId,
        ExecuteFunctionAttemptRequest request)
    {
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            CleanupLocked(now);

            if (!string.Equals(request.ProtocolVersion, PoleFunctionProtocol.Version, StringComparison.Ordinal))
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.UnsupportedProtocol,
                    $"Protocol version '{request.ProtocolVersion}' is not supported.",
                    StatusCodes.Status426UpgradeRequired);
            }

            if (!attempts.TryGetValue(attemptId, out var attempt))
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.AttemptConflict,
                    "The attempt is not known by this Agent instance.",
                    StatusCodes.Status404NotFound);
            }

            if (string.IsNullOrWhiteSpace(request.ReservationToken) ||
                !TokenMatches(attempt.ReservationToken, request.ReservationToken))
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.InvalidReservationToken,
                    "The reservation token is invalid.",
                    StatusCodes.Status403Forbidden);
            }

            if (!PayloadMatchesTrigger(request.Payload, attempt.Request.Attempt.TriggerKind))
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.InvalidPayload,
                    "The invocation payload does not match the handler trigger kind.",
                    StatusCodes.Status400BadRequest);
            }

            if (request.Payload is HttpFunctionInvocationPayload http &&
                http.Body is { Length: var bodyLength } && bodyLength > options.MaxRequestBodyBytes)
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.InvalidPayload,
                    "The invocation body exceeds the configured limit.",
                    StatusCodes.Status413PayloadTooLarge);
            }

            if (request.Payload is HttpFunctionInvocationPayload httpPayload &&
                !FunctionPayloadLimits.IsValid(httpPayload, options))
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.InvalidPayload,
                    "The HTTP invocation metadata exceeds the configured limits.",
                    StatusCodes.Status413PayloadTooLarge);
            }

            var payloadFingerprint = Fingerprint(request.Payload);
            if (attempt.PayloadFingerprint is not null &&
                !string.Equals(attempt.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.AttemptConflict,
                    "The attempt is already associated with a different payload.",
                    StatusCodes.Status409Conflict);
            }

            if (attempt.State == StoredAttemptState.Expired)
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.ReservationExpired,
                    "The attempt reservation has expired.",
                    StatusCodes.Status410Gone);
            }

            if (attempt.State == StoredAttemptState.Reserved && lifecycle.IsDraining)
            {
                return Failure<StartedAttemptLease>(
                    PoleFunctionProtocolErrors.AgentDraining,
                    "The Function Agent is draining.",
                    StatusCodes.Status503ServiceUnavailable,
                    retryable: true);
            }

            if (attempt.State is StoredAttemptState.Started or StoredAttemptState.Completed)
            {
                return AttemptOperationResult<StartedAttemptLease>.Success(
                    CreateLease(attempt, request.Payload, shouldExecute: false));
            }

            attempt.State = StoredAttemptState.Started;
            attempt.StartedAtUtc = now;
            attempt.PayloadFingerprint = payloadFingerprint;
            appReserved--;
            appStarted++;
            var counter = handlerCounters[attempt.Request.Attempt.HandlerName];
            counter.Reserved--;
            counter.Started++;

            return AttemptOperationResult<StartedAttemptLease>.Success(
                CreateLease(attempt, request.Payload, shouldExecute: true));
        }
    }

    public AttemptObservation Observe(string attemptId)
    {
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            CleanupLocked(now);
            if (!attempts.TryGetValue(attemptId, out var attempt))
                return new AttemptObservation(new UnknownFunctionAttemptStatus(attemptId), null);

            return new AttemptObservation(
                ToStatus(attempt),
                attempt.State == StoredAttemptState.Started
                    ? attempt.CompletionSource.Task
                    : null);
        }
    }

    public FunctionAgentStatusResponse GetAgentStatus()
    {
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            CleanupLocked(now);
            var recent = attempts.Values
                .OrderByDescending(attempt => attempt.CompletedAtUtc ?? attempt.StartedAtUtc ?? attempt.ReservedAtUtc)
                .Take(Math.Min(options.MaxRetainedAttempts, 100))
                .Select(ToSummaryStatus)
                .ToArray();

            return new FunctionAgentStatusResponse(
                PoleFunctionProtocol.Version,
                identity.InstanceId,
                lifecycle.IsDraining
                    ? FunctionAgentLifecycleState.Draining
                    : FunctionAgentLifecycleState.Accepting,
                ToCapacity(options.MaxConcurrencyPerInstance, appReserved, appStarted),
                handlerCounters
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new FunctionHandlerCapacityStatus(
                        item.Key,
                        ToCapacity(item.Value.Limit, item.Value.Reserved, item.Value.Started)))
                    .ToArray(),
                recent,
                attempts.Count > recent.Length,
                now);
        }
    }

    public void Complete(
        string attemptId,
        FunctionAttemptOutcome outcome,
        FunctionInvocationResult? result,
        FunctionAttemptFailure? failure)
    {
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            if (!attempts.TryGetValue(attemptId, out var attempt) ||
                attempt.State != StoredAttemptState.Started ||
                attempt.StartedAtUtc is null)
            {
                return;
            }

            var completion = new CompletedFunctionAttemptStatus(
                attemptId,
                attempt.StartedAtUtc.Value,
                now,
                outcome,
                result,
                failure);
            attempt.State = StoredAttemptState.Completed;
            attempt.CompletedAtUtc = now;
            attempt.Completion = completion;
            appStarted--;
            handlerCounters[attempt.Request.Attempt.HandlerName].Started--;
            attempt.CompletionSource.TrySetResult(completion);
            CleanupLocked(now);
        }
    }

    public void Cleanup() =>
        CleanupAt(timeProvider.GetUtcNow());

    internal void CleanupAt(DateTimeOffset now)
    {
        lock (gate)
            CleanupLocked(now);
    }

    private void CleanupLocked(DateTimeOffset now)
    {
        foreach (var attempt in attempts.Values.Where(attempt =>
                     attempt.State == StoredAttemptState.Reserved && attempt.ExpiresAtUtc <= now))
        {
            attempt.State = StoredAttemptState.Expired;
            attempt.CompletedAtUtc = now;
            appReserved--;
            handlerCounters[attempt.Request.Attempt.HandlerName].Reserved--;
        }

        var removable = attempts.Values
            .Where(attempt =>
                (attempt.State is StoredAttemptState.Completed or StoredAttemptState.Expired) &&
                attempt.CompletedAtUtc is not null &&
                attempt.CompletedAtUtc.Value + options.AttemptRetention <= now)
            .Select(attempt => attempt.Request.Attempt.AttemptId)
            .ToList();

        foreach (var attemptId in removable)
            attempts.Remove(attemptId);

        var overflow = attempts.Count - options.MaxRetainedAttempts;
        if (overflow <= 0)
            return;

        var oldestTerminal = attempts.Values
            .Where(attempt => attempt.State is StoredAttemptState.Completed or StoredAttemptState.Expired)
            .OrderBy(attempt => attempt.CompletedAtUtc)
            .Take(overflow)
            .Select(attempt => attempt.Request.Attempt.AttemptId)
            .ToArray();
        foreach (var attemptId in oldestTerminal)
            attempts.Remove(attemptId);
    }

    private StartedAttemptLease CreateLease(
        StoredFunctionAttempt attempt,
        FunctionInvocationPayload payload,
        bool shouldExecute) =>
        new(
            attempt.Request.Attempt,
            attempt.Request.DeadlineUtc,
            attempt.Request.TraceContext,
            payload,
            attempt.StartedAtUtc ?? attempt.Completion?.StartedAtUtc ?? attempt.ReservedAtUtc,
            shouldExecute,
            attempt.CompletionSource.Task);

    private FunctionAttemptStatus ToStatus(StoredFunctionAttempt attempt) =>
        attempt.State switch
        {
            StoredAttemptState.Reserved => new ReservedFunctionAttemptStatus(
                attempt.Request.Attempt.AttemptId,
                attempt.ReservedAtUtc,
                attempt.ExpiresAtUtc),
            StoredAttemptState.Started => new StartedFunctionAttemptStatus(
                attempt.Request.Attempt.AttemptId,
                attempt.StartedAtUtc!.Value),
            StoredAttemptState.Completed => attempt.Completion!,
            StoredAttemptState.Expired => new ExpiredFunctionAttemptStatus(
                attempt.Request.Attempt.AttemptId,
                attempt.CompletedAtUtc!.Value),
            _ => new UnknownFunctionAttemptStatus(attempt.Request.Attempt.AttemptId)
        };

    private FunctionAttemptStatus ToSummaryStatus(StoredFunctionAttempt attempt)
    {
        var status = ToStatus(attempt);
        return status is CompletedFunctionAttemptStatus completed
            ? completed with
            {
                Result = null,
                Failure = completed.Failure is null
                    ? null
                    : completed.Failure with { Message = string.Empty }
            }
            : status;
    }

    private static FunctionCapacityStatus ToCapacity(int limit, int reserved, int started) =>
        new(limit, reserved, started, Math.Max(0, limit - reserved - started));

    private string Fingerprint(FunctionInvocationPayload payload)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions);
        return Convert.ToHexString(SHA256.HashData(serialized));
    }

    private static bool ReservationMatches(
        ReserveFunctionAttemptRequest left,
        ReserveFunctionAttemptRequest right) =>
        left.Attempt == right.Attempt &&
        left.DeadlineUtc == right.DeadlineUtc &&
        left.TraceContext == right.TraceContext;

    private static bool PayloadMatchesTrigger(
        FunctionInvocationPayload payload,
        FunctionTriggerKind triggerKind) =>
        (triggerKind, payload) switch
        {
            (FunctionTriggerKind.Http, HttpFunctionInvocationPayload) => true,
            (FunctionTriggerKind.Timer, TimerFunctionInvocationPayload) => true,
            _ => false
        };

    private static bool TokenMatches(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static AttemptOperationResult<T> Failure<T>(
        string code,
        string message,
        int statusCode,
        bool retryable = false,
        TimeSpan? retryAfter = null)
        where T : class =>
        AttemptOperationResult<T>.Failure(
            new AttemptOperationError(code, message, statusCode, retryable, retryAfter));
}

internal sealed class FunctionAttemptCleanupService(
    FunctionAttemptStore store,
    TimeProvider timeProvider)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                store.Cleanup();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
