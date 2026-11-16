using PrintDesk.Ipp;

namespace PrintDesk.State;

enum Health : byte { Ready, Busy, Warning, Error, Offline }

/// <summary>Printer jargon (IPP state reasons and alerts) in plain words.</summary>
static class Words
{
    public static Health HealthOf(PrinterInfo info)
    {
        if (info.State == 5 || info.Reasons.Any(IsError) || !info.AcceptingJobs) return Health.Error;
        if (info.State == 4) return Health.Busy;
        if (info.Reasons.Any(r => r.EndsWith("-warning", StringComparison.Ordinal) || r.EndsWith("-report", StringComparison.Ordinal))) return Health.Warning;
        return Health.Ready;
    }

    public static string Headline(PrinterInfo info) => HealthOf(info) switch
    {
        Health.Error => "Needs attention",
        Health.Busy => "Printing",
        Health.Warning => "Ready",
        _ => "Ready",
    };

    private static bool IsError(string r) =>
        r.EndsWith("-error", StringComparison.Ordinal)
        || (!r.EndsWith("-warning", StringComparison.Ordinal) && !r.EndsWith("-report", StringComparison.Ordinal)
            && r is not ("none" or "moving-to-paused") && Known.ContainsKey(Strip(r)) && Severe.Contains(Strip(r)));

    private static string Strip(string r) =>
        r.EndsWith("-error", StringComparison.Ordinal) ? r[..^6]
        : r.EndsWith("-warning", StringComparison.Ordinal) ? r[..^8]
        : r.EndsWith("-report", StringComparison.Ordinal) ? r[..^7]
        : r;

    private static readonly HashSet<string> Severe =
    [
        "media-empty", "media-jam", "media-needed", "door-open", "cover-open", "marker-supply-empty", "toner-empty",
        "paused", "stopped-partly", "input-tray-missing", "output-tray-missing", "output-area-full", "interlock-open",
        "media-size-mismatch", "media-type-mismatch", "other",
    ];

    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["media-empty"] = "Out of paper",
        ["media-needed"] = "Load paper",
        ["media-jam"] = "Paper jam",
        ["media-low"] = "Paper is running low",
        ["media-size-mismatch"] = "The paper loaded doesn't match the paper size set on the printer",
        ["media-type-mismatch"] = "The paper loaded doesn't match the paper type set on the printer",
        ["door-open"] = "A door is open",
        ["cover-open"] = "The cover is open",
        ["interlock-open"] = "A cover or door is open",
        ["input-tray-missing"] = "A paper tray is missing",
        ["output-tray-missing"] = "The output tray is missing or closed",
        ["output-area-full"] = "The output tray is full",
        ["output-area-almost-full"] = "The output tray is almost full",
        ["marker-supply-empty"] = "An ink cartridge is empty",
        ["marker-supply-low"] = "Ink is running low",
        ["marker-waste-almost-full"] = "The ink absorber is almost full",
        ["marker-waste-full"] = "The ink absorber is full",
        ["toner-empty"] = "Out of toner",
        ["toner-low"] = "Toner is running low",
        ["paused"] = "The printer is paused",
        ["moving-to-paused"] = "The printer is pausing",
        ["stopped-partly"] = "Part of the printer has stopped",
        ["spool-area-full"] = "The printer's memory is full; it will take new jobs once the current ones finish",
        ["connecting-to-device"] = "Connecting",
        ["timed-out"] = "The printer stopped responding",
        ["shutdown"] = "The printer is shutting down",
        ["offline"] = "The printer is offline",
        ["cleaner-life-almost-over"] = "A maintenance part is almost worn out",
        ["other"] = "The printer reports a problem: check its screen",
    };

    /// <summary>The issues worth showing, most serious first, each once.</summary>
    public static IReadOnlyList<(bool Serious, string Text)> Issues(PrinterInfo info)
    {
        var list = new List<(bool, string)>();
        foreach (var r in info.Reasons)
        {
            string key = Strip(r);
            if (key is "none" or "moving-to-paused" or "connecting-to-device") continue;
            string text = Known.TryGetValue(key, out var t) ? t : Humanize(key);
            bool serious = IsError(r);
            if (!list.Any(x => x.Item2 == text)) list.Add((serious, text));
        }
        if (info.Message is { } m && !list.Any(x => x.Item2.Equals(m, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, (HealthOf(info) == Health.Error, m));
        foreach (var s in info.Supplies)
            if (s.Percent is 0 && !list.Any(x => x.Item2.Contains(s.Name, StringComparison.OrdinalIgnoreCase)))
                list.Add((false, $"{PrettySupply(s.Name)} is empty"));
        return list.OrderByDescending(x => x.Item1).ToList();
    }

    /// <summary>Why a job sits in processing-stopped: the printer's most serious issue.</summary>
    public static string StoppedBecause(PrinterInfo info)
    {
        var issues = Issues(info);
        return issues.Count > 0 ? $"Stopped: {issues[0].Text}" : "Stopped: check the printer's screen";
    }

    private static string Humanize(string key)
    {
        var s = key.Replace('-', ' ');
        return s.Length == 0 ? key : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>"Black(PGBK)" to "Black (PGBK)", "cyan ink" to "Cyan ink".</summary>
    public static string PrettySupply(string name)
    {
        var s = name.Replace("(", " (").Replace("  ", " ").Trim();
        return s.Length == 0 ? name : char.ToUpperInvariant(s[0]) + s[1..];
    }

    public static string FormatList(PrinterInfo info)
    {
        var bits = new List<string>();
        if (info.Supports("image/jpeg")) bits.Add("JPEG");
        if (info.Supports("image/pwg-raster")) bits.Add("PWG raster");
        if (info.Supports("application/pdf")) bits.Add("PDF");
        if (info.Supports("image/urf")) bits.Add("AirPrint");
        return bits.Count == 0 ? "no driverless formats" : string.Join(" · ", bits);
    }
}
