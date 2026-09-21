using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using QuiverLauncher.Models;
using QuiverLauncher.Services;

namespace QuiverLauncher.Tests;

public class LibraryStartupOfflineTests
{
    private sealed class SlowCatalog : HttpMessageHandler
    {
        public TaskCompletionSource CatalogStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "api.github.com")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"tag_name":"2.0","assets":[{"name":"app.zip","browser_download_url":"https://example.com/app.zip"}]}""") };
            CatalogStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }
    }

    [AvaloniaFact]
    public async Task Library_versions_refresh_while_catalog_is_blocked_and_shutdown_cancels_startup()
    {
        var previous = QuiverLauncherPaths.OverrideUserDataRoot;
        var root = Path.Combine(Path.GetTempPath(), "quiver-independent-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        QuiverLauncherPaths.OverrideUserDataRoot = root;
        MainView? view = null;
        try
        {
            var store = new FileSettingsStore();
            store.Current.FirstStartup = false;
            store.Current.PromptCatalogUpdates = false;
            store.Current.AppsPath = Path.Combine(root, "Apps");
            store.Current.AppCatalogSources = [new() { Id = "slow", Name = "Slow", Location = "https://fixture.invalid/catalog.json", Enabled = true }];
            store.Save(store.Current);
            var handler = new SlowCatalog();
            using var http = new HttpClient(handler);
            using var manager = new GameManager(store, http);
            await manager.CatalogService.SaveLocalAppsAsync([new GameInfo
            { Name = "App", FolderName = "App", Repository = "startup-independent/" + Guid.NewGuid().ToString("N") }]);
            view = new MainView(new() { SettingsStore = store, GameManager = manager,
                InitializeOnOpen = false, EnableInput = false, EnableMusic = false });
            var startup = view.InitializeGamesAsync();
            await handler.CatalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            view.Library.IsInitialLoading.Should().BeFalse("local cards are ready even though catalog requests are blocked");
            var app = manager.Games.Single();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (app.LatestVersion != "2.0" && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            app.LatestVersion.Should().Be("2.0");
            startup.IsCompleted.Should().BeFalse();
            await view.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            manager.Games.Single().Should().BeSameAs(app);
        }
        finally
        {
            if (view != null) await view.ShutdownAsync();
            QuiverLauncherPaths.OverrideUserDataRoot = previous;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Failed_startup_or_reload_preserves_disk_and_loaded_apps_and_allows_recovery()
    {
        var previous = QuiverLauncherPaths.OverrideUserDataRoot;
        var root = Path.Combine(Path.GetTempPath(), "quiver-startup-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        QuiverLauncherPaths.OverrideUserDataRoot = root;
        try
        {
            var store = new FileSettingsStore();
            store.Current.FirstStartup = false;
            store.Current.AppCatalogSources = [];
            using var manager = new GameManager(store);
            var path = manager.CatalogService.AppsConfigPath;
            await File.WriteAllTextAsync(path, "{truncated");
            var load = () => manager.ReloadLibraryFromDiskAsync(allowNetwork: false);
            await load.Should().ThrowAsync<System.Text.Json.JsonException>();
            (await File.ReadAllTextAsync(path)).Should().Be("{truncated");

            // A failed initialization must not poison the cached startup task.
            var good = "{\"apps\":[{\"name\":\"Saved app\",\"repository\":\"fixture/saved\",\"folderName\":\"Saved\"}]}";
            await File.WriteAllTextAsync(path, good);
            await load();
            var loaded = manager.Games.Should().ContainSingle().Subject;
            (await File.ReadAllTextAsync(path)).Should().Be(good);
            await File.WriteAllTextAsync(path, "{damaged after startup");
            await load.Should().ThrowAsync<System.Text.Json.JsonException>();
            manager.Games.Should().ContainSingle().Which.Should().BeSameAs(loaded);
            (await File.ReadAllTextAsync(path)).Should().Be("{damaged after startup");
        }
        finally
        {
            QuiverLauncherPaths.OverrideUserDataRoot = previous;
            Directory.Delete(root, true);
        }
    }

    private sealed class BlockedNetwork : HttpMessageHandler
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Offline fixture") };
        }
    }

    [AvaloniaFact]
    public async Task Startup_displays_saved_library_and_source_counts_before_network_finishes()
    {
        var previous = QuiverLauncherPaths.OverrideUserDataRoot;
        var root = Path.Combine(Path.GetTempPath(), "quiver-startup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        QuiverLauncherPaths.OverrideUserDataRoot = root;
        var handler = new BlockedNetwork();
        MainView? view = null;
        Window? window = null;
        Task? startup = null;
        try
        {
            var store = new FileSettingsStore();
            store.Current.FirstStartup = false;
            store.Current.PromptCatalogUpdates = false;
            store.Current.AppsPath = Path.Combine(root, "Apps");
            store.Current.AppCatalogSources = [new() { Id = "cached", Name = "Cached list",
                Location = "https://fixture.invalid/catalog.json", Enabled = true, CachedListVersion = "1" }];
            store.Save(store.Current);
            using var http = new HttpClient(handler);
            using var manager = new GameManager(store, http);
            var apps = Enumerable.Range(0, 125).Select(i => new GameInfo
                { Name = $"App {i}", FolderName = $"App{i}", Repository = $"startup-fixture/app{i}" }).ToList();
            await manager.CatalogService.SaveLocalAppsAsync(apps);
            await manager.CatalogService.ExportLocalAppsToFileAsync(
                Path.Combine(manager.CatalogService.CatalogSourcesCacheFolder, "cached.json"), apps);
            view = new MainView(new() { SettingsStore = store, GameManager = manager,
                InitializeOnOpen = false, EnableInput = false, EnableMusic = false });
            window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            startup = view.InitializeGamesAsync();
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            view.Library.IsInitialLoading.Should().BeFalse();
            startup.IsCompleted.Should().BeFalse("the network is still blocked");
            manager.Games.Should().HaveCount(125);
            var originals = manager.Games.ToArray();
            store.Current.AppCatalogSources.Single(s => s.Id == "cached").LibraryAppCount.Should().Be(125);
            store.Current.AppCatalogSources.Single(s => s.Id == "cached").ListAppCount.Should().Be(125);
            handler.Release.TrySetResult();
            await startup.WaitAsync(TimeSpan.FromSeconds(15));
            manager.Games.Should().Equal(originals, "online failure must preserve loaded app instances");
            (await manager.CatalogService.LoadLocalAppsAsync()).Should().HaveCount(125);
            await view.ShutdownAsync();
        }
        finally
        {
            handler.Release.TrySetResult();
            window?.Close();
            if (view != null) await view.ShutdownAsync();
            QuiverLauncherPaths.OverrideUserDataRoot = previous;
            Directory.Delete(root, true);
        }
    }
}
