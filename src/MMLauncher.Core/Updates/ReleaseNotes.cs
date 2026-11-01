using System.Text.RegularExpressions;

namespace MMLauncher.Core.Updates;

/// <summary>One line of release notes, ready to show: a heading, a bullet point or plain text.</summary>
public sealed record NoteLine(NoteKind Kind, string Text);

public enum NoteKind { Version, Heading, Bullet, Text, Gap }

/// <summary>
/// Turns a release's notes (GitHub markdown) into plain lines for the update window: headings,
/// bullets and paragraphs, with the markdown marks removed. Sections only meant for the GitHub
/// page ("Downloads", "Server admins") are left out.
/// </summary>
public static partial class ReleaseNotes
{
    private static readonly string[] SkipSections = ["downloads", "server admins"];

    [GeneratedRegex(@"\[([^\]]+)\]\((?:[^)]+)\)")] private static partial Regex LinkRx();
    [GeneratedRegex(@"\*\*|__|`")] private static partial Regex MarkRx();

    public static List<NoteLine> Parse(string markdown)
    {
        var lines = new List<NoteLine>();
        bool skipping = false;
        foreach (var raw in (markdown ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                var title = Clean(line.TrimStart('#'));
                skipping = SkipSections.Any(s => title.StartsWith(s, StringComparison.OrdinalIgnoreCase));
                if (!skipping && title.Length > 0) lines.Add(new NoteLine(NoteKind.Heading, title));
                continue;
            }
            if (skipping) continue;
            if (line.Length == 0) { if (lines.Count > 0 && lines[^1].Kind != NoteKind.Gap) lines.Add(new NoteLine(NoteKind.Gap, "")); continue; }
            // A line that's only a link (e.g. the code signing policy) belongs on the web page.
            if (LinkRx().Replace(line, "").Trim().Length == 0) continue;
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
                lines.Add(new NoteLine(NoteKind.Bullet, Clean(line[2..])));
            else
                lines.Add(new NoteLine(NoteKind.Text, Clean(line)));
        }
        while (lines.Count > 0 && lines[^1].Kind == NoteKind.Gap) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Notes of several releases, newest first, each under its version.</summary>
    public static List<NoteLine> Combine(IEnumerable<(Version Version, string Notes)> releases, int maxReleases = 5)
    {
        var all = new List<NoteLine>();
        foreach (var (version, notes) in releases.OrderByDescending(r => r.Version).Take(maxReleases))
        {
            var parsed = Parse(notes);
            if (parsed.Count == 0) continue;
            if (all.Count > 0) all.Add(new NoteLine(NoteKind.Gap, ""));
            all.Add(new NoteLine(NoteKind.Version, $"v{version.ToString(3)}"));
            all.AddRange(parsed);
        }
        return all;
    }

    private static string Clean(string s)
    {
        s = LinkRx().Replace(s, "$1");
        s = MarkRx().Replace(s, "");
        return new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }
}
