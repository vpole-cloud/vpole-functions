using System.Text;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

internal static class FunctionPayloadLimits
{
    public static bool IsValid(HttpFunctionInvocationPayload payload, PoleFunctionsOptions options) =>
        Encoding.UTF8.GetByteCount(payload.Path ?? string.Empty) +
        Encoding.UTF8.GetByteCount(payload.QueryString ?? string.Empty) <= options.MaxPathAndQueryBytes &&
        AreHeadersValid(payload.Headers, options);

    public static bool AreHeadersValid(
        IReadOnlyList<FunctionHttpHeader>? headers,
        PoleFunctionsOptions options)
    {
        if (headers is null || headers.Count > options.MaxHeaderCount)
            return false;

        long totalBytes = 0;
        foreach (var header in headers)
        {
            if (header is null || string.IsNullOrWhiteSpace(header.Name) || header.Values is null)
                return false;

            totalBytes += Encoding.UTF8.GetByteCount(header.Name);
            foreach (var value in header.Values)
            {
                if (value is null)
                    return false;
                totalBytes += Encoding.UTF8.GetByteCount(value);
            }

            if (totalBytes > options.MaxHeaderBytes)
                return false;
        }

        return true;
    }
}
