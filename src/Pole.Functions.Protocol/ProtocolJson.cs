using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pole.Functions.Protocol;

public static class PoleFunctionProtocolJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            MaxDepth = 32,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        if (options.Converters.Any(converter => converter is JsonStringEnumConverter))
            return;

        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
    }
}
