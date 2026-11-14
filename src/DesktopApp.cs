using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SessionPurge
{
    // Minimal wrapper over the SQLite library that ships with Windows (winsqlite3.dll); nothing is downloaded.
    internal sealed class SqliteDatabase : IDisposable
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int length, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
        private IntPtr handle;
        private static byte[] Utf8(string text) { return Encoding.UTF8.GetBytes(text + "\0"); }

        internal SqliteDatabase(string path, bool create = false)
        {
            if (sqlite3_open_v2(Utf8(path), out handle, create ? 6 : 2, IntPtr.Zero) != 0) { Dispose(); throw new SafetyException("The Codex desktop database could not be opened."); }
            sqlite3_busy_timeout(handle, 2000);
        }
        // Runs one statement and returns the first column of every row as text (null for NULL).
        internal List<string> Query(string sql, params string[] values)
        {
            IntPtr statement;
            if (sqlite3_prepare_v2(handle, Utf8(sql), -1, out statement, IntPtr.Zero) != 0) { if (statement != IntPtr.Zero) sqlite3_finalize(statement); throw new SafetyException("The Codex desktop database layout is not supported."); }
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    byte[] text = Encoding.UTF8.GetBytes(values[i]);
                    if (sqlite3_bind_text(statement, i + 1, text, text.Length, new IntPtr(-1)) != 0) throw new SafetyException("The Codex desktop database layout is not supported.");
                }
                var rows = new List<string>();
                for (int code; (code = sqlite3_step(statement)) != 101; )
                {
                    if (code != 100) throw new SafetyException(code == 5 || code == 6 ? "The Codex desktop database is busy; another process is using it." : "The Codex desktop database could not be changed (SQLite error " + code + ").");
                    IntPtr text = sqlite3_column_text(statement, 0);
                    if (text == IntPtr.Zero) { rows.Add(null); continue; }
                    var bytes = new byte[sqlite3_column_bytes(statement, 0)];
                    Marshal.Copy(text, bytes, 0, bytes.Length);
                    rows.Add(Encoding.UTF8.GetString(bytes));
                }
                return rows;
            }
            finally { sqlite3_finalize(statement); }
        }
        public void Dispose()
        {
            if (handle != IntPtr.Zero) { sqlite3_close_v2(handle); handle = IntPtr.Zero; }
        }
    }

    internal sealed class DesktopCleanup
    {
        internal int Entries;
        internal int Caches;
        internal List<string> Notes = new List<string>();
        internal string Stopped;
        private static readonly string[] CacheFolders = { "Cache", "Code Cache", "GPUCache" };

        internal static string DatabasePath(string home) { return Path.Combine(home, "sqlite", "codex.db"); }
        internal static IEnumerable<string> CacheRoots()
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            try { return Directory.Exists(packages) ? Directory.GetDirectories(packages, "OpenAI.Codex_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Codex", "web", "Codex", "Default")).Where(Directory.Exists).ToList() : new List<string>(); }
            catch (IOException) { return new List<string>(); }
            catch (UnauthorizedAccessException) { return new List<string>(); }
        }

        // Removes local-host chat-list rows whose thread has no rollout in the scanned home, then empties the
        // desktop app's web caches. Refuses while any process has the app database open.
        internal static DesktopCleanup Run(string home, ICollection<string> liveThreads, IEnumerable<string> cacheRoots, Func<IEnumerable<string>, string> openBy)
        {
            var result = new DesktopCleanup();
            string database = DatabasePath(home);
            if (!File.Exists(database)) { result.Notes.Add("No Codex desktop chat list was found in this home."); return result; }
            if (!PlainPath(home, database)) { result.Stopped = "The Codex desktop database sits behind a link and was left alone."; return result; }
            var roots = new List<string>();
            foreach (string root in cacheRoots)
            {
                if (PlainPath(Path.GetPathRoot(root), root)) roots.Add(root);
                else result.Notes.Add("A cache location sits behind a link and was left alone.");
            }
            string holders = openBy(new[] { database, database + "-wal", database + "-shm" });
            if (holders != null) { result.Stopped = "The Codex desktop app is running. " + holders.Replace(" Close them and rescan.", " Quit it from its tray icon and try again."); return result; }
            var live = new HashSet<string>(liveThreads, StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var db = new SqliteDatabase(database))
                {
                    db.Query("pragma locking_mode = exclusive");
                    db.Query("BEGIN IMMEDIATE");
                    bool committed = false;
                    try
                    {
                        // The first check can race with the app starting; with the database locked, ask again.
                        string again = openBy(new[] { database, database + "-wal", database + "-shm" });
                        if (again != null) throw new SafetyException("The Codex desktop app opened the chat list during cleanup. Quit it from its tray icon and try again.");
                        var local = db.Query("select c.thread_id from local_thread_catalog c join local_thread_catalog_hosts h on h.host_id = c.host_id where h.host_kind = 'local'");
                        var stale = local.Where(id => id != null && !live.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
                        foreach (string id in stale) db.Query("delete from local_thread_catalog where thread_id = ?1 and host_id in (select host_id from local_thread_catalog_hosts where host_kind = 'local')", id);
                        if (db.Query("pragma quick_check").FirstOrDefault() != "ok") throw new SafetyException("The Codex desktop database failed its integrity check; nothing was changed.");
                        db.Query("COMMIT");
                        committed = true;
                        result.Entries = stale.Count;
                    }
                    finally { if (!committed) { try { db.Query("ROLLBACK"); } catch (SafetyException) { } } }
                    try { db.Query("pragma wal_checkpoint(truncate)"); } catch (SafetyException) { }
                }
                result.Notes.Add(result.Entries + " stale chat-list entr" + (result.Entries == 1 ? "y" : "ies") + " removed; entries from other hosts such as ChatGPT were kept.");
            }
            catch (SafetyException exception) { result.Stopped = exception.Message; return result; }
            foreach (string root in roots)
                foreach (string name in CacheFolders)
                {
                    string folder = Path.Combine(root, name);
                    if (!Directory.Exists(folder)) continue;
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { result.Notes.Add(name + " is a link and was left alone."); continue; }
                    result.Caches += Empty(folder, result);
                }
            result.Notes.Add(result.Caches + " cached file(s) cleared.");
            return result;
        }
        // True when no folder or file from below the stop point down to the path is a link or junction.
        private static bool PlainPath(string stop, string path)
        {
            stop = (stop ?? "").TrimEnd('\\');
            string current = Path.GetFullPath(path).TrimEnd('\\');
            while (current.Length > stop.Length && current.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(current) || File.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                }
                current = Path.GetDirectoryName(current);
                if (current == null) break;
            }
            return true;
        }
        private static int Empty(string folder, DesktopCleanup result)
        {
            int removed = 0;
            foreach (string entry in Directory.GetFileSystemEntries(folder))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { Directory.Delete(entry); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { removed += Empty(entry, result); Directory.Delete(entry); }
                    else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); removed++; }
                }
                catch (IOException) { result.Notes.Add("A cached file was in use and was skipped."); }
                catch (UnauthorizedAccessException) { result.Notes.Add("A cached file could not be removed and was skipped."); }
            }
            return removed;
        }
        internal string Describe()
        {
            return (Stopped != null ? "Not cleaned: " + Stopped : string.Join("\r\n", Notes.Distinct().ToArray()));
        }
    }
}
