using System.Text.Json.Serialization;

namespace Pole.Functions.Protocol;

public sealed record FunctionAttemptIdentity(
    string TenantId,
    string FunctionAppId,
    string RevisionId,
    string HandlerName,
    FunctionTriggerKind TriggerKind,
    string InvocationId,
    string AttemptId,
    int AttemptNumber);

public sealed record FunctionInvocationTraceContext(
    string? TraceParent,
    string? TraceState,
    string CorrelationId);

public sealed record ReserveFunctionAttemptRequest(
    string ProtocolVersion,
    FunctionAttemptIdentity Attempt,
    DateTimeOffset DeadlineUtc,
    FunctionInvocationTraceContext TraceContext);

public sealed record ReserveFunctionAttemptResponse(
    string AttemptId,
    string ReservationToken,
    DateTimeOffset ReservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsReplay);

public sealed record ExecuteFunctionAttemptRequest(
    string ProtocolVersion,
    string ReservationToken,
    FunctionInvocationPayload Payload);

public sealed record StartFunctionAttemptResponse(
    string AttemptId,
    DateTimeOffset StartedAtUtc,
    bool IsReplay,
    string StatusUrl);

public sealed record FunctionProtocolError(
    string Code,
    string Message,
    bool Retryable = false);

public enum FunctionAttemptOutcome
{
    Succeeded,
    RetryableFailure,
    PermanentFailure,
    TimedOut,
    Cancelled,
    PlatformFailure
}

public enum FunctionFailureOrigin
{
    User,
    Runtime
}

public sealed record FunctionAttemptFailure(
    string Code,
    string Message,
    FunctionFailureOrigin Origin);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "state")]
[JsonDerivedType(typeof(ReservedFunctionAttemptStatus), "reserved")]
[JsonDerivedType(typeof(StartedFunctionAttemptStatus), "started")]
[JsonDerivedType(typeof(CompletedFunctionAttemptStatus), "completed")]
[JsonDerivedType(typeof(ExpiredFunctionAttemptStatus), "expired")]
[JsonDerivedType(typeof(UnknownFunctionAttemptStatus), "unknown")]
public abstract record FunctionAttemptStatus(string AttemptId);

public sealed record ReservedFunctionAttemptStatus(
    string AttemptId,
    DateTimeOffset ReservedAtUtc,
    DateTimeOffset ExpiresAtUtc)
    : FunctionAttemptStatus(AttemptId);

public sealed record StartedFunctionAttemptStatus(
    string AttemptId,
    DateTimeOffset StartedAtUtc)
    : FunctionAttemptStatus(AttemptId);

public sealed record CompletedFunctionAttemptStatus(
    string AttemptId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    FunctionAttemptOutcome Outcome,
    FunctionInvocationResult? Result,
    FunctionAttemptFailure? Failure)
    : FunctionAttemptStatus(AttemptId);

public sealed record ExpiredFunctionAttemptStatus(
    string AttemptId,
    DateTimeOffset ExpiredAtUtc)
    : FunctionAttemptStatus(AttemptId);

public sealed record UnknownFunctionAttemptStatus(string AttemptId)
    : FunctionAttemptStatus(AttemptId);

public sealed record FunctionAttemptStatusResponse(
    string AgentInstanceId,
    DateTimeOffset ObservedAtUtc,
    FunctionAttemptStatus Status);

public sealed record FunctionCapacityStatus(
    int Limit,
    int Reserved,
    int Started,
    int Available);

public sealed record FunctionHandlerCapacityStatus(
    string HandlerName,
    FunctionCapacityStatus Capacity);

public sealed record FunctionAgentStatusResponse(
    string ProtocolVersion,
    string AgentInstanceId,
    FunctionAgentLifecycleState LifecycleState,
    FunctionCapacityStatus AppCapacity,
    IReadOnlyList<FunctionHandlerCapacityStatus> Handlers,
    IReadOnlyList<FunctionAttemptStatus> RecentAttempts,
    bool RecentAttemptsTruncated,
    DateTimeOffset ObservedAtUtc);
