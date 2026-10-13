using System.Runtime.InteropServices;

namespace Steamworks;

// Same import name as the Windows build of Steamworks.NET.
public static class SteamAPI
{
    [DllImport("steam_api64", EntryPoint = "SteamAPI_Init")]
    private static extern int NativeInit();

    public static bool Init() => NativeInit() != 0;
}
