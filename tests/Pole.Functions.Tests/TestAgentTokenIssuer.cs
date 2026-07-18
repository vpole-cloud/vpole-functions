using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pole.Functions.Protocol;

namespace Pole.Functions.Tests;

internal sealed class TestAgentTokenIssuer(
    string issuer,
    string audience,
    string keyId,
    RSA signingKey,
    TimeProvider timeProvider)
{
    private readonly JsonWebTokenHandler tokenHandler = new() { SetDefaultTimesOnTokenCreation = false };

    public string Issuer => issuer;

    public string Audience => audience;

    public string KeyId => keyId;

    public string SubjectPublicKeyInfoBase64 =>
        Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo());

    public string Issue(
        string tenantId,
        string functionAppId,
        string revisionId,
        string targetUid,
        string operation,
        FunctionAttemptIdentity? attempt = null,
        string? attemptId = null)
    {
        var now = timeProvider.GetUtcNow();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "pole-function-runtime"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(PoleFunctionAgentToken.Claims.TenantId, tenantId),
            new(PoleFunctionAgentToken.Claims.FunctionAppId, functionAppId),
            new(PoleFunctionAgentToken.Claims.RevisionId, revisionId),
            new(PoleFunctionAgentToken.Claims.TargetUid, targetUid),
            new(PoleFunctionAgentToken.Claims.Operation, operation)
        };
        AddAttemptClaims(claims, attempt, attemptId);

        var securityKey = new RsaSecurityKey(signingKey)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        return tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.AddSeconds(-5).UtcDateTime,
            Expires = now.AddMinutes(1).UtcDateTime,
            TokenType = PoleFunctionAgentToken.Type,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)
        });
    }

    private static void AddAttemptClaims(
        List<Claim> claims,
        FunctionAttemptIdentity? attempt,
        string? attemptId)
    {
        var effectiveAttemptId = attempt?.AttemptId ?? attemptId;
        if (!string.IsNullOrWhiteSpace(effectiveAttemptId))
            claims.Add(new Claim(PoleFunctionAgentToken.Claims.AttemptId, effectiveAttemptId));
        if (attempt is null)
            return;

        claims.Add(new Claim(PoleFunctionAgentToken.Claims.InvocationId, attempt.InvocationId));
        claims.Add(new Claim(PoleFunctionAgentToken.Claims.HandlerName, attempt.HandlerName));
        claims.Add(new Claim(
            PoleFunctionAgentToken.Claims.AttemptNumber,
            attempt.AttemptNumber.ToString(CultureInfo.InvariantCulture)));
    }
}
