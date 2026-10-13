using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sts2Ios;

// Steamworks.NET P/Invokes "steam_api64", which doesn't exist on iOS. The
// no-op Steam API stubs (src/stubs) are linked into the ios_boot GDExtension,
// which Godot has already loaded by now, so point every steam_api import there.
internal static class SteamStub
{
    private static readonly string[] Candidates =
    {
        "Frameworks/libios_boot.ios.framework/libios_boot.ios",
        "Frameworks/libios_boot.ios.dylib",
        "libios_boot.linux.x86_64.so", // local smoke tests on Linux
    };

    public static void Install()
    {
        NativeLibrary.SetDllImportResolver(typeof(Steamworks.SteamAPI).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("steam_api", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        // NativeAOT's AppContext.BaseDirectory can be the library's own folder
        // (Frameworks/sts2.framework), so also try the app executable's folder.
        var bases = new[]
            {
                Path.GetDirectoryName(Godot.OS.GetExecutablePath()),
                Path.GetDirectoryName(Environment.ProcessPath),
                AppContext.BaseDirectory,
            }
            .Where(b => !string.IsNullOrEmpty(b))
            .Distinct();

        foreach (var dir in bases)
        foreach (var relative in Candidates)
        {
            var path = Path.Combine(dir, relative);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                Hooks.Note($"{libraryName} -> {path}");
                return handle;
            }
        }
        Hooks.Note($"{libraryName}: no stub library found");
        return IntPtr.Zero;
    }
}
