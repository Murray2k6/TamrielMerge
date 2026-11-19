using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TamrielMerge.Setup;

/// <summary>
/// Nexus Mods, for requirements that are only published there (user, 2026-11-02: "add a login to nexus button on the tool
/// so it can log you in if you are missing something too").
/// <para>
/// The login is the personal API key Nexus documents for tools (https://next.nexusmods.com/settings/api-keys), checked
/// with users/validate.json. Nexus's one-click single sign-on (its SSO websocket) needs an application slug that Nexus
/// staff register and approve, which TamrielMerge doesn't have. The key is kept in TamrielMerge.settings.json sealed with
/// DPAPI for this Windows user ("nexus"), never in plain text, and is only ever sent to api.nexusmods.com: the download
/// links it gets are signed CDN links fetched without it. Nothing here prints or logs it.
/// </para>
/// <para>
/// Nexus lets only Premium members download through the API; a free account gets the mod's files page in the browser
/// and <see cref="WatchDownloads"/> imports what lands in the Downloads folder. TamrielMerge doesn't register itself
/// for nxm:// links: the user's Mod Organizer 2 owns them.
/// </para>
/// </summary>
public static class Nexus
{
    const string Api = "https://api.nexusmods.com/v1/";
    /// <summary>Where a Nexus member finds their personal API key (at the bottom of the page).</summary>
    public const string KeyPage = "https://next.nexusmods.com/settings/api-keys";
    const string KeySetting = "nexus", UserSetting = "nexusUser", PremiumSetting = "nexusPremium";
    /// <summary>Extra DPAPI entropy, so another program running as the same user can't unseal the key by just calling
    /// Unprotect on the settings value.</summary>
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TamrielMerge Nexus Mods API key");
    static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The app's version for Nexus's Application-Version header (major.minor.build of the assembly).</summary>
    public static string AppVersion => typeof(Nexus).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "1.0.0";

    /// <summary>One client for every call: a client per call leaves sockets in TIME_WAIT and can run out of them. No
    /// timeout of its own: an API call is given <see cref="ApiTimeout"/>, a download as long as it takes.</summary>
    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TamrielMerge/{AppVersion}");
        return client;
    }

    /// <summary>A Nexus member: their name and whether they are Premium (only Premium may download through the API).</summary>
    public sealed record User(string Name, bool Premium);

    /// <summary>A file of a mod on Nexus. <paramref name="Category"/> is Nexus's category name (MAIN, OPTIONAL, UPDATE,
    /// MISCELLANEOUS, OLD_VERSION), null when it gives none; <paramref name="Bytes"/> 0 when unknown.</summary>
    public sealed record ModFile(int FileId, string Name, string FileName, string Version, string? Category, long Bytes, string Description);

    /// <summary>A failure with a message fit to show the user as it is.</summary>
    public sealed class NexusException(string message, HttpStatusCode? status = null) : Exception(message)
    {
        public HttpStatusCode? Status => status;
    }

    // ---- The login ----

    /// <summary>Whether a key is stored that this Windows user can unseal.</summary>
    public static bool LoggedIn => StoredKey() is not null;

    /// <summary>The member last validated (name and Premium, kept beside the sealed key), or null when logged out.</summary>
    public static User? StoredUser =>
        LoggedIn && PluginList.Setting(UserSetting) is { Length: > 0 } name ? new User(name, PluginList.Setting(PremiumSetting) == "premium") : null;

    /// <summary>The stored key, unsealed; null when there is none or it was sealed for another Windows user or machine
    /// (a settings file copied over), which counts as logged out.</summary>
    static string? StoredKey()
    {
        if (!OperatingSystem.IsWindows() || PluginList.Setting(KeySetting) is not { Length: > 0 } stored) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>Stores a validated key, sealed for this Windows user, with the member's name and account type.</summary>
    public static void SaveLogin(string apiKey, User user)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the Nexus key is kept with Windows' data protection");
        byte[] sealedKey = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), Entropy, DataProtectionScope.CurrentUser);
        PluginList.SetSetting(KeySetting, Convert.ToBase64String(sealedKey));
        PluginList.SetSetting(UserSetting, user.Name);
        PluginList.SetSetting(PremiumSetting, user.Premium ? "premium" : "free");
    }

    /// <summary>Deletes the stored key and the member's name.</summary>
    public static void LogOut()
    {
        PluginList.SetSetting(KeySetting, null);
        PluginList.SetSetting(UserSetting, null);
        PluginList.SetSetting(PremiumSetting, null);
    }

    /// <summary>Checks a key with Nexus (users/validate.json): the member it belongs to. A wrong or revoked key is a
    /// <see cref="NexusException"/> with status 401.</summary>
    public static async Task<User> Validate(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new NexusException("Paste your Nexus API key first.", HttpStatusCode.Unauthorized);
        using var doc = await GetJson("users/validate.json", apiKey.Trim(), "the API key check", null, ct);
        var root = doc.RootElement;
        string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        if (name.Length == 0) throw new NexusException("Nexus accepted the key but didn't say whose it is: try again.");
        // Nexus has answered with "is_premium?" as well as "is_premium" over the years.
        bool premium = root.TryGetProperty("is_premium", out var p) && p.ValueKind == JsonValueKind.True
                       || root.TryGetProperty("is_premium?", out var q) && q.ValueKind == JsonValueKind.True;
        return new User(name, premium);
    }

    /// <summary>Validates the stored key again (Premium can have started or lapsed since the login) and stores what
    /// Nexus says now.</summary>
    public static async Task<User> CheckLogin(CancellationToken ct = default)
    {
        string key = StoredKey() ?? throw new NexusException("Not logged in to Nexus Mods.", HttpStatusCode.Unauthorized);
        var user = await Validate(key, ct);
        SaveLogin(key, user);
        return user;
    }

    // ---- The API ----

    /// <summary>A mod's files (games/{game}/mods/{modId}/files.json): free accounts may list them too.</summary>
    public static async Task<List<ModFile>> FilesOf(string game, int modId, CancellationToken ct = default)
    {
        using var doc = await GetJson($"games/{Uri.EscapeDataString(game)}/mods/{modId}/files.json", RequireKey(), $"mod {modId} of {game}", null, ct);
        var files = new List<ModFile>();
        if (!doc.RootElement.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) return files;
        foreach (var f in list.EnumerateArray())
        {
            string Text(string key) => f.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int id = f.TryGetProperty("file_id", out var fid) && fid.TryGetInt32(out int i) ? i : 0;
            if (id == 0) continue;
            long bytes = f.TryGetProperty("size_in_bytes", out var sb) && sb.ValueKind == JsonValueKind.Number && sb.TryGetInt64(out long b) ? b
                : f.TryGetProperty("size_kb", out var sk) && sk.ValueKind == JsonValueKind.Number && sk.TryGetInt64(out long kb) ? kb * 1024 : 0;
            files.Add(new ModFile(id, Text("name"), Text("file_name"), Text("version"), Text("category_name") is { Length: > 0 } c ? c : null, bytes, Text("description")));
        }
        return files;
    }

    /// <summary>Where a file downloads from (games/{game}/mods/{modId}/files/{fileId}/download_link.json): its servers, the
    /// preferred first. Premium only, unless <paramref name="key"/> and <paramref name="expires"/> come from an nxm:// link
    /// the member clicked on the site.</summary>
    public static async Task<List<(string Server, string Url)>> DownloadLink(string game, int modId, int fileId, string? key = null, long? expires = null, CancellationToken ct = default)
    {
        string path = $"games/{Uri.EscapeDataString(game)}/mods/{modId}/files/{fileId}/download_link.json";
        if (key is not null && expires is not null) path += $"?key={Uri.EscapeDataString(key)}&expires={expires.Value}";
        using var doc = await GetJson(path, RequireKey(), $"file {fileId} of mod {modId}",
            "Nexus lets only Premium members download through its API. With a free account, download the file from the mod's page " +
            "(Manual download): TamrielMerge imports it from your Downloads folder.", ct);
        var links = new List<(string, string)>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var l in doc.RootElement.EnumerateArray())
                if (l.TryGetProperty("URI", out var u) && u.GetString() is { Length: > 0 } url)
                    links.Add((l.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", url));
        if (links.Count == 0) throw new NexusException($"Nexus gave no download server for file {fileId} of mod {modId}: try again later.");
        return links;
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="path"/> (through path.part, so a stopped download never
    /// passes for a whole one), reporting bytes done and the total when the server gives it. The API key isn't sent:
    /// Nexus's download links are signed, and the key goes nowhere but api.nexusmods.com.</summary>
    public static async Task Download(string url, string path, IProgress<(long Done, long? Total)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string part = path + ".part";
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new NexusException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone
                    ? $"Nexus's download link for {Path.GetFileName(path)} has expired or was refused ({(int)response.StatusCode}): try again."
                    : $"Downloading {Path.GetFileName(path)} failed: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.", response.StatusCode);
            long? total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long done = 0, reported = 0;
                int read;
                progress?.Report((0, total));
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    // Every 4 MB, not every read: the app redraws on each report.
                    if (done - reported >= 4 << 20) { progress?.Report((done, total)); reported = done; }
                }
                progress?.Report((done, total));
                if (total is { } t && done != t) throw new NexusException($"Downloading {Path.GetFileName(path)} stopped at {done:N0} of {t:N0} bytes: try again.");
            }
            File.Move(part, path, overwrite: true);
        }
        catch (HttpRequestException e)
        {
            throw new NexusException($"Couldn't download {Path.GetFileName(path)} ({e.Message}): check the internet connection and try again.");
        }
        catch (IOException e) when (e is not FileNotFoundException)
        {
            throw new NexusException($"Downloading {Path.GetFileName(path)} broke off ({e.Message}): try again.");
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- Rate limits ----
    // Nexus allows each member 20,000 requests a day and, once those are used, 500 an hour; every answer carries what is
    // left. A request is only refused here when both are used up (the hourly allowance applies after the daily one).

    static readonly object limitsLock = new();
    static int? hourlyLeft, dailyLeft;
    static string? hourlyReset, dailyReset;

    /// <summary>What Nexus's last answer said is left this hour and today, null before any answer.</summary>
    public static (int? Hourly, int? Daily) RequestsLeft { get { lock (limitsLock) return (hourlyLeft, dailyLeft); } }

    static void ReadLimits(HttpResponseMessage response)
    {
        static string? Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        lock (limitsLock)
        {
            if (int.TryParse(Header(response, "x-rl-hourly-remaining"), out int h)) hourlyLeft = h;
            if (int.TryParse(Header(response, "x-rl-daily-remaining"), out int d)) dailyLeft = d;
            hourlyReset = Header(response, "x-rl-hourly-reset") ?? hourlyReset;
            dailyReset = Header(response, "x-rl-daily-reset") ?? dailyReset;
        }
    }

    static string LimitMessage()
    {
        lock (limitsLock)
        {
            string when = hourlyReset is { } hr && DateTimeOffset.TryParse(hr, out var at) ? $" after {at.ToLocalTime():HH:mm}"
                : dailyReset is { } dr && DateTimeOffset.TryParse(dr, out var day) ? $" after {day.ToLocalTime():ddd HH:mm}" : " later";
            return $"Nexus's request allowance for your account is used up for now (today's and this hour's): try again{when}.";
        }
    }

    static void CheckLimit()
    {
        lock (limitsLock)
            if (hourlyLeft is <= 0 && dailyLeft is <= 0) throw new NexusException(LimitMessage(), HttpStatusCode.TooManyRequests);
    }

    // ---- Requests ----

    static string RequireKey() => StoredKey() ?? throw new NexusException("Log in to Nexus Mods first.", HttpStatusCode.Unauthorized);

    /// <summary>A GET on the API with Nexus's headers; failures become plain messages (<paramref name="forbidden"/> for a
    /// 403). The key is a header of this request only, never a default of the shared client.</summary>
    static async Task<JsonDocument> GetJson(string path, string apiKey, string what, string? forbidden, CancellationToken ct)
    {
        CheckLimit();
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.Add("apikey", apiKey);
        request.Headers.Add("Application-Name", "TamrielMerge");
        request.Headers.Add("Application-Version", AppVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            ReadLimits(response);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, what, forbidden, NexusMessage(body));
            try { return JsonDocument.Parse(body); }
            catch (JsonException) { throw new NexusException($"Nexus Mods sent something that isn't an answer for {what}: try again later."); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NexusException($"Nexus Mods didn't answer within {ApiTimeout.TotalSeconds:0} seconds ({what}): try again later.");
        }
        catch (HttpRequestException e)
        {
            throw new NexusException($"Couldn't reach Nexus Mods ({e.Message}): check the internet connection.");
        }
    }

    /// <summary>The "message" of Nexus's error answer ({"code":401,"message":"..."}), or "".</summary>
    static string NexusMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    static NexusException Failure(HttpStatusCode status, string what, string? forbidden, string nexusSays)
    {
        string said = nexusSays.Length > 0 ? $" (Nexus: \"{nexusSays.TrimEnd('.')}\")" : "";
        return new NexusException(status switch
        {
            HttpStatusCode.Unauthorized => $"Nexus didn't accept the API key: it's mistyped or was revoked. Log in again with the key from {KeyPage}.",
            HttpStatusCode.Forbidden => forbidden ?? $"Nexus refused {what}{said}.",
            HttpStatusCode.NotFound => $"Nexus Mods has no {what}{said}: it may have been removed or hidden.",
            HttpStatusCode.TooManyRequests => LimitMessage(),
            >= HttpStatusCode.InternalServerError => $"Nexus Mods is having trouble ({(int)status}) with {what}: try again later.",
            _ => $"Nexus Mods answered {(int)status} for {what}{said}.",
        }, status);
    }

    // ---- Getting a requirement ----

    /// <summary>
    /// Premium: lists the source's mod files, downloads those it picks and still wants into tools\downloads\nexus, imports
    /// each, and stops as soon as the requirement is met. An archive is deleted once imported (what it installed stays);
    /// one whose import failed is kept, so trying again doesn't download it again. Returns what was installed.
    /// </summary>
    public static async Task<List<string>> Fetch(NexusSource source, IProgress<string>? report = null, IProgress<(string File, long Done, long? Total)>? progress = null, CancellationToken ct = default)
    {
        var files = await FilesOf(source.Game, source.ModId, ct);
        var picked = files.Where(source.Picks).ToList();
        if (picked.Count == 0) throw new NexusException($"{source.Title} on Nexus (mod {source.ModId}) has no file {source.RuleText}: its files may have been reorganised.");
        var wanted = source.Wanted?.Invoke(picked) ?? picked;
        if (wanted.Count == 0)
        {
            report?.Report($"Every file of {source.Title} {source.RuleText} is already imported: none has what is still missing.");
            return [];
        }
        long bytes = wanted.Sum(f => f.Bytes);
        report?.Report($"{wanted.Count} file{(wanted.Count == 1 ? "" : "s")} of {source.Title} to download{(bytes > 0 ? $" ({bytes / 1048576.0:N0} MB)" : "")}: {string.Join(", ", wanted.Select(f => f.Name))}");
        string dir = Path.Combine(Dependencies.Root, "downloads", "nexus", $"{source.Game}-{source.ModId}");
        var installed = new List<string>();
        foreach (var file in wanted)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(dir, SafeFileName(file.FileName.Length > 0 ? file.FileName : $"{file.FileId}.zip"));
            // A whole earlier download (kept because its import failed) is used again.
            if (!(File.Exists(path) && file.Bytes > 0 && new FileInfo(path).Length == file.Bytes))
            {
                var links = await DownloadLink(source.Game, source.ModId, file.FileId, ct: ct);
                report?.Report($"downloading {file.Name} from {links[0].Server}");
                var bar = progress is null ? null : new SyncProgress<(long Done, long? Total)>(p => progress.Report((file.Name, p.Done, p.Total ?? (file.Bytes > 0 ? file.Bytes : null))));
                await Download(links[0].Url, path, bar, ct);
            }
            report?.Report($"importing {file.Name}");
            var lines = await Task.Run(() => source.Import(path, file), ct);
            installed.AddRange(lines);
            report?.Report($"{file.Name}: {string.Join(", ", lines)}");
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (source.Ready()) break;
        }
        return installed;
    }

    /// <summary>
    /// Free accounts: watches the Downloads folder for <paramref name="howLong"/> and imports each archive of the source's
    /// mod as it lands (named "&lt;file&gt;-&lt;mod id&gt;-&lt;version&gt;-&lt;time&gt;" by Nexus, or one of
    /// <paramref name="files"/>' names), once it is whole: the same size twice running, no browser .part/.crdownload
    /// beside it, and openable on its own. Archives of the mod already there when it starts are imported too. Stops early
    /// when the requirement is met or <paramref name="ct"/> is cancelled. The user's files are only read.
    /// </summary>
    public static async Task<List<string>> WatchDownloads(NexusSource source, IReadOnlyList<ModFile>? files, TimeSpan howLong, IProgress<string>? report = null, CancellationToken ct = default, string? folder = null)
    {
        folder ??= DownloadsFolder();   // another folder only for a test: the user's Downloads stay untouched by it
        var deadline = DateTime.UtcNow + howLong;
        var names = new HashSet<string>((files ?? []).Select(f => f.FileName).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<string>();
        string marker = $"-{source.ModId}-";
        bool FromMod(string name) =>
            (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            && (name.Contains(marker, StringComparison.OrdinalIgnoreCase) || names.Contains(name));
        while (DateTime.UtcNow < deadline && !source.Ready())
        {
            ct.ThrowIfCancellationRequested();
            List<string> candidates;
            try { candidates = Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Where(f => FromMod(Path.GetFileName(f)) && !handled.Contains(f)).ToList() : []; }
            catch (IOException) { candidates = []; }
            catch (UnauthorizedAccessException) { candidates = []; }
            foreach (var path in candidates)
            {
                long length;
                try { length = new FileInfo(path).Length; } catch (IOException) { continue; }
                bool steady = length > 0 && sizes.TryGetValue(path, out long before) && before == length;
                sizes[path] = length;
                if (!steady || File.Exists(path + ".part") || File.Exists(path + ".crdownload") || !Whole(path)) continue;
                handled.Add(path);
                string name = Path.GetFileName(path);
                var file = files?.FirstOrDefault(f => f.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                report?.Report($"importing {name} from {folder}");
                try
                {
                    var lines = await Task.Run(() => source.Import(path, file), ct);
                    installed.AddRange(lines);
                    report?.Report($"{name}: {string.Join(", ", lines)}");
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NexusException or JsonException)
                {
                    report?.Report($"{name} couldn't be imported: {e.Message}");
                }
                if (source.Ready()) break;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return installed;
    }

    /// <summary>Whether nothing else has the file open for writing (a browser still finishing it).</summary>
    static bool Whole(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    /// <summary>Unpacks a downloaded archive into <paramref name="toDir"/>: a .zip with .NET's own zip reader, a .7z or
    /// .rar with the tool's 7-Zip (the installed one, else its private copy in tools\7zip, set up when first needed).</summary>
    public static void Unpack(string archive, string toDir)
    {
        Directory.CreateDirectory(toDir);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            try { ZipFile.ExtractToDirectory(archive, toDir, overwriteFiles: true); return; }
            // Some "zip" uploads are really 7z or use methods .NET can't read: 7-Zip reads those.
            catch (InvalidDataException) { }
        }
        string sevenZip;
        try { sevenZip = Dependencies.SevenZip(); }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            throw new NexusException($"{Path.GetFileName(archive)} needs 7-Zip to unpack, and it couldn't be set up ({e.Message}): install 7-Zip from 7-zip.org and try again.");
        }
        var psi = new ProcessStartInfo(sevenZip) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "x", "-y", $"-o{toDir}", archive }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new NexusException("7-Zip didn't start.");
        // Both streams read to the end, so a full pipe never stalls 7-Zip.
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(30))) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new NexusException($"7-Zip took over 30 minutes on {Path.GetFileName(archive)}."); }
        string said = (error.Result.Trim() is { Length: > 0 } err ? err : output.Result).Trim();
        if (p.ExitCode != 0) throw new InvalidDataException($"7-Zip couldn't unpack {Path.GetFileName(archive)} (exit {p.ExitCode}){(said.Length > 0 ? ": " + said.Split('\n').Last().Trim() : "")}");
    }

    // ---- The Downloads folder ----

    /// <summary>The user's Downloads folder as Windows knows it (moved to another drive, redirected), else
    /// %USERPROFILE%\Downloads.</summary>
    public static string DownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");   // FOLDERID_Downloads
            if (SHGetKnownFolderPath(in downloads, 0, IntPtr.Zero, out IntPtr path) == 0)
                try { if (Marshal.PtrToStringUni(path) is { Length: > 0 } p) return p; }
                finally { Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>IProgress that runs its handler where it is reported (Progress&lt;T&gt; would post it to whatever
    /// thread made it), for passing reports on to the caller's own progress.</summary>
    sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>
/// Where a requirement comes from on Nexus Mods: the game's domain (skyrimspecialedition), the mod's id, and the rule
/// picking its files — a regular expression on the file's name, Nexus's category ("MAIN"), or both.
/// <see cref="Wanted"/> narrows what the rule picks to what is still missing, <see cref="Import"/> installs one
/// downloaded archive (never deleting it), and <see cref="Ready"/> says whether the requirement is met.
/// </summary>
public sealed record NexusSource(string Game, int ModId, string Title)
{
    public string? NamePattern { get; init; }
    public string? Category { get; init; }
    public Func<IReadOnlyList<Nexus.ModFile>, IReadOnlyList<Nexus.ModFile>>? Wanted { get; init; }
    public required Func<string, Nexus.ModFile?, List<string>> Import { get; init; }
    public required Func<bool> Ready { get; init; }

    /// <summary>The mod's files page, where a free account downloads by hand.</summary>
    public string FilesPage => $"https://www.nexusmods.com/{Game}/mods/{ModId}?tab=files";

    public bool Picks(Nexus.ModFile f) =>
        (Category is null || string.Equals(f.Category, Category, StringComparison.OrdinalIgnoreCase))
        && (NamePattern is null || Regex.IsMatch(f.Name, NamePattern, RegexOptions.IgnoreCase) || Regex.IsMatch(f.FileName, NamePattern, RegexOptions.IgnoreCase));

    /// <summary>The rule in words, for messages ("in category MAIN").</summary>
    public string RuleText => string.Join(" and ", new[]
    {
        Category is null ? null : $"in category {Category}",
        NamePattern is null ? null : $"named like {NamePattern}",
    }.Where(s => s is not null)) is { Length: > 0 } t ? t : "at all";
}
