using System.Net;
using System.Text.Json;
using FluentAssertions;
using QuiverLauncher.Core.Models;
using QuiverLauncher.Core.Services;
using QuiverLauncher.Models;
using QuiverLauncher.Services;

namespace QuiverLauncher.Tests;

public class StartupIndexVersionTests : IDisposable
{
    private const string Url = "https://example.test/platform-index.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quiver-startup-index-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new() { AppCatalogSources = [new() { PlatformMetadataUrl = Url, Enabled = true }] };
    public StartupIndexVersionTests() { Directory.CreateDirectory(_root); PublishedPlatformCache.Initialize(_root); }
    public void Dispose() { PublishedPlatformCache.Initialize(_root); Directory.Delete(_root, true); }
    private static GameInfo App(string? pin = null) => new()
    {
        Name = "Example", Repository = "startup-index/" + Guid.NewGuid().ToString("N"),
        FolderName = Guid.NewGuid().ToString("N"), PreferredVersion = pin, InstalledVersion = "1.0", Status = GameStatus.Installed
    };
    private static PublishedPlatformRecord Entry(GameInfo app, int age = 0, string version = "2.0", string? pin = null, int revision = 1) =>
        new("github", app.Repository!, pin, version, ["app-win64.zip"], DateTimeOffset.UtcNow.AddHours(-age), revision);
    private static string Document(params PublishedPlatformRecord[] entries) => JsonSerializer.Serialize(
        new PublishedPlatformDocument { GeneratedAt = DateTimeOffset.UtcNow, Entries = entries.ToList() }, PublishedPlatformDocument.JsonOptions);
    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpResponseMessage Release(HttpRequestMessage request) => Ok(request.RequestUri!.AbsolutePath.EndsWith("/latest")
        ? """{"tag_name":"3.0","assets":[{"name":"app-win64.zip","browser_download_url":"https://example.test/app.zip"}]}"""
        : """[{"tag_name":"3.0","assets":[{"name":"app-win64.zip","browser_download_url":"https://example.test/app.zip"}]}]""");
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private void SaveIndex(string url, params PublishedPlatformRecord[] entries)
    {
        var path = Path.Combine(_root, "published-platforms"); Directory.CreateDirectory(path);
        // Match production's one-file-per-URL cache so later saves replace the previous document.
        var fileName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".json";
        File.WriteAllText(Path.Combine(path, fileName), JsonSerializer.Serialize(new
        {
            Url = url, ETag = "fixture", Modified = (DateTimeOffset?)null,
            Document = PublishedPlatformDocument.Parse(Document(entries))
        }, PublishedPlatformDocument.JsonOptions));
        PublishedPlatformCache.Initialize(_root);
    }
    private static void Cache(GameInfo app, int age, string version = "1.5")
    {
        GitHubApiCache.SetCache(null, app.Repository!, version, "", new() { tag_name = version }, persist: false);
        GitHubApiCache.TryGetLastKnownVersion(null, app.Repository, out var cached);
        cached!.LastChecked = DateTime.UtcNow.AddHours(-age);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void Saved_index_is_immediately_usable_without_a_release_payload(int revision)
    {
        var app = App("2.0");
        Cache(app, 48);
        SaveIndex(Url, Entry(app, pin: "2.0", revision: revision));
        StartupVersionResolver.Apply(app, _settings).Should().BeTrue();
        app.LatestVersion.Should().Be("2.0"); app.LatestVersionLabel.Should().Be("Latest: 2.0");
        app.PreferredVersion.Should().Be("2.0"); app.Status.Should().Be(GameStatus.UpdateAvailable);
        app.GetLatestRelease().Should().BeNull();
        GitHubApiCache.TryGetLastKnownVersion(null, app.Repository, out var cache);
        cache!.Version.Should().Be("1.5");
        cache.LastChecked.Should().BeBefore(DateTime.UtcNow.AddHours(-24));
    }

    [Fact]
    public async Task One_shared_request_covers_multiple_apps_and_only_missing_app_uses_repository()
    {
        var apps = new[] { App(), App(), App() };
        foreach (var app in apps) Cache(app, 48);
        _settings.AppCatalogSources.Add(new() { PlatformMetadataUrl = Url });
        var shared = 0; var repositories = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == Url) { shared++; return Task.FromResult(Ok(Document(Entry(apps[0]), Entry(apps[1])))); }
            repositories.Add(request.RequestUri.AbsolutePath); return Task.FromResult(Release(request));
        }));
        var result = await new LibraryUpdateChecker(client, _settings).CheckStartupAsync(apps, TestContext.Current.CancellationToken);
        result.Successful.Should().Be(3); shared.Should().Be(1);
        repositories.Should().ContainSingle().Which.Should().Contain(apps[2].Repository!);
        apps.Select(app => app.LatestVersion).Should().Equal("2.0", "2.0", "3.0");
    }

    [Theory]
    [InlineData("offline")] [InlineData("malformed")] [InlineData("timeout")] [InlineData("stale")] [InlineData("missing")]
    public async Task Unusable_index_only_checks_stale_or_missing_versions(string failure)
    {
        var fresh = App(); var stale = App(); var missing = App();
        Cache(fresh, 12); Cache(stale, 48);
        var repositories = new List<string>();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri != Url) { repositories.Add(request.RequestUri.AbsolutePath); return Release(request); }
            if (failure == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return failure switch
            {
                "offline" => new(HttpStatusCode.ServiceUnavailable),
                "malformed" => Ok("broken"),
                "stale" => Ok(Document(Entry(stale, 48), Entry(missing, 48))),
                _ => Ok(Document())
            };
        }));
        var result = await new LibraryUpdateChecker(client, _settings).CheckStartupAsync([fresh, stale, missing],
            TestContext.Current.CancellationToken, indexTimeout: TimeSpan.FromMilliseconds(100));
        result.Successful.Should().Be(3); repositories.Should().HaveCount(2);
        repositories.Should().NotContain(path => path.Contains(fresh.Repository!));
        fresh.LatestVersion.Should().Be("1.5");
    }

    [Fact]
    public void Disabled_source_and_unpinned_entry_cannot_supply_a_pinned_version()
    {
        var app = App("2.0");
        SaveIndex(Url, Entry(app));
        StartupVersionResolver.Resolve(app, _settings).Should().BeNull();
        SaveIndex("https://disabled.test/index.json", Entry(app, pin: "2.0"));
        _settings.AppCatalogSources.Add(new() { PlatformMetadataUrl = "https://disabled.test/index.json", Enabled = false });
        StartupVersionResolver.Resolve(app, _settings).Should().BeNull();
    }

    [Fact]
    public void Newer_direct_evidence_wins_and_stale_index_keeps_pending_hint()
    {
        var app = App(); Cache(app, 0, "3.0"); SaveIndex(Url, Entry(app, 2));
        StartupVersionResolver.Apply(app, _settings).Should().BeTrue(); app.LatestVersion.Should().Be("3.0");
        var old = App(); SaveIndex(Url, Entry(old, 48));
        StartupVersionResolver.Apply(old, _settings).Should().BeFalse();
        old.LatestVersionLabel.Should().Be("Latest: 2.0 (pending check)"); old.GetLatestRelease().Should().BeNull();
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Manual_and_scheduled_checks_still_query_repositories(bool manual)
    {
        var app = App(); SaveIndex(Url, Entry(app)); var requests = 0;
        using var client = new HttpClient(new Handler((request, _) => { requests++; return Task.FromResult(Release(request)); }));
        await new LibraryUpdateChecker(client, _settings).CheckAsync([app], manual, TimeSpan.FromHours(6), null, TestContext.Current.CancellationToken);
        requests.Should().Be(1); app.LatestVersion.Should().Be("3.0");
    }

    [Fact]
    public async Task Cancellation_during_index_refresh_does_not_start_fallback()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = new HttpClient(new Handler(async (_, token) =>
        { requests++; started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok(Document()); }));
        var check = new LibraryUpdateChecker(client, _settings).CheckStartupAsync([App()], cancel.Token);
        await started.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        requests.Should().Be(1);
    }

    [Fact]
    public async Task Local_library_loading_applies_saved_index_before_any_network_request()
    {
        var previous = QuiverLauncherPaths.OverrideUserDataRoot;
        QuiverLauncherPaths.OverrideUserDataRoot = _root;
        try
        {
            var app = App("2.0");
            var cacheDirectory = Path.Combine(_root, "Cache", "published-platforms");
            SaveIndex(Url, Entry(app, pin: "2.0"));
            Directory.CreateDirectory(cacheDirectory);
            foreach (var file in Directory.GetFiles(Path.Combine(_root, "published-platforms")))
                File.Copy(file, Path.Combine(cacheDirectory, Path.GetFileName(file)));
            var store = new FileSettingsStore(Path.Combine(_root, "settings.json"));
            store.Current.AppCatalogSources = _settings.AppCatalogSources;
            store.Current.AppsPath = Path.Combine(_root, "Apps"); store.Save(store.Current);
            using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No network during local load")));
            using var manager = new GameManager(store, client);
            await manager.CatalogService.SaveLocalAppsAsync([app]);
            await manager.ReloadLibraryFromDiskAsync(allowNetwork: false);
            var loaded = manager.LibraryApps.Single();
            loaded.LatestVersionLabel.Should().Be("Latest: 2.0");
            loaded.PreferredVersion.Should().Be("2.0");
            loaded.GetLatestRelease().Should().BeNull();
        }
        finally { QuiverLauncherPaths.OverrideUserDataRoot = previous; }
    }

    [Theory]
    [InlineData(GameStatus.Downloading)] [InlineData(GameStatus.Installing)] [InlineData(GameStatus.Updating)]
    public void Published_version_keeps_active_operation_state(GameStatus status)
    {
        var app = App(); app.Status = status;
        SaveIndex(Url, Entry(app));
        StartupVersionResolver.Apply(app, _settings);
        app.Status.Should().Be(status);
    }
}
