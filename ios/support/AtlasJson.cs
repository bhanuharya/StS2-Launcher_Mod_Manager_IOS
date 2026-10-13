using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MegaCrit.Sts2.Core.Assets;

namespace Sts2Ios;

// The game reads its texture-atlas sheets with reflection-based System.Text.Json
// through AtlasManager._jsonOptions. Under NativeAOT that path needs generic
// instantiations that were never compiled, so atlas loading fails. Give those
// options a source-generated resolver for the sheet types, with the reflection
// resolver kept as a fallback for anything else.
[JsonSerializable(typeof(TpSheetData))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AtlasJsonContext : JsonSerializerContext { }

internal static class AtlasJson
{
    public static void Install()
    {
        var atlasManager = typeof(TpSheetData).Assembly.GetType("MegaCrit.Sts2.Core.Assets.AtlasManager");
        var field = atlasManager?.GetField(
            "_jsonOptions",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
        );
        if (field == null)
        {
            Hooks.Note("AtlasManager._jsonOptions not found; atlas JSON left unchanged");
            return;
        }

#pragma warning disable IL2026, IL3050 // reflection fallback is intentional (feature switch is on)
        var resolver = JsonTypeInfoResolver.Combine(AtlasJsonContext.Default, new DefaultJsonTypeInfoResolver());
#pragma warning restore IL2026, IL3050

        if (field.GetValue(null) is JsonSerializerOptions options && !options.IsReadOnly)
        {
            options.TypeInfoResolver = resolver;
            Hooks.Note("atlas JSON: source-generated resolver attached");
            return;
        }

        var replacement = field.GetValue(null) is JsonSerializerOptions existing
            ? new JsonSerializerOptions(existing)
            : new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        replacement.TypeInfoResolver = resolver;
        field.SetValue(null, replacement);
        Hooks.Note("atlas JSON: options replaced with source-generated resolver");
    }
}
