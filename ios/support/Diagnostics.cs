using System.Runtime.InteropServices;
using Godot;

namespace Sts2Ios;

// One block of device facts at startup, so a godot.log sent back from the
// phone (Files app > game folder > logs) answers the usual first questions.
internal static class Diagnostics
{
    public static void Log()
    {
        Hooks.Note($"device: {OS.GetModelName()} {OS.GetName()} {OS.GetVersion()}, {OS.GetProcessorCount()} cores");
        Hooks.Note($".NET: {RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier}");
        Hooks.Note($"user dir: {OS.GetUserDataDir()}");

        var memory = OS.GetMemoryInfo();
        Hooks.Note(
            $"memory: physical {Gb(memory["physical"])} GB, available {Gb(memory["available"])} GB"
        );

        Hooks.Note(
            $"renderer: {RenderingServer.GetCurrentRenderingDriverName()} / {RenderingServer.GetCurrentRenderingMethod()} on {RenderingServer.GetVideoAdapterName()}"
        );
        var rd = RenderingServer.GetRenderingDevice();
        if (rd != null)
        {
            // The PC .pck ships BC (S3TC/BPTC) textures; without support Godot
            // expands them to RGBA in memory.
            bool bc7 = rd.TextureIsFormatSupportedForUsage(
                RenderingDevice.DataFormat.Bc7UnormBlock,
                RenderingDevice.TextureUsageBits.SamplingBit
            );
            Hooks.Note($"BC7 textures supported: {bc7}");
        }
    }

    private static string Gb(Variant bytes) => (bytes.AsInt64() / 1073741824.0).ToString("0.0");
}
