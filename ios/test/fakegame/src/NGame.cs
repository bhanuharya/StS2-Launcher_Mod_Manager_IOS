using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Saves;

namespace MegaCrit.Sts2.Core.Nodes;

// Runs each thing the iOS patches change and reports it. Unpatched (desktop)
// behaviour fails every check on purpose.
public partial class NGame : Node
{
    private const string AtlasJsonText =
        "{\"textures\":[{\"image\":\"atlas_0.png\",\"size\":{\"w\":128,\"h\":128},\"sprites\":["
        + "{\"filename\":\"a.png\",\"region\":{\"x\":0,\"y\":0,\"w\":32,\"h\":32}},"
        + "{\"filename\":\"b.png\",\"region\":{\"x\":32,\"y\":0,\"w\":64,\"h\":64}}]}]}";

    private bool _ok = true;

    public override async void _Ready()
    {
        try
        {
            bool platform = await InitializePlatform();
            Check("platform", platform, $"InitializePlatform={platform}");
        }
        catch (Exception e)
        {
            Check("platform", false, e.Message);
        }

        Run("debugInfo", () => OsDebugInfo.LogSystemInfo());
        Run("sentry", () => SentryService.Initialize());
        Check("uploadData", !new PrefsSave().UploadData, "UploadData=false");

        var io = new GodotFileIo();
        io.CreateDirectory(".");
        io.CreateDirectory("saves");
        Check("createDirectory", io.Created.SequenceEqual(new[] { "user://saves" }), string.Join(",", io.Created));

        try
        {
            bool steam = Steamworks.SteamAPI.Init();
            Check("steamStub", steam, $"SteamAPI.Init={steam}");
        }
        catch (Exception e)
        {
            Check("steamStub", false, $"{e.GetType().Name}: {e.Message}");
        }

        try
        {
            var sheet = AtlasManager.Load(AtlasJsonText);
            var sprites = sheet.Textures[0].Sprites;
            Check("atlasJson", sprites.Count == 2 && sprites[1].Region.W == 64, $"{sprites.Count} sprites");
        }
        catch (Exception e)
        {
            Check("atlasJson", false, $"{e.GetType().Name}: {e.Message}");
        }

        GD.Print($"FAKEGAME RESULT {(_ok ? "PASS" : "FAIL")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private async Task<bool> InitializePlatform()
    {
        await Task.Yield();
        throw new InvalidOperationException("Steam is not running");
    }

    private void Run(string name, Action action)
    {
        try
        {
            action();
            Check(name, true, "skipped");
        }
        catch (Exception e)
        {
            Check(name, false, e.Message);
        }
    }

    private void Check(string name, bool pass, string detail)
    {
        GD.Print($"FAKEGAME {name}: {(pass ? "ok" : "FAIL")} ({detail})");
        _ok &= pass;
    }
}
