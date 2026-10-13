using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace StS2iOSProbe;

// Phase 0 of the iOS port: checks that a Godot 4.5.1 C# app built in CI
// (NativeAOT, no Mac on the user's side) installs and runs on the device, and
// records the facts the real port depends on. The most important one is whether
// the GPU can sample BC-compressed textures, which the desktop game's .pck uses.
// The report is shown on screen and saved to user://probe_report.txt, which the
// Files app can reach (On My iPhone > StS2 iOS Probe).
public partial class Probe : Control
{
    private Label _report;
    private Label _touch;
    private readonly Dictionary<int, Vector2> _points = new();
    private string _lastPointer = "none yet";

    public override void _Ready()
    {
        var scroll = new ScrollContainer();
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        scroll.OffsetLeft = 24;
        scroll.OffsetTop = 24;
        scroll.OffsetRight = -24;
        scroll.OffsetBottom = -24;
        AddChild(scroll);

        var box = new VBoxContainer();
        box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(box);

        _touch = new Label { Text = "Touch / Pencil: " + _lastPointer };
        _touch.AddThemeFontSizeOverride("font_size", 22);
        _touch.AddThemeColorOverride("font_color", new Color(0.95f, 0.77f, 0.06f));
        box.AddChild(_touch);

        _report = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _report.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _report.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_report);

        // Runtime checks that touch async + thread pool, which the game uses heavily.
        _ = RunReportAsync();
    }

    private async Task RunReportAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("StS2 iOS Probe — Phase 0");
        sb.AppendLine($"Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        Section(sb, "Device", () =>
        {
            sb.AppendLine($"OS: {OS.GetName()} {OS.GetVersion()}");
            sb.AppendLine($"Model: {OS.GetModelName()}");
            sb.AppendLine($"CPU: {OS.GetProcessorName()} x{OS.GetProcessorCount()}");
            var mem = OS.GetMemoryInfo();
            foreach (var key in mem.Keys)
                sb.AppendLine($"Memory {key}: {FormatBytes(mem[key].AsInt64())}");
            sb.AppendLine($"user:// = {OS.GetUserDataDir()}");
        });

        Section(sb, "Display", () =>
        {
            sb.AppendLine($"Screen: {DisplayServer.ScreenGetSize()} @ scale {DisplayServer.ScreenGetScale()}");
            sb.AppendLine($"DPI: {DisplayServer.ScreenGetDpi()}  Refresh: {DisplayServer.ScreenGetRefreshRate()} Hz");
            sb.AppendLine($"Window: {DisplayServer.WindowGetSize()}  Safe area: {DisplayServer.GetDisplaySafeArea()}");
        });

        Section(sb, "Rendering", () =>
        {
            sb.AppendLine($"Driver: {RenderingServer.GetCurrentRenderingDriverName()}");
            sb.AppendLine($"Method: {RenderingServer.GetCurrentRenderingMethod()}");
            sb.AppendLine($"Adapter: {RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetVideoAdapterVendor()})");
            sb.AppendLine($"API: {RenderingServer.GetVideoAdapterApiVersion()}");

            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null)
            {
                sb.AppendLine("RenderingDevice: unavailable (compatibility renderer?)");
                return;
            }

            // The desktop .pck ships BC (S3TC/BPTC) textures. If these say NO, the
            // port has to transcode textures instead of loading the .pck as-is.
            var formats = new (string Name, RenderingDevice.DataFormat Format)[]
            {
                ("BC1 (DXT1)", RenderingDevice.DataFormat.Bc1RgbaUnormBlock),
                ("BC3 (DXT5)", RenderingDevice.DataFormat.Bc3UnormBlock),
                ("BC7 (BPTC)", RenderingDevice.DataFormat.Bc7UnormBlock),
                ("ETC2", RenderingDevice.DataFormat.Etc2R8G8B8UnormBlock),
                ("ASTC 4x4", RenderingDevice.DataFormat.Astc4X4UnormBlock),
            };
            foreach (var (name, format) in formats)
            {
                bool ok = rd.TextureIsFormatSupportedForUsage(
                    format,
                    RenderingDevice.TextureUsageBits.SamplingBit
                );
                sb.AppendLine($"Texture {name}: {(ok ? "YES" : "NO")}");
            }
        });

        Section(sb, ".NET runtime", () =>
        {
            sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"RID: {RuntimeInformation.RuntimeIdentifier}");
            sb.AppendLine($"Dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported}");
            sb.AppendLine($"Dynamic code compiled: {RuntimeFeature.IsDynamicCodeCompiled}");
        });

        // StS2 discovers its content (cards, relics, ...) through reflection, so
        // check that NativeAOT kept type metadata and can instantiate by Type.
        Section(sb, "Reflection", () =>
        {
            var types = typeof(Probe).Assembly.GetTypes();
            var sample = types
                .Where(t => typeof(ProbeSample).IsAssignableFrom(t) && !t.IsAbstract)
                .Select(t => (ProbeSample)Activator.CreateInstance(t))
                .Select(s => s.Name)
                .ToList();
            sb.AppendLine($"Types in assembly: {types.Length}");
            sb.AppendLine($"Activator.CreateInstance: {string.Join(", ", sample)}");
            var prop = typeof(ProbeSampleA).GetProperty(
                nameof(ProbeSample.Name),
                BindingFlags.Public | BindingFlags.Instance
            );
            sb.AppendLine($"PropertyInfo.GetValue: {prop?.GetValue(new ProbeSampleA())}");
        });

        Section(sb, "Globalization", () =>
        {
            foreach (var name in new[] { "en-US", "ko-KR", "id-ID", "zh-CN", "ja-JP" })
            {
                var ci = new CultureInfo(name);
                sb.AppendLine($"{name}: {ci.NativeName} | {1234567.89.ToString("N", ci)}");
            }
        });

        try
        {
            int fromPool = await Task.Run(() => System.Environment.CurrentManagedThreadId);
            sb.AppendLine($"[Threads] Task.Run OK (worker thread {fromPool})");
        }
        catch (Exception e)
        {
            sb.AppendLine($"[Threads] FAILED: {e.GetType().Name}: {e.Message}");
        }

        string text = sb.ToString();
        GD.Print(text);
        Callable.From(() => ShowReport(text)).CallDeferred();
    }

    private void ShowReport(string text)
    {
        _report.Text = text + "\nSaved to: " + SaveReport(text);
    }

    private static string SaveReport(string text)
    {
        const string path = "user://probe_report.txt";
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null)
            return $"(save failed: {FileAccess.GetOpenError()})";
        file.StoreString(text);
        return ProjectSettings.GlobalizePath(path);
    }

    private static void Section(StringBuilder sb, string title, Action body)
    {
        sb.AppendLine($"[{title}]");
        try
        {
            body();
        }
        catch (Exception e)
        {
            sb.AppendLine($"FAILED: {e.GetType().Name}: {e.Message}");
        }
        sb.AppendLine();
    }

    private static string FormatBytes(long bytes) =>
        bytes < 0 ? "n/a" : $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventScreenTouch t:
                if (t.Pressed)
                    _points[t.Index] = t.Position;
                else
                    _points.Remove(t.Index);
                _lastPointer = $"touch #{t.Index} {(t.Pressed ? "down" : "up")} at {t.Position}";
                break;
            case InputEventScreenDrag d:
                _points[d.Index] = d.Position;
                _lastPointer = $"drag #{d.Index} at {d.Position} pressure {d.Pressure:0.00}";
                break;
            case InputEventMouseMotion m when m.ButtonMask == 0:
                // Apple Pencil hover arrives as plain mouse motion with no button held.
                _lastPointer = $"hover at {m.Position}";
                break;
            default:
                return;
        }
        if (_touch != null)
            _touch.Text = $"Touch / Pencil: {_lastPointer}  (active: {_points.Count})";
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var p in _points.Values)
            DrawCircle(p, 36, new Color(0.75f, 0.22f, 0.17f, 0.6f));
    }
}

public abstract class ProbeSample
{
    public abstract string Name { get; }
}

public sealed class ProbeSampleA : ProbeSample
{
    public override string Name => "A";
}

public sealed class ProbeSampleB : ProbeSample
{
    public override string Name => "B";
}
