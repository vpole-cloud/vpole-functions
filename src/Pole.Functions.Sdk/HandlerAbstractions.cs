using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

public sealed record PoleFunctionContext(
    FunctionAttemptIdentity Attempt,
    DateTimeOffset DeadlineUtc,
    FunctionInvocationTraceContext TraceContext,
    IServiceProvider Services);

public sealed record PoleHttpRequest(
    string Method,
    string Path,
    string QueryString,
    IReadOnlyList<FunctionHttpHeader> Headers,
    ReadOnlyMemory<byte> Body);

public sealed record PoleHttpResponse(
    int StatusCode,
    IReadOnlyList<FunctionHttpHeader> Headers,
    ReadOnlyMemory<byte> Body)
{
    public static PoleHttpResponse Empty(int statusCode = StatusCodes.Status204NoContent) =>
        new(statusCode, [], ReadOnlyMemory<byte>.Empty);

    public static PoleHttpResponse Text(
        string value,
        int statusCode = StatusCodes.Status200OK,
        string contentType = "text/plain; charset=utf-8") =>
        new(
            statusCode,
            [new FunctionHttpHeader("Content-Type", [contentType])],
            System.Text.Encoding.UTF8.GetBytes(value));

    public static PoleHttpResponse Json<T>(
        T value,
        int statusCode = StatusCodes.Status200OK,
        JsonSerializerOptions? options = null) =>
        new(
            statusCode,
            [new FunctionHttpHeader("Content-Type", ["application/json; charset=utf-8"])],
            JsonSerializer.SerializeToUtf8Bytes(value, options ?? JsonSerializerOptions.Web));
}

public sealed record PoleTimerInfo(
    string TriggerId,
    int ScheduleVersion,
    string OccurrenceId,
    DateTimeOffset ScheduledForUtc,
    DateTimeOffset FiredAtUtc,
    bool IsPastDue,
    int MissedOccurrenceCount);

public delegate ValueTask<PoleHttpResponse> PoleHttpFunctionHandler(
    PoleHttpRequest request,
    PoleFunctionContext context,
    CancellationToken cancellationToken);

public delegate ValueTask PoleTimerFunctionHandler(
    PoleTimerInfo timer,
    PoleFunctionContext context,
    CancellationToken cancellationToken);

public interface IPoleHttpFunctionHandler
{
    ValueTask<PoleHttpResponse> InvokeAsync(
        PoleHttpRequest request,
        PoleFunctionContext context,
        CancellationToken cancellationToken);
}

public interface IPoleTimerFunctionHandler
{
    ValueTask InvokeAsync(
        PoleTimerInfo timer,
        PoleFunctionContext context,
        CancellationToken cancellationToken);
}

public sealed class PoleFunctionException(
    string code,
    string message,
    bool retryable = true,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
