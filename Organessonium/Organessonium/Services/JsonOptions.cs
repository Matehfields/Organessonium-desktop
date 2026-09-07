using System.Text.Json;
using System.Text.Encodings.Web;

namespace Organessonium.Services;

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Web = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions WebIndented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = true
    };
}
