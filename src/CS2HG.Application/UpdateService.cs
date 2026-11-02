using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CS2HG.Application;

public sealed record UpdateInfo(Version Version, string Tag, string Url);

/// <summary>Looks for a newer release of the app on GitHub (the releases page, nothing is downloaded).</summary>
public sealed class UpdateService(ILogger<UpdateService> logger)
{
    private const string LatestReleaseApi = "https://api.github.com/repos/EzequielVieira/cs2clutchEz/releases/latest";

    public static Version Current { get; } = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>The newer release, or null when this is the latest one (or GitHub could not be reached).</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("cs2clutchEz", Current.ToString(3)));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi, ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (ParseTag(tag) is not { } version || !IsNewer(version, Current))
            {
                logger.LogInformation("Update check: {Tag} is not newer than {Current}", tag, Current.ToString(3));
                return null;
            }

            logger.LogInformation("Update available: {Tag}", tag);
            return new UpdateInfo(version, tag, url ?? Core.Brand.ReleasesUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Update check failed");
            return null;
        }
    }

    public static Version? ParseTag(string? tag) =>
        Version.TryParse((tag ?? "").Trim().TrimStart('v', 'V'), out var v) ? v : null;

    /// <summary>Compares major.minor.patch (the 4th assembly version field is ignored).</summary>
    public static bool IsNewer(Version candidate, Version current) => Triple(candidate) > Triple(current);

    private static Version Triple(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
