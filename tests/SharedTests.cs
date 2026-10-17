using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SessionPurge.Tests
{
    internal static partial class TestMain
    {
        private static string HistoryRow(Provider provider, string id)
        {
            return provider == Provider.Claude ? "{\"display\":\"fixture prompt\",\"pastedContents\":{},\"project\":\"fixture-project\",\"timestamp\":1760000000000,\"sessionId\":" + Quote(id) + "}\r\n" : "{\"session_id\":" + Quote(id) + ",\"ts\":1760000000,\"text\":\"fixture prompt\"}\r\n";
        }
        private static void WriteStore(string home, string relative, string text)
        {
            File.WriteAllText(Path.Combine(home, relative), text, new UTF8Encoding(false));
        }
        private static void RoutineRecords()
        {
            string claude = ClaudeLine(A).Replace("2.1.32", "2.1.400").Replace(Quote(Body), "[{\"type\":\"tool_use\",\"id\":\"tool-fixture\",\"name\":\"Read\",\"input\":{\"file_path\":\"NEVER_FOLLOW_THIS_PATH\"}}]") + Title("custom-title", "customTitle", A, "Fixture title");
            Assert(Named(Provider.Claude, claude).Eligible, "Routine Claude tool envelope rejected.");
            string codex = CodexText(A).Replace("0.130.0", "0.140.2") + "{\"timestamp\":\"2026-01-01T12:01:00Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"fixture-turn\",\"cwd\":\"NEVER_FOLLOW_THIS_PATH\",\"model\":\"fixture-model\",\"sandbox_policy\":{\"type\":\"workspace-write\"}}}\n" + "{\"timestamp\":\"2026-01-01T12:01:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"exec_command\",\"call_id\":\"fixture-tool\",\"arguments\":\"NEVER_EXECUTE_THIS\"}}\n";
            Assert(Named(Provider.Codex, codex).Eligible, "Routine Codex context/tool envelope rejected.");
            foreach (string item in new[] { "{\"type\":\"reasoning\",\"summary\":[],\"content\":null,\"encrypted_content\":\"fixture\"}", "{\"type\":\"function_call_output\",\"call_id\":\"fixture-tool\",\"output\":\"fixture output\"}", "{\"type\":\"custom_tool_call_output\",\"call_id\":\"fixture-tool\",\"output\":\"fixture output\"}", "{\"type\":\"web_search_call\",\"status\":\"completed\",\"action\":{}}", "{\"type\":\"local_shell_call\",\"call_id\":\"fixture-tool\",\"status\":\"completed\",\"action\":{}}" })
                Assert(Named(Provider.Codex, codex + "{\"timestamp\":\"2026-01-01T12:02:00Z\",\"type\":\"response_item\",\"payload\":" + item + "}\n").Eligible, "Nameless routine Codex item rejected.");
            Assert(!Named(Provider.Codex, codex.Replace("exec_command", "spawn_agent")).Eligible, "Delegation bypassed ownership checks.");
            Assert(!Named(Provider.Codex, codex.Replace("exec_command", "functions.Spawn_Agent")).Eligible, "Namespaced delegation bypassed ownership checks.");
            Assert(!Named(Provider.Claude, claude.Replace("\"name\":\"Read\"", "\"name\":\"tools.Agent\"")).Eligible, "Namespaced Claude delegation bypassed ownership checks.");
            string snapshot = "{\"type\":\"file-history-snapshot\",\"messageId\":" + Quote(C) + ",\"snapshot\":{\"messageId\":" + Quote(C) + ",\"trackedFileBackups\":{},\"timestamp\":\"2026-01-01T12:00:00Z\"},\"isSnapshotUpdate\":false}\n";
            Assert(Named(Provider.Claude, snapshot + claude).Eligible, "Snapshot forward reference to owned message refused.");
            Assert(!Named(Provider.Claude, snapshot.Replace(C, B) + claude).Eligible, "Snapshot foreign message reference accepted.");
        }
        private static void ClaudeSharedDeletion() { SharedDeletion(Provider.Claude); }
        private static void CodexSharedDeletion() { SharedDeletion(Provider.Codex); }
        private static void SharedDeletion(Provider provider)
        {
            string home = Home(); string first = provider == Provider.Claude ? Claude(home, A) : Codex(home, A, false); string second = provider == Provider.Claude ? Claude(home, B) : Codex(home, B, false);
            string peer = File.ReadAllText(second); string retained = HistoryRow(provider, B); string orphan = HistoryRow(provider, C);
            WriteStore(home, "history.jsonl", HistoryRow(provider, A) + retained + HistoryRow(provider, A) + orphan);
            if (provider == Provider.Codex) WriteStore(home, "session_index.jsonl", IndexLine(A, "Old title") + IndexLine(B, "Peer title") + IndexLine(A, "Latest title"));
            var catalog = Scan(home, provider); Assert(catalog.Sessions.All(s => s.Eligible), string.Join("; ", catalog.Sessions.Select(s => s.Blocked).ToArray()));
            var plan = Plan(catalog, A);
            Assert(plan.StoreEdits.Count == (provider == Provider.Claude ? 1 : 2) && plan.StoreEdits.All(e => e.RemovedRows == 2), "Preview record counts wrong.");
            Assert(plan.Describe().Contains("history.jsonl") && !plan.Describe().Contains("fixture prompt"), "Preview leaked body or omitted store.");
            var result = Delete(plan);
            Assert(result.Deleted == 1 && result.Stopped == null && !File.Exists(first), result.Describe());
            Assert(File.ReadAllText(second) == peer && File.ReadAllText(Path.Combine(home, "history.jsonl")) == retained + orphan, "Unselected data changed.");
            if (provider == Provider.Codex) Assert(File.ReadAllText(Path.Combine(home, "session_index.jsonl")) == IndexLine(B, "Peer title"), "Unselected title rows changed.");
            Assert(Directory.GetFiles(home, "*", SearchOption.AllDirectories).Length == (provider == Provider.Claude ? 2 : 3), "Unexpected application residual files.");
        }
        private static void ProjectIndexDeletion()
        {
            string home = Home(); string first = Claude(home, A); Claude(home, B);
            string retained = "{\"sessionId\":" + Quote(B) + ",\"fileMtime\":9007199254740993,\"summary\":\"Escaped \\\"entry, ] }\\\"\",\"projectPath\":\"NEVER_FOLLOW\"}";
            string index = "{\"version\":1,\"entries\":[{\"sessionId\":" + Quote(A) + "}," + retained + "],\"originalPath\":\"NEVER_FOLLOW\"}\n";
            string path = Path.Combine(Path.GetDirectoryName(first), "sessions-index.json"); File.WriteAllText(path, index, new UTF8Encoding(false));
            var catalog = Scan(home, Provider.Claude); Assert(catalog.Sessions.All(s => s.Eligible), "Valid project index rejected.");
            var result = Delete(Plan(catalog, A)); Assert(result.Deleted == 1 && result.Stopped == null, result.Describe());
            string after = File.ReadAllText(path); Assert(after.Contains(retained) && !after.Contains(A) && after.Contains("NEVER_FOLLOW"), "Raw retained values or outer fields changed.");
        }
        private static void CoordinatedRollback()
        {
            for (int phase = 0; phase <= 3; phase++)
            {
                string home = Home(); string first = Claude(home, A); string second = Claude(home, B); string history = HistoryRow(Provider.Claude, A) + HistoryRow(Provider.Claude, B); WriteStore(home, "history.jsonl", history);
                var plan = Plan(Scan(home, Provider.Claude), A, B);
                var result = Engine.Delete(plan, "DELETE", CancellationToken.None, delegate(int step) { if (step == phase) throw new IOException("synthetic failure"); });
                Assert(result.Deleted == 0 && result.Stopped != null && File.Exists(first) && File.Exists(second) && File.ReadAllText(Path.Combine(home, "history.jsonl")) == history, "Rollback failed at step " + phase + ": " + result.Describe());
                Assert(Delete(Plan(Scan(home, Provider.Claude), A, B)).Deleted == 2, "Retry after rollback failed.");
            }
            string canceled = Home(); string transcript = Claude(canceled, A); string before = HistoryRow(Provider.Claude, A); WriteStore(canceled, "history.jsonl", before);
            using (var cancel = new CancellationTokenSource())
            {
                var result = Engine.Delete(Plan(Scan(canceled, Provider.Claude), A), "DELETE", cancel.Token, delegate(int step) { if (step == 1) cancel.Cancel(); });
                Assert(result.Deleted == 0 && File.Exists(transcript) && File.ReadAllText(Path.Combine(canceled, "history.jsonl")) == before, "Cancellation failed rollback.");
            }
        }
        private static void CoordinatedRaces()
        {
            string home = Home(); string transcript = Codex(home, A, false); string history = HistoryRow(Provider.Codex, A); WriteStore(home, "history.jsonl", history);
            var plan = Plan(Scan(home, Provider.Codex), A); string path = Path.Combine(home, "history.jsonl"); DateTime stamp = File.GetLastWriteTimeUtc(path);
            WriteStore(home, "history.jsonl", history.Replace("fixture prompt", "changed prompt")); File.SetLastWriteTimeUtc(path, stamp);
            Assert(Delete(plan).Deleted == 0 && File.Exists(transcript), "Shared hash mutation missed.");
            plan = Plan(Scan(home, Provider.Codex), A);
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) Assert(Delete(plan).Deleted == 0 && File.Exists(transcript), "Shared writer ignored.");
        }
        private static void CoordinatedInventory()
        {
            string home = Home(); string transcript = Claude(home, A); string history = HistoryRow(Provider.Claude, A); WriteStore(home, "history.jsonl", history);
            var result = Engine.Delete(Plan(Scan(home, Provider.Claude), A), "DELETE", CancellationToken.None, delegate(int step) { if (step == 2) WriteStore(home, "future-state", "fixture"); });
            Assert(result.Deleted == 0 && File.Exists(transcript) && File.ReadAllText(Path.Combine(home, "history.jsonl")) == history, "Final inventory failed rollback: " + result.Describe());
        }
        private static void CoordinatedCrash()
        {
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B); string history = HistoryRow(Provider.Claude, A) + HistoryRow(Provider.Claude, B); WriteStore(home, "history.jsonl", history);
            string marker = Path.Combine(work, "atomic-marker");
            var start = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--atomic-child \"" + home + "\" \"" + marker + "\"") { UseShellExecute = false, CreateNoWindow = true };
            using (var child = Process.Start(start)) { if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Atomic child interruption timed out."); } }
            Assert(File.Exists(marker) && File.Exists(first) && File.Exists(second) && File.ReadAllText(Path.Combine(home, "history.jsonl")) == history, "Process termination did not roll back staged deletion and write.");
            Assert(Directory.GetFiles(home, "*", SearchOption.AllDirectories).Length == 3, "Application temporary files remained.");
            Assert(Delete(Plan(Scan(home, Provider.Claude), A, B)).Deleted == 2, "Retry after process termination failed.");
        }
        private static void SharedOwnership()
        {
            foreach (string index in new[] {
                "{\"version\":2,\"entries\":[]}",
                "{\"version\":1,\"entries\":[{\"sessionId\":" + Quote(A) + ",\"fullPath\":\"NEVER_FOLLOW\\\\" + B + ".jsonl\"}]}",
                "{\"version\":1,\"entries\":[{\"sessionId\":" + Quote(A) + ",\"isSidechain\":true}]}",
                "{\"version\":1,\"entries\":[{\"sessionId\":" + Quote(A) + "},{\"sessionId\":" + Quote(A) + "}]}",
                "{\"version\":1,\"entries\":[],\"future_owner\":\"unknown\"}" })
            {
                string home = Home(); string transcript = Claude(home, A); string path = Path.Combine(Path.GetDirectoryName(transcript), "sessions-index.json"); File.WriteAllText(path, index);
                Blocked(home, Provider.Claude); Assert(File.ReadAllText(path) == index && File.Exists(transcript), "Unsupported project index mutated.");
            }
            foreach (Provider provider in new[] { Provider.Claude, Provider.Codex })
            {
                string home = Home(); if (provider == Provider.Claude) Claude(home, A); else Codex(home, A, false);
                WriteStore(home, "history.jsonl", HistoryRow(provider, A).Replace(A, "unknown-owner")); Blocked(home, provider);
                WriteStore(home, "history.jsonl", HistoryRow(provider, A));
                string outside = Path.Combine(work, "shared-hardlink-" + Guid.NewGuid().ToString("N")); Assert(CreateHardLink(outside, Path.Combine(home, "history.jsonl"), IntPtr.Zero), "Shared fixture hard link failed.");
                Blocked(home, provider); Assert(File.ReadAllText(outside) == HistoryRow(provider, A), "Shared hard link changed.");
            }
        }
        private static void SharedPreflight()
        {
            string home = Home(); string transcript = Codex(home, A, false); WriteStore(home, "history.jsonl", HistoryRow(Provider.Codex, A)); WriteStore(home, "session_index.jsonl", IndexLine(B, "Unselected title"));
            var plan = Plan(Scan(home, Provider.Codex), A); string path = Path.Combine(home, "history.jsonl");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try { Assert(Delete(plan).Deleted == 0 && File.Exists(transcript), "Read-only shared target ignored."); }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
            plan = Plan(Scan(home, Provider.Codex), A);
            using (var writer = new FileStream(Path.Combine(home, "session_index.jsonl"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) Assert(Delete(plan).Deleted == 0 && File.Exists(transcript), "Unchanged shared peer was not guarded.");
        }
    }
}
