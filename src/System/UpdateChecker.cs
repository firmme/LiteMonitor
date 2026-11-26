using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using LiteMonitor.src.Core;
using LiteMonitor.Updates;

namespace LiteMonitor;

public static class UpdateChecker
{
    public const string RepositoryUrl = "https://github.com/firmme/LiteMonitor";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    private const string LatestReleaseApi = "https://api.github.com/repos/firmme/LiteMonitor/releases/latest";

    private static readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };
    private static readonly SemaphoreSlim CheckGate = new(1, 1);

    public static (string latest, string changelog, string releaseDate)? LatestVersionInfo { get; private set; }
    public static bool IsUpdateFound => LatestVersionInfo != null;

    public static async Task CheckAsync(bool showMessage = false)
    {
        if (!await CheckGate.WaitAsync(0)) return;
        bool chinese = LanguageManager.CurrentLang is "zh" or "zh-tw";

        try
        {
            var settings = Settings.Load();
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("LiteMonitor/" + GetCurrentVersion());
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                LatestVersionInfo = null;
                if (showMessage) Show(chinese ? "仓库尚未发布版本。" : "The repository has no published release.");
                return;
            }

            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<Release>()
                ?? throw new InvalidDataException("Empty GitHub release response.");
            if (!Version.TryParse(release.Tag?.TrimStart('v'), out var version))
                throw new InvalidDataException("Invalid GitHub release version.");

            string current = GetCurrentVersion();
            if (Version.Parse(current) >= version)
            {
                LatestVersionInfo = null;
                if (showMessage)
                    Show(chinese ? $"当前已是最新版本 v{current}。" : $"You are using the latest version, v{current}.");
                return;
            }

            string date = release.Published?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
            string notes = release.Body ?? "";
            LatestVersionInfo = (version.ToString(), notes, date);
            if (!showMessage && !settings.AutoCheckUpdate) return;

            string fileName = $"LiteMonitor_v{version}-win-x64.zip";
            string tag = Uri.EscapeDataString(release.Tag!);
            string releaseUrl = $"{ReleasesUrl}/tag/{tag}";
            string downloadUrl = $"{ReleasesUrl}/download/{tag}/{fileName}";
            bool canInstall = UpdatePackage.HasPublicKey
                && release.Assets?.Any(asset => asset.Name == fileName) == true
                && release.Assets.Any(asset => asset.Name == fileName + ".sig");

            using var dialog = new UpdateDialog(new DownloadContext
            {
                Title = chinese ? "发现新版本" : "New version",
                VersionLabel = $"LiteMonitor v{version}",
                Description = $"{date}\n\n{notes}",
                Urls = canInstall ? [downloadUrl] : [],
                ReleasePageUrl = releaseUrl,
                UpdateVersion = canInstall ? version : null,
                ActionButtonText = "Update"
            }, settings);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] {ex}");
            if (showMessage)
                Show(chinese ? "无法检查更新，请稍后重试。" : "Could not check for updates. Try again later.",
                    MessageBoxIcon.Warning);
        }
        finally
        {
            CheckGate.Release();
        }

        void Show(string message, MessageBoxIcon icon = MessageBoxIcon.Information) =>
            MessageBox.Show(message, chinese ? "检查更新" : "Check for updates", MessageBoxButtons.OK, icon);
    }

    public static string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (version ?? Application.ProductVersion).Split('+', 2)[0];
    }

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset? Published,
        [property: JsonPropertyName("assets")] Asset[]? Assets);

    private sealed record Asset([property: JsonPropertyName("name")] string? Name);
}
