using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SceneKit;

/// <summary>One editing state on disk. <c>Master</c> is the master file to load for this state (inside <c>Folder</c>, or the
/// database's master for backup entries); <c>Layers</c> are the ordered roots for a layered load
/// (the checkpoint folder, the session's base roots, then the database folder). <c>Parent</c> is the index of the checkpoint the
/// state was made on (0: the database as opened; null: the previous index).</summary>
public sealed record CheckpointInfo(string Folder, string Session, int Index, string Label, DateTime Time, string Master, string Source,
    long Tris, long Draws, string Scope, string Params, int? Parent, IReadOnlyList<string> Layers, bool IsBackup);

/// <summary>Checkpoints on disk: <c>&lt;BaseDir&gt;/&lt;key&gt;/&lt;session&gt;/&lt;nn&gt;_&lt;label&gt;/</c>, each holding the changed files of that
/// state (SaveCopy without unchanged files) plus <c>checkpoint.json</c>; every session folder has a <c>session.json</c>.</summary>
public sealed class CheckpointStore
{
    const string CheckpointJson = "checkpoint.json", SessionJson = "session.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    sealed class CkJson
    {
        public int Index { get; set; }
        public string Label { get; set; } = "";
        public string Time { get; set; } = "";
        public string Master { get; set; } = "";
        public string Source { get; set; } = "";
        public long Tris { get; set; }
        public long Draws { get; set; }
        public string Scope { get; set; } = "";
        public string Params { get; set; } = "";
        public int? Parent { get; set; }
        public bool IsBackup { get; set; }
        public string LayerRoot { get; set; } = "";
    }

    sealed class SessionJsonData
    {
        public string Source { get; set; } = "";
        public string[] BaseRoots { get; set; } = Array.Empty<string>();
    }

    public CheckpointStore(string baseDir) => BaseDir = baseDir;

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SceneInspector", "checkpoints");

    public string BaseDir { get; }

    /// <summary>16 hex characters of SHA-256 over lowercased full path | length | mtime ticks.</summary>
    public static string KeyFor(string masterPath)
    {
        string full = Path.GetFullPath(masterPath);
        string text = $"{full.ToLowerInvariant()}|{new FileInfo(full).Length}|{File.GetLastWriteTimeUtc(full).Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }

    /// <summary>"yyyyMMdd_HHmmss", with "_2", "_3" … appended while any key already has that session.</summary>
    public string NewSession()
    {
        string s = DateTime.Now.ToString("yyyyMMdd_HHmmss"), free = s;
        for (int i = 2; Taken(free); i++) free = $"{s}_{i}";
        return free;
    }

    bool Taken(string session)
    {
        if (!Directory.Exists(BaseDir)) return false;
        foreach (var key in Directory.EnumerateDirectories(BaseDir))
            if (Directory.Exists(Path.Combine(key, session))) return true;
        return false;
    }

    /// <summary>The label as used in folder names (checkpoints, exported states).</summary>
    internal static string Sanitize(string label)
    {
        var sb = new StringBuilder();
        foreach (char c in label) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length > 32 ? sb.ToString(0, 32) : sb.ToString();
    }

    string SessionDir(string key, string session) => Path.Combine(BaseDir, key, session);

    static T? Read<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static void WriteJson(string path, object data) => File.WriteAllText(path, JsonSerializer.Serialize(data, Json));

    /// <summary>Writes the changed files of the scene's current state. Failures (disk full, no permission, …) are not caught:
    /// they surface as IOException / UnauthorizedAccessException and the scene is not touched.</summary>
    public CheckpointInfo Write(Scene scene, string key, string session, int index, string label, string scope, string prms, int? parent)
    {
        string sessionDir = SessionDir(key, session);
        string folder = Path.Combine(sessionDir, $"{index:00}_{Sanitize(label)}");
        // the database's master, also for a scene restored from a checkpoint (layered: its master lies in the checkpoint folder)
        string source = DatabaseRoot.Master(scene);
        Directory.CreateDirectory(sessionDir);
        if (scene.Layers?.Any(l => DatabaseRoot.Same(l, folder)) == true)
            throw new IOException($"{folder} is the checkpoint the scene was restored from; it is not overwritten");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);   // a reused index: no stale files from the earlier state
        string sj = Path.Combine(sessionDir, SessionJson);
        if (!File.Exists(sj)) WriteJson(sj, new SessionJsonData { Source = source });

        var res = SaveCopy.ToFolder(scene, folder, new SaveCopyOptions { IncludeUnchanged = false, Progress = _ => { } });
        var totals = History.QuickTotals(scene);
        var ck = new CkJson
        {
            Index = index, Label = label, Time = DateTime.Now.ToString("o"), Master = Path.GetFullPath(res.Master), Source = source,
            Tris = totals.GetValueOrDefault("triangles"), Draws = totals.GetValueOrDefault("draw_calls"),
            Scope = scope, Params = prms, Parent = parent, IsBackup = false, LayerRoot = Path.GetFullPath(folder),
        };
        WriteJson(Path.Combine(folder, CheckpointJson), ck);
        return Info(ck, folder, session, Read<SessionJsonData>(sj) ?? new SessionJsonData { Source = source });
    }

    /// <summary>Adds the "00_backup" entry that stands for the database as it was before a Save Over (its files are in
    /// <paramref name="backupFolder"/>). <paramref name="source"/> (the original master's full path) is only needed when the
    /// session folder does not exist yet and no other session of the key knows the source; otherwise it is derived.</summary>
    public CheckpointInfo AddBackupEntry(string key, string session, string backupFolder, string masterName, string label, string? source = null)
    {
        string sessionDir = SessionDir(key, session), sj = Path.Combine(sessionDir, SessionJson);
        Directory.CreateDirectory(sessionDir);
        var sess = Read<SessionJsonData>(sj);
        if (sess == null)
        {
            string? src = source ?? Directory.EnumerateDirectories(Path.Combine(BaseDir, key))
                .Select(d => Read<SessionJsonData>(Path.Combine(d, SessionJson))?.Source).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            sess = new SessionJsonData { Source = src ?? throw new InvalidOperationException("source of the database is unknown") };
            WriteJson(sj, sess);
        }
        string rootDir = Path.GetDirectoryName(sess.Source)!;
        string folder = Path.Combine(sessionDir, "00_backup");
        for (int i = 2; Directory.Exists(folder); i++) folder = Path.Combine(sessionDir, $"00_backup_{i}");
        Directory.CreateDirectory(folder);
        string inBackup = Path.Combine(backupFolder, masterName);
        var ck = new CkJson
        {
            Index = 0, Label = label, Time = DateTime.Now.ToString("o"), Master = Path.GetFullPath(File.Exists(inBackup) ? inBackup : Path.Combine(rootDir, masterName)),
            Source = sess.Source, Tris = 0, Draws = 0, Scope = "", Params = "", Parent = null, IsBackup = true, LayerRoot = backupFolder,
        };
        WriteJson(Path.Combine(folder, CheckpointJson), ck);
        return Info(ck, folder, session, sess);
    }

    static CheckpointInfo Info(CkJson ck, string folder, string session, SessionJsonData sess)
    {
        string root = Path.GetDirectoryName(ck.Source) ?? "";
        // a moved (rekeyed) checkpoint folder: the stored LayerRoot / Master are stale, the folder it is found in is the truth
        string master = ck.Master;
        if (!ck.IsBackup && DatabaseRoot.Under(ck.LayerRoot, ck.Master)) master = Path.Combine(folder, Path.GetRelativePath(ck.LayerRoot, ck.Master));
        var layers = (ck.IsBackup
            ? new[] { ck.LayerRoot, root }
            : new[] { folder }.Concat(sess.BaseRoots).Append(root)).Distinct(DatabaseRoot.PathComparer).ToList();
        return new CheckpointInfo(folder, session, ck.Index, ck.Label, DateTime.Parse(ck.Time, null, System.Globalization.DateTimeStyles.RoundtripKind),
            master, ck.Source, ck.Tris, ck.Draws, ck.Scope, ck.Params, ck.Parent, layers, ck.IsBackup);
    }

    /// <summary>All checkpoints of all sessions under the key, oldest first. Unreadable entries are skipped.</summary>
    public List<CheckpointInfo> List(string key)
    {
        var all = new List<CheckpointInfo>();
        string keyDir = Path.Combine(BaseDir, key);
        if (!Directory.Exists(keyDir)) return all;
        foreach (var sdir in Directory.EnumerateDirectories(keyDir))
        {
            var sess = Read<SessionJsonData>(Path.Combine(sdir, SessionJson));
            if (sess == null) continue;
            foreach (var cdir in Directory.EnumerateDirectories(sdir))
            {
                var ck = Read<CkJson>(Path.Combine(cdir, CheckpointJson));
                if (ck == null) continue;
                try { all.Add(Info(ck, cdir, Path.GetFileName(sdir), sess)); }
                catch (FormatException) { }
            }
        }
        return all.OrderBy(c => c.Time).ToList();
    }

    static IEnumerable<string> CheckpointFolders(string keyDir) =>
        !Directory.Exists(keyDir) ? Enumerable.Empty<string>()
            : Directory.EnumerateDirectories(keyDir).SelectMany(Directory.EnumerateDirectories).Where(d => File.Exists(Path.Combine(d, CheckpointJson)));

    static long Bytes(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    /// <summary>(number of checkpoint folders, total bytes of their files) of one key.</summary>
    public (int count, long bytes) Size(string key)
    {
        var folders = CheckpointFolders(Path.Combine(BaseDir, key)).ToList();
        return (folders.Count, folders.Sum(Bytes));
    }

    /// <summary>Moves every session of <paramref name="oldKey"/> under <paramref name="newKey"/> and prepends the backup folder to
    /// their base roots (after a Save Over the old checkpoints sit on top of the backup, not of the changed database).</summary>
    public void Rekey(string oldKey, string newKey, string backupFolder)
    {
        string oldDir = Path.Combine(BaseDir, oldKey), newDir = Path.Combine(BaseDir, newKey);
        if (!Directory.Exists(oldDir)) return;
        Directory.CreateDirectory(newDir);
        foreach (var sdir in Directory.GetDirectories(oldDir))
        {
            string dst = Path.Combine(newDir, Path.GetFileName(sdir));
            try { Directory.Move(sdir, dst); }
            catch (IOException)
            {
                CopyDir(sdir, dst);
                Directory.Delete(sdir, true);
            }
            string sj = Path.Combine(dst, SessionJson);
            var sess = Read<SessionJsonData>(sj);
            if (sess == null) continue;
            sess.BaseRoots = sess.BaseRoots.Prepend(backupFolder).ToArray();
            WriteJson(sj, sess);
        }
        try { Directory.Delete(oldDir, false); } catch (IOException) { }
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Deletes checkpoint folders older than <paramref name="maxAge"/>, then the oldest ones (not of
    /// <paramref name="keepSession"/>, never the newest of all) while the total exceeds <paramref name="maxBytes"/>. Folders that
    /// cannot be deleted are skipped. Returns the number of folders removed.</summary>
    public int Prune(TimeSpan maxAge, long maxBytes, string? keepSession)
    {
        if (!Directory.Exists(BaseDir)) return 0;
        var items = new List<(string folder, string session, DateTime time, long bytes)>();
        foreach (var keyDir in Directory.EnumerateDirectories(BaseDir))
            foreach (var f in CheckpointFolders(keyDir))
            {
                var ck = Read<CkJson>(Path.Combine(f, CheckpointJson));
                DateTime t;
                try { t = ck != null ? DateTime.Parse(ck.Time, null, System.Globalization.DateTimeStyles.RoundtripKind) : Directory.GetLastWriteTime(f); }
                catch (FormatException) { t = Directory.GetLastWriteTime(f); }
                long bytes = 0;
                try { bytes = Bytes(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                items.Add((f, Path.GetFileName(Path.GetDirectoryName(f))!, t, bytes));
            }
        items.Sort((a, b) => a.time.CompareTo(b.time));
        int removed = 0;
        long total = items.Sum(i => i.bytes);
        bool Delete(string f)
        {
            try { Directory.Delete(f, true); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        var now = DateTime.Now;
        var left = new List<(string folder, string session, DateTime time, long bytes)>();
        foreach (var it in items)
        {
            bool old = maxAge != TimeSpan.MaxValue && now - it.time > maxAge;
            if (old && Delete(it.folder)) { removed++; total -= it.bytes; }
            else left.Add(it);
        }
        for (int i = 0; i < left.Count - 1 && total > maxBytes; i++)   // left is oldest first; the newest one always stays
        {
            if (keepSession != null && left[i].session == keepSession) continue;
            if (Delete(left[i].folder)) { removed++; total -= left[i].bytes; }
        }

        foreach (var keyDir in Directory.EnumerateDirectories(BaseDir))
        {
            foreach (var sdir in Directory.EnumerateDirectories(keyDir))
                if (!CheckpointFolders(keyDir).Any(c => Path.GetDirectoryName(c) == sdir))
                    try { Directory.Delete(sdir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { if (!Directory.EnumerateFileSystemEntries(keyDir).Any()) Directory.Delete(keyDir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }
}
