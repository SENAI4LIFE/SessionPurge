using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SessionPurge.Tests
{
    internal static partial class TestMain
    {
        private static string work;
        private static int passed;
        private static int failed;
        private static readonly List<string> unavailable = new List<string>();
        private sealed class UnavailableException : Exception { internal UnavailableException(string message) : base(message) { } }
        private static readonly string A = "11111111-1111-4111-8111-111111111111";
        private static readonly string B = "22222222-2222-4222-8222-222222222222";
        private static readonly string C = "33333333-3333-4333-8333-333333333333";
        private const string Body = "SYNTHETIC_SECRET_NOT_FOR_DISPLAY";
        private static readonly DateTime Now = DateTime.UtcNow;
        private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(60);
        private static readonly HistoryEngine Engine = new HistoryEngine(delegate { return null; }, delegate { return Now; });

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string link, string target, IntPtr attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        private delegate bool WindowVisitor(IntPtr window, IntPtr data);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int type, HookProc procedure, IntPtr module, uint thread);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint thread, WindowVisitor visitor, IntPtr data);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        private const int CbtHook = 5, ActivateEvent = 5, FocusEvent = 9, ShowNoActivate = 4, WmKeyDown = 0x0100;
        private static IntPtr focusRequest;
        private static readonly HookProc ActivationGuard = delegate(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == ActivateEvent) return (IntPtr)1;
            if (code == FocusEvent) focusRequest = wParam;
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };

        [STAThread]
        private static int Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Control.CheckForIllegalCrossThreadCalls = true;
            if (args.Length == 3 && args[0] == "--interrupt-child") return CrashChild(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--atomic-child") return CrashChild(args[1], args[2], true);
            if (args.Length != 1) return 2;
            string developmentRoot = Path.GetFullPath(args[0]);
            string expected = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "dev"));
            if (!developmentRoot.Equals(expected, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(developmentRoot)) return 2;
            IntPtr guard = SetWindowsHookEx(CbtHook, ActivationGuard, IntPtr.Zero, GetCurrentThreadId());
            if (guard == IntPtr.Zero) { Console.WriteLine("Window activation guard unavailable; GUI tests were not run."); return 2; }
            work = Path.Combine(developmentRoot, "fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string previousSqlite = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
            Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", null);
            try
            {
                Run("Claude selected deletion preserves unselected bytes and configuration", ClaudeDeletion);
                Run("Codex selected deletion and archived layouts", CodexDeletion);
                Run("Project grouping and metadata-only preview", PreviewPrivacy);
                Run("Exact confirmation required", Confirmation);
                Run("Expired plan refused", Expired);
                Run("Empty, duplicate, foreign and blocked selections refused", InvalidSelections);
                Run("Malformed JSON, duplicate keys, invalid UTF-8 and truncation refused", Malformed);
                Run("Newer and prerelease provider versions are listed and deletable", Versions);
                Run("Foreign owner IDs refused and stable patch updates supported", Mixed);
                Run("Shared metadata and databases preserved without opening", Shared);
                Run("Session folders and files naming a session block only that session; unreadable project indexes block deletion", Artifacts);
                Run("Unrelated top-level items do not block deletion", UnknownLayout);
                Run("Duplicate session identities block entire home", DuplicateIdentity);
                Run("Codex subagent threads are deletable, their parent waits for them, shared-history rollouts are refused", Linked);
                Run("Subagent tool use blocks its session; unknown content blocks are tolerated", NestedBlocks);
                Run("Claude messages referring to another transcript block deletion", MessageChain);
                Run("Recent and future-dated transcripts refused", Recent);
                Run("Running provider processes produce a warning, not a block", Active);
                Run("Concurrent writer prevents preflight deletion", Writer);
                Run("Second-file lock prevents deletion of first file", PreflightAll);
                Run("Same-length modification with preserved timestamp detected", ContentRace);
                Run("Unselected peer content and ownership changes block deletion", PeerContentRace);
                Run("Unselected peer writers block deletion preflight", PeerWriter);
                Run("All peer transcripts stay locked until deletion finishes", PeerHeld);
                Run("Coordinated deletion locks and revalidates unselected peers", CoordinatedPeers);
                Run("File replacement with identical contents detected", IdentityRace);
                Run("New history file after preview detected", InventoryRace);
                Run("Shared metadata introduced immediately before unlink blocks deletion", CheckpointRace);
                Run("Shared metadata introduced between unlinks stops remaining deletion", PartialRace);
                Run("Cancellation before deletion preserves everything", CancelBefore);
                Run("Interruption between files preserves remaining selection and rescan works", CancelBetween);
                Run("Abrupt process interruption leaves no temporary copies and allows recovery", AbruptInterruption);
                Run("Read-only files block the entire deletion preflight", ReadOnly);
                Run("Denied delete permission preserves the target", PermissionDenied);
                Run("Hard-linked files refused and outside link preserved", HardLinks);
                Run("History junctions refused without traversing target", Junctions);
                Run("Root junction refused", RootJunction);
                Run("Symlink refused or creation unavailable without privilege", Symlinks);
                Run("Outside paths, alternate streams and ambiguous components refused", Paths);
                Run("Production refuses arbitrary folders before filesystem access", ConfiguredBoundary);
                Run("JSON depth and numeric syntax limits enforced", JsonLimits);
                Run("BOM, blank rows, oversized rows and empty files refused", FileLimits);
                Run("A malformed or oversized transcript blocks only sessions in its own project", UncertainPeer);
                Run("External SQLite home blocks Codex deletion", ExternalSqlite);
                Run("Claude title precedence, provenance and latest rename", ClaudeNames);
                Run("Claude summaries require an owned message leaf", SummaryNames);
                Run("Only meaningful human user prompts become names", PromptNames);
                Run("Missing or malformed titles fall back deterministically", TitleFallback);
                Run("Names cannot make unsupported transcripts deletable", NamesKeepBoundary);
                Run("Codex session index uses the last matching append", CodexIndexNames);
                Run("Codex inline title events require a matching thread ID", CodexInlineNames);
                Run("Malformed name indexes preserve transcript fallback", InvalidNameIndex);
                Run("Foreign names and duplicate identities are not displayed", NameOwnership);
                Run("Name index locks and hard links fail without mutation", NameIndexSafety);
                Run("Credential-like, internal and deceptive labels suppressed", NamePrivacy);
                Run("Name whitespace, length and Unicode normalization", NameFormatting);
                Run("Unreadable records block deletion but keep verified titles", PartialName);
                Run("Naming never opens synthetic authentication or settings files", NamingDoesNotReadCredentials);
                Run("GUI launch, synthetic scan, grouping and search", () => OnUiLoop(Gui));
                Run("GUI privacy, title search, single layout and confirmation expiry", () => OnUiLoop(GuiNamesAndLayout));
                Run("Embedded icon has every size and is used by both windows", () => OnUiLoop(IconLoading));
                Run("Theme colors meet 4.5:1 contrast and high contrast keeps system colors", () => OnUiLoop(ThemeColors));
                Run("GUI workflow deletes only the confirmed session, F5 rescans and Ctrl+F focuses search", () => OnUiLoop(GuiWorkflow));
                Run("Selection and deletion plans are capped at 500 sessions", () => OnUiLoop(SelectionCap));
                Run("Production executable loads and starts with no provider selected", () => OnUiLoop(Production));
                Run("Routine tool, context, title and stable version records supported", RoutineRecords);
                Run("Claude shared prompt records commit with selected transcripts", ClaudeSharedDeletion);
                Run("Codex shared prompt and title records commit with selected transcripts", CodexSharedDeletion);
                Run("Claude project index preserves retained raw values", ProjectIndexDeletion);
                Run("Coordinated failures and cancellation roll back all writes and deletes", CoordinatedRollback);
                Run("Coordinated shared metadata races preserve the selection", CoordinatedRaces);
                Run("Coordinated final inventory detects newly introduced state", CoordinatedInventory);
                Run("Coordinated process termination rolls back staged writes and deletes", CoordinatedCrash);
                Run("Shared ownership, version, field and link contradictions refused", SharedOwnership);
                Run("Shared read-only and unrelated-store preflight preserves all targets", SharedPreflight);
                Run("Current Claude record types, keys and home folders are deletable", ModernClaudeHome);
                Run("Current Codex record types, keys and home folders are deletable; SQLite thread stores block specifically", ModernCodexHome);
                Run("A transcript still being written does not block deleting another session", LivePeer);
                Run("Links, session-named files and per-session folders appearing during deletion refuse it", LateLinks);
                Run("Claude session folders and per-session folders are verified, listed and deleted in one transaction", ClaudeSessionArtifacts);
                Run("A running Claude session is never deleted", RunningClaudeSession);
                Run("Codex threads are deleted with the Codex CLI, subagents first, and verified by rescanning", CodexCliDeletion);
                Console.WriteLine("UNAVAILABLE CASES: " + (unavailable.Count == 0 ? "none" : string.Join("; ", unavailable.ToArray())));
                Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed; " + unavailable.Count + " unavailable.");
                return failed == 0 ? 0 : 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", previousSqlite);
                Cleanup(work);
                UnhookWindowsHookEx(guard);
            }
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (UnavailableException exception) { unavailable.Add(name); Console.WriteLine("UNAVAILABLE " + name + " [" + exception.Message + "]"); }
            catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + " [" + exception.GetType().Name + ": " + exception.Message + "]"); }
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Refused(Action action)
        {
            bool refused = false;
            try { action(); } catch (SafetyException) { refused = true; }
            Assert(refused, "Expected safe refusal.");
        }
        private static string Home()
        {
            string path = Path.Combine(work, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
        private static string ClaudeLine(string id)
        {
            return "{\"type\":\"user\",\"sessionId\":\"" + id + "\",\"version\":\"2.1.32\",\"uuid\":\"" + C + "\",\"parentUuid\":null,\"timestamp\":\"2026-01-01T12:00:00Z\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"" + Body + "\"}}\n";
        }
        private static string CodexText(string id)
        {
            return "{\"timestamp\":\"2026-01-01T12:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"timestamp\":\"2026-01-01T12:00:00Z\",\"cwd\":\"fixture-project\",\"cli_version\":\"0.130.0\",\"source\":\"cli\"}}\n" +
                "{\"timestamp\":\"2026-01-01T12:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + Body + "\"}]}}\n";
        }
        private static string Put(string home, Provider provider, string id, string text, bool archived)
        {
            string directory = provider == Provider.Claude ? Path.Combine(home, "projects", "fixture-project") : archived ? Path.Combine(home, "archived_sessions") : Path.Combine(home, "sessions", "2026", "01", "01");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, provider == Provider.Claude ? id + ".jsonl" : "rollout-2026-01-01T12-00-00-" + id + ".jsonl");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, Now.AddDays(-10));
            return path;
        }
        private static string Claude(string home, string id) { return Put(home, Provider.Claude, id, ClaudeLine(id), false); }
        private static void Age(string folder)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string child in Directory.GetDirectories(folder)) Age(child);
            Directory.SetLastWriteTimeUtc(folder, Now.AddDays(-10));
        }
        private static string Stamp(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, Now.AddDays(-10));
            return path;
        }
        private static string Codex(string home, string id, bool archived) { return Put(home, Provider.Codex, id, CodexText(id), archived); }
        private static Catalog Scan(string home, Provider provider) { return Engine.Scan(provider, home, CancellationToken.None); }
        private static DeletionPlan Plan(Catalog catalog, params string[] ids) { return Engine.Plan(catalog, catalog.Sessions.Where(s => ids.Contains(s.Id))); }
        private static DeletionResult Delete(DeletionPlan plan) { return Engine.Delete(plan, "DELETE", CancellationToken.None); }
        private static void Blocked(string home, Provider provider)
        {
            var catalog = Scan(home, provider);
            Assert(catalog.Sessions.Count > 0 && catalog.Sessions.All(s => !s.Eligible), "Home should be browse-only.");
        }
        private static void ClaudeDeletion()
        {
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B);
            string before = File.ReadAllText(second);
            string auth = Path.Combine(home, "auth.json"); File.WriteAllText(auth, Body);
            var catalog = Scan(home, Provider.Claude);
            Assert(catalog.Sessions.All(s => s.Eligible), "Standalone fixtures should be eligible.");
            var result = Delete(Plan(catalog, A));
            Assert(result.Deleted == 1 && result.Stopped == null && !File.Exists(first), result.Describe());
            Assert(File.ReadAllText(second) == before && File.ReadAllText(auth) == Body, "Unrelated bytes changed.");
            Assert(Directory.Exists(Path.GetDirectoryName(first)), "Directory should remain.");
        }
        private static void CodexDeletion()
        {
            string home = Home(); string first = Codex(home, A, false); string second = Codex(home, B, true);
            var catalog = Scan(home, Provider.Codex);
            Assert(catalog.Sessions.Count == 2 && catalog.Sessions.All(s => s.Eligible), "Codex fixture envelopes should be eligible.");
            var result = Delete(Plan(catalog, A, B));
            Assert(result.Deleted == 2 && result.Stopped == null && !File.Exists(first) && !File.Exists(second), result.Describe());
        }
        private static void PreviewPrivacy()
        {
            string home = Home(); Claude(home, A);
            var catalog = Scan(home, Provider.Claude); var session = catalog.Sessions.Single();
            Assert(session.Project == "fixture-project" && session.Rows == 1 && session.Structure.Count == 1, "Metadata missing.");
            string preview = Plan(catalog, A).Describe() + string.Join("", session.Structure.ToArray());
            Assert(!preview.Contains(Body) && preview.Contains(session.RelativePath) && preview.Contains(session.Bytes.ToString("N0")), "Unsafe or incomplete preview.");
        }
        private static void Confirmation()
        {
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A);
            foreach (string text in new[] { "", "delete", " DELETE", "DELETE " }) Assert(Engine.Delete(plan, text, CancellationToken.None).Deleted == 0 && File.Exists(file), "Invalid confirmation accepted.");
        }
        private static void Expired()
        {
            DateTime now = Now;
            var engine = new HistoryEngine(delegate { return null; }, delegate { return now; });
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B);
            var catalog = engine.Scan(Provider.Claude, home, CancellationToken.None);
            var expired = engine.Plan(catalog, catalog.Sessions.Where(s => s.Id == A));
            Assert(expired.Expires == now.AddMinutes(2), "Preview lifetime is not two minutes.");
            now = expired.Expires.AddTicks(1);
            Assert(engine.Delete(expired, "DELETE", CancellationToken.None).Deleted == 0 && File.Exists(first), "Expired preview accepted.");
            var edge = engine.Plan(catalog, catalog.Sessions.Where(s => s.Id == B));
            now = edge.Expires;
            Assert(engine.Delete(edge, "DELETE", CancellationToken.None).Deleted == 1 && !File.Exists(second), "Preview refused before it expired.");
            string shared = Home(); string transcript = Claude(shared, A); string history = HistoryRow(Provider.Claude, A); WriteStore(shared, "history.jsonl", history);
            catalog = engine.Scan(Provider.Claude, shared, CancellationToken.None);
            var coordinated = engine.Plan(catalog, catalog.Sessions);
            var result = engine.Delete(coordinated, "DELETE", CancellationToken.None, delegate { now = coordinated.Expires.AddTicks(1); });
            Assert(result.Deleted == 0 && File.Exists(transcript) && File.ReadAllText(Path.Combine(shared, "history.jsonl")) == history, "Preview expiring before commit was committed.");
        }
        private static void InvalidSelections()
        {
            string home = Home(); Claude(home, A); var catalog = Scan(home, Provider.Claude); var session = catalog.Sessions.Single();
            Refused(() => Engine.Plan(catalog, new SessionRecord[0]));
            Refused(() => Engine.Plan(catalog, new[] { session, session }));
            Refused(() => Engine.Plan(catalog, new[] { new SessionRecord() }));
            session.Blocked = "test"; Refused(() => Engine.Plan(catalog, new[] { session }));
        }
        private static void Malformed()
        {
            foreach (string text in new[] { "{broken}\n", "{\"type\":\"user\",\"type\":\"assistant\"}\n", ClaudeLine(A).TrimEnd('\n'), "[]\n" })
            { string home = Home(); Put(home, Provider.Claude, A, text, false); Blocked(home, Provider.Claude); }
            string invalid = Home(); string path = Claude(invalid, A); File.WriteAllBytes(path, new byte[] { 0xff, 10 }); File.SetLastWriteTimeUtc(path, Now.AddDays(-10)); Blocked(invalid, Provider.Claude);
        }
        private static void Versions()
        {
            foreach (string version in new[] { "3.0.0", "2.2.0", "2.1.301", "2.1.0-beta.1" })
            {
                string home = Home(); Put(home, Provider.Claude, A, ClaudeLine(A).Replace("2.1.32", version), false);
                var session = Scan(home, Provider.Claude).Sessions.Single();
                Assert(session.Eligible && session.Version == version, "Claude version " + version + " was refused or not shown.");
            }
            foreach (string version in new[] { "1.0.0", "0.162.0-alpha.17.2" })
            {
                string home = Home(); Put(home, Provider.Codex, A, CodexText(A).Replace("0.130.0", version), false);
                var session = Scan(home, Provider.Codex).Sessions.Single();
                Assert(session.Eligible && session.Version == version && session.Project == "fixture-project", "Codex version " + version + " was refused or not shown.");
            }
        }
        private static void Mixed()
        {
            string foreign = Home(); Put(foreign, Provider.Claude, A, ClaudeLine(B), false); Blocked(foreign, Provider.Claude);
            string home = Home(); string second = ClaudeLine(A).Replace("2.1.32", "2.1.33").Replace("\"uuid\":" + Quote(C), "\"uuid\":" + Quote(B)).Replace("\"parentUuid\":null", "\"parentUuid\":" + Quote(C));
            Put(home, Provider.Claude, A, ClaudeLine(A) + second, false);
            Assert(Scan(home, Provider.Claude).Sessions.Single().Eligible, "A structurally valid resumed stable-patch transcript was refused.");
        }
        private static void Shared()
        {
            foreach (string name in new[] { "history.jsonl", "session_index.jsonl", "state_5.sqlite", "state_6.sqlite", "state_5.sqlite-wal", "thread_history_1.sqlite" })
            {
                string home = Home(); Codex(home, A, false); string path = Path.Combine(home, name); File.WriteAllText(path, Body);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Blocked(home, Provider.Codex);
                Assert(File.ReadAllText(path) == Body, "Shared bytes changed.");
            }
        }
        private static void Artifacts()
        {
            string home = Home(); string first = Claude(home, A); Claude(home, B); Claude(home, C);
            string folder = Path.Combine(home, "projects", "fixture-project", A);
            Directory.CreateDirectory(Path.Combine(folder, "subagents"));
            const string D = "77777777-7777-4777-8777-777777777777";
            Claude(home, D);
            Stamp(Path.Combine(folder, "subagents", "result-" + D + ".json"), Body);
            Directory.CreateDirectory(Path.Combine(home, "projects", "fixture-project", "artifacts", "nested"));
            Stamp(Path.Combine(home, "projects", "fixture-project", "artifacts", "nested", "result-" + C + ".json"), Body);
            Age(folder);
            var catalog = Scan(home, Provider.Claude);
            Assert(catalog.Sessions.Single(s => s.Id == A).Eligible, "A session with its own session folder was refused: " + catalog.Sessions.Single(s => s.Id == A).Blocked);
            Assert(catalog.Sessions.Single(s => s.Id == C).Blocked.Contains("carry this session ID"), "A file naming the session did not block it.");
            Assert(catalog.Sessions.Single(s => s.Id == D).Blocked.Contains("carry this session ID"), "A file inside another session folder naming the session did not block it.");
            Assert(catalog.Sessions.Single(s => s.Id == B).Eligible, "Another session's artifacts blocked an unrelated session.");
            Assert(Delete(Plan(catalog, B)).Deleted == 1 && Directory.Exists(Path.Combine(folder, "subagents")), "Unrelated session deletion failed or touched a session folder.");
            Assert(Delete(Plan(Scan(home, Provider.Claude), A)).Deleted == 1 && !File.Exists(first) && !Directory.Exists(folder), "A session folder was not deleted with its transcript.");
            Assert(Scan(home, Provider.Claude).Sessions.Single(s => s.Id == D).Eligible, "Deleting the folder that named a session did not release it.");
            string indexed = Home(); string file = Claude(indexed, A); File.WriteAllText(Path.Combine(Path.GetDirectoryName(file), "sessions-index.json"), Body); Blocked(indexed, Provider.Claude);
        }
        private static void UnknownLayout()
        {
            string home = Home(); string file = Claude(home, A); File.WriteAllText(Path.Combine(home, "future-store.bin"), Body); Directory.CreateDirectory(Path.Combine(home, "future-folder"));
            var catalog = Scan(home, Provider.Claude);
            Assert(catalog.Sessions.Single().Eligible && Delete(Plan(catalog, A)).Deleted == 1 && !File.Exists(file), "Unrelated top-level items blocked deletion.");
            Assert(File.ReadAllText(Path.Combine(home, "future-store.bin")) == Body, "Unrelated top-level item changed.");
        }
        private static void DuplicateIdentity()
        {
            string home = Home(); string file = Claude(home, A); string other = Path.Combine(home, "projects", "other"); Directory.CreateDirectory(other); File.Copy(file, Path.Combine(other, A + ".jsonl"));
            Blocked(home, Provider.Claude);
        }
        private static void Linked()
        {
            string home = Home(); string parent = Codex(home, B, false);
            string child = Put(home, Provider.Codex, A, CodexText(A).Replace("\"source\":\"cli\"", "\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"" + B + "\",\"depth\":1}}},\"parent_thread_id\":\"" + B + "\",\"forked_from_id\":\"" + B + "\",\"agent_nickname\":\"Fixture helper\""), false);
            var catalog = Scan(home, Provider.Codex);
            var subagent = catalog.Sessions.Single(s => s.Id == A);
            Assert(subagent.Eligible && subagent.Name == "Fixture helper", "Subagent thread was refused or unnamed.");
            Assert(catalog.Sessions.Single(s => s.Id == B).Eligible, "Parent with an eligible subagent was refused.");
            Refused(() => Plan(catalog, B));
            Assert(Delete(Plan(catalog, A, B)).Deleted == 2 && !File.Exists(child) && !File.Exists(parent), "Parent and subagent were not deleted together.");
            foreach (string link in new[] { "\"source\":\"cli\",\"history_base\":{\"thread_id\":\"" + B + "\"}", "\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"" + B + "\"}}}", "\"source\":\"cli\",\"forked_from_id\":\"" + B + "\"" })
            {
                string shared = Home(); Codex(shared, B, false); Put(shared, Provider.Codex, A, CodexText(A).Replace("\"source\":\"cli\"", link), false);
                var linked = Scan(shared, Provider.Codex);
                Assert(linked.Sessions.All(s => s.Eligible) && linked.Sessions.Single(s => s.Id == B).Dependents.Single() == A, "Thread links were not recorded: " + link);
                Refused(() => Plan(linked, B));
            }
            string twice = Home(); Codex(twice, B, false); Put(twice, Provider.Codex, A, CodexText(A) + CodexText(A).Split('\n')[0].Replace("\"source\":\"cli\"", "\"source\":\"cli\",\"history_base\":{\"thread_id\":\"" + B + "\"}") + "\n", false);
            Assert(!Scan(twice, Provider.Codex).Sessions.Single(s => s.Id == B).Eligible, "A second rollout header hid a link.");
            string unknown = Home(); Codex(unknown, B, false); Put(unknown, Provider.Codex, A, "{broken}\n" + CodexText(A), false);
            Assert(Scan(unknown, Provider.Codex).Sessions.Single(s => s.Id == B).Blocked.Contains("links between threads are unknown"), "An unidentifiable rollout did not block possible parents.");
        }
        private static void Recent()
        {
            foreach (DateTime time in new[] { Now, Now.AddDays(1), Now.AddMinutes(-15).AddSeconds(1) }) { string home = Home(); string file = Claude(home, A); File.SetLastWriteTimeUtc(file, time); Blocked(home, Provider.Claude); }
            string edge = Home(); string old = Claude(edge, A); File.SetLastWriteTimeUtc(old, Now.AddMinutes(-15));
            Assert(Scan(edge, Provider.Claude).Sessions.Single().Eligible, "A transcript exactly 15 minutes old was refused.");
        }
        private static void NestedBlocks()
        {
            string assistant = ClaudeLine(A).Replace("\"type\":\"user\"", "\"type\":\"assistant\"").Replace("\"role\":\"user\"", "\"role\":\"assistant\"");
            foreach (string tool in new[] { "Agent", "Task", "tools.Agent" })
            {
                string home = Home(); Put(home, Provider.Claude, A, assistant.Replace(Quote(Body), "[{\"type\":\"tool_use\",\"id\":\"t\",\"name\":" + Quote(tool) + ",\"input\":{}}]"), false);
                Assert(Scan(home, Provider.Claude).Sessions.Single().Blocked.Contains("subagents"), "Subagent tool use did not block its session: " + tool);
            }
            foreach (string body in new[] { "[{\"type\":\"text\",\"text\":\"fixture\",\"future_link\":\"x\"}]", "[null]", "[{\"type\":\"future_block\"}]" })
            {
                string home = Home(); Put(home, Provider.Claude, A, ClaudeLine(A).Replace(Quote(Body), body), false);
                Assert(Scan(home, Provider.Claude).Sessions.Single().Eligible, "Unknown content block refused: " + body);
            }
            string codex = Home(); Put(codex, Provider.Codex, A, CodexText(A).Replace("input_text", "tool_result"), false);
            Assert(Scan(codex, Provider.Codex).Sessions.Single().Eligible, "Unknown Codex content block refused.");
        }
        private static void MessageChain()
        {
            foreach (string key in new[] { "parentUuid", "logicalParentUuid" })
            {
                string home = Home(); Put(home, Provider.Claude, A, ClaudeLine(A).Replace("\"parentUuid\":null", "\"" + key + "\":\"" + B + "\"" + (key == "parentUuid" ? "" : ",\"parentUuid\":null")), false);
                Assert(Scan(home, Provider.Claude).Sessions.Single().Blocked.Contains("another transcript"), "Reference to another transcript accepted: " + key);
            }
            string resumed = Home(); Claude(resumed, A); Put(resumed, Provider.Claude, B, ClaudeLine(B).Replace("\"uuid\":" + Quote(C), "\"uuid\":" + Quote(A)).Replace("\"parentUuid\":null", "\"parentUuid\":" + Quote(C)), false);
            var chain = Scan(resumed, Provider.Claude);
            Assert(chain.Sessions.Single(s => s.Id == A).Blocked.Contains("builds on this one"), "A transcript other sessions continue from stayed eligible.");
            string forked = Home(); Claude(forked, A); Put(forked, Provider.Claude, B, ClaudeLine(B).Replace("\"isSidechain\":false", "\"isSidechain\":false,\"forkedFromSessionId\":" + Quote(A)), false);
            Assert(Scan(forked, Provider.Claude).Sessions.Single(s => s.Id == A).Blocked.Contains("builds on this one"), "A session another session forked from stayed eligible.");
            string owned = Home(); Claude(owned, A); Put(owned, Provider.Claude, B, ClaudeLine(B).Replace(Quote(C), Quote(M1)) + ClaudeLine(A).Replace(Quote(C), Quote(M2)), false);
            Assert(Scan(owned, Provider.Claude).Sessions.Single(s => s.Id == A).Blocked.Contains("builds on this one"), "A session whose records sit in another file stayed eligible.");
            string partial = Home(); Claude(partial, A); Put(partial, Provider.Claude, B, ClaudeLine(B).Replace(Quote(C), Quote(M1)) + ClaudeLine(B).Replace(Quote(C), Quote(M2)).Replace("\"parentUuid\":null", "\"parentUuid\":" + Quote(C)).TrimEnd('\n'), false);
            Assert(Scan(partial, Provider.Claude).Sessions.Single(s => s.Id == A).Blocked.Contains("builds on this one"), "An unterminated last record hid a reference.");
            string valid = Home(); string second = ClaudeLine(A).Replace("\"uuid\":\"" + C + "\"", "\"uuid\":\"" + B + "\"").Replace("\"parentUuid\":null", "\"parentUuid\":\"" + C + "\"");
            Put(valid, Provider.Claude, A, ClaudeLine(A) + second, false);
            Assert(Scan(valid, Provider.Claude).Sessions.Single().Eligible, "Valid local parent chain rejected.");
        }
        private static void Active()
        {
            string home = Home(); string file = Claude(home, A);
            var active = new HistoryEngine(delegate { return "Synthetic active provider"; }, delegate { return Now; });
            var catalog = active.Scan(Provider.Claude, home, CancellationToken.None);
            Assert(catalog.Sessions.All(s => s.Eligible) && catalog.Notices.Contains("Synthetic active provider"), "Running processes blocked scanning or gave no warning.");
            var plan = active.Plan(catalog, catalog.Sessions);
            Assert(plan.Warning == "Synthetic active provider" && plan.Describe().StartsWith("Synthetic active provider"), "Deletion preview omitted the process warning.");
            Assert(active.Delete(plan, "DELETE", CancellationToken.None).Deleted == 1 && !File.Exists(file), "The process warning blocked deletion.");
        }
        private static void Writer()
        {
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A);
            using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) Assert(Delete(plan).Deleted == 0 && File.Exists(file), "Open writer was deleted.");
        }
        private static void PreflightAll()
        {
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B); var plan = Plan(Scan(home, Provider.Claude), A, B);
            using (var writer = new FileStream(second, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) Assert(Delete(plan).Deleted == 0 && File.Exists(first) && File.Exists(second), "Partial preflight deleted files.");
        }
        private static void ContentRace()
        {
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A); DateTime time = File.GetLastWriteTimeUtc(file);
            File.WriteAllText(file, ClaudeLine(A).Replace(Body, new string('X', Body.Length)), new UTF8Encoding(false)); File.SetLastWriteTimeUtc(file, time);
            Assert(Delete(plan).Deleted == 0 && File.Exists(file), "Content race missed.");
        }
        private static void IdentityRace()
        {
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A); DateTime time = File.GetLastWriteTimeUtc(file);
            string replacement = Path.Combine(home, "replacement"); File.WriteAllText(replacement, ClaudeLine(A), new UTF8Encoding(false)); File.SetLastWriteTimeUtc(replacement, time); File.Delete(file); File.Move(replacement, file);
            Assert(Delete(plan).Deleted == 0 && File.Exists(file), "Identity race missed.");
        }
        private static void InventoryRace()
        {
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A); Claude(home, B);
            Assert(Delete(plan).Deleted == 0 && File.Exists(file), "Inventory race missed.");
        }
        private static void CheckpointRace()
        {
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A);
            var result = Engine.Delete(plan, "DELETE", CancellationToken.None, delegate { File.WriteAllText(Path.Combine(home, "history.jsonl"), Body); });
            Assert(result.Deleted == 0 && File.Exists(file), "Pre-unlink artifact race missed.");
        }
        private static void PartialRace()
        {
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B); var plan = Plan(Scan(home, Provider.Claude), A, B);
            var result = Engine.Delete(plan, "DELETE", CancellationToken.None, delegate(int index) { if (index == 1) File.WriteAllText(Path.Combine(home, "history.jsonl"), Body); });
            Assert(result.Deleted == 1 && !File.Exists(first) && File.Exists(second) && result.Stopped != null, "Between-unlinks artifact race missed.");
        }
        private static void CancelBefore()
        {
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A); var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert(Engine.Delete(plan, "DELETE", cancellation.Token).Deleted == 0 && File.Exists(file), "Canceled plan deleted files."); cancellation.Dispose();
        }
        private static void CancelBetween()
        {
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B); var plan = Plan(Scan(home, Provider.Claude), A, B);
            using (var cancellation = new CancellationTokenSource())
            {
                var result = Engine.Delete(plan, "DELETE", cancellation.Token, delegate(int index) { if (index == 1) cancellation.Cancel(); });
                Assert(result.Deleted == 1 && !File.Exists(first) && File.Exists(second), "Interruption boundary failed.");
            }
            var later = Scan(home, Provider.Claude);
            Assert(later.Sessions.Count == 1 && Delete(Plan(later, B)).Deleted == 1, "Rescan recovery failed.");
        }
        private static int CrashChild(string path, string marker, bool coordinated = false)
        {
            string safeDev = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "dev")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(safeDev, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(marker).StartsWith(safeDev, StringComparison.OrdinalIgnoreCase)) return 2;
            var plan = Plan(Scan(path, Provider.Claude), A, B);
            Engine.Delete(plan, "DELETE", CancellationToken.None, delegate(int index) { if (index == (coordinated ? 2 : 1)) { File.WriteAllText(marker, "ready"); Process.GetCurrentProcess().Kill(); } });
            return 3;
        }
        private static void AbruptInterruption()
        {
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B); string marker = Path.Combine(work, "child-marker");
            var start = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--interrupt-child \"" + home + "\" \"" + marker + "\"") { UseShellExecute = false, CreateNoWindow = true };
            using (var child = Process.Start(start)) { if (!child.WaitForExit((int)HangLimit.TotalMilliseconds)) { child.Kill(); throw new Exception("Child interruption timed out."); } }
            Assert(File.Exists(marker) && !File.Exists(first) && File.Exists(second), "Abrupt interruption did not reach intended boundary.");
            Assert(Directory.GetFiles(home, "*", SearchOption.AllDirectories).Length == 1, "Unexpected residual data.");
            Assert(Delete(Plan(Scan(home, Provider.Claude), B)).Deleted == 1, "Restart recovery failed.");
        }
        private static void ReadOnly()
        {
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B); var plan = Plan(Scan(home, Provider.Claude), A, B); File.SetAttributes(second, FileAttributes.ReadOnly);
            try { Assert(Delete(plan).Deleted == 0 && File.Exists(first) && File.Exists(second), "Read-only preflight failed."); }
            finally { File.SetAttributes(second, FileAttributes.Normal); }
        }
        private static void PermissionDenied()
        {
            string home = Home(); string file = Claude(home, A); string parent = Path.GetDirectoryName(file); var plan = Plan(Scan(home, Provider.Claude), A);
            FileSystemAccessRule fileRule, parentRule;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                fileRule = new FileSystemAccessRule(identity.User, FileSystemRights.Delete, AccessControlType.Deny);
                parentRule = new FileSystemAccessRule(identity.User, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
            }
            var fileSecurity = File.GetAccessControl(file); fileSecurity.AddAccessRule(fileRule);
            var parentSecurity = Directory.GetAccessControl(parent); parentSecurity.AddAccessRule(parentRule);
            bool fileDenied = false, parentDenied = false;
            try
            {
                try { File.SetAccessControl(file, fileSecurity); fileDenied = true; Directory.SetAccessControl(parent, parentSecurity); parentDenied = true; }
                catch (UnauthorizedAccessException) { throw new UnavailableException("Synthetic ACL setup was denied by the execution environment; delete-ACL enforcement was not validated."); }
                Assert(Delete(plan).Deleted == 0 && File.Exists(file), "Denied permission was ignored.");
            }
            finally
            {
                if (parentDenied) { parentSecurity.RemoveAccessRule(parentRule); Directory.SetAccessControl(parent, parentSecurity); }
                if (fileDenied) { fileSecurity.RemoveAccessRule(fileRule); File.SetAccessControl(file, fileSecurity); }
            }
        }
        private static void HardLinks()
        {
            string home = Home(); string file = Claude(home, A); string outside = Path.Combine(work, "hardlink-" + Guid.NewGuid().ToString("N"));
            Assert(CreateHardLink(outside, file, IntPtr.Zero), "Could not create synthetic hard link.");
            Blocked(home, Provider.Claude); Assert(File.ReadAllText(outside) == ClaudeLine(A), "Hard link changed.");
        }
        private static void MakeJunction(string link, string target)
        {
            var start = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(start)) { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit(); Assert(process.ExitCode == 0, "Could not create synthetic junction."); }
        }
        private static void Junctions()
        {
            string home = Home(); Claude(home, A); string outside = Home(); File.WriteAllText(Path.Combine(outside, "preserve.txt"), Body);
            string link = Path.Combine(home, "projects", "linked-project"); MakeJunction(link, outside);
            try { Blocked(home, Provider.Claude); Assert(File.ReadAllText(Path.Combine(outside, "preserve.txt")) == Body, "Junction target changed."); }
            finally { Directory.Delete(link); }
            string nested = Path.Combine(home, "projects", "fixture-project", "artifacts", "linked"); Directory.CreateDirectory(Path.GetDirectoryName(nested)); MakeJunction(nested, outside);
            try { Blocked(home, Provider.Claude); }
            finally { Directory.Delete(nested); }
        }
        private static void RootJunction()
        {
            string outside = Home(); Claude(outside, A); string link = Path.Combine(work, "root-link-" + Guid.NewGuid().ToString("N")); MakeJunction(link, outside);
            try { Refused(() => Scan(link, Provider.Claude)); }
            finally { Directory.Delete(link); }
        }
        private static void Symlinks()
        {
            string home = Home(); string file = Claude(home, A); string target = Path.Combine(work, "symlink-target-" + Guid.NewGuid().ToString("N")); File.Move(file, target);
            if (!CreateSymbolicLink(file, target, 2) && !CreateSymbolicLink(file, target, 0)) throw new UnavailableException("Symbolic-link creation unavailable (Windows error " + Marshal.GetLastWin32Error() + "); hard links and junctions are tested separately.");
            try { Blocked(home, Provider.Claude); Assert(File.ReadAllText(target) == ClaudeLine(A), "Symlink target changed."); }
            finally { File.Delete(file); }
        }
        private static void Paths()
        {
            string home = Home();
            foreach (string path in new[] { "..\\outside", "C:\\outside", "file.jsonl:stream", "a\\..\\b", "a.\\b", "a \\b", "a/b", "a\u202eb", "NUL.jsonl", "a\\COM1" }) Refused(() => SafeFileSystem.Inside(home, path));
            foreach (string path in new[] { "\\\\server\\share", "C:\\", "relative", home + "\\..", home + ":stream" }) Refused(() => SafeFileSystem.CanonicalRoot(path));
        }
        private static void ConfiguredBoundary()
        {
            var production = new HistoryEngine();
            Refused(() => production.Scan(Provider.Claude, Home(), CancellationToken.None));
            Refused(() => production.Scan(Provider.Codex, Home(), CancellationToken.None));
        }
        private static void JsonLimits()
        {
            foreach (string json in new[] { "{\"x\":01}", "{\"x\":1.}", "{\"x\":1e}", "{\"x\":NaN}", "{\"x\":1,}", "{\"x\":\"\\q\"}", "{\"x\":\"\\ud800\"}", "{\"x\":\"\\udfff\"}", "{\"x\":1}junk", "{\"x\":" + new string('[', 60) + "0" + new string(']', 60) + "}" }) Refused(() => StrictJson.Parse(json));
            Assert(StrictJson.Parse("{\"x\":[true,false,null,-1.25e+2,\"\\u0061\"]}").Count == 1, "Valid JSON rejected.");
        }
        private static void FileLimits()
        {
            foreach (string text in new[] { "", "\ufeff" + ClaudeLine(A), ClaudeLine(A) + "\n", "{\"x\":\"" + new string('x', 1048577) + "\"}\n" })
            { string home = Home(); Put(home, Provider.Claude, A, text, false); Blocked(home, Provider.Claude); }
        }
        private static void UncertainPeer()
        {
            string home = Home(); Claude(home, A); Put(home, Provider.Claude, B, "{malformed}\n", false);
            string other = Stamp(Path.Combine(home, "projects", "other-project", C + ".jsonl"), ClaudeLine(C));
            var catalog = Scan(home, Provider.Claude);
            Assert(!catalog.Sessions.Single(s => s.Id == B).Eligible && catalog.Sessions.Single(s => s.Id == A).Blocked.Contains("could not be read completely"), "A malformed transcript did not protect sessions in its project.");
            Assert(catalog.Sessions.Single(s => s.Id == C).Eligible && Delete(Plan(catalog, C)).Deleted == 1 && !File.Exists(other), "A malformed transcript blocked a session in another project.");
            string oversized = Home(); Claude(oversized, A); Stamp(Path.Combine(oversized, "projects", "fixture-project", B + ".jsonl"), ClaudeLine(B) + new string(' ', 33 * 1024 * 1024) + "\n");
            Assert(Scan(oversized, Provider.Claude).Sessions.Single(s => s.Id == A).Blocked.Contains("could not be read completely"), "An oversized transcript did not protect sessions in its project.");
            string unterminated = Home(); Claude(unterminated, A); Put(unterminated, Provider.Claude, B, ClaudeLine(B).Replace(Quote(C), Quote(M1)) + "{\"parentUuid\":" + Quote(C) + ",\"broken", false);
            Assert(Scan(unterminated, Provider.Claude).Sessions.Single(s => s.Id == A).Blocked.Contains("could not be read completely"), "An unparseable last record did not protect sessions in its project.");
        }
        private static void ExternalSqlite()
        {
            string home = Home(); Codex(home, A, false);
            try { Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", "synthetic-external-home"); Blocked(home, Provider.Codex); }
            finally { Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", null); }
        }
        private static void Gui()
        {
            string home = Home(); Claude(home, A); Claude(home, B);
            using (var window = new MainWindow(Engine))
            {
                Assert(window.StartsWithoutProvider, "GUI scanned or selected a provider at launch.");
                ShowHidden(window);
                Assert(window.StartsWithoutProvider, "Showing the GUI selected a provider.");
                Await(window.ScanFixture(Provider.Claude, home));
                Assert(window.VisibleSessions == 2, "GUI synthetic scan failed.");
                window.SetSearch(A); Assert(window.VisibleSessions == 1, "GUI ID filter failed.");
                window.SetSearch("missing"); Assert(window.VisibleSessions == 0, "GUI missing filter failed.");
                window.SetSearch("fixture-project"); Assert(window.VisibleSessions == 2, "GUI project filter failed.");
                var grid = Find<DataGridView>(window, "sessions");
                var sessionRow = grid.Rows.Cast<DataGridViewRow>().First(r => r.Tag is SessionRecord);
                grid.CurrentCell = sessionRow.Cells[1]; Application.DoEvents();
                Assert(!AllControls(window).Any(c => c.Text.Contains(Body)), "GUI exposed body.");
                Find<Button>(window, "selectVisible").PerformClick(); Application.DoEvents();
                var review = Find<Button>(window, "reviewDeletion");
                Assert(review.Enabled, "Bulk selection did not enable review.");
                window.SetSearch("missing"); Assert(!review.Enabled, "Changing filter retained selection.");
                window.SetSearch("fixture-project");
                Render(window);
                window.Close();
            }
            using (var dialog = new ConfirmWindow(Plan(Scan(home, Provider.Claude), A), Engine.Clock))
            {
                ShowHidden(dialog);
                var input = Find<TextBox>(dialog, "confirmation");
                var remove = Find<Button>(dialog, "deletePermanently");
                Assert(!remove.Enabled, "Deletion confirmation starts enabled.");
                input.Text = "delete"; Assert(!remove.Enabled, "Lowercase confirmation accepted by GUI.");
                input.Text = "DELETE"; Assert(remove.Enabled, "Exact confirmation did not enable deletion.");
                dialog.Close();
            }
        }
        private static void GuiWorkflow()
        {
            string home = Home(); string first = Claude(home, A); string second = Claude(home, B);
            using (var window = new MainWindow(Engine))
            {
                ShowHidden(window); Await(window.ScanFixture(Provider.Claude, home));
                var grid = Find<DataGridView>(window, "sessions");
                var scan = Find<Button>(window, "scan");
                var search = Find<TextBox>(window, "search");
                window.SetSearch(A);
                grid.Rows[0].Cells[0].Value = true;
                Exception failure = null;
                EventHandler conceal = delegate
                {
                    EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr handle, IntPtr data)
                    {
                        var pending = Control.FromHandle(handle) as ConfirmWindow;
                        if (pending != null && !pending.Visible) Conceal(pending);
                        return true;
                    }, IntPtr.Zero);
                };
                Application.EnterThreadModal += conceal;
                window.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var dialog = Application.OpenForms.OfType<ConfirmWindow>().Single();
                        if (dialog.Opacity != 0 || dialog.Location.X != -32000) throw new Exception("Confirmation dialog was not concealed before display.");
                        Find<TextBox>(dialog, "confirmation").Text = "DELETE";
                        Find<Button>(dialog, "deletePermanently").PerformClick();
                        if (dialog.DialogResult != DialogResult.OK) throw new Exception("Delete permanently stayed unavailable.");
                    }
                    catch (Exception exception) { failure = exception; foreach (var open in Application.OpenForms.OfType<ConfirmWindow>().ToList()) open.Close(); }
                }));
                try { Find<Button>(window, "reviewDeletion").PerformClick(); }
                finally { Application.EnterThreadModal -= conceal; }
                Assert(failure == null, "Confirmation dialog could not be completed: " + failure);
                Pump(() => scan.Enabled);
                Assert(!File.Exists(first) && File.Exists(second) && window.VisibleSessions == 0, "GUI deletion did not remove exactly the confirmed session.");
                window.SetSearch("");
                SendMessage(search.Handle, WmKeyDown, (IntPtr)Keys.F5, IntPtr.Zero);
                Pump(() => scan.Enabled && window.VisibleSessions == 1);
                Assert(((SessionRecord)grid.Rows[0].Tag).Id == B, "F5 from a child control did not rescan.");
                grid.Focus(); Assert(focusRequest == grid.Handle, "Could not request focus for the session list.");
                typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(window, new object[] { new KeyEventArgs(Keys.Control | Keys.F) });
                Assert(focusRequest == search.Handle, "Ctrl+F did not move focus to search.");
                window.Close();
            }
        }
        private static void SelectionCap()
        {
            string home = Home();
            for (int i = 0; i < 501; i++) Claude(home, Guid.NewGuid().ToString());
            var catalog = Scan(home, Provider.Claude);
            Assert(catalog.Sessions.Count == 501 && catalog.Sessions.All(s => s.Eligible), "Cap fixture is not eligible.");
            Refused(() => Engine.Plan(catalog, catalog.Sessions));
            Assert(Engine.Plan(catalog, catalog.Sessions.Take(500)).Sessions.Count == 500, "A 500-session plan was refused.");
            using (var window = new MainWindow(Engine))
            {
                ShowHidden(window); Await(window.ScanFixture(Provider.Claude, home));
                var grid = Find<DataGridView>(window, "sessions");
                var review = Find<Button>(window, "reviewDeletion");
                Find<Button>(window, "selectVisible").PerformClick();
                Assert(grid.Rows.Cast<DataGridViewRow>().Count(r => Equals(r.Cells[0].Value, true)) == 500 && review.Enabled, "Select visible did not stop at 500 sessions.");
                grid.Rows.Cast<DataGridViewRow>().First(r => !Equals(r.Cells[0].Value, true)).Cells[0].Value = true;
                Assert(!review.Enabled, "Review was enabled for 501 selected sessions.");
                window.Close();
            }
        }
        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control control in root.Controls) { yield return control; foreach (Control child in AllControls(control)) yield return child; }
        }
        private static T Find<T>(Control root, string name) where T : Control
        {
            var found = root.Controls.Find(name, true);
            Assert(found.Length == 1 && found[0] is T, "Control not found: " + name);
            return (T)found[0];
        }
        private static void Conceal(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-32000, -32000);
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            ShowWindow(form.Handle, ShowNoActivate);
        }
        private static void ShowHidden(Form form)
        {
            bool shown = false;
            form.Shown += delegate { shown = true; };
            Conceal(form);
            form.Show();
            Pump(() => shown);
        }
        private static void OnUiLoop(Action body)
        {
            Exception failure = null;
            var loop = new ApplicationContext();
            EventHandler start = null;
            start = delegate
            {
                Application.Idle -= start;
                try { body(); }
                catch (Exception exception) { failure = exception; }
                finally { loop.ExitThread(); }
            };
            Application.Idle += start;
            Application.Run(loop);
            if (failure != null) throw failure;
        }
        private static void Pump(Func<bool> done)
        {
            var elapsed = Stopwatch.StartNew();
            for (Application.DoEvents(); !done(); Application.DoEvents())
            {
                Assert(elapsed.Elapsed < HangLimit, "UI operation did not finish.");
                MsgWaitForMultipleObjectsEx(0, null, 100, 0x04FF, 0x0004);
            }
        }
        private static void Await(Task task)
        {
            Pump(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }
        private static void Render(Form window)
        {
            using (var image = new System.Drawing.Bitmap(window.Width, window.Height)) window.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, window.Width, window.Height));
        }
        private static void Production()
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string file = Path.Combine(root, "SessionPurge.exe");
            Assert(File.Exists(file) && Directory.GetFiles(Path.Combine(root, "src")).All(source => File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(file)), "Production executable is missing or older than src; run build.ps1.");
            System.Reflection.Assembly assembly;
            try { assembly = System.Reflection.Assembly.LoadFile(file); }
            catch (Exception exception)
            {
                if (exception.HResult != unchecked((int)0x800711C7)) throw;
                throw new UnavailableException("Windows Application Control blocked loading the unsigned production executable.");
            }
            Assert(assembly.GetName().Name == "SessionPurge", "Production assembly identity mismatch.");
            var type = assembly.GetType("SessionPurge.MainWindow", true);
            using (var window = (Form)Activator.CreateInstance(type, true))
            {
                var property = type.GetProperty("StartsWithoutProvider", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert((bool)property.GetValue(window, null), "Production starts with a selected provider.");
                ShowHidden(window);
                Assert(window.Visible && window.Text == "SessionPurge", "Production form failed to render.");
                window.Close();
            }
        }
        private static void Cleanup(string directory)
        {
            string resolved = Path.GetFullPath(directory);
            string boundary = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "dev")) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new Exception("Fixture cleanup escaped its boundary.");
            foreach (string entry in Directory.GetFileSystemEntries(resolved))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry); else File.Delete(entry); }
                else if ((attributes & FileAttributes.Directory) != 0) Cleanup(entry);
                else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
            }
            Directory.Delete(resolved);
        }
    }
}
