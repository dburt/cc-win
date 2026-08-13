using System.Text.Json;

namespace ClaudeSessions;

/// <summary>One reported defect or cleanup, as a ReportFindings call describes it.</summary>
public sealed record Finding(
    string File,
    int? Line,
    string Category,
    string Verdict,
    string Outcome,
    string Headline,
    string Detail,
    string FailureScenario)
{
    public string LineText => Line is { } n ? $"line {n}" : "";
}

/// <summary>The findings reported against one file, in the order they were reported.</summary>
public sealed record FindingGroup(string File, List<Finding> Findings);

/// <summary>
/// A parsed ReportFindings call. <see cref="TryParse"/> returns null rather than throwing, so a
/// payload that is truncated, malformed or simply a different shape falls back to the ordinary
/// tool rendering instead of losing the record.
/// </summary>
public sealed class FindingsReport
{
    private FindingsReport(string level, List<Finding> findings)
    {
        Level = level;
        Findings = findings;
        Groups = findings
            .GroupBy(f => f.File, StringComparer.Ordinal)   // GroupBy keeps first-seen key order
            .Select(g => new FindingGroup(g.Key, g.ToList()))
            .ToList();
    }

    public string Level { get; }
    public List<Finding> Findings { get; }
    public List<FindingGroup> Groups { get; }

    public bool IsEmpty => Findings.Count == 0;

    /// <summary>The one-line story for the collapsed header.</summary>
    public string Overview
    {
        get
        {
            if (IsEmpty) return "No findings — nothing survived verification";

            var parts = new List<string> { $"{Findings.Count} finding{(Findings.Count == 1 ? "" : "s")}" };
            if (Count("CONFIRMED") is > 0 and var confirmed) parts.Add($"{confirmed} confirmed");
            if (Count("PLAUSIBLE") is > 0 and var plausible) parts.Add($"{plausible} plausible");
            return string.Join(" · ", parts);
        }
    }

    private int Count(string verdict) =>
        Findings.Count(f => string.Equals(f.Verdict, verdict, StringComparison.OrdinalIgnoreCase));

    public static FindingsReport? TryParse(string toolName, string? inputJson)
    {
        if (toolName != "ReportFindings" || string.IsNullOrWhiteSpace(inputJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("findings", out var array) || array.ValueKind != JsonValueKind.Array)
                return null;

            var findings = new List<Finding>();
            foreach (var f in array.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) continue;

                var summary = Str(f, "summary");
                var headline = Str(f, "short_summary");
                findings.Add(new Finding(
                    File: Str(f, "file"),
                    Line: f.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) ? n : null,
                    Category: Str(f, "category"),
                    Verdict: Str(f, "verdict"),
                    Outcome: Str(f, "outcome"),
                    // Prefer the compact headline — and then don't repeat the long form beneath it.
                    // The headline is a plain label, so inline-code markers are stripped rather
                    // than shown raw; the body below keeps its Markdown.
                    Headline: Unbacktick(headline.Length > 0 ? headline : summary),
                    Detail: headline.Length > 0 ? summary : "",
                    FailureScenario: Str(f, "failure_scenario")));
            }

            return new FindingsReport(Str(root, "level"), findings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Unbacktick(string s) => s.Replace("`", "");

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
