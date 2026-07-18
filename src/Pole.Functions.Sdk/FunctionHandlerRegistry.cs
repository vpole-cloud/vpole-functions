using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

internal abstract class FunctionHandlerDescriptor(
    string name,
    FunctionTriggerKind triggerKind,
    int? maxConcurrencyPerInstance)
{
    public string Name { get; } = name;
    public FunctionTriggerKind TriggerKind { get; } = triggerKind;
    public int? ConfiguredMaxConcurrencyPerInstance { get; } = maxConcurrencyPerInstance;

    public abstract ValueTask<FunctionInvocationResult> InvokeAsync(
        IServiceProvider services,
        FunctionInvocationPayload payload,
        PoleFunctionContext context,
        CancellationToken cancellationToken);
}

internal sealed class DelegateHttpFunctionHandlerDescriptor(
    string name,
    int? maxConcurrencyPerInstance,
    PoleHttpFunctionHandler handler)
    : FunctionHandlerDescriptor(name, FunctionTriggerKind.Http, maxConcurrencyPerInstance)
{
    public override async ValueTask<FunctionInvocationResult> InvokeAsync(
        IServiceProvider services,
        FunctionInvocationPayload payload,
        PoleFunctionContext context,
        CancellationToken cancellationToken)
    {
        if (payload is not HttpFunctionInvocationPayload http)
            throw new InvalidFunctionPayloadException(Name, FunctionTriggerKind.Http);

        var response = await handler(
            new PoleHttpRequest(
                http.Method,
                http.Path,
                http.QueryString,
                http.Headers,
                http.Body ?? []),
            context,
            cancellationToken);
        return response.ToProtocolResult();
    }
}

internal sealed class ServiceHttpFunctionHandlerDescriptor<THandler>(
    string name,
    int? maxConcurrencyPerInstance)
    : FunctionHandlerDescriptor(name, FunctionTriggerKind.Http, maxConcurrencyPerInstance)
    where THandler : class, IPoleHttpFunctionHandler
{
    public override async ValueTask<FunctionInvocationResult> InvokeAsync(
        IServiceProvider services,
        FunctionInvocationPayload payload,
        PoleFunctionContext context,
        CancellationToken cancellationToken)
    {
        if (payload is not HttpFunctionInvocationPayload http)
            throw new InvalidFunctionPayloadException(Name, FunctionTriggerKind.Http);

        var handler = services.GetRequiredService<THandler>();
        var response = await handler.InvokeAsync(
            new PoleHttpRequest(
                http.Method,
                http.Path,
                http.QueryString,
                http.Headers,
                http.Body ?? []),
            context,
            cancellationToken);
        return response.ToProtocolResult();
    }
}

internal sealed class DelegateTimerFunctionHandlerDescriptor(
    string name,
    int? maxConcurrencyPerInstance,
    PoleTimerFunctionHandler handler)
    : FunctionHandlerDescriptor(name, FunctionTriggerKind.Timer, maxConcurrencyPerInstance)
{
    public override async ValueTask<FunctionInvocationResult> InvokeAsync(
        IServiceProvider services,
        FunctionInvocationPayload payload,
        PoleFunctionContext context,
        CancellationToken cancellationToken)
    {
        if (payload is not TimerFunctionInvocationPayload timer)
            throw new InvalidFunctionPayloadException(Name, FunctionTriggerKind.Timer);

        await handler(timer.ToTimerInfo(), context, cancellationToken);
        return new TimerFunctionInvocationResult();
    }
}

internal sealed class ServiceTimerFunctionHandlerDescriptor<THandler>(
    string name,
    int? maxConcurrencyPerInstance)
    : FunctionHandlerDescriptor(name, FunctionTriggerKind.Timer, maxConcurrencyPerInstance)
    where THandler : class, IPoleTimerFunctionHandler
{
    public override async ValueTask<FunctionInvocationResult> InvokeAsync(
        IServiceProvider services,
        FunctionInvocationPayload payload,
        PoleFunctionContext context,
        CancellationToken cancellationToken)
    {
        if (payload is not TimerFunctionInvocationPayload timer)
            throw new InvalidFunctionPayloadException(Name, FunctionTriggerKind.Timer);

        var handler = services.GetRequiredService<THandler>();
        await handler.InvokeAsync(timer.ToTimerInfo(), context, cancellationToken);
        return new TimerFunctionInvocationResult();
    }
}

internal sealed record RegisteredFunctionHandler(
    FunctionHandlerDescriptor Descriptor,
    int MaxConcurrencyPerInstance);

internal sealed class FunctionHandlerRegistry
{
    private static readonly Regex HandlerNamePattern = new(
        "^[a-z][a-z0-9-]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly IReadOnlyDictionary<string, RegisteredFunctionHandler> handlers;

    public FunctionHandlerRegistry(
        IEnumerable<FunctionHandlerDescriptor> descriptors,
        IOptions<PoleFunctionsOptions> options)
    {
        var configured = descriptors.ToList();
        if (configured.Count == 0)
            throw new InvalidOperationException("At least one Pole Function handler must be registered.");

        var duplicate = configured
            .GroupBy(handler => handler.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Function handler '{duplicate.Key}' is registered more than once.");

        var result = new Dictionary<string, RegisteredFunctionHandler>(StringComparer.Ordinal);
        foreach (var descriptor in configured)
        {
            if (!HandlerNamePattern.IsMatch(descriptor.Name))
            {
                throw new InvalidOperationException(
                    $"Function handler name '{descriptor.Name}' must match {HandlerNamePattern}.");
            }

            var limit = options.Value.HandlerConcurrencyPerInstance.TryGetValue(
                descriptor.Name,
                out var configuredLimit)
                ? configuredLimit
                : descriptor.ConfiguredMaxConcurrencyPerInstance ??
                  options.Value.DefaultHandlerConcurrencyPerInstance;
            if (limit <= 0 || limit > options.Value.MaxConcurrencyPerInstance)
            {
                throw new InvalidOperationException(
                    $"Function handler '{descriptor.Name}' concurrency must be between 1 and " +
                    $"{options.Value.MaxConcurrencyPerInstance}.");
            }

            result.Add(descriptor.Name, new RegisteredFunctionHandler(descriptor, limit));
        }

        var unknownConfiguredHandler = options.Value.HandlerConcurrencyPerInstance.Keys
            .FirstOrDefault(handlerName => !result.ContainsKey(handlerName));
        if (unknownConfiguredHandler is not null)
        {
            throw new InvalidOperationException(
                $"Concurrency is configured for unknown Function handler '{unknownConfiguredHandler}'.");
        }

        handlers = result;
        All = result.Values
            .OrderBy(handler => handler.Descriptor.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<RegisteredFunctionHandler> All { get; }

    public bool TryGet(string name, out RegisteredFunctionHandler handler) =>
        handlers.TryGetValue(name, out handler!);
}

internal sealed class InvalidFunctionPayloadException(
    string handlerName,
    FunctionTriggerKind expectedKind)
    : Exception($"Handler '{handlerName}' requires a {expectedKind} payload.");

internal static class FunctionHandlerMappingExtensions
{
    public static HttpFunctionInvocationResult ToProtocolResult(this PoleHttpResponse response) =>
        new(
            response.StatusCode,
            response.Headers,
            response.Body.IsEmpty ? null : response.Body.ToArray());

    public static PoleTimerInfo ToTimerInfo(this TimerFunctionInvocationPayload timer) =>
        new(
            timer.TriggerId,
            timer.ScheduleVersion,
            timer.OccurrenceId,
            timer.ScheduledForUtc,
            timer.FiredAtUtc,
            timer.IsPastDue,
            timer.MissedOccurrenceCount);
}
