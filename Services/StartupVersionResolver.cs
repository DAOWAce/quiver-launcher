using QuiverLauncher.Core.Models;
using QuiverLauncher.Core.Services;
using QuiverLauncher.Models;

namespace QuiverLauncher.Services;

internal enum StartupVersionSource { RepositoryCache, PublishedIndex }
internal sealed record StartupVersionEvidence(string Version, DateTimeOffset VerifiedAt,
    StartupVersionSource Source, GitHubRelease? Release = null)
{
    public bool IsFresh(DateTimeOffset now) => VerifiedAt <= now.AddMinutes(5) && now - VerifiedAt < TimeSpan.FromHours(24);
}

/// <summary>Version evidence only: a published tag does not authorize or describe a download.</summary>
internal static class StartupVersionResolver
{
    internal static string[] SourceUrls(AppSettings settings) => settings.AppCatalogSources
        .Where(source => source.Enabled && !string.IsNullOrWhiteSpace(source.PlatformMetadataUrl))
        .Select(source => source.PlatformMetadataUrl!).Distinct(StringComparer.Ordinal).ToArray();

    internal static StartupVersionEvidence? Resolve(GameInfo app, AppSettings settings)
    {
        if (app.IsManuallyManaged || string.IsNullOrWhiteSpace(app.Repository)) return null;
        StartupVersionEvidence? evidence = null;
        if (GitHubApiCache.TryGetLastKnownVersion(app.RepositorySource, app.Repository, out var cached) && cached != null &&
            (string.IsNullOrWhiteSpace(app.PreferredVersion) || ReleaseVersionIdentity.AreVersionsEquivalent(app.PreferredVersion, cached.Version)))
        {
            var release = cached.CachedRelease;
            if (release != null && !ReleaseVersionIdentity.AreVersionsEquivalent(release.tag_name, cached.Version)) release = null;
            evidence = new(cached.Version, new DateTimeOffset(DateTime.SpecifyKind(cached.LastChecked, DateTimeKind.Utc)),
                StartupVersionSource.RepositoryCache, release);
        }
        if (PublishedPlatformCache.TryGet(app.EffectiveRepositorySource, app.Repository, app.PreferredVersion,
                SourceUrls(settings), out var published) && published != null && !string.IsNullOrWhiteSpace(published.ReleaseTag) &&
            published.ValidatedAt <= DateTimeOffset.UtcNow.AddMinutes(5) &&
            (string.IsNullOrWhiteSpace(app.PreferredVersion) || ReleaseVersionIdentity.AreVersionsEquivalent(app.PreferredVersion, published.ReleaseTag)) &&
            (evidence == null || published.ValidatedAt > evidence.VerifiedAt))
            evidence = new(published.ReleaseTag, published.ValidatedAt, StartupVersionSource.PublishedIndex);
        return evidence;
    }

    internal static bool Apply(GameInfo app, AppSettings settings)
    {
        var evidence = Resolve(app, settings);
        if (evidence == null) return false;
        app.ApplyStartupVersion(evidence);
        return evidence.IsFresh(DateTimeOffset.UtcNow);
    }

    internal static async Task RefreshIndexAsync(HttpClient client, AppSettings settings, CancellationToken token,
        TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        var refresh = Task.WhenAll(SourceUrls(settings).Select(url => PublishedPlatformCache.RefreshAsync(client, url, token: deadline.Token)));
        try { await refresh.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A stalled index must not hold up per-repository fallback, even with a non-cooperative transport.
            _ = refresh.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        token.ThrowIfCancellationRequested();
    }
}
