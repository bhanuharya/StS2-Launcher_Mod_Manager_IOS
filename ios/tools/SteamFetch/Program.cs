// SteamFetch: downloads selected files of a Steam app with a saved login, so
// the iOS CI build doesn't need a fresh sign-in approval each run. Steam
// refuses to approve sign-ins from GitHub's runners (they're in another
// country from the account), but a refresh token made on your own PC works
// from anywhere.
//
//   SteamFetch login
//       Interactive. Signs in (approve in the Steam mobile app or type the
//       code) and prints the account name and refresh token. Run it on your
//       own PC and store the token as the STEAM_REFRESH_TOKEN secret.
//
//   SteamFetch download --user NAME --app ID --dir OUT --filelist FILE [--branch public]
//       Token from the STEAM_REFRESH_TOKEN environment variable. FILE lists
//       depot paths, one per line; "regex:<pattern>" lines match by regex.
//       Downloads the matching files of the app's Windows 64-bit depots.
//
// The depot logic follows the Android launcher's src/STS2Mobile/Steam/DepotDownloader.cs.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

return await SteamFetchProgram.Run(args);

static class SteamFetchProgram
{
    public static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0] == "login")
                return await Login();
            if (args.Length >= 1 && args[0] == "download")
                return await Download(ParseOptions(args.Skip(1).ToArray()));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
        Console.Error.WriteLine(
            "usage: SteamFetch login\n"
                + "       SteamFetch download --user NAME --app ID --dir OUT --filelist FILE [--branch public]"
        );
        return 2;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (var i = 0; i + 1 < args.Length; i += 2)
            options[args[i].TrimStart('-')] = args[i + 1];
        return options;
    }

    private static async Task<int> Login()
    {
        Console.Write("Steam account name: ");
        var user = Console.ReadLine()?.Trim() ?? "";
        Console.Write("Password: ");
        var password = ReadHidden();

        await using var session = await SteamSession.ConnectAsync();
        var auth = await session.Client.Authentication.BeginAuthSessionViaCredentialsAsync(
            new AuthSessionDetails
            {
                Username = user,
                Password = password,
                IsPersistentSession = true,
                DeviceFriendlyName = "StS2 iOS build (SteamFetch)",
                Authenticator = new UserConsoleAuthenticator(),
            }
        );
        var result = await auth.PollingWaitForResultAsync();

        Console.WriteLine();
        Console.WriteLine($"Signed in as: {result.AccountName}");
        Console.WriteLine("Refresh token (keep it secret; it works like your password):");
        Console.WriteLine();
        Console.WriteLine(result.RefreshToken);
        Console.WriteLine();
        Console.WriteLine("Add it as the STEAM_REFRESH_TOKEN repository secret, and set STEAM_USERNAME");
        Console.WriteLine($"to {result.AccountName}. It stays valid for months; run this again if Steam");
        Console.WriteLine("rejects it later.");
        return 0;
    }

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                    chars.RemoveAt(chars.Count - 1);
                continue;
            }
            chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    private static async Task<int> Download(Dictionary<string, string> o)
    {
        var user = o.GetValueOrDefault("user") ?? throw new ArgumentException("--user is required");
        var appId = uint.Parse(o.GetValueOrDefault("app") ?? throw new ArgumentException("--app is required"));
        var dir = o.GetValueOrDefault("dir") ?? throw new ArgumentException("--dir is required");
        var branch = o.GetValueOrDefault("branch") ?? "public";
        var token = Environment.GetEnvironmentVariable("STEAM_REFRESH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("set STEAM_REFRESH_TOKEN");
        var filter = FileFilter.Load(o.GetValueOrDefault("filelist") ?? throw new ArgumentException("--filelist is required"));

        await using var session = await SteamSession.ConnectAsync();
        await session.LogOnAsync(user, token.Trim());
        Console.WriteLine($"Logged in as {user}");

        var downloader = new DepotFetcher(session, appId, branch, dir, filter);
        var count = await downloader.RunAsync();
        Console.WriteLine($"Downloaded {count} files to {dir}");
        return count > 0 ? 0 : 1;
    }
}

// A connected SteamClient with its callback loop running on a background thread.
sealed class SteamSession : IAsyncDisposable
{
    public SteamClient Client { get; }
    public SteamUser User { get; }
    public SteamApps Apps { get; }
    public SteamContent Content { get; }

    private readonly CallbackManager _callbacks;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _pump;
    private TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<SteamUser.LoggedOnCallback> _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SteamSession()
    {
        Client = new SteamClient();
        User = Client.GetHandler<SteamUser>()!;
        Apps = Client.GetHandler<SteamApps>()!;
        Content = Client.GetHandler<SteamContent>()!;
        _callbacks = new CallbackManager(Client);
        _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult(true));
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            _connected.TrySetException(new IOException("couldn't connect to Steam"));
            _loggedOn.TrySetException(new IOException("disconnected from Steam"));
        });
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(cb => _loggedOn.TrySetResult(cb));
        _pump = new Thread(() =>
        {
            while (!_stop.IsCancellationRequested)
                _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
        })
        {
            IsBackground = true,
        };
    }

    public static async Task<SteamSession> ConnectAsync()
    {
        var session = new SteamSession();
        session._pump.Start();
        session.Client.Connect();
        await session._connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return session;
    }

    public async Task LogOnAsync(string user, string refreshToken)
    {
        User.LogOn(
            new SteamUser.LogOnDetails
            {
                Username = user,
                AccessToken = refreshToken,
                ShouldRememberPassword = true,
            }
        );
        var result = await _loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(60));
        if (result.Result != EResult.OK)
            throw new InvalidOperationException(
                $"Steam rejected the login: {result.Result}"
                    + (result.Result is EResult.AccessDenied or EResult.InvalidPassword or EResult.Expired
                        ? ". Run `SteamFetch login` again and update the STEAM_REFRESH_TOKEN secret."
                        : "")
            );
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            User.LogOff();
            Client.Disconnect();
            await Task.Delay(200);
        }
        catch { }
        _stop.Cancel();
    }
}

sealed class FileFilter
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Regex> _patterns = new();

    public static FileFilter Load(string path)
    {
        var filter = new FileFilter();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("regex:", StringComparison.Ordinal))
                filter._patterns.Add(new Regex(line[6..], RegexOptions.IgnoreCase | RegexOptions.Compiled));
            else
                filter._exact.Add(line.Replace('\\', '/'));
        }
        return filter;
    }

    public bool Matches(string depotPath)
    {
        var p = depotPath.Replace('\\', '/');
        return _exact.Contains(p) || _patterns.Any(r => r.IsMatch(p));
    }
}

sealed class DepotFetcher
{
    private const int MaxRetries = 6;
    private const int Parallel = 16;

    private readonly SteamSession _s;
    private readonly uint _appId;
    private readonly string _branch;
    private readonly string _dir;
    private readonly FileFilter _filter;
    private readonly SteamKit2.CDN.Client _cdn;
    private readonly ConcurrentDictionary<(uint, string), string> _cdnTokens = new();
    private List<Server> _servers = new();
    private int _serverIndex;

    public DepotFetcher(SteamSession s, uint appId, string branch, string dir, FileFilter filter)
    {
        _s = s;
        _appId = appId;
        _branch = branch;
        _dir = dir;
        _filter = filter;
        _cdn = new SteamKit2.CDN.Client(s.Client);
    }

    public async Task<int> RunAsync()
    {
        var tokens = await _s.Apps.PICSGetAccessTokens(new[] { _appId }, Enumerable.Empty<uint>());
        ulong appToken = 0;
        tokens.AppTokens?.TryGetValue(_appId, out appToken);
        var info = await _s.Apps.PICSGetProductInfo(
            new[] { new SteamApps.PICSRequest(_appId, appToken) },
            Enumerable.Empty<SteamApps.PICSRequest>()
        );
        var app = info.Results?.SelectMany(r => r.Apps.Values).FirstOrDefault(a => a.ID == _appId)
            ?? throw new InvalidOperationException($"no app info for {_appId} (does the account own it?)");
        var depots = app.KeyValues["depots"];
        var buildId = depots["branches"][_branch]["buildid"].Value;
        Console.WriteLine($"App {_appId}, branch {_branch}, build {buildId ?? "?"}");

        var servers = await ContentServerDirectoryService.LoadAsync(_s.Client.Configuration, CancellationToken.None);
        _servers = servers.Where(x => x.Type is "SteamCache" or "CDN").OrderBy(x => x.WeightedLoad).ToList();
        if (_servers.Count == 0)
            _servers = servers.ToList();
        if (_servers.Count == 0)
            throw new InvalidOperationException("no Steam CDN servers");

        var total = 0;
        foreach (var depot in depots.Children)
        {
            if (!uint.TryParse(depot.Name, out var depotId))
                continue;
            var config = depot["config"];
            var os = config["oslist"].Value;
            var arch = config["osarch"].Value;
            if (!string.IsNullOrEmpty(os) && !os.Contains("windows"))
                continue;
            if (!string.IsNullOrEmpty(arch) && arch != "64")
                continue;
            // Shared redistributables (DirectX, VC++) live in other apps; the
            // game's own files never come from them.
            if (depot["depotfromapp"] != KeyValue.Invalid)
                continue;

            var gid = depot["manifests"][_branch]["gid"].Value ?? depot["manifests"]["public"]["gid"].Value;
            if (gid == null || !ulong.TryParse(gid, out var manifestId))
                continue;
            try
            {
                total += await FetchDepotAsync(depotId, manifestId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Depot {depotId}: skipped ({ex.Message})");
            }
        }
        return total;
    }

    private Server NextServer() => _servers[(int)((uint)Interlocked.Increment(ref _serverIndex) % _servers.Count)];

    private async Task<string?> CdnToken(uint depotId, Server server)
    {
        if (_cdnTokens.TryGetValue((depotId, server.Host!), out var cached))
            return cached;
        var r = await _s.Content.GetCDNAuthToken(_appId, depotId, server.Host!);
        if (r.Result != EResult.OK)
            return null;
        _cdnTokens[(depotId, server.Host!)] = r.Token;
        return r.Token;
    }

    private async Task<int> FetchDepotAsync(uint depotId, ulong manifestId)
    {
        var key = await _s.Apps.GetDepotDecryptionKey(depotId, _appId);
        if (key.Result != EResult.OK)
            throw new InvalidOperationException($"no depot key ({key.Result})");
        var requestCode = await _s.Content.GetManifestRequestCode(depotId, _appId, manifestId, _branch);

        DepotManifest? manifest = null;
        for (var attempt = 0; manifest == null; attempt++)
        {
            var server = NextServer();
            try
            {
                manifest = await DownloadManifestAsync(depotId, manifestId, requestCode, server, key.DepotKey);
            }
            catch (Exception) when (attempt < MaxRetries)
            {
                await Task.Delay(500 * (attempt + 1));
            }
        }

        var files = manifest.Files!
            .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory) && _filter.Matches(f.FileName))
            .ToList();
        var bytes = files.Sum(f => (long)f.TotalSize);
        Console.WriteLine($"Depot {depotId}: {files.Count} of {manifest.Files!.Count} files, {bytes / 1048576.0:0.0} MB");

        var paths = new Dictionary<DepotManifest.FileData, string>();
        foreach (var file in files)
        {
            var path = Path.Combine(_dir, file.FileName.Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                fs.SetLength((long)file.TotalSize);
            paths[file] = path;
        }

        // All chunks of all files share one pool of downloads.
        using var gate = new SemaphoreSlim(Parallel);
        var tasks = files.SelectMany(file => file.Chunks.Select(async chunk =>
        {
            await gate.WaitAsync();
            try
            {
                var data = await FetchChunkAsync(depotId, chunk, key.DepotKey);
                lock (file)
                {
                    using var fs = new FileStream(paths[file], FileMode.Open, FileAccess.Write);
                    fs.Position = (long)chunk.Offset;
                    fs.Write(data);
                }
            }
            finally
            {
                gate.Release();
            }
        }));
        await Task.WhenAll(tasks);

        foreach (var file in files)
        {
            using (var fs = File.OpenRead(paths[file]))
            {
                var sha = SHA1.HashData(fs);
                if (file.FileHash != null && file.FileHash.Length == sha.Length && !sha.SequenceEqual(file.FileHash))
                    throw new InvalidDataException($"{file.FileName}: SHA-1 mismatch");
            }
            Console.WriteLine($"  {file.FileName} ({file.TotalSize / 1048576.0:0.0} MB)");
        }
        return files.Count;
    }

    private async Task<DepotManifest> DownloadManifestAsync(
        uint depotId, ulong manifestId, ulong requestCode, Server server, byte[] depotKey)
    {
        try
        {
            return await _cdn.DownloadManifestAsync(depotId, manifestId, requestCode, server, depotKey);
        }
        catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            var token = await CdnToken(depotId, server) ?? throw new IOException("CDN auth refused");
            return await _cdn.DownloadManifestAsync(
                depotId, manifestId, requestCode, server, depotKey, cdnAuthToken: token);
        }
    }

    private async Task<byte[]> FetchChunkAsync(uint depotId, DepotManifest.ChunkData chunk, byte[] depotKey)
    {
        var buffer = new byte[chunk.UncompressedLength];
        for (var attempt = 0; ; attempt++)
        {
            var server = NextServer();
            try
            {
                int written;
                try
                {
                    written = await _cdn.DownloadDepotChunkAsync(depotId, chunk, server, buffer, depotKey);
                }
                catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    var token = await CdnToken(depotId, server) ?? throw new IOException("CDN auth refused");
                    written = await _cdn.DownloadDepotChunkAsync(
                        depotId, chunk, server, buffer, depotKey, cdnAuthToken: token);
                }
                if (written != chunk.UncompressedLength)
                    throw new IOException($"short chunk ({written}/{chunk.UncompressedLength})");
                return buffer;
            }
            catch (Exception) when (attempt < MaxRetries)
            {
                await Task.Delay(500 * (attempt + 1));
            }
        }
    }
}
