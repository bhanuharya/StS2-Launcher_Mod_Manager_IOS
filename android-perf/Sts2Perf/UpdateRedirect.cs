using HarmonyLib;
using STS2Mobile.Steam;

namespace STS2Perf;

// The launcher's self-update compares the installed version with the latest
// GitHub release. LauncherPatcher points it at this fork's releases; this
// makes it report the perf build's own version (e.g. 0.4.2.1), since the
// version the Java side reports is the upstream one compiled into the APK.
internal static class UpdateRedirect
{
    public static void Apply(Harmony harmony)
    {
        PerfEntry.Patch(
            harmony,
            AccessTools.Method(typeof(AppUpdateChecker), "GetInstalledVersion"),
            "AppUpdateChecker.GetInstalledVersion",
            typeof(UpdateRedirect),
            postfix: nameof(InstalledVersionPostfix)
        );
    }

    private static void InstalledVersionPostfix(ref string __result)
    {
        if (__result != null)
            __result = PerfEntry.Version;
    }
}
