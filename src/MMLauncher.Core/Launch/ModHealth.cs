using MMLauncher.Core.Logging;
using MMLauncher.Core.Steam;

namespace MMLauncher.Core.Launch;

/// <summary>Result of checking one mod's files on disk.</summary>
public sealed record ModHealthReport(ulong Id, string? Folder, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
    public string Summary => Ok ? "Files look good" : string.Join("; ", Problems);
}

/// <summary>
/// Local, read-only checks that catch the common ways a downloaded mod goes bad:
/// interrupted downloads, files deleted by antivirus or cleanup tools, and missing
/// signature files (which get you kicked from servers that verify signatures).
/// It can't prove a file's contents are right (Steam doesn't give out checksums),
/// so a clean result means "nothing obviously wrong".
/// </summary>
public static class ModHealth
{
    public static ModHealthReport Check(ulong id, InstalledMod? installed)
    {
        if (installed == null || !Directory.Exists(installed.Folder))
            return new ModHealthReport(id, installed?.Folder, ["not downloaded"]);
        return Check(id, installed.Folder);
    }

    public static ModHealthReport Check(ulong id, string folder)
    {
        var problems = new List<string>();
        try
        {
            if (!Directory.Exists(folder))
                return new ModHealthReport(id, folder, ["folder missing"]);

            var addons = Directory.EnumerateDirectories(folder)
                .FirstOrDefault(d => Path.GetFileName(d).Equals("addons", StringComparison.OrdinalIgnoreCase));
            if (addons == null)
            {
                problems.Add("addons folder missing");
                return new ModHealthReport(id, folder, problems);
            }

            var files = Directory.EnumerateFiles(addons).Select(f => new FileInfo(f)).ToList();
            var pbos = files.Where(f => f.Extension.Equals(".pbo", StringComparison.OrdinalIgnoreCase)).ToList();
            var sigs = files.Where(f => f.Extension.Equals(".bisign", StringComparison.OrdinalIgnoreCase)).ToList();

            if (pbos.Count == 0)
                problems.Add("no .pbo files in addons");

            var empty = pbos.Where(p => p.Length == 0).Select(p => p.Name).ToList();
            if (empty.Count > 0)
                problems.Add($"{empty.Count} empty .pbo file(s): {Names(empty)}");

            // Only judge signatures when the author signs their files: if the mod ships any
            // .bisign, every .pbo needs one ("name.pbo.<key>.bisign").
            if (sigs.Count > 0)
            {
                var unsigned = pbos
                    .Where(p => !sigs.Any(s => s.Name.StartsWith(p.Name + ".", StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Name).ToList();
                if (unsigned.Count > 0)
                    problems.Add($"{unsigned.Count} .pbo file(s) missing their signature: {Names(unsigned)}");
            }
        }
        catch (Exception ex)
        {
            problems.Add("couldn't read files: " + ex.Message);
        }
        return new ModHealthReport(id, folder, problems);
    }

    private static string Names(List<string> names) =>
        string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" (+{names.Count - 3} more)" : "");

    /// <summary>Thrown when a mod's folder can't be moved aside; the message names the mod and file.</summary>
    public sealed class RepairBlockedException(string message, Exception inner) : Exception(message, inner);

    /// <summary>Where repaired mods' old copies wait until the fresh download is confirmed.</summary>
    public static string TrashFolder(LocalSteam steam) =>
        // ...\workshop\content\221100 -> ...\workshop\mmlauncher-repair-trash (same drive, so moving is instant)
        Path.Combine(Directory.GetParent(steam.WorkshopPath!)?.Parent?.FullName ?? steam.WorkshopPath!, "mmlauncher-repair-trash");

    private static string ModFolder(LocalSteam steam, ulong id)
    {
        if (steam.WorkshopPath == null) throw new InvalidOperationException("No workshop folder known.");
        var root = Path.GetFullPath(steam.WorkshopPath).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, id.ToString()));
        if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(target) != id.ToString())
            throw new InvalidOperationException($"Refusing to touch unexpected path {target}");
        return target;
    }

    /// <summary>
    /// Step 1 of a repair: moves &lt;workshop&gt;\&lt;id&gt; aside in one go, so the mod is either fully
    /// out of the way or completely untouched (never half-deleted). If any file in it is open in
    /// another program, nothing changes and a RepairBlockedException names the file.
    /// Returns where the old copy went, or null if there was nothing to move.
    /// </summary>
    public static string? MoveAsideForRepair(LocalSteam steam, ulong id, string modName)
    {
        var target = ModFolder(steam, id);
        var info = new DirectoryInfo(target);
        if (!info.Exists) return null;
        if (info.LinkTarget != null)
        {
            info.Delete(); // a link: remove the link only, never what it points to
            Log.Info($"Repair: removed link {target}");
            return null;
        }

        var trash = TrashFolder(steam);
        Directory.CreateDirectory(trash);
        var dest = Path.Combine(trash, $"{id}-{DateTime.Now:yyyyMMddHHmmss}");
        try
        {
            Directory.Move(target, dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var locked = FindLockedFile(target);
            var what = locked != null ? $"'{Path.GetRelativePath(target, locked)}' in {modName}" : $"a file in {modName}";
            throw new RepairBlockedException(
                $"{what} is open in another program (an editor, File Explorer preview, DayZ Tools, antivirus scan…). Close it and try again.", ex);
        }
        Log.Info($"Repair: moved {target} aside to {dest}");
        return dest;
    }

    /// <summary>Puts an old copy back if the fresh download didn't arrive, so a failed repair loses nothing.</summary>
    public static bool RestoreIfMissing(LocalSteam steam, ulong id, string oldCopy)
    {
        var target = ModFolder(steam, id);
        if (!Directory.Exists(oldCopy)) return false;
        if (Directory.Exists(target) && LocalSteam.HasAnyFile(target)) return false;
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // file-less placeholder folders only
            Directory.Move(oldCopy, target);
            Log.Info($"Repair: download didn't arrive, restored old copy of {id}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Repair: couldn't restore old copy of {id}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deletes old copies left in the repair trash. Anything still locked stays for next time.
    /// Returns how many couldn't be removed yet.
    /// </summary>
    public static int PurgeTrash(LocalSteam steam)
    {
        if (steam.WorkshopPath == null) return 0;
        var trash = TrashFolder(steam);
        if (!Directory.Exists(trash)) return 0;
        int left = 0;
        foreach (var dir in new DirectoryInfo(trash).EnumerateDirectories())
        {
            try
            {
                if (dir.LinkTarget != null) { dir.Delete(); continue; }
                foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    if (f.IsReadOnly) f.IsReadOnly = false;
                dir.Delete(recursive: true);
            }
            catch (Exception ex)
            {
                left++;
                Log.Warn($"Repair trash: couldn't delete {dir.FullName} yet ({ex.Message})");
            }
        }
        try { if (!Directory.EnumerateFileSystemEntries(trash).Any()) Directory.Delete(trash); } catch { }
        return left;
    }

    /// <summary>Finds a file under the folder that another program has open, if any.</summary>
    private static string? FindLockedFile(string folder)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(5000))
            {
                try { using var _ = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { return f; }
                catch (UnauthorizedAccessException) { /* read-only file, not a lock */ }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Removes every link in DayZ\!MMWorkshop. They're recreated on the next join.
    /// Real folders are left alone.
    /// </summary>
    public static int RemoveModLinks(string dayzPath)
    {
        var root = Path.Combine(dayzPath, GameLauncher.JunctionFolder);
        if (!Directory.Exists(root)) return 0;
        int removed = 0;
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (dir.LinkTarget == null) continue;
            dir.Delete();
            removed++;
        }
        Log.Info($"Removed {removed} mod link(s) from {root}");
        return removed;
    }
}
