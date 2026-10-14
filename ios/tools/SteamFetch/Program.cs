// SteamFetch: downloads selected files of a Steam app on a build machine.
//
// Steam blocks approving a sign-in in the Steam mobile app when the build
// machine is far from the account's usual location (GitHub's runners are in
// the US/UK). Two ways around that:
//
//   SteamFetch login [--code]
//       Interactive, on your own PC. Signs in (approve in the Steam app, or
//       with --code type the Steam Guard code) and prints the account name
//       and a refresh token. Store the token as the STEAM_REFRESH_TOKEN
//       secret and builds sign in with it, with no code or approval.
//
//   SteamFetch download --user NAME --app ID --dir OUT --filelist FILE
//                       [--branch public] [--code-command PROGRAM]
//       Signs in with STEAM_REFRESH_TOKEN if set, else with STEAM_PASSWORD
//       and a Steam Guard code. The code comes from PROGRAM (run as
//       `PROGRAM <device|email:ADDRESS> <first|retry>`, prints the code), or
//       from the console without it. FILE lists depot paths, one per line;
//       "regex:<pattern>" lines match by regex. Downloads the matching files
//       of the app's Windows 64-bit depots.
//
// The depot logic follows the Android launcher's src/STS2Mobile/Steam/DepotDownloader.cs.

using System.Collections.Concurrent;
using System.Diagnostics;
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
                return await Login(useCode: args.Contains("--code"));
            if (args.Length >= 1 && args[0] == "download")
                return await Download(ParseOptions(args.Skip(1).ToArray()));
        }
        catch (AsyncJobFailedException)
        {
            Console.Error.WriteLine("ERROR: lost the connection to Steam in the middle of a request.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
        Console.Error.WriteLine(
            "usage: SteamFetch login [--code]\n"
                + "       SteamFetch download --user NAME --app ID --dir OUT --filelist FILE [--branch public]\n"
                + "                           [--code-command PROGRAM]"
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

    private static async Task<int> Login(bool useCode)
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
                Authenticator = useCode ? new CodeAuthenticator(PromptForCode) : new UserConsoleAuthenticator(),
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

    private static Task<string> PromptForCode(string kind, bool retry)
    {
        if (retry)
            Console.WriteLine("That code was wrong or expired.");
        Console.Write(
            kind.StartsWith("email:")
                ? $"Code Steam e-mailed to {kind[6..]}: "
                : "Steam Guard code (Steam app > Steam Guard): "
        );
        return Task.FromResult(Console.ReadLine()?.Trim() ?? "");
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
        var token = Environment.GetEnvironmentVariable("STEAM_REFRESH_TOKEN")?.Trim();
        var password = Environment.GetEnvironmentVariable("STEAM_PASSWORD");
        if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(password))
            throw new ArgumentException("set STEAM_REFRESH_TOKEN, or STEAM_PASSWORD");
        var filter = FileFilter.Load(o.GetValueOrDefault("filelist") ?? throw new ArgumentException("--filelist is required"));

        SteamSession session;
        if (string.IsNullOrEmpty(token))
        {
            var codeCommand = o.GetValueOrDefault("code-command");
            Func<string, bool, Task<string>> getCode = codeCommand != null
                ? (kind, retry) => RunCodeCommand(codeCommand, kind, retry)
                : PromptForCode;
            (session, token, user) = await SignInWithCodeAsync(user, password!, getCode);
        }
        else
        {
            session = await SteamSession.ConnectAsync();
        }
        await using var _ = session;
        await session.LogOnAsync(user, token);
        Console.WriteLine($"Logged in as {user}");

        var downloader = new DepotFetcher(session, appId, branch, dir, filter);
        var count = await downloader.RunAsync();
        Console.WriteLine($"Downloaded {count} files to {dir}");
        return count > 0 ? 0 : 1;
    }

    // Steam closes a connection that hasn't signed in within about a minute,
    // longer than it takes someone to read the Steam app and send the code.
    // And once a sign-in starts, the Steam app shows an Approve/Deny prompt
    // over the code. So for app codes: get the code first, with no sign-in
    // under way, then connect and send it within seconds. A rejected code
    // repeats that. E-mail codes are only sent once a sign-in starts, so
    // with the answer "email" it starts one and waits for that code on it.
    private static async Task<(SteamSession, string, string)> SignInWithCodeAsync(
        string user, string password, Func<string, bool, Task<string>> getCode)
    {
        string? code = await getCode("device", false);
        // "email": no authenticator app; start a sign-in so Steam e-mails a code.
        if (string.Equals(code, "email", StringComparison.OrdinalIgnoreCase))
            code = null;
        var codeWasRejected = false;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            Console.WriteLine("Signing in with the code...");
            var session = await SteamSession.ConnectAsync();
            var needCode = false;
            var authenticator = new CodeAuthenticator(async (kind, previousWasIncorrect) =>
            {
                if (kind != "device")
                    return await getCode(kind, previousWasIncorrect);
                if (previousWasIncorrect || code == null)
                {
                    needCode = true;
                    codeWasRejected = previousWasIncorrect;
                    throw new OperationCanceledException("waiting for a Steam Guard code");
                }
                var next = code;
                code = null;
                return next;
            });
            try
            {
                var auth = await session.Client.Authentication.BeginAuthSessionViaCredentialsAsync(
                    new AuthSessionDetails
                    {
                        Username = user,
                        Password = password,
                        IsPersistentSession = false,
                        DeviceFriendlyName = "StS2 iOS build (SteamFetch)",
                        Authenticator = authenticator,
                    }
                );
                var result = await auth.PollingWaitForResultAsync();
                return (session, result.RefreshToken, result.AccountName);
            }
            catch (Exception) when (needCode)
            {
                await session.DisposeAsync();
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
            code = await getCode("device", codeWasRejected);
        }
        throw new InvalidOperationException("Steam didn't accept a code after several tries");
    }

    // Runs PROGRAM <kind> <first|retry>; its standard output is the code. Its
    // standard error goes to our log.
    private static async Task<string> RunCodeCommand(string program, string kind, bool retry)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(kind);
        psi.ArgumentList.Add(retry ? "retry" : "first");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"can't run {program}");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"no Steam Guard code ({program} exited with {process.ExitCode})");
        return output.Trim();
    }
}

// Signs in with a Steam Guard code (from the Steam app or e-mail) instead of
// waiting for approval in the Steam app, which Steam refuses for sign-ins far
// from the account's usual location.
sealed class CodeAuthenticator : IAuthenticator
{
    private readonly Func<string, bool, Task<string>> _getCode;

    public CodeAuthenticator(Func<string, bool, Task<string>> getCode) => _getCode = getCode;

    public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        _getCode("device", previousCodeWasIncorrect);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        _getCode("email:" + email, previousCodeWasIncorrect);
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
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            if (!cb.UserInitiated)
                Console.WriteLine("Steam closed the connection.");
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
