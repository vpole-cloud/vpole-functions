using Microsoft.Extensions.Options;

namespace Pole.Functions.Sdk;

public sealed class PoleFunctionsOptions
{
    public const string SectionName = "PoleFunctions";

    public string FunctionAppId { get; set; } = string.Empty;
    public string RevisionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public int MaxConcurrencyPerInstance { get; set; } = 16;
    public int DefaultHandlerConcurrencyPerInstance { get; set; } = 16;
    public TimeSpan ReservationLease { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan AttemptRetention { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxRetainedAttempts { get; set; } = 1_000;
    public TimeSpan MaxLongPoll { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxInvocationDuration { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxRequestBodyBytes { get; set; } = 1_048_576;
    public int MaxResponseBodyBytes { get; set; } = 1_048_576;
    public int MaxProtocolRequestBytes { get; set; } = 2_097_152;
    public int MaxHeaderCount { get; set; } = 64;
    public int MaxHeaderBytes { get; set; } = 32_768;
    public int MaxPathAndQueryBytes { get; set; } = 8_192;
    public bool AllowUnauthenticatedAgentRequests { get; set; }
    public PoleFunctionAgentAuthenticationOptions AgentAuthentication { get; set; } = new();
    public Dictionary<string, int> HandlerConcurrencyPerInstance { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class PoleFunctionsOptionsValidator : IValidateOptions<PoleFunctionsOptions>
{
    public ValidateOptionsResult Validate(string? name, PoleFunctionsOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.FunctionAppId))
            failures.Add($"{nameof(options.FunctionAppId)} is required.");
        if (string.IsNullOrWhiteSpace(options.RevisionId))
            failures.Add($"{nameof(options.RevisionId)} is required.");
        ValidateAuthentication(options, failures);
        if (options.MaxConcurrencyPerInstance <= 0)
            failures.Add($"{nameof(options.MaxConcurrencyPerInstance)} must be positive.");
        if (options.DefaultHandlerConcurrencyPerInstance <= 0 ||
            options.DefaultHandlerConcurrencyPerInstance > options.MaxConcurrencyPerInstance)
        {
            failures.Add(
                $"{nameof(options.DefaultHandlerConcurrencyPerInstance)} must be between 1 and {nameof(options.MaxConcurrencyPerInstance)}.");
        }
        if (options.ReservationLease <= TimeSpan.Zero)
            failures.Add($"{nameof(options.ReservationLease)} must be positive.");
        if (options.AttemptRetention <= TimeSpan.Zero)
            failures.Add($"{nameof(options.AttemptRetention)} must be positive.");
        if (options.MaxRetainedAttempts <= 0)
            failures.Add($"{nameof(options.MaxRetainedAttempts)} must be positive.");
        if (options.MaxLongPoll <= TimeSpan.Zero)
            failures.Add($"{nameof(options.MaxLongPoll)} must be positive.");
        if (options.MaxInvocationDuration <= TimeSpan.Zero || options.MaxInvocationDuration > TimeSpan.FromDays(1))
            failures.Add($"{nameof(options.MaxInvocationDuration)} must be positive and no greater than one day.");
        if (options.MaxRequestBodyBytes <= 0)
            failures.Add($"{nameof(options.MaxRequestBodyBytes)} must be positive.");
        if (options.MaxResponseBodyBytes <= 0)
            failures.Add($"{nameof(options.MaxResponseBodyBytes)} must be positive.");
        if (options.MaxProtocolRequestBytes < ((long)options.MaxRequestBodyBytes * 4 / 3) + 65_536)
        {
            failures.Add(
                $"{nameof(options.MaxProtocolRequestBytes)} must allow for the Base64 request body and protocol envelope.");
        }
        if (options.MaxHeaderCount <= 0)
            failures.Add($"{nameof(options.MaxHeaderCount)} must be positive.");
        if (options.MaxHeaderBytes <= 0)
            failures.Add($"{nameof(options.MaxHeaderBytes)} must be positive.");
        if (options.MaxPathAndQueryBytes <= 0)
            failures.Add($"{nameof(options.MaxPathAndQueryBytes)} must be positive.");
        if (options.AttemptRetention < options.MaxLongPoll)
            failures.Add($"{nameof(options.AttemptRetention)} must be greater than or equal to {nameof(options.MaxLongPoll)}.");
        if (options.MaxRetainedAttempts < options.MaxConcurrencyPerInstance)
            failures.Add($"{nameof(options.MaxRetainedAttempts)} must be greater than or equal to {nameof(options.MaxConcurrencyPerInstance)}.");
        foreach (var (handlerName, limit) in options.HandlerConcurrencyPerInstance)
        {
            if (string.IsNullOrWhiteSpace(handlerName) || limit <= 0 || limit > options.MaxConcurrencyPerInstance)
            {
                failures.Add(
                    $"{nameof(options.HandlerConcurrencyPerInstance)} entry '{handlerName}' must be between 1 and " +
                    $"{nameof(options.MaxConcurrencyPerInstance)}.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateAuthentication(PoleFunctionsOptions options, List<string> failures)
    {
        var authentication = options.AgentAuthentication;
        if (!authentication.IsConfigured)
            return;
        if (string.IsNullOrWhiteSpace(options.TenantId))
            failures.Add($"{nameof(options.TenantId)} is required when Agent authentication is configured.");
        if (string.IsNullOrWhiteSpace(authentication.Issuer))
            failures.Add($"{nameof(options.AgentAuthentication)}.{nameof(authentication.Issuer)} is required.");
        if (string.IsNullOrWhiteSpace(authentication.Audience))
            failures.Add($"{nameof(options.AgentAuthentication)}.{nameof(authentication.Audience)} is required.");
        if (string.IsNullOrWhiteSpace(authentication.TargetUid))
            failures.Add($"{nameof(options.AgentAuthentication)}.{nameof(authentication.TargetUid)} is required.");
        if (authentication.ClockSkewSeconds is < 0 or > 60)
            failures.Add($"{nameof(options.AgentAuthentication)}.{nameof(authentication.ClockSkewSeconds)} must be between 0 and 60.");
        if (authentication.TrustedKeys.Count == 0)
            failures.Add($"{nameof(options.AgentAuthentication)}.{nameof(authentication.TrustedKeys)} must contain at least one key.");
        if (authentication.TrustedKeys.Any(key =>
                string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.SubjectPublicKeyInfoBase64)))
        {
            failures.Add($"{nameof(options.AgentAuthentication)} trusted keys require a key id and public key.");
        }
    }
}

public sealed class PoleFunctionAgentAuthenticationOptions
{
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string TargetUid { get; set; } = string.Empty;
    public int ClockSkewSeconds { get; set; } = 5;
    public List<PoleFunctionAgentTrustedKeyOptions> TrustedKeys { get; set; } = [];

    internal bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Issuer) ||
        !string.IsNullOrWhiteSpace(Audience) ||
        !string.IsNullOrWhiteSpace(TargetUid) ||
        TrustedKeys.Count > 0;
}

public sealed class PoleFunctionAgentTrustedKeyOptions
{
    public string KeyId { get; set; } = string.Empty;
    public string SubjectPublicKeyInfoBase64 { get; set; } = string.Empty;
}
