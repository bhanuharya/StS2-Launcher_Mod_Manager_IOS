using System;
using System.IO;
using Godot;

namespace STS2Perf;

// The perf build's own settings, in user://sts2perf.cfg (the app's private
// files dir). Read lazily: OS.GetUserDataDir() is a Godot call and must not
// run from PerfEntry.Apply.
internal static class PerfSettings
{
    private const string FileName = "sts2perf.cfg";
    private static bool? _fastGraphics;

    // Fast graphics is the default: this build exists for lower-end devices.
    public static bool FastGraphics
    {
        get
        {
            Load();
            return _fastGraphics.Value;
        }
        set
        {
            _fastGraphics = value;
            Save();
        }
    }

    private static string FilePath => Path.Combine(OS.GetUserDataDir(), FileName);

    private static void Load()
    {
        if (_fastGraphics.HasValue)
            return;
        _fastGraphics = true;
        try
        {
            if (!File.Exists(FilePath))
                return;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim() == "graphics")
                    _fastGraphics = parts[1].Trim() != "sharp";
            }
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"settings read failed: {ex.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, $"graphics={(_fastGraphics == false ? "sharp" : "fast")}\n");
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"settings write failed: {ex.Message}");
        }
    }
}
