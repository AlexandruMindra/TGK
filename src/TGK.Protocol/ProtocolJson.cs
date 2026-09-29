using System.Text.Json;

namespace TGK.Protocol;

/// <summary>JSON settings shared by client and server: camelCase, and missing or null non-nullable members are rejected.</summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Applies the protocol settings to an existing instance, e.g. ASP.NET's <c>JsonOptions.SerializerOptions</c>.</summary>
    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
