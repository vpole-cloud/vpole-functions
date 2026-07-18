using System.Text.Json.Serialization;

namespace Pole.Functions.Protocol;

public sealed record FunctionHttpHeader(
    string Name,
    IReadOnlyList<string> Values);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HttpFunctionInvocationPayload), "http")]
[JsonDerivedType(typeof(TimerFunctionInvocationPayload), "timer")]
public abstract record FunctionInvocationPayload;

public sealed record HttpFunctionInvocationPayload(
    string Method,
    string Path,
    string QueryString,
    IReadOnlyList<FunctionHttpHeader> Headers,
    byte[]? Body)
    : FunctionInvocationPayload;

public sealed record TimerFunctionInvocationPayload(
    string TriggerId,
    int ScheduleVersion,
    string OccurrenceId,
    DateTimeOffset ScheduledForUtc,
    DateTimeOffset FiredAtUtc,
    bool IsPastDue,
    int MissedOccurrenceCount)
    : FunctionInvocationPayload;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HttpFunctionInvocationResult), "http")]
[JsonDerivedType(typeof(TimerFunctionInvocationResult), "timer")]
public abstract record FunctionInvocationResult;

public sealed record HttpFunctionInvocationResult(
    int StatusCode,
    IReadOnlyList<FunctionHttpHeader> Headers,
    byte[]? Body)
    : FunctionInvocationResult;

public sealed record TimerFunctionInvocationResult : FunctionInvocationResult;
