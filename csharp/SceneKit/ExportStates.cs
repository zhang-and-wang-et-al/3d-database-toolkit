using System.Runtime.InteropServices;
using System.Text;

namespace SceneKit;

/// <summary>NTFS hard links (Windows only).</summary>
public static class HardLinks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>Creates <paramref name="link"/> as a hard link to <paramref name="existing"/>; false on any failure (another
    /// volume, a file system without hard links, <paramref name="link"/> already exists …) or off Windows.</summary>
    public static bool TryLink(string existing, string link)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return CreateHardLinkW(link, existing, IntPtr.Zero); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}

/// <summary>The export folder, its state folders (state_00_original first), the README and how many files of the state
/// folders are hard links to <c>base/</c> or plain copies (the copies into <c>base/</c> itself are not counted).</summary>
public sealed record ExportStatesResult(string Folder, IReadOnlyList<string> StateFolders, string Readme, int Linked, int Copied);

/// <summary>Export States: the unchanged database and every checkpoint state as numbered, self-contained folders
/// (<c>state_&lt;nn&gt;_&lt;label&gt;/</c>, each a Save Copy with unchanged files) plus <c>base/</c> (the unchanged original) and a
/// README. Unchanged files of the states are hard links to the same file in <c>base/</c> where the volume allows, else
/// copies. The opened database is never written.</summary>
public static class ExportStates
{
    public const string ReadmeName = "README.txt";

    /// <summary>The states from the original to <paramref name="current"/> (a checkpoint index of one session), oldest first:
    /// <see cref="CheckpointInfo.Parent"/> back to the root (0 = the original; no parent = the previous index). Undone
    /// steps that a later step replaced are not on the path. Backup entries are left out.</summary>
    public static List<CheckpointInfo> Chain(IReadOnlyList<CheckpointInfo> all, int? current)
    {
        var byIndex = new Dictionary<int, CheckpointInfo>();
        foreach (var c in all) if (!c.IsBackup) byIndex[c.Index] = c;
        var o = new List<CheckpointInfo>();
        var seen = new HashSet<int>();
        for (int? i = current; i is int k && k > 0 && seen.Add(k) && byIndex.TryGetValue(k, out var ck); i = ck.Parent ?? k - 1)
            o.Add(ck);
        o.Reverse();
        return o;
    }

    /// <summary><paramref name="original"/>: the save-over backup entry, when there is one; it is the original then
    /// (<c>base/</c> and state_00), not the database on disk.</summary>
    public static ExportStatesResult Run(string databaseMaster, IReadOnlyList<CheckpointInfo> states, string folder,
        Action<string>? progress = null, Func<string, string, bool>? link = null, CheckpointInfo? original = null)
    {
        link ??= HardLinks.TryLink;
        progress ??= _ => { };
        databaseMaster = Path.GetFullPath(databaseMaster);
        string root = Path.GetDirectoryName(databaseMaster)!;
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (DatabaseRoot.Same(folder, root) || DatabaseRoot.Under(root, folder) || DatabaseRoot.Under(folder, root))
            throw new ArgumentException("the export must go outside the opened database folder", nameof(folder));

        // a file written over in place must not be a link (an earlier export's, or one made earlier in this run)
        static void Fresh(string dst) { if (File.Exists(dst)) File.Delete(dst); }

        progress("writing base/ (the unchanged original) …");
        string baseDir = Path.Combine(folder, "base");
        var orig = original == null ? SceneIO.Load(databaseMaster)
            : SceneIO.Load(original.Master, new LoadOptions { Layers = original.Layers.ToList(), Origin = original.Source });
        long tris0 = History.QuickTotals(orig).GetValueOrDefault("triangles");
        var baseRes = SaveCopy.ToFolder(orig, baseDir, new SaveCopyOptions
        {
            IncludeUnchanged = true, Progress = progress, CopyFile = (s, d) => { Fresh(d); File.Copy(s, d); },
        });
        // database file -> its byte-for-byte copy in base/ (written files such as the master differ from their source)
        var shared = new Dictionary<string, string>(DatabaseRoot.PathComparer);
        foreach (var (path, kind) in baseRes.Files)
            if (kind == "copied") shared[Path.Combine(root, Path.GetRelativePath(baseDir, path))] = path;

        int linked = 0, copied = 0;
        var folders = new List<string>();
        void Write(Scene scene, string name)
        {
            string stateDir = Path.Combine(folder, name);
            progress($"writing {name}/ …");
            SaveCopy.ToFolder(scene, stateDir, new SaveCopyOptions
            {
                IncludeUnchanged = true, Progress = progress,
                CopyFile = (src, dst) =>
                {
                    Fresh(dst);
                    string? baseFile = DatabaseRoot.Under(baseDir, src) ? src : shared.GetValueOrDefault(Path.GetFullPath(src));
                    if (baseFile != null && Linkable(Path.GetRelativePath(stateDir, dst)) && link(baseFile, dst)) linked++;
                    else { File.Copy(src, dst); copied++; }
                },
            });
            folders.Add(stateDir);
        }

        Write(SceneIO.Load(baseRes.Master), "state_00_original");
        for (int i = 0; i < states.Count; i++)
        {
            var ck = states[i];
            Write(SceneIO.Load(ck.Master, new LoadOptions { Layers = ck.Layers.ToList(), Origin = ck.Source }),
                $"state_{i + 1:00}_{CheckpointStore.Sanitize(ck.Label)}");
        }

        progress($"writing {ReadmeName} …");
        string readme = Path.Combine(folder, ReadmeName);
        File.WriteAllText(readme, Readme(databaseMaster, states, folders, tris0));
        return new ExportStatesResult(folder, folders, readme, linked, copied);
    }

    /// <summary>Files the writers write over in place (the manifest, images in "&lt;stem&gt;_textures") are never linked:
    /// writing through a link would change <c>base/</c> and every state sharing the file. Scene files are written to a
    /// temporary file and moved over, which replaces a link.</summary>
    static bool Linkable(string rel)
    {
        if (string.Equals(rel, SaveCopy.ManifestName, StringComparison.OrdinalIgnoreCase)) return false;
        var dirs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1);
        return !dirs.Any(d => d.EndsWith("_textures", StringComparison.OrdinalIgnoreCase));
    }

    static string Readme(string databaseMaster, IReadOnlyList<CheckpointInfo> states, IReadOnlyList<string> folders, long tris0)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Database: {databaseMaster}");
        sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"{Path.GetFileName(folders[0])}  —  the unchanged database, tris {tris0}");
        long before = tris0;
        for (int i = 0; i < states.Count; i++)
        {
            var ck = states[i];
            string prms = ck.Params.Length > 0 ? ck.Params : "no parameters";
            sb.AppendLine($"{Path.GetFileName(folders[i + 1])}  —  {ck.Time:yyyy-MM-dd HH:mm:ss}, {ck.Scope}, {prms}, tris {before} → {ck.Tris}");
            before = ck.Tris;
        }
        sb.AppendLine();
        sb.AppendLine("Load the folders in order in the renderer; the first one that fails names the step that broke it. base/ holds the " +
            "unchanged original; the state folders share unchanged files with it (hard links) where the volume allows.");
        return sb.ToString();
    }
}
