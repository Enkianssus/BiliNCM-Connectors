using System.Reflection;
using System.Text;
using System.Text.Json;

namespace QQMusicControlPoc;

internal static class QQMusicCompatibilityReporter
{
    private const string Endpoint =
        "https://app.enkianss.us/api/v1/compatibility-reports";

    public static async Task<bool> ReportCurrentIfNeededAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var analysis = QQMusicNativeNextAnalyzer.AnalyzeCurrent();
            if (analysis.ExecutionAllowed)
            {
                return true;
            }

            var payload = new
            {
                schemaVersion = 1,
                player = "qqmusic",
                playerVersion = analysis.FileVersion,
                connectorVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? string.Empty,
                architecture = analysis.Machine,
                clientSha256 = analysis.ClientSha256,
                commonSha256 = analysis.CommonSha256,
                knownProfileMatched = analysis.KnownProfileMatched,
                executionAllowed = analysis.ExecutionAllowed,
                summary = analysis.Summary,
                checks = analysis.Checks.Select(check => new
                {
                    name = check.Name,
                    required = check.Required,
                    passed = check.Passed,
                    // The same-install-directory check contains the user's
                    // local installation path. Its pass/fail result is useful,
                    // but the path itself must never leave the machine.
                    detail = check.Name.Equals(
                        "same-install-directory",
                        StringComparison.Ordinal)
                            ? string.Empty
                            : check.Detail
                }),
                candidates = analysis.Candidates.Select(candidate => new
                {
                    name = candidate.Name,
                    rvas = candidate.Rvas,
                    evidence = candidate.Evidence
                })
            };
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "AwooMusicBot-QQMusic-Connector/1.4");
            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(
                Endpoint,
                content,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Compatibility reporting is best-effort. Network, D1 or analysis
            // failures must never affect local playback control.
            return false;
        }
    }
}
