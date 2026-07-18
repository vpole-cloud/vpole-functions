using System.Text.Json;
using Pole.Functions.Protocol;
using Xunit;

namespace Pole.Functions.Tests;

public sealed class ProtocolContractTests
{
    [Fact]
    public void Attempt_status_round_trips_with_string_discriminators_and_enums()
    {
        var options = PoleFunctionProtocolJson.CreateOptions();
        var completed = new FunctionAttemptStatusResponse(
            "agent-1",
            DateTimeOffset.Parse("2026-07-17T10:00:00Z"),
            new CompletedFunctionAttemptStatus(
                "attempt-1",
                DateTimeOffset.Parse("2026-07-17T09:59:58Z"),
                DateTimeOffset.Parse("2026-07-17T09:59:59Z"),
                FunctionAttemptOutcome.Succeeded,
                new HttpFunctionInvocationResult(200, [], "ok"u8.ToArray()),
                null));

        var json = JsonSerializer.Serialize(completed, options);
        var restored = JsonSerializer.Deserialize<FunctionAttemptStatusResponse>(json, options);

        Assert.Contains("\"state\":\"completed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"succeeded\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"http\"", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal(completed.AgentInstanceId, restored.AgentInstanceId);
        Assert.Equal(completed.ObservedAtUtc, restored.ObservedAtUtc);
        var restoredStatus = Assert.IsType<CompletedFunctionAttemptStatus>(restored.Status);
        Assert.Equal(FunctionAttemptOutcome.Succeeded, restoredStatus.Outcome);
        var restoredResult = Assert.IsType<HttpFunctionInvocationResult>(restoredStatus.Result);
        Assert.Equal("ok"u8.ToArray(), restoredResult.Body);
    }

    [Fact]
    public void Numeric_enums_are_rejected()
    {
        var options = PoleFunctionProtocolJson.CreateOptions();

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<FunctionHandlerMetadata>(
                """{"name":"hello","triggerKind":0,"maxConcurrencyPerInstance":1}""",
                options));
    }
}
