using HarmonyLib;
using STS2Mobile.Steam;

namespace STS2Perf;

// Most of the launcher's Korean-only text is swapped for English at build
// time (translations.json). That only replaces literal pieces, so text whose
// word order has to change is fixed here instead.
internal static class EnglishText
{
    public static void Apply(Harmony harmony)
    {
        // "{act}막 {floor}층" (Save Manager's "Current run" row).
        PerfEntry.Patch(
            harmony,
            AccessTools.Method(typeof(SaveProgressSummary), nameof(SaveProgressSummary.FormatCurrentRun)),
            "SaveProgressSummary.FormatCurrentRun",
            typeof(EnglishText),
            prefix: nameof(FormatCurrentRunPrefix)
        );
    }

    private static bool FormatCurrentRunPrefix(SaveProgressSummary __instance, ref string __result)
    {
        __result = __instance.HasCurrentRun
            ? $"Act {__instance.CurrentRunAct}, Floor {__instance.CurrentRunFloor}"
            : "—";
        return false;
    }
}
