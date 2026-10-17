using System;
using System.Collections.Generic;
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
            Assert(Named(Provider.Codex, codex.Replace("exec_command", "functions.Spawn_Agent")).Eligible, "A Codex thread without live subagent threads was refused.");
            Assert(!Named(Provider.Claude, claude.Replace("\"type\":\"user\"", "\"type\":\"assistant\"").Replace("\"role\":\"user\"", "\"role\":\"assistant\"").Replace("\"name\":\"Read\"", "\"name\":\"tools.Agent\"")).Eligible, "Namespaced Claude delegation bypassed the subagent check.");
            string snapshot = "{\"type\":\"file-history-snapshot\",\"messageId\":" + Quote(C) + ",\"snapshot\":{\"messageId\":" + Quote(C) + ",\"trackedFileBackups\":{},\"timestamp\":\"2026-01-01T12:00:00Z\"},\"isSnapshotUpdate\":false}\n";
            Assert(Named(Provider.Claude, snapshot + claude).Eligible, "Snapshot forward reference to owned message refused.");
            Assert(!Named(Provider.Claude, snapshot.Replace(C, B) + claude).Eligible, "Snapshot foreign message reference accepted.");
            Assert(!Named(Provider.Claude, claude + "{\"type\":\"summary\",\"leafUuid\":" + Quote(B) + ",\"summary\":\"Foreign\"}\n").Eligible, "Summary of another transcript accepted.");
            Assert(!Named(Provider.Claude, claude.Replace("\"parentUuid\":null", "\"parentUuid\":null,\"sourceToolAssistantUUID\":" + Quote(B))).Eligible, "Tool result from another transcript accepted.");
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
            using (var child = Process.Start(start)) { if (!child.WaitForExit((int)HangLimit.TotalMilliseconds)) { child.Kill(); throw new Exception("Atomic child interruption timed out."); } }
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
                "{\"version\":1,\"entries\":[{\"sessionId\":" + Quote(A) + "},{\"sessionId\":" + Quote(A) + "}]}" })
            {
                string home = Home(); string transcript = Claude(home, A); string path = Path.Combine(Path.GetDirectoryName(transcript), "sessions-index.json"); File.WriteAllText(path, index);
                Blocked(home, Provider.Claude); Assert(File.ReadAllText(path) == index && File.Exists(transcript), "Unsupported project index mutated.");
            }
            foreach (Provider provider in new[] { Provider.Claude, Provider.Codex })
            {
                string home = Home(); if (provider == Provider.Claude) Claude(home, A); else Codex(home, A, false);
                string foreign = HistoryRow(provider, A).Replace(A, "unknown-owner") + "{\"future_field\":true," + HistoryRow(provider, A).Substring(1);
                WriteStore(home, "history.jsonl", foreign);
                var tolerant = Scan(home, provider);
                Assert(tolerant.Sessions.Single().Eligible && Plan(tolerant, A).StoreEdits.Single().RemovedRows == 1, "Rows without an owner or with new fields were not tolerated.");
                string folder = Home(); Put(folder, Provider.Claude, A, ClaudeLine(A), false); WriteStore(folder, Path.Combine("projects", "fixture-project", "sessions-index.json"), "{\"version\":1,\"entries\":[{\"sessionId\":" + Quote(A) + ",\"futureField\":1}],\"futureRoot\":true}");
                if (provider == Provider.Claude) Assert(Plan(Scan(folder, Provider.Claude), A).StoreEdits.Single().RemovedRows == 1, "Project index with new fields was not tolerated.");
                WriteStore(home, "history.jsonl", HistoryRow(provider, A));
                string outside = Path.Combine(work, "shared-hardlink-" + Guid.NewGuid().ToString("N")); Assert(CreateHardLink(outside, Path.Combine(home, "history.jsonl"), IntPtr.Zero), "Shared fixture hard link failed.");
                Blocked(home, provider); Assert(File.ReadAllText(outside) == HistoryRow(provider, A), "Shared hard link changed.");
            }
        }
        private const string M1 = "44444444-4444-4444-8444-444444444444", M2 = "55555555-5555-4555-8555-555555555555", M3 = "66666666-6666-4666-8666-666666666666";
        private static string Envelope(string type, string uuid, string parent)
        {
            return "\"parentUuid\":" + (parent == null ? "null" : Quote(parent)) + ",\"isSidechain\":false,\"userType\":\"external\",\"cwd\":\"C:\\\\fixture\",\"sessionId\":" + Quote(A) + ",\"version\":\"2.1.301\",\"gitBranch\":\"main\",\"entrypoint\":\"claude-vscode\",\"type\":" + Quote(type) + ",\"uuid\":" + Quote(uuid) + ",\"timestamp\":\"2026-01-01T12:00:00Z\"";
        }
        private static void ModernClaudeHome()
        {
            string home = Home();
            string transcript =
                "{" + Envelope("user", M1, null) + ",\"perTurnEffort\":\"high\",\"promptId\":\"p\",\"permissionMode\":\"default\",\"origin\":{\"kind\":\"human\"},\"promptSource\":\"typed\",\"turnOrigin\":\"user\",\"turnPosition\":0,\"message\":{\"role\":\"user\",\"content\":\"Plan the release checklist\"}}\n" +
                "{" + Envelope("attachment", M2, M1) + ",\"attachment\":{\"type\":\"fixture\"},\"rendered\":\"x\",\"renderedRole\":\"user\"}\n" +
                "{" + Envelope("assistant", M3, M2) + ",\"perTurnEffort\":\"high\",\"requestId\":\"r\",\"apiBlockIndex\":0,\"effort\":\"high\",\"requestedModel\":\"fixture\",\"requestRef\":\"x\",\"serverClassifierRequest\":{},\"wireToolInputs\":{},\"thinkingDisplay\":\"summary\",\"thinkingDurationMs\":5,\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"x\",\"signature\":\"s\"},{\"type\":\"tool_use\",\"id\":\"t\",\"name\":\"Read\",\"input\":{},\"caller\":{\"type\":\"direct\"}},{\"type\":\"text\",\"text\":\"done\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{},\"container\":null,\"context_management\":null,\"diagnostics\":{},\"stop_details\":{},\"input_transformations\":[]}}\n" +
                "{\"type\":\"api-request\",\"sessionId\":" + Quote(A) + ",\"id\":\"q\",\"timestamp\":\"2026-01-01T12:00:00Z\",\"version\":\"2.1.301\",\"params\":{},\"querySource\":\"repl\",\"requestDigest\":\"d\",\"shapeHash\":\"h\",\"cacheControl\":{},\"keep\":true,\"tail\":[],\"base\":null}\n" +
                "{\"type\":\"api-request-blob\",\"sessionId\":" + Quote(A) + ",\"hash\":\"h\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"Agent\",\"input\":{}},{\"type\":\"tool_addition\",\"tool\":{}}]}}\n" +
                "{\"type\":\"atis-latch\",\"sessionId\":" + Quote(A) + ",\"atis\":{}}\n" +
                "{\"type\":\"last-prompt\",\"sessionId\":" + Quote(A) + ",\"leafUuid\":" + Quote(M3) + ",\"lastPrompt\":\"Plan the release checklist\"}\n" +
                "{\"type\":\"mode\",\"sessionId\":" + Quote(A) + ",\"mode\":\"default\"}\n" +
                "{\"type\":\"cost-state\",\"sessionId\":" + Quote(A) + ",\"totalCostUSD\":0,\"modelUsage\":{},\"hasUnknownModelCost\":false}\n" +
                "{\"type\":\"file-history-delta\",\"messageId\":" + Quote(M3) + ",\"snapshotMessageId\":" + Quote(M3) + ",\"trackingPath\":\"x\",\"backup\":{},\"timestamp\":\"2026-01-01T12:00:00Z\"}\n" +
                "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"2026-01-01T12:00:00Z\",\"sessionId\":" + Quote(A) + ",\"content\":\"x\",\"commandUuid\":\"c\",\"deliveryId\":\"d\",\"reason\":\"r\"}\n" +
                "{\"type\":\"ai-title\",\"sessionId\":" + Quote(A) + ",\"aiTitle\":\"Release checklist planning\"}\n";
            string file = Put(home, Provider.Claude, A, transcript, false);
            foreach (string folder in new[] { "backups", "cache", "file-history\\" + A, "ide", "session-env\\" + A, "sessions", "plugins", "skills", "projects\\fixture-project\\memory" }) Directory.CreateDirectory(Path.Combine(home, folder));
            foreach (string name in new[] { ".credentials.json", ".last-cleanup", "settings.json", "projects\\fixture-project\\memory\\MEMORY.md" }) File.WriteAllText(Path.Combine(home, name), Body);
            Age(Path.Combine(home, "file-history", A)); Age(Path.Combine(home, "session-env", A));
            var catalog = Scan(home, Provider.Claude);
            var session = catalog.Sessions.Single();
            Assert(session.Eligible, "Current Claude transcript refused: " + session.Blocked);
            Assert(session.Version == "2.1.301" && session.Name == "Release checklist planning" && session.Project == "fixture-project" && session.Structure.Count == 6, "Current Claude transcript metadata missing.");
            var plan = Plan(catalog, A);
            Assert(plan.Artifacts.Count == 2 && plan.Artifacts.All(a => a.Folder) && catalog.Notices.Any(n => n.StartsWith("1 other item")), "Per-session folders or ignored items were not reported.");
            Assert(plan.Describe().Contains("file-history\\" + A + "\\") && plan.Describe().Contains("session-env\\" + A + "\\") && !plan.Describe().Contains("Plan the release"), "Preview did not list per-session folders or leaked content.");
            Assert(Delete(plan).Deleted == 1 && !File.Exists(file), "Current Claude transcript was not deleted.");
            Assert(!Directory.Exists(Path.Combine(home, "file-history", A)) && !Directory.Exists(Path.Combine(home, "session-env", A)) && Directory.Exists(Path.Combine(home, "file-history")), "Per-session folders were not deleted with the transcript.");
            Assert(File.ReadAllText(Path.Combine(home, "projects", "fixture-project", "memory", "MEMORY.md")) == Body && Directory.Exists(Path.Combine(home, "sessions")), "Unrelated folders changed.");
        }
        private static void ModernCodexHome()
        {
            string home = Home();
            string rollout =
                "{\"timestamp\":\"2026-01-01T12:00:00Z\",\"type\":\"session_meta\",\"ordinal\":0,\"payload\":{\"id\":" + Quote(A) + ",\"session_id\":" + Quote(A) + ",\"timestamp\":\"2026-01-01T12:00:00Z\",\"cwd\":\"C:\\\\fixture\\\\app\",\"originator\":\"codex_vscode\",\"cli_version\":\"0.162.0-alpha.17.2\",\"source\":\"vscode\",\"thread_source\":\"user\",\"model_provider\":\"openai\",\"base_instructions\":{\"text\":\"x\",\"provenance\":\"default\"},\"history_mode\":\"paginated\",\"context_window\":1,\"creator_user_id\":\"u\",\"creator_account_id\":\"a\",\"runtime_workspace_roots\":[]}}\n" +
                "{\"timestamp\":\"2026-01-01T12:00:01Z\",\"type\":\"response_item\",\"ordinal\":1,\"metadata\":{},\"payload\":{\"type\":\"message\",\"role\":\"user\",\"id\":\"m\",\"content\":[{\"type\":\"input_text\",\"text\":\"Refactor the parser\"}],\"internal_chat_message_metadata_passthrough\":{}}}\n" +
                "{\"timestamp\":\"2026-01-01T12:00:02Z\",\"type\":\"token_usage_record\",\"ordinal\":2,\"payload\":{\"response_id\":\"r\",\"usage\":{}}}\n" +
                "{\"timestamp\":\"2026-01-01T12:00:03Z\",\"type\":\"world_state\",\"ordinal\":3,\"payload\":{\"full\":true,\"state\":{}}}\n" +
                "{\"timestamp\":\"2026-01-01T12:00:04Z\",\"type\":\"inter_agent_communication_metadata\",\"ordinal\":4,\"payload\":{\"trigger_turn\":\"t\"}}\n" +
                "{\"timestamp\":\"2026-01-01T12:00:05Z\",\"type\":\"event_msg\",\"ordinal\":5,\"payload\":{\"type\":\"item_completed\",\"thread_id\":" + Quote(A) + ",\"turn_id\":\"t\",\"item\":{},\"completed_at_ms\":1}}\n" +
                "{\"timestamp\":\"2026-01-01T12:00:06Z\",\"type\":\"response_item\",\"ordinal\":6,\"payload\":{\"type\":\"agent_message\",\"author\":\"a\",\"recipient\":\"b\",\"id\":\"x\",\"content\":[{\"type\":\"encrypted_content\",\"encrypted_content\":\"x\"}]}}\n" +
                "{\"timestamp\":\"2026-01-01T12:00:07Z\",\"type\":\"compacted\",\"ordinal\":7,\"payload\":{\"message\":\"\",\"replacement_history\":[],\"window_id\":\"w\"}}\n";
            string file = Put(home, Provider.Codex, A, rollout, false);
            foreach (string folder in new[] { ".sandbox", ".sandbox-bin", ".tmp", "cache", "thread-writer-locks", "rollout-migrations", "plugins", "skills" }) Directory.CreateDirectory(Path.Combine(home, folder));
            foreach (string name in new[] { "cap_sid", "installation_id", "models_cache.json", "config.toml", "auth.json", ".sandbox_migration" }) File.WriteAllText(Path.Combine(home, name), Body);
            WriteStore(home, "session_index.jsonl", IndexLine(A, "Parser refactor"));
            var session = Scan(home, Provider.Codex).Sessions.Single();
            Assert(session.Eligible, "Current Codex rollout refused: " + session.Blocked);
            Assert(session.Version == "0.162.0-alpha.17.2" && session.Project == "C:\\fixture\\app" && session.Name == "Parser refactor" && session.Structure.Count == 8, "Current Codex rollout metadata missing.");
            var catalog = Scan(home, Provider.Codex);
            Assert(Delete(Plan(catalog, A)).Deleted == 1 && !File.Exists(file) && File.ReadAllText(Path.Combine(home, "session_index.jsonl")) == "", "Current Codex rollout or its index row was not deleted.");
            File.WriteAllText(Path.Combine(home, "thread_history_1.sqlite"), Body);
            Put(home, Provider.Codex, B, CodexText(B), false);
            Assert(Scan(home, Provider.Codex).Sessions.Single().Blocked.Contains("SQLite"), "Codex SQLite thread store did not block with a specific reason.");
        }
        private static void LivePeer()
        {
            string home = Home(); string selected = Claude(home, A); string live = Claude(home, B); File.SetLastWriteTimeUtc(live, Now);
            var catalog = Scan(home, Provider.Claude);
            Assert(catalog.Sessions.Single(s => s.Id == B).Blocked.Contains("Recently modified"), "Live transcript was not blocked.");
            var result = Engine.Delete(Plan(catalog, A), "DELETE", CancellationToken.None, delegate { File.AppendAllText(live, ClaudeLine(B).Replace(Quote(C), Quote(M1))); });
            Assert(result.Deleted == 1 && !File.Exists(selected) && File.Exists(live), "A transcript being written blocked deletion of another session: " + result.Stopped);
            string stale = Home(); string file = Claude(stale, A);
            var plan = Plan(Scan(stale, Provider.Claude), A);
            Directory.CreateDirectory(Path.Combine(stale, "tasks", A));
            Assert(Delete(plan).Deleted == 0 && File.Exists(file), "A per-session folder created after the review was not disclosed.");
        }
        private static void LateLinks()
        {
            string tasks = Home(); string transcript = Claude(tasks, A); Directory.CreateDirectory(Path.Combine(tasks, "tasks"));
            var result = Engine.Delete(Plan(Scan(tasks, Provider.Claude), A), "DELETE", CancellationToken.None, delegate { Directory.CreateDirectory(Path.Combine(tasks, "tasks", A)); });
            Assert(result.Deleted == 0 && File.Exists(transcript), "A per-session folder created during deletion was not disclosed.");
            string named = Home(); transcript = Claude(named, A); string ignored = Path.Combine(named, "projects", "fixture-project", "artifacts"); Directory.CreateDirectory(ignored);
            result = Engine.Delete(Plan(Scan(named, Provider.Claude), A), "DELETE", CancellationToken.None, delegate { File.WriteAllText(Path.Combine(ignored, "result-" + A + ".json"), Body); });
            Assert(result.Deleted == 0 && File.Exists(transcript), "A file naming the session created after scanning was not detected.");
            string codex = Home(); string selected = Codex(codex, A, false); string blocked = Codex(codex, B, false); File.SetLastWriteTimeUtc(blocked, Now);
            var catalog = Scan(codex, Provider.Codex);
            Assert(catalog.Sessions.Single(s => s.Id == A).Eligible && !catalog.Sessions.Single(s => s.Id == B).Eligible, "Late-link fixture has the wrong eligibility.");
            result = Engine.Delete(Plan(catalog, A), "DELETE", CancellationToken.None, delegate { File.WriteAllText(blocked, CodexText(B).Replace("\"source\":\"cli\"", "\"source\":\"cli\",\"history_base\":{\"thread_id\":\"" + A + "\"}"), new UTF8Encoding(false)); });
            Assert(result.Deleted == 0 && File.Exists(selected), "A rollout that started building on the selection during deletion was ignored.");
            string appended = Home(); selected = Codex(appended, A, false); blocked = Codex(appended, B, false); File.SetLastWriteTimeUtc(blocked, Now);
            result = Engine.Delete(Plan(Scan(appended, Provider.Codex), A), "DELETE", CancellationToken.None, delegate { File.AppendAllText(blocked, CodexText(B).Split('\n')[0].Replace("\"source\":\"cli\"", "\"source\":\"cli\",\"history_base\":{\"thread_id\":\"" + A + "\"}") + "\n", new UTF8Encoding(false)); });
            Assert(result.Deleted == 0 && File.Exists(selected), "A second header appended during deletion was ignored.");
            string claudeHome = Home(); selected = Claude(claudeHome, A); string resumed = Claude(claudeHome, B); File.SetLastWriteTimeUtc(resumed, Now);
            result = Engine.Delete(Plan(Scan(claudeHome, Provider.Claude), A), "DELETE", CancellationToken.None, delegate { File.AppendAllText(resumed, ClaudeLine(B).Replace(Quote(C), Quote(M1)).Replace("\"parentUuid\":null", "\"parentUuid\":" + Quote(C)), new UTF8Encoding(false)); });
            Assert(result.Deleted == 0 && File.Exists(selected), "A transcript that started continuing the selection during deletion was ignored.");
            foreach (bool rewrite in new[] { true, false })
            {
                string peerHome = Home(); selected = Claude(peerHome, A); string peer = Put(peerHome, Provider.Claude, B, ClaudeLine(B).Replace(Quote(C), Quote(M1)), false); File.SetLastWriteTimeUtc(peer, Now);
                string changed = ClaudeLine(B).Replace(Quote(C), Quote(M2)).Replace("\"parentUuid\":null", "\"parentUuid\":" + Quote(C)).TrimEnd('\n');
                result = Engine.Delete(Plan(Scan(peerHome, Provider.Claude), A), "DELETE", CancellationToken.None, delegate { if (rewrite) File.WriteAllText(peer, changed + "\n", new UTF8Encoding(false)); else File.AppendAllText(peer, changed, new UTF8Encoding(false)); });
                Assert(result.Deleted == 0 && File.Exists(selected), rewrite ? "An in-place rewrite of a blocked peer was ignored." : "An unterminated appended record was ignored.");
            }
        }
        private static string[] SessionArtifacts(string home, string id)
        {
            string project = Path.Combine(home, "projects", "fixture-project", id);
            var files = new[]
            {
                Stamp(Path.Combine(project, "subagents", "agent-a1b2c3.jsonl"), "{\"type\":\"user\",\"isSidechain\":true}\n"),
                Stamp(Path.Combine(project, "subagents", "agent-a1b2c3.meta.json"), "{}"),
                Stamp(Path.Combine(project, "tool-results", "toolu_fixture.txt"), Body),
                Stamp(Path.Combine(home, "file-history", id, "0f1e2d3c@v1"), Body),
                Stamp(Path.Combine(home, "file-history", id, "0f1e2d3c@v2"), Body),
                Stamp(Path.Combine(home, "session-env", id, "sessionstart-hook-1.sh"), Body),
                Stamp(Path.Combine(home, "tasks", id, "1.json"), "{}")
            };
            foreach (string folder in new[] { project, Path.Combine(home, "file-history", id), Path.Combine(home, "session-env", id), Path.Combine(home, "tasks", id) }) Age(folder);
            return files;
        }
        private static string DelegatingTranscript(string id)
        {
            return ClaudeLine(id).Replace("\"type\":\"user\"", "\"type\":\"assistant\"").Replace("\"role\":\"user\"", "\"role\":\"assistant\"").Replace(Quote(Body), "[{\"type\":\"tool_use\",\"id\":\"t\",\"name\":\"Agent\",\"input\":{}}]");
        }
        private static void ClaudeSessionArtifacts()
        {
            string home = Home(); string transcript = Put(home, Provider.Claude, A, DelegatingTranscript(A), false); string peer = Claude(home, B);
            string[] files = SessionArtifacts(home, A); string peerFile = Stamp(Path.Combine(home, "projects", "fixture-project", B, "tool-results", "toolu_peer.txt"), Body); Age(Path.Combine(home, "projects", "fixture-project", B));
            var catalog = Scan(home, Provider.Claude);
            Assert(catalog.Sessions.All(s => s.Eligible), "Sessions with their own folders were refused: " + string.Join("; ", catalog.Sessions.Select(s => s.Blocked).ToArray()));
            var plan = Plan(catalog, A);
            string preview = plan.Describe();
            Assert(plan.Transactional && plan.Artifacts.Count(a => a.Folder) == 6 && plan.Artifacts.Count(a => !a.Folder) == files.Length, "Review did not cover every session file and folder.");
            Assert(plan.Artifacts.All(a => preview.Contains(a.RelativePath)) && !preview.Contains(Body), "Review omitted a session file or leaked content.");
            var result = Delete(plan);
            Assert(result.Deleted == 1 && !File.Exists(transcript) && files.All(f => !File.Exists(f)), "Session files were not deleted: " + result.Stopped);
            Assert(!Directory.Exists(Path.Combine(home, "projects", "fixture-project", A)) && !Directory.Exists(Path.Combine(home, "file-history", A)) && !Directory.Exists(Path.Combine(home, "session-env", A)) && !Directory.Exists(Path.Combine(home, "tasks", A)), "Session folders remained.");
            Assert(File.Exists(peer) && File.Exists(peerFile) && Directory.Exists(Path.Combine(home, "file-history")) && Directory.Exists(Path.Combine(home, "tasks")), "Another session or the shared folders changed.");

            string rollback = Home(); transcript = Put(rollback, Provider.Claude, A, DelegatingTranscript(A), false); files = SessionArtifacts(rollback, A);
            result = Engine.Delete(Plan(Scan(rollback, Provider.Claude), A), "DELETE", CancellationToken.None, delegate { throw new IOException("synthetic failure"); });
            Assert(result.Deleted == 0 && File.Exists(transcript) && files.All(File.Exists), "A failure did not roll back the session folders.");

            string changed = Home(); Put(changed, Provider.Claude, A, DelegatingTranscript(A), false); files = SessionArtifacts(changed, A);
            plan = Plan(Scan(changed, Provider.Claude), A);
            File.WriteAllText(files[2], new string('X', Body.Length)); File.SetLastWriteTimeUtc(files[2], Now.AddDays(-10));
            Assert(Delete(plan).Deleted == 0 && File.Exists(files[0]), "A session file changed after the review was deleted.");
            plan = Plan(Scan(changed, Provider.Claude), A);
            Stamp(Path.Combine(changed, "projects", "fixture-project", A, "tool-results", "late.txt"), Body);
            Assert(Delete(plan).Deleted == 0 && File.Exists(files[0]), "A session file added after the review was ignored.");

            string recent = Home(); Put(recent, Provider.Claude, A, DelegatingTranscript(A), false); files = SessionArtifacts(recent, A); File.SetLastWriteTimeUtc(files[3], Now);
            Assert(Scan(recent, Provider.Claude).Sessions.Single().Blocked.Contains("last 15 minutes"), "A recently changed session file did not block.");

            string linked = Home(); Put(linked, Provider.Claude, A, DelegatingTranscript(A), false); files = SessionArtifacts(linked, A);
            string outside = Path.Combine(work, "artifact-link-" + Guid.NewGuid().ToString("N")); Assert(CreateHardLink(outside, files[4], IntPtr.Zero), "Fixture hard link failed.");
            Assert(Scan(linked, Provider.Claude).Sessions.Single().Blocked.Contains("cannot be deleted safely"), "A hard-linked session file did not block.");
            Assert(File.ReadAllText(outside) == Body, "Hard-linked session file changed.");

            string junction = Home(); Put(junction, Provider.Claude, A, DelegatingTranscript(A), false); SessionArtifacts(junction, A); string target = Home();
            string link = Path.Combine(junction, "projects", "fixture-project", A, "tool-results", "linked"); MakeJunction(link, target);
            try { Assert(!Scan(junction, Provider.Claude).Sessions.Single().Eligible, "A junction inside a session folder did not block."); }
            finally { Directory.Delete(link); }

            string loose = Home(); Put(loose, Provider.Claude, A, DelegatingTranscript(A), false);
            Assert(Scan(loose, Provider.Claude).Sessions.Single().Blocked.Contains("not in its session folder"), "Subagents without a session folder were not blocked.");
        }
        private static void RunningClaudeSession()
        {
            string home = Home(); string transcript = Claude(home, A); Claude(home, B);
            Stamp(Path.Combine(home, "sessions", "1.json"), "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"sessionId\":" + Quote(A) + ",\"kind\":\"interactive\"}");
            Stamp(Path.Combine(home, "sessions", "2.json"), "{\"pid\":2147483000,\"sessionId\":" + Quote(B) + "}");
            Stamp(Path.Combine(home, "sessions", "3.key"), "{\"sessionId\":" + Quote(B) + "}");
            var catalog = Scan(home, Provider.Claude);
            Assert(catalog.Sessions.Single(s => s.Id == A).Blocked.Contains("Running in Claude Code"), "The running session was not blocked.");
            Assert(catalog.Sessions.Single(s => s.Id == B).Eligible, "A session whose process has exited was blocked.");
            var plan = Plan(catalog, B);
            Stamp(Path.Combine(home, "sessions", "4.json"), "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"sessionId\":" + Quote(B) + "}");
            Assert(Delete(plan).Deleted == 0 && File.Exists(transcript), "A session resumed after the review was deleted.");
        }
        private static string CodexChild(string id, string parent)
        {
            return CodexText(id).Replace("\"source\":\"cli\"", "\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":" + Quote(parent) + ",\"depth\":1}}},\"parent_thread_id\":" + Quote(parent) + ",\"agent_nickname\":\"Helper\"");
        }
        private static string CodexHome(out string parent, out string first, out string second)
        {
            string home = Home();
            parent = Codex(home, B, false); first = Put(home, Provider.Codex, A, CodexChild(A, B), false); second = Put(home, Provider.Codex, C, CodexChild(C, B), false);
            foreach (string name in new[] { "state_5.sqlite", "state_5.sqlite-wal", "thread_history_1.sqlite" }) File.WriteAllText(Path.Combine(home, name), Body);
            WriteStore(home, "session_index.jsonl", IndexLine(B, "Parent") + IndexLine(A, "Child one") + IndexLine(C, "Child two"));
            return home;
        }
        private static Func<string, string> CodexStub(string home, List<string> calls, Func<string, string> failure)
        {
            return delegate(string id)
            {
                calls.Add(id);
                string mode = failure == null ? null : failure(id);
                if (mode == "keep") return null;
                if (mode != null && mode.StartsWith("error:")) return mode.Substring(6);
                foreach (string path in Directory.GetFiles(Path.Combine(home, "sessions"), "*" + id + ".jsonl", SearchOption.AllDirectories)) File.Delete(path);
                string index = Path.Combine(home, "session_index.jsonl");
                File.WriteAllText(index, string.Concat(File.ReadAllLines(index).Where(line => !line.Contains(id)).Select(line => line + "\n").ToArray()), new UTF8Encoding(false));
                return mode != null && mode.StartsWith("partial:") ? mode.Substring(8) : null;
            };
        }
        private static void CodexCliDeletion()
        {
            string parent, first, second;
            string home = CodexHome(out parent, out first, out second);
            var calls = new List<string>();
            var engine = new HistoryEngine(delegate { return null; }, delegate { return Now; }, CodexStub(home, calls, null));
            var catalog = engine.Scan(Provider.Codex, home, CancellationToken.None);
            Assert(catalog.ViaCodex && catalog.Sessions.All(s => s.Eligible), "Codex sessions with the CLI available were refused: " + string.Join("; ", catalog.Sessions.Select(s => s.Blocked).ToArray()));
            Refused(() => engine.Plan(catalog, catalog.Sessions.Where(s => s.Id == B)));
            var plan = engine.Plan(catalog, catalog.Sessions);
            Assert(plan.ViaCodex && plan.Sessions.Last().Id == B && plan.Describe().Contains("codex delete --force " + B), "Codex plan is not children first or omits the commands.");
            var result = engine.Delete(plan, "DELETE", CancellationToken.None);
            Assert(result.Stopped == null && result.Deleted == 3 && calls.Count == 3 && calls.Last() == B && !File.Exists(parent) && !File.Exists(first) && !File.Exists(second), "Codex CLI deletion failed: " + result.Stopped);
            Assert(result.Notes.Any(n => n.Contains("Rescan confirmed")) && !result.Notes.Any(n => n.Contains("still has")) && result.Notes.Any(n => n.Contains("SQLite")), "Codex deletion was not verified: " + string.Join(" | ", result.Notes.ToArray()));

            foreach (string mode in new[] { "fail", "keep", "extra", "partial", "peer" })
            {
                home = CodexHome(out parent, out first, out second);
                string unselected = Codex(home, "88888888-8888-4888-8888-888888888888", false);
                calls = new List<string>();
                Func<string, string> failure = delegate(string id)
                {
                    if (calls.Count < 2) return null;
                    if (mode == "extra") { File.Delete(parent); return null; }
                    if (mode == "peer") { File.AppendAllText(unselected, InlineName("88888888-8888-4888-8888-888888888888", "Changed")); return null; }
                    return mode == "fail" ? "error:codex delete failed (exit 1): synthetic" : mode == "partial" ? "partial:codex delete failed (exit 1): after removal" : "keep";
                };
                engine = new HistoryEngine(delegate { return null; }, delegate { return Now; }, CodexStub(home, calls, failure));
                catalog = engine.Scan(Provider.Codex, home, CancellationToken.None);
                result = engine.Delete(engine.Plan(catalog, catalog.Sessions.Where(s => s.Id != "88888888-8888-4888-8888-888888888888")), "DELETE", CancellationToken.None);
                int expected = mode == "fail" || mode == "keep" ? 1 : 2;
                Assert(result.Stopped != null && result.Deleted == expected && calls.Count == 2, "Codex deletion did not stop safely in mode " + mode + ": " + result.Stopped);
                Assert(mode == "extra" || File.Exists(parent), "Codex deletion continued past a failure in mode " + mode + ".");
                Assert(result.Notes.Any(n => n.Contains("Rescan")), "A partial Codex deletion was not verified in mode " + mode + ".");
                Assert(mode != "partial" || result.Notes.Any(n => n.Contains("reported an error after removing")), "A partial Codex failure was not reported.");
            }
            home = CodexHome(out parent, out first, out second);
            calls = new List<string>();
            string truncate = home;
            engine = new HistoryEngine(delegate { return null; }, delegate { return Now; }, CodexStub(home, calls, delegate(string id) { if (calls.Count == 3) WriteStore(truncate, "session_index.jsonl", ""); return null; }));
            catalog = engine.Scan(Provider.Codex, home, CancellationToken.None);
            WriteStore(home, "session_index.jsonl", File.ReadAllText(Path.Combine(home, "session_index.jsonl")) + IndexLine("88888888-8888-4888-8888-888888888888", "Unrelated"));
            catalog = engine.Scan(Provider.Codex, home, CancellationToken.None);
            result = engine.Delete(engine.Plan(catalog, catalog.Sessions), "DELETE", CancellationToken.None);
            Assert(result.Stopped == null && result.Notes.Any(n => n.Contains("records of other threads changed")), "Loss of another thread's index row went unreported: " + string.Join(" | ", result.Notes.ToArray()));
            foreach (string registry in new[] { "{\"pid\":", "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"sessionId\":42}" })
            {
                string claudeHome = Home(); Claude(claudeHome, A); Stamp(Path.Combine(claudeHome, "sessions", "9.json"), registry);
                Assert(Scan(claudeHome, Provider.Claude).Sessions.Single().Blocked.Contains("running sessions could not be read"), "An unreadable running-session entry did not block: " + registry);
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
