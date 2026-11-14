using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SessionPurge.Tests
{
    internal static partial class TestMain
    {
        private static string DesktopDatabase(string home, params string[][] rows)
        {
            Directory.CreateDirectory(Path.Combine(home, "sqlite"));
            string path = Path.Combine(home, "sqlite", "codex.db");
            using (var db = new SqliteDatabase(path, true))
            {
                db.Query("create table local_thread_catalog_hosts (host_id text primary key, host_kind text not null)");
                db.Query("create table local_thread_catalog (host_id text not null, thread_id text not null, display_title text, primary key (host_id, thread_id))");
                db.Query("insert into local_thread_catalog_hosts values ('local', 'local')");
                db.Query("insert into local_thread_catalog_hosts values ('chatgpt:acct', 'chatgpt')");
                foreach (var row in rows) db.Query("insert into local_thread_catalog values (?1, ?2, ?3)", row);
            }
            return path;
        }
        private static List<string> DesktopRows(string path)
        {
            using (var db = new SqliteDatabase(path)) return db.Query("select host_id || '/' || thread_id from local_thread_catalog order by 1");
        }
        private static void DesktopCleaning()
        {
            string home = Home(); Codex(home, A, false);
            string path = DesktopDatabase(home, new[] { "local", A, "kept" }, new[] { "local", B, "stale" }, new[] { "local", C, "stale" }, new[] { "chatgpt:acct", B, "account chat" }, new[] { "chatgpt:acct", Other, "account chat" });
            string cache = Path.Combine(Home(), "Default"); string kept = Path.Combine(cache, "Network", "cookies");
            Directory.CreateDirectory(Path.Combine(cache, "Cache", "nested")); Directory.CreateDirectory(Path.Combine(cache, "Code Cache")); Directory.CreateDirectory(Path.GetDirectoryName(kept));
            File.WriteAllText(Path.Combine(cache, "Cache", "nested", "f_1"), Body); File.WriteAllText(Path.Combine(cache, "Code Cache", "x"), Body); File.WriteAllText(kept, Body);
            var catalog = Scan(home, Provider.Codex);

            var busy = new HistoryEngine(delegate { return "Files of this session are open in ChatGPT (PID 1). Close them and rescan."; }, delegate { return Now; });
            var refused = busy.CleanDesktop(catalog, new[] { cache });
            Assert(refused.Stopped != null && refused.Stopped.Contains("running") && DesktopRows(path).Count == 5 && File.Exists(Path.Combine(cache, "Code Cache", "x")), "A running desktop app did not stop the cleanup.");

            var engine = new HistoryEngine(delegate { return null; }, delegate { return Now; });
            var result = engine.CleanDesktop(catalog, new[] { cache });
            Assert(result.Stopped == null && result.Entries == 2 && result.Caches == 2, "Desktop cleanup counts are wrong: " + result.Describe());
            Assert(DesktopRows(path).SequenceEqual(new[] { "chatgpt:acct/" + B, "chatgpt:acct/" + Other, "local/" + A }.OrderBy(s => s, StringComparer.Ordinal)), "Only stale local entries should be removed.");
            Assert(!Directory.GetFileSystemEntries(Path.Combine(cache, "Cache")).Any() && File.Exists(kept), "Caches were not emptied, or other app data was touched.");
            Assert(engine.CleanDesktop(catalog, new[] { cache }).Entries == 0, "A second cleanup should find nothing.");

            Codex(home, Other, false);
            DesktopDatabase(Home()); // unrelated database elsewhere stays untouched
            using (var db = new SqliteDatabase(path)) db.Query("insert into local_thread_catalog values ('local', ?1, 'new')", Other);
            Assert(engine.CleanDesktop(catalog, new[] { cache }).Entries == 0 && DesktopRows(path).Contains("local/" + Other), "A thread created after the scan was treated as deleted.");

            string linked = Home(); Codex(linked, A, false);
            string real = Path.Combine(Home(), "elsewhere"); Directory.CreateDirectory(Path.Combine(real, "Cache")); File.WriteAllText(Path.Combine(real, "Cache", "keep"), Body);
            string through = Path.Combine(Home(), "Default"); Directory.CreateDirectory(through);
            MakeJunction(Path.Combine(through, "Cache"), Path.Combine(real, "Cache"));
            DesktopDatabase(linked);
            var skipped = engine.CleanDesktop(Scan(linked, Provider.Codex), new[] { through });
            Assert(skipped.Stopped == null && File.Exists(Path.Combine(real, "Cache", "keep")), "A cache folder behind a junction was emptied.");
            string behind = Path.Combine(Home(), "behind"); MakeJunction(behind, Path.Combine(real, ".."));
            Assert(File.Exists(Path.Combine(real, "Cache", "keep")) && engine.CleanDesktop(Scan(linked, Provider.Codex), new[] { Path.Combine(behind, "elsewhere") }).Caches == 0 && File.Exists(Path.Combine(real, "Cache", "keep")), "A cache root behind a junction was emptied.");

            string unrecognized = Home(); Codex(unrecognized, A, false); Directory.CreateDirectory(Path.Combine(unrecognized, "sessions", "2026", "01", "01", "stray"));
            DesktopDatabase(unrecognized, new[] { "local", B, "stale" });
            Refused(() => engine.CleanDesktop(Scan(unrecognized, Provider.Codex)));
            Assert(DesktopRows(Path.Combine(unrecognized, "sqlite", "codex.db")).Count == 1, "An unrecognized history folder item did not stop the cleanup.");

            string foreign = Home(); Codex(foreign, A, false); Directory.CreateDirectory(Path.Combine(foreign, "sqlite")); File.WriteAllText(Path.Combine(foreign, "sqlite", "codex.db"), Body);
            var broken = engine.CleanDesktop(Scan(foreign, Provider.Codex), new string[0]);
            Assert(broken.Stopped != null && File.ReadAllText(Path.Combine(foreign, "sqlite", "codex.db")) == Body, "A file that is not a database was changed.");
            Refused(() => engine.CleanDesktop(Scan(Home(), Provider.Claude)));
        }
    }
}
