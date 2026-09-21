using System.Text.Json;
using FluentAssertions;
using QuiverLauncher.Core.Models;
using QuiverLauncher.Core.Services;

namespace QuiverLauncher.Tests;

public class NonAppArchiveTests
{
    private static readonly string[] KartPadAssets =
    [
        "KartPad-v0.5.0-android-arm64.apk", "KartPad-v0.5.0-ios-unsigned.ipa",
        "KartPad-v0.5.0-macos-arm64.zip", "KartPad-v0.5.0-notices.zip",
        "KartPad-v0.5.0-source.tar.gz", "SHA256SUMS.txt"
    ];
    private static GitHubRelease Release(params string[] names) => new()
    {
        tag_name = "v0.5.0",
        assets = names.Select(name => new GitHubAsset { name = name, browser_download_url = "https://example.test/" + name }).ToArray()
    };

    [Theory]
    [InlineData("KartPad-v0.5.0-notices.zip")]
    [InlineData("KartPad-v0.5.0-source.tar.gz")]
    [InlineData("app_NOTICE.ZIP")]
    [InlineData("app.SOURCES.tar.xz")]
    [InlineData("app-source-code.7z")]
    [InlineData("app_source_code.rar")]
    [InlineData("app.source.code.tgz")]
    [InlineData("app source code.tar.bz2")]
    [InlineData("app-SRC.tar.zst")]
    [InlineData("notices.zip")]
    [InlineData("source.tar")]
    public void Non_app_archives_are_never_platform_evidence_or_download_choices(string name)
    {
        DownloadAssetPolicy.IsAuxiliary(name).Should().BeTrue();
        PlatformAssetMatcher.IsWindowsAsset(name).Should().BeFalse();
        CatalogPlatformSupport.FromAssetNames([name]).Should().Be(CatalogPlatformFlags.None);
        GitHubReleaseService.GetDownloadableAssets(Release(name), name).Should().BeEmpty();
        foreach (var platform in new[] { "Windows", "Linux-X64", "Linux-ARM64", "macOS", "Android" })
        {
            PlatformAssetMatcher.MatchesPlatform(name, platform).Should().BeFalse();
            var choices = DownloadAssetPolicy.Select(Release(name), platform);
            choices.Eligible.Should().BeEmpty();
            choices.Uncertain.Should().BeEmpty();
            choices.Automatic.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("Resource.zip")]
    [InlineData("Resources.zip")]
    [InlineData("MyNotices.zip")]
    [InlineData("SourceGame.zip")]
    [InlineData("Source-Code-Game-windows.zip")]
    [InlineData("app-source-windows.zip")]
    [InlineData("app.zip")]
    [InlineData("app-win64.zip")]
    public void Similar_app_names_and_generic_windows_archives_remain_installable(string name)
    {
        DownloadAssetPolicy.IsAuxiliary(name).Should().BeFalse();
        DownloadAssetPolicy.Select(Release(name), "Windows").Automatic!.name.Should().Be(name);
    }

    [Fact]
    public void Kartpad_supports_android_and_mac_without_windows_or_linux_downloads()
    {
        var release = Release(KartPadAssets);
        CatalogPlatformSupport.FromAssetNames(KartPadAssets).Should().Be(CatalogPlatformFlags.Android | CatalogPlatformFlags.Mac);
        GitHubReleaseService.GetDownloadableAssets(release).Select(a => a.name).Should().Equal(KartPadAssets.Take(3));
        DownloadAssetPolicy.Select(release, "Android").Automatic!.name.Should().Be(KartPadAssets[0]);
        DownloadAssetPolicy.Select(release, "macOS").Automatic!.name.Should().Be(KartPadAssets[2]);
        foreach (var platform in new[] { "Windows", "Linux-X64", "Linux-ARM64" })
        {
            var choices = DownloadAssetPolicy.Select(release, platform);
            choices.Eligible.Should().BeEmpty();
            choices.Uncertain.Should().BeEmpty();
            choices.Automatic.Should().BeNull();
        }
    }

    [Fact]
    public void Existing_local_and_published_metadata_are_reclassified_without_rewriting_cache_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quiver-archive-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string repository = "chrissotraidis/kartpad";
            var checkedAt = DateTimeOffset.UtcNow.AddDays(-2);
            var local = Path.Combine(directory, "catalog_platform_index_v1.json");
            var localJson = JsonSerializer.Serialize(new Dictionary<string, CatalogPlatformEntry>
            {
                [CatalogPlatformIndex.Key("github", repository)] = new("v0.5.0", KartPadAssets, checkedAt, 2)
            });
            File.WriteAllText(local, localJson);
            var publishedDirectory = Path.Combine(directory, "published-platforms");
            Directory.CreateDirectory(publishedDirectory);
            var published = Path.Combine(publishedDirectory, "existing.json");
            var publishedJson = JsonSerializer.Serialize(new
            {
                Url = "https://example.test/platforms.json", ETag = "fixture", Modified = checkedAt,
                Document = new PublishedPlatformDocument
                {
                    GeneratedAt = checkedAt,
                    Entries = [new("github", repository, null, "v0.5.0", KartPadAssets, checkedAt)]
                }
            }, PublishedPlatformDocument.JsonOptions);
            File.WriteAllText(published, publishedJson);
            CatalogPlatformIndex.Initialize(directory);
            CatalogPlatformIndex.TryGet("github", repository, null, null, out var localEntry).Should().BeTrue();
            PublishedPlatformCache.TryGet("github", repository, null, out var publishedEntry).Should().BeTrue();
            foreach (var entry in new[] { localEntry!, publishedEntry! })
            {
                var flags = CatalogPlatformSupport.FromMetadata(entry, null);
                flags.Should().Be(CatalogPlatformFlags.Android | CatalogPlatformFlags.Mac);
                CatalogPlatformSupport.Matches(flags, CatalogPlatformFlags.Windows).Should().BeFalse();
                CatalogPlatformSupport.Matches(flags, CatalogPlatformFlags.Linux).Should().BeFalse();
                CatalogPlatformSupport.FromMetadata(entry, "notices").Should().Be(CatalogPlatformFlags.None);
            }
            File.ReadAllText(local).Should().Be(localJson);
            File.ReadAllText(published).Should().Be(publishedJson);
        }
        finally { Directory.Delete(directory, true); }
    }
}
