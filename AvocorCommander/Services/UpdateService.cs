using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvocorCommander.Services;

// ── GitHub API models ──────────────────────────────────────────────────────

file class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("body")]     public string Body    { get; set; } = "";
    [JsonPropertyName("assets")]   public List<GitHubAsset> Assets { get; set; } = [];
}

file class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")]  public string Url  { get; set; } = "";
}

// ── Public surface ─────────────────────────────────────────────────────────

public class UpdateInfo
{
    public string Version    { get; set; } = "";
    public string AssetUrl   { get; set; } = "";
    public string DbAssetUrl { get; set; } = "";   // empty = no db update in this release
    public string Notes      { get; set; } = "";
}

/// <summary>
/// Checks for updates via GitHub Releases on a private repository.
///
/// Setup (one-time):
///   1. Create a private GitHub repo (e.g. "avocor-commander-releases").
///   2. Generate a fine-grained Personal Access Token:
///        GitHub → Settings → Developer settings → Fine-grained tokens
///        → Permissions: Contents = Read-only  (on this repo only)
///   3. Fill in RepoOwner, RepoName, and Token below.
///   4. To release a new version:
///        a. Publish the project (dotnet publish …)
///        b. Go to the GitHub repo → Releases → Draft a new release
///        c. Tag: "3.0.2"  (no "v" prefix)
///        d. Attach AvocorCommander.exe as a release asset
///        e. Publish the release — users will see the prompt on next check
/// </summary>
public class UpdateService
{
    // ── Configure these ───────────────────────────────────────────────────
    private const string RepoOwner = "theshabobo";
    private const string RepoName  = "avocorcommander";
    private const string Token     = "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";
    // ─────────────────────────────────────────────────────────────────────

    private static readonly HttpClient _api;
    private static readonly HttpClient _apiAsset;  // resolves CDN redirect, no auto-follow
    private static readonly HttpClient _cdn;        // downloads from CDN, no auth

    static UpdateService()
    {
        // GitHub metadata API — JSON, follows redirects, authenticated
        _api = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("AvocorCommander/3.0");
        _api.DefaultRequestHeaders.Accept
            .Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _api.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);

        // GitHub asset API — requests the CDN redirect URL but does NOT follow it.
        // GitHub private assets redirect to a pre-signed CDN URL that must be
        // fetched without an Authorization header, so we handle the redirect manually.
        var noRedirectHandler = new HttpClientHandler { AllowAutoRedirect = false };
        _apiAsset = new HttpClient(noRedirectHandler) { Timeout = TimeSpan.FromSeconds(15) };
        _apiAsset.DefaultRequestHeaders.UserAgent.ParseAdd("AvocorCommander/3.0");
        _apiAsset.DefaultRequestHeaders.Accept
            .Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        _apiAsset.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);

        // CDN downloader — no auth, used to stream the actual file from the pre-signed URL
        _cdn = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _cdn.DefaultRequestHeaders.UserAgent.ParseAdd("AvocorCommander/3.0");
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(3, 0, 0);

    public bool IsConfigured =>
        RepoOwner != "YOUR_GITHUB_USERNAME" &&
        Token     != "YOUR_FINE_GRAINED_PAT";

    /// <summary>Returns update info if a newer release exists, null otherwise.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!IsConfigured) return null;

        var url      = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        var response = await _api.GetAsync(url);

        // 404 = no releases published yet → treat as "up to date"
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}.\n" +
                body[..Math.Min(300, body.Length)]);
        }

        var json     = await response.Content.ReadAsStringAsync();
        var release  = JsonSerializer.Deserialize<GitHubRelease>(json);
        if (release == null) return null;

        // Tag is expected to be plain "3.0.2" (or "v3.0.2" — strip the v)
        var tag = release.TagName.TrimStart('v');
        if (!Version.TryParse(tag, out var remote) || remote <= CurrentVersion)
            return null;

        // Find the exe asset (required) and optional db asset
        var exeAsset = release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var dbAsset  = release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".db",  StringComparison.OrdinalIgnoreCase));

        if (exeAsset == null) return null;

        return new UpdateInfo
        {
            Version     = tag,
            AssetUrl    = exeAsset.Url,
            DbAssetUrl  = dbAsset?.Url ?? "",
            Notes       = release.Body,
        };
    }

    /// <summary>Downloads the release assets and swaps the exe via a temp batch script.
    /// If a db asset is present it is placed as AvocorCommander_update.db next to the
    /// exe; DatabaseService will merge it on next startup.</summary>
    public async Task DownloadAndInstallAsync(UpdateInfo info, IProgress<int>? progress = null)
    {
        var appDir     = AppContext.BaseDirectory;
        var exePath    = Path.Combine(appDir, "AvocorCommander.exe");
        var tmpExePath = Path.Combine(Path.GetTempPath(), "AvocorCommander_update.exe");
        var tmpDbPath  = Path.Combine(Path.GetTempPath(), "AvocorCommander_update.db");
        var destDbPath = Path.Combine(appDir, "AvocorCommander_update.db");

        // Download exe (counts toward progress); db download is a bonus step
        await DownloadAssetAsync(info.AssetUrl, tmpExePath, progress);

        if (!string.IsNullOrEmpty(info.DbAssetUrl))
            await DownloadAssetAsync(info.DbAssetUrl, tmpDbPath, null, minSize: 1024);

        // Batch script: wait for this process to exit, swap exe, drop db update file, restart
        var batPath  = Path.Combine(Path.GetTempPath(), "avocor_update.bat");
        var dbLine   = string.IsNullOrEmpty(info.DbAssetUrl)
            ? ""
            : $"move /y \"{tmpDbPath}\" \"{destDbPath}\"\r\n";

        File.WriteAllText(batPath,
            $"@echo off\r\n" +
            $"timeout /t 2 /nobreak >nul\r\n" +
            $"move /y \"{tmpExePath}\" \"{exePath}\"\r\n" +
            dbLine +
            $"start \"\" \"{exePath}\"\r\n" +
            $"del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
        {
            CreateNoWindow  = true,
            UseShellExecute = false,
        });

        System.Windows.Application.Current.Shutdown();
    }

    private static async Task DownloadAssetAsync(string assetApiUrl, string destPath,
                                                  IProgress<int>? progress, long minSize = 1_000_000)
    {
        // ── Step 1: Resolve the CDN pre-signed URL ────────────────────────────
        // GitHub private release assets respond with a 302 redirect to a
        // pre-signed CDN URL. We request without auto-follow so we can extract
        // the Location header and download from the CDN without an auth header
        // (sending auth to the CDN causes it to reject or truncate the response).

        var redirectResp = await _apiAsset.GetAsync(assetApiUrl);

        Uri? cdnUri = redirectResp.Headers.Location;
        if (cdnUri == null)
        {
            // No redirect — older GitHub API behaviour or public repo; fall back to
            // reading the response body directly if it looks like a real file.
            cdnUri = new Uri(assetApiUrl);
        }

        // ── Step 2: Download the file from the CDN (no auth) ─────────────────

        var response = await _cdn.GetAsync(cdnUri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        // Guard: reject HTML or JSON error pages
        var ct = response.Content.Headers.ContentType?.MediaType ?? "";
        if (ct.Contains("text/html") || ct.Contains("application/json"))
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"CDN returned an unexpected response ({ct}).\n{body[..Math.Min(300, body.Length)]}");
        }

        var total  = response.Content.Headers.ContentLength ?? -1L;
        var buffer = new byte[131072];

        await using var src  = await response.Content.ReadAsStreamAsync();
        await using var dest = File.Create(destPath);

        long done = 0;
        int  read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (total > 0)
                progress?.Report((int)(done * 100 / total));
        }

        // Guard: reject suspiciously small files
        if (done < minSize)
            throw new InvalidOperationException(
                $"Downloaded file is too small ({done:N0} bytes) — expected > {minSize:N0} bytes. " +
                "Check that the correct file was attached to the GitHub release.");
    }
}
