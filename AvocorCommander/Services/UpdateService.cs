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
    private static readonly HttpClient _download;

    static UpdateService()
    {
        _api = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("AvocorCommander/3.0");
        _api.DefaultRequestHeaders.Accept
            .Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _api.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);

        _download = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _download.DefaultRequestHeaders.UserAgent.ParseAdd("AvocorCommander/3.0");
        _download.DefaultRequestHeaders.Accept
            .Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        _download.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);
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

        // Find the exe asset
        var asset = release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (asset == null) return null;

        return new UpdateInfo
        {
            Version  = tag,
            AssetUrl = asset.Url,   // GitHub API URL — requires auth header to download
            Notes    = release.Body,
        };
    }

    /// <summary>Downloads the release asset and swaps the exe via a temp batch script.</summary>
    public async Task DownloadAndInstallAsync(UpdateInfo info, IProgress<int>? progress = null)
    {
        var appDir  = AppContext.BaseDirectory;
        var exePath = Path.Combine(appDir, "AvocorCommander.exe");
        var tmpPath = Path.Combine(Path.GetTempPath(), "AvocorCommander_update.exe");

        await DownloadAssetAsync(info.AssetUrl, tmpPath, progress);

        // Batch script: wait for this process to exit, swap exe, restart
        var batPath = Path.Combine(Path.GetTempPath(), "avocor_update.bat");
        File.WriteAllText(batPath,
            $"@echo off\r\n" +
            $"timeout /t 2 /nobreak >nul\r\n" +
            $"move /y \"{tmpPath}\" \"{exePath}\"\r\n" +
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
                                                  IProgress<int>? progress)
    {
        // GitHub private asset download requires the octet-stream Accept header + Bearer token
        var response = await _download.GetAsync(assetApiUrl,
                           HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        // Guard: reject HTML error pages
        var ct = response.Content.Headers.ContentType?.MediaType ?? "";
        if (ct.Contains("text/html") || ct.Contains("application/json"))
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"GitHub returned an unexpected response ({ct}).\n{body[..Math.Min(300, body.Length)]}");
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
        if (done < 1_000_000)
            throw new InvalidOperationException(
                $"Downloaded file is too small ({done:N0} bytes) — expected > 1 MB. " +
                "Check that the release asset was attached correctly.");
    }
}
