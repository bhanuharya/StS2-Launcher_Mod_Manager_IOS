using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using STS2Mobile;

namespace STS2Perf;

// Called by ModEntry.LoadPerfAddon() right after the launcher creates its
// Harmony instance, before any of its own patches. That is still the
// runtime's init context: only Harmony and plain .NET here. Everything that
// touches Godot objects runs later, from the hooks installed below.
public static class PerfEntry
{
    public static readonly string Version =
        typeof(PerfEntry)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PerfVersion")
            ?.Value ?? "0.0.0.0";

    private static bool _applied;

    public static void Apply()
    {
        if (_applied)
            return;
        _applied = true;

        Log($"STS2Perf {Version} loaded");
        var harmony = new Harmony("com.sts2perf");
        Run("update check", () => UpdateRedirect.Apply(harmony));
        Run("graphics toggle", () => GraphicsToggle.Apply(harmony));
        Run("render mode", () => RenderMode.Apply(harmony));
        Run("english text", () => EnglishText.Apply(harmony));
    }

    internal static void Log(string msg) => PatchHelper.Log($"[Perf] {msg}");

    private static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log($"{what} setup failed: {ex}");
        }
    }

    // Patches `target` and fails loudly, so a renamed upstream method shows
    // up in the log instead of silently doing nothing.
    internal static void Patch(
        Harmony harmony,
        MethodBase target,
        string what,
        Type hooks,
        string prefix = null,
        string postfix = null
    )
    {
        if (target == null)
            throw new MissingMethodException($"{what}: target not found");
        harmony.Patch(
            target,
            prefix: prefix != null ? new HarmonyMethod(hooks, prefix) : null,
            postfix: postfix != null ? new HarmonyMethod(hooks, postfix) : null
        );
    }
}
