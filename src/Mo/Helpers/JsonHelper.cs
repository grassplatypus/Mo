using System.Text.Json;
using System.Text.Json.Serialization;
using Mo.Models;

namespace Mo.Helpers;

[JsonSerializable(typeof(DisplayProfile))]
[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
public partial class MoJsonContext : JsonSerializerContext;

public static class JsonHelper
{
    public static readonly JsonSerializerOptions Options = MoJsonContext.Default.Options;
}
