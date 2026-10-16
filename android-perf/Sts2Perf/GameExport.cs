using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Godot;
using STS2Mobile;

namespace STS2Perf;

// export=at_menu (Perf settings → Export game files (once)): once the game
// is up, writes StS2LauncherMM/Export/sts2-analysis-<time>.zip with what it
// takes to read the game's own code and scenes, so slow parts can be found
// in the code instead of guessed at from outside:
//
//  - sts2.dll: the game's C# code, this device's own copy;
//  - every scene and resource in the game's package as text: each node's
//    type, properties, script and signal connections, sub-resources one level
//    deep (materials, particle settings), from Godot's SceneState;
//  - every shader's source.
//
// No pictures, sounds or fonts. The zip stays on the device until you share
// it. Runs a few files per frame (FrameBudgetMs), with a line on screen
// showing progress; loading every scene once takes a few minutes, best done
// at the main menu. Switches itself back off when it starts, so a crash
// can't make it run again on every launch.
internal static class GameExport
{
    private const double FrameBudgetMs = 12;
    private const int MaxValueLength = 400;
    private static readonly string[] SceneExtensions = { ".tscn", ".scn" };
    private static readonly string[] ResourceExtensions = { ".tres", ".res" };
    private static readonly string[] ShaderExtensions = { ".gdshader", ".gdshaderinc" };

    private static bool _running;
    private static List<string> _todo;
    private static int _index;
    private static int _failed;
    private static ZipArchive _zip;
    private static string _zipPath;
    private static Label _status;
    private static readonly Stopwatch Wall = new();

    // From PerfRuntime.ApplyAll once the game's UI is up.
    public static void MaybeStart()
    {
        if (_running || PerfSettings.Value("export") != "at_menu" || Engine.GetMainLoop() is not SceneTree tree)
            return;
        PerfSettings.Set("export", "off");
        _running = true;
        try
        {
            var dir = Path.Combine(AppPaths.ExternalRoot, "Export");
            Directory.CreateDirectory(dir);
            _zipPath = Path.Combine(dir, $"sts2-analysis-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            _zip = ZipFile.Open(_zipPath, ZipArchiveMode.Create);
            AddGameCode();
            _todo = new List<string>();
            Collect("res://", _todo);
            _index = 0;
            _failed = 0;
            Wall.Restart();
            ShowStatus(tree, $"Exporting game files for analysis: 0 / {_todo.Count}");
            tree.ProcessFrame += OnFrame;
            PerfEntry.Log($"export: {_todo.Count} scenes, resources and shaders to {_zipPath}");
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"export failed: {ex.Message}");
            Close(tree, $"Export failed: {ex.Message}");
        }
    }

    private static void AddGameCode()
    {
        var game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "sts2");
        var candidates = new[]
        {
            game?.Location,
            Path.Combine(OS.GetUserDataDir(), "game", "data_sts2_windows_x86_64", "sts2.dll"),
            Path.Combine(OS.GetUserDataDir(), ".godot", "mono", "publish", "arm64", "sts2.dll"),
        };
        var dll = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (dll == null)
        {
            PerfEntry.Log("export: sts2.dll not found");
            return;
        }
        _zip.CreateEntryFromFile(dll, "sts2.dll", CompressionLevel.Optimal);
        var pdb = Path.ChangeExtension(dll, ".pdb");
        if (File.Exists(pdb))
            _zip.CreateEntryFromFile(pdb, "sts2.pdb", CompressionLevel.Optimal);
        WriteEntry(
            "export.txt",
            $"game: {game?.GetName().Version} from {dll}\nperf build: {PerfEntry.Version}\nexported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n"
        );
    }

    // Every scene, resource and shader in the package. An exported game lists
    // "x.tscn.remap" for a scene converted to binary; loading "x.tscn" follows it.
    private static void Collect(string dir, List<string> files)
    {
        foreach (var sub in DirAccess.GetDirectoriesAt(dir))
            if (!sub.StartsWith(".", StringComparison.Ordinal))
                Collect(dir.TrimEnd('/') + "/" + sub, files);
        var seen = new HashSet<string>();
        foreach (var file in DirAccess.GetFilesAt(dir))
        {
            var name = file.EndsWith(".remap", StringComparison.Ordinal) ? file[..^6] : file;
            var ext = Path.GetExtension(name);
            if ((SceneExtensions.Contains(ext) || ResourceExtensions.Contains(ext) || ShaderExtensions.Contains(ext)) && seen.Add(name))
                files.Add(dir.TrimEnd('/') + "/" + name);
        }
    }

    private static void OnFrame()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var slice = Stopwatch.StartNew();
        while (_index < _todo.Count && slice.Elapsed.TotalMilliseconds < FrameBudgetMs)
        {
            var path = _todo[_index++];
            try
            {
                Export(path);
            }
            catch (Exception ex)
            {
                _failed++;
                WriteEntry(EntryName(path) + ".error.txt", ex.ToString());
            }
            if (_index % 200 == 0)
                GC.Collect();
        }
        if (_status != null && GodotObject.IsInstanceValid(_status))
            _status.Text = $"Exporting game files for analysis: {_index} / {_todo.Count} (stay on the main menu)";
        if (_index < _todo.Count)
            return;
        tree.ProcessFrame -= OnFrame;
        _zip.Dispose();
        _zip = null;
        var mb = new FileInfo(_zipPath).Length / 1048576.0;
        var shown = _zipPath.Replace(AppPaths.ExternalRoot, "StS2LauncherMM");
        PerfEntry.Log($"export: done, {_todo.Count} files ({_failed} failed) in {Wall.Elapsed.TotalSeconds:0} s, {mb:0.0} MB: {_zipPath}");
        Close(tree, $"Saved {shown} ({mb:0.0} MB). Send that file to Claude.");
    }

    private static void Export(string path)
    {
        var ext = Path.GetExtension(path);
        if (ShaderExtensions.Contains(ext))
        {
            WriteEntry(EntryName(path), Godot.FileAccess.GetFileAsString(path));
            return;
        }
        var resource = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Reuse);
        if (resource == null)
        {
            _failed++;
            WriteEntry(EntryName(path) + ".error.txt", "didn't load");
            return;
        }
        WriteEntry(EntryName(path) + ".txt", resource is PackedScene scene ? DumpScene(scene) : DumpResource(resource));
    }

    private static string EntryName(string path) => "res/" + path["res://".Length..];

    private static string DumpScene(PackedScene scene)
    {
        var state = scene.GetState();
        var text = new StringBuilder();
        for (var i = 0; i < state.GetNodeCount(); i++)
        {
            text.Append(state.GetNodePath(i)).Append(" : ").Append(state.GetNodeType(i));
            if (state.GetNodeInstance(i) is { } instance)
                text.Append(" instance ").Append(instance.ResourcePath);
            var groups = state.GetNodeGroups(i);
            if (groups.Length > 0)
                text.Append(" groups ").Append(string.Join(",", groups));
            text.AppendLine();
            for (var p = 0; p < state.GetNodePropertyCount(i); p++)
                text.Append("    ").Append(state.GetNodePropertyName(i, p)).Append(" = ").AppendLine(Value(state.GetNodePropertyName(i, p), state.GetNodePropertyValue(i, p), 2));
        }
        for (var c = 0; c < state.GetConnectionCount(); c++)
            text.AppendLine($"signal {state.GetConnectionSource(c)}.{state.GetConnectionSignal(c)} -> {state.GetConnectionTarget(c)}.{state.GetConnectionMethod(c)}");
        return text.ToString();
    }

    private static string DumpResource(Resource resource)
    {
        var text = new StringBuilder($"{resource.GetClass()} {resource.ResourcePath}\n");
        if (Opaque(resource))
            return text.ToString();
        foreach (var (name, value) in Stored(resource))
            text.Append("    ").Append(name).Append(" = ").AppendLine(Value(name, value, 2));
        return text.ToString();
    }

    // Pictures, sounds, fonts, meshes: their type and path only.
    private static bool Opaque(Resource r) => r is Texture or AudioStream or Font or Mesh or Image;

    private static IEnumerable<(string Name, Variant Value)> Stored(GodotObject o)
    {
        foreach (var property in o.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)property["usage"].AsInt64();
            var name = property["name"].AsString();
            if ((usage & PropertyUsageFlags.Storage) == 0 || name.StartsWith("resource_", StringComparison.Ordinal))
                continue;
            yield return (name, o.Get(name));
        }
    }

    private static string Value(string name, Variant value, int depth)
    {
        if (value.VariantType == Variant.Type.Object)
        {
            if (value.AsGodotObject() is not Resource r)
                return value.AsGodotObject()?.GetClass() ?? "null";
            var path = r.ResourcePath;
            if (!string.IsNullOrEmpty(path) && !path.Contains("::"))
                return $"{r.GetClass()}(\"{path}\")";
            if (depth <= 0 || Opaque(r))
                return $"{r.GetClass()}(embedded)";
            return $"{r.GetClass()} {{ {string.Join(", ", Stored(r).Select(s => $"{s.Name}={Value(s.Name, s.Value, depth - 1)}"))} }}";
        }
        var text = value.ToString();
        // Shader code in full; anything else long (curves, arrays) cut short.
        return name == "code" || text.Length <= MaxValueLength ? text : text[..MaxValueLength] + "…";
    }

    private static void WriteEntry(string name, string content)
    {
        using var writer = new StreamWriter(_zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void ShowStatus(SceneTree tree, string text)
    {
        var layer = new CanvasLayer { Layer = 128 };
        _status = new Label { Text = text, Position = new Vector2(24, 24) };
        _status.AddThemeFontSizeOverride("font_size", 28);
        _status.AddThemeColorOverride("font_color", Colors.White);
        _status.AddThemeColorOverride("font_outline_color", Colors.Black);
        _status.AddThemeConstantOverride("outline_size", 8);
        layer.AddChild(_status);
        tree.Root.AddChild(layer);
    }

    private static void Close(SceneTree tree, string message)
    {
        _zip?.Dispose();
        _zip = null;
        _running = false;
        if (_status == null || !GodotObject.IsInstanceValid(_status))
            ShowStatus(tree, message);
        else
            _status.Text = message;
        var layer = _status.GetParent();
        tree.CreateTimer(30, processAlways: true).Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
        };
    }
}
