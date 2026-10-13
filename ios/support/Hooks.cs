using System;
using System.Reflection;
using System.Threading.Tasks;
using Godot;

namespace Sts2Ios;

// Entry points that ios/tools/IlPatcher wires into sts2.dll before NativeAOT
// compiles it. iOS has no JIT, so the Android launcher's Harmony patches
// can't run there. These are the same fixes as static rewrites, kept in step
// with src/STS2Mobile/Patches/PlatformPatches.cs.
public static class Hooks
{
    private const string Tag = "[iOS]";

    // Called once during .NET startup, right after Godot registers sts2's
    // script classes and before any autoload or scene loads. The game pack is
    // already mounted by ios_boot at this point.
    //
    // Godot hasn't stored its engine-to-C# callbacks yet (that happens when
    // godotsharp_game_main_init returns), so touching a Godot object or
    // singleton here jumps through a null pointer. Only pure .NET setup and
    // GD.Print run now; anything else waits for the first frame.
    public static void OnRuntimeReady()
    {
        Note("runtime ready");
        Run("steam stub resolver", SteamStub.Install);
        Run("atlas JSON", AtlasJson.Install);
        Run("deferred startup", () => Callable.From(OnFirstFrame).CallDeferred());
    }

    private static void OnFirstFrame()
    {
        Run("diagnostics", Diagnostics.Log);
    }

    public static void Note(string message) => GD.Print($"{Tag} {message}");

    // Return values for method bodies the patcher replaces.
    public static Task CompletedTask() => Task.CompletedTask;

    public static Task<bool> TrueTask() => Task.FromResult(true);

    // GodotFileIo.CreateDirectory guard: NullPlatformUtilStrategy's constructor
    // creates ".", which resolves to a path without a Godot scheme on mobile and
    // fails. Same rule as PlatformPatches.CreateDirectoryPrefix.
    public static bool AllowCreateDirectory(object fileIo, string directoryPath)
    {
        try
        {
            var getFullPath = fileIo.GetType().GetMethod(
                "GetFullPath",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null
            );
            if (getFullPath?.Invoke(fileIo, new object[] { directoryPath }) is string fullPath)
                return fullPath.Contains("://");
        }
        catch (Exception e)
        {
            GD.PushWarning($"{Tag} CreateDirectory guard: {e.Message}");
        }
        return true;
    }

    internal static void Run(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            GD.PushError($"{Tag} {name} failed: {e}");
        }
    }
}
