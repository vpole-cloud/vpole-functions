using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

public enum PoleFunctionAgentOperation
{
    Metadata,
    ReserveAttempt,
    ExecuteAttempt,
    ObserveAttempt,
    AgentStatus
}

public sealed record PoleFunctionAgentAuthorizationContext(
    PoleFunctionAgentOperation Operation,
    string? AttemptId = null,
    FunctionAttemptIdentity? Attempt = null,
    bool IsPreflight = false);

public interface IPoleFunctionAgentAuthenticator
{
    ValueTask<bool> AuthorizeAsync(
        HttpContext httpContext,
        PoleFunctionAgentAuthorizationContext authorizationContext,
        CancellationToken cancellationToken);
}

internal sealed class PoleFunctionJwtAgentAuthenticator : IPoleFunctionAgentAuthenticator, IDisposable
{
    private readonly PoleFunctionsOptions options;
    private readonly JsonWebTokenHandler tokenHandler = new();
    private readonly TokenValidationParameters? validationParameters;
    private readonly List<RSA> trustedKeys = [];

    public PoleFunctionJwtAgentAuthenticator(IOptions<PoleFunctionsOptions> options)
    {
        this.options = options.Value;
        if (!this.options.AgentAuthentication.IsConfigured)
            return;

        try
        {
            validationParameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidIssuer = this.options.AgentAuthentication.Issuer,
                ValidateAudience = true,
                ValidAudience = this.options.AgentAuthentication.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(this.options.AgentAuthentication.ClockSkewSeconds),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidTypes = [PoleFunctionAgentToken.Type],
                TryAllIssuerSigningKeys = false,
                IssuerSigningKeys = this.options.AgentAuthentication.TrustedKeys.Select(CreateKey).ToArray()
            };
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public async ValueTask<bool> AuthorizeAsync(
        HttpContext httpContext,
        PoleFunctionAgentAuthorizationContext authorizationContext,
        CancellationToken cancellationToken)
    {
        if (validationParameters is null)
            return options.AllowUnauthenticatedAgentRequests;
        if (httpContext.Request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(httpContext.Request.Headers.Authorization, out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            return false;

        var validation = await tokenHandler.ValidateTokenAsync(authorization.Parameter, validationParameters);
        return validation.IsValid && validation.ClaimsIdentity is { } identity &&
               HasClaim(identity, PoleFunctionAgentToken.Claims.TenantId, options.TenantId) &&
               HasClaim(identity, PoleFunctionAgentToken.Claims.FunctionAppId, options.FunctionAppId) &&
               HasClaim(identity, PoleFunctionAgentToken.Claims.RevisionId, options.RevisionId) &&
               HasClaim(identity, PoleFunctionAgentToken.Claims.TargetUid, options.AgentAuthentication.TargetUid) &&
               HasClaim(identity, PoleFunctionAgentToken.Claims.Operation, Operation(authorizationContext.Operation)) &&
               MatchesAttempt(identity, authorizationContext);
    }

    public void Dispose()
    {
        foreach (var key in trustedKeys)
            key.Dispose();
    }

    private SecurityKey CreateKey(PoleFunctionAgentTrustedKeyOptions configured)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(configured.SubjectPublicKeyInfoBase64), out _);
            if (rsa.KeySize < 2048)
                throw new InvalidOperationException("Function Agent trusted RSA keys must be at least 2048 bits.");
            trustedKeys.Add(rsa);
            return new RsaSecurityKey(rsa) { KeyId = configured.KeyId };
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private bool MatchesAttempt(
        ClaimsIdentity identity,
        PoleFunctionAgentAuthorizationContext authorizationContext)
    {
        if (authorizationContext.AttemptId is not null &&
            !HasClaim(identity, PoleFunctionAgentToken.Claims.AttemptId, authorizationContext.AttemptId))
            return false;
        if (authorizationContext.Attempt is not { } attempt)
            return true;

        return string.Equals(attempt.TenantId, options.TenantId, StringComparison.Ordinal) &&
               string.Equals(attempt.FunctionAppId, options.FunctionAppId, StringComparison.Ordinal) &&
               string.Equals(attempt.RevisionId, options.RevisionId, StringComparison.Ordinal) &&
               HasClaim(identity, PoleFunctionAgentToken.Claims.AttemptId, attempt.AttemptId) &&
               HasClaim(identity, PoleFunctionAgentToken.Claims.InvocationId, attempt.InvocationId) &&
               HasClaim(identity, PoleFunctionAgentToken.Claims.HandlerName, attempt.HandlerName) &&
               HasClaim(
                   identity,
                   PoleFunctionAgentToken.Claims.AttemptNumber,
                   attempt.AttemptNumber.ToString(CultureInfo.InvariantCulture));
    }

    private static bool HasClaim(ClaimsIdentity identity, string type, string expected) =>
        string.Equals(identity.FindFirst(type)?.Value, expected, StringComparison.Ordinal);

    private static string Operation(PoleFunctionAgentOperation operation) => operation switch
    {
        PoleFunctionAgentOperation.Metadata => PoleFunctionAgentToken.Operations.Metadata,
        PoleFunctionAgentOperation.ReserveAttempt => PoleFunctionAgentToken.Operations.ReserveAttempt,
        PoleFunctionAgentOperation.ExecuteAttempt => PoleFunctionAgentToken.Operations.ExecuteAttempt,
        PoleFunctionAgentOperation.ObserveAttempt => PoleFunctionAgentToken.Operations.ObserveAttempt,
        PoleFunctionAgentOperation.AgentStatus => PoleFunctionAgentToken.Operations.AgentStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };
}
