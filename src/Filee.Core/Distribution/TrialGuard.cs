// Trial window of the 格式转换器 distribution builds (this fork only, never upstream): distribution.json next to
// the executables says how many days a version may be used and which notice to show once it is over. A build
// without the file (development, a user's own compile) is unrestricted. The first-run date is recorded per
// version in the user data folder, so a newer version starts a fresh window - the distribution's update path is
// "get the new build", which is what the notice points to.

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Filee.Core.Distribution;

/// <summary>What distribution.json says: <paramref name="TrialDays"/> days of use per version, and the notice.</summary>
public sealed record DistributionConfig(int TrialDays, string ExpiredMessage);

/// <summary>Reads distribution.json and decides whether this version's trial window is over.</summary>
public static class TrialGuard
{
    /// <summary>The file, next to Filee.exe and filee-cli.exe (or one folder up from a cli copy).</summary>
    public const string ConfigFileName = "distribution.json";

    /// <summary>First-run record, per version, in the user data folder.</summary>
    private const string StateFileName = "trial.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Version of this build (Directory.Build.props); a different version restarts the window.</summary>
    public static string CurrentVersion =>
        typeof(TrialGuard).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(TrialGuard).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Reads <see cref="ConfigFileName"/> from the first folder that has it. A missing file, invalid JSON or
    /// <c>trialDays</c> &lt;= 0 mean no restriction (null): an unreadable configuration must never lock everybody
    /// out of their files.
    /// </summary>
    public static DistributionConfig? TryLoad(params string[] directories)
    {
        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, ConfigFileName);
            if (!File.Exists(path))
                continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path));
                var days = node?["trialDays"]?.GetValue<int>() ?? 0;
                if (days <= 0)
                    return null;
                var message = node?["expiredMessage"]?.GetValue<string>();
                return new DistributionConfig(days, string.IsNullOrWhiteSpace(message)
                    ? "This version's free period is over."
                    : message);
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidOperationException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="config"/>'s window has passed and the app must stop and show its notice. The
    /// first-run record is written for versions that have none, so the window starts with the first start; a
    /// clock set back before the first run counts as zero days used.
    /// </summary>
    public static bool IsExpired(DistributionConfig config, string dataDirectory)
    {
        var version = CurrentVersion;
        var path = Path.Combine(dataDirectory, StateFileName);
        var firstRun = ReadFirstRun(path, version);
        if (firstRun is null)
        {
            firstRun = DateTime.Today;
            WriteFirstRun(path, version, firstRun.Value);
        }
        return Math.Max(0, DateTime.Today.Subtract(firstRun.Value).Days) >= config.TrialDays;
    }

    private static DateTime? ReadFirstRun(string path, string version)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node?["version"]?.GetValue<string>() != version)
                return null;
            return DateTime.TryParseExact(node?["firstRun"]?.GetValue<string>(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var stored)
                ? stored
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void WriteFirstRun(string path, string version, DateTime firstRun)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var node = new JsonObject
            {
                ["version"] = version,
                ["firstRun"] = firstRun.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            var temp = path + ".tmp";
            File.WriteAllText(temp, node.ToJsonString(Indented));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only data folder (rare): without the record the window restarts at the next start.
        }
    }
}
