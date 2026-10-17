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
        private static int skipped;
        private sealed class UnavailableException : Exception { internal UnavailableException(string message) : base(message) { } }
        private static readonly string A = "11111111-1111-4111-8111-111111111111";
        private static readonly string B = "22222222-2222-4222-8222-222222222222";
        private static readonly string C = "33333333-3333-4333-8333-333333333333";
        private const string Body = "SYNTHETIC_SECRET_NOT_FOR_DISPLAY";
        private static readonly HistoryEngine Engine = new HistoryEngine(delegate { return null; });

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string link, string target, IntPtr attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 3 && args[0] == "--interrupt-child") return CrashChild(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--atomic-child") return CrashChild(args[1], args[2], true);
            if (args.Length != 1) return 2;
            string developmentRoot = Path.GetFullPath(args[0]);
            string expected = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "dev"));
            if (!developmentRoot.Equals(expected, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(developmentRoot)) return 2;
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
                Run("Unknown major versions and prereleases refused", Versions);
                Run("Foreign owner IDs refused and stable patch updates supported", Mixed);
                Run("Shared metadata and databases preserved without opening", Shared);
                Run("Related artifacts and project indexes block deletion", Artifacts);
                Run("Unknown top-level layouts block deletion", UnknownLayout);
                Run("Duplicate session identities block entire home", DuplicateIdentity);
                Run("Linked Codex parent and child records block entire home", Linked);
                Run("Nested provider tools and unknown content blocks refused", NestedBlocks);
                Run("Claude message parent chain and identities verified", MessageChain);
                Run("Recent and future-dated transcripts refused", Recent);
                Run("Active process and uncertain activity gates block deletion", Active);
                Run("Concurrent writer prevents preflight deletion", Writer);
                Run("Second-file lock prevents deletion of first file", PreflightAll);
                Run("Same-length modification with preserved timestamp detected", ContentRace);
                Run("Unselected peer content and ownership changes block deletion", PeerContentRace);
                Run("Unselected peer writers block deletion preflight", PeerWriter);
                Run("All peer transcripts stay locked until deletion finishes", PeerHeld);
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
                Run("Malformed transcript blocks otherwise valid peers", UncertainPeer);
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
                Run("Truncated transcripts never expose partially parsed titles", PartialName);
                Run("Naming never opens synthetic authentication or settings files", NamingDoesNotReadCredentials);
                Run("GUI launch, synthetic scan, grouping and search", Gui);
                Run("GUI privacy, title search, compact layout and confirmation expiry", GuiNamesAndLayout);
                Run("Production executable loads and starts with no provider selected", Production);
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
                Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed; " + skipped + " unavailable.");
                return failed == 0 ? 0 : 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_SQLITE_HOME", previousSqlite);
                Cleanup(work);
            }
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (UnavailableException exception) { skipped++; Console.WriteLine("UNAVAILABLE " + name + " [" + exception.Message + "]"); }
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
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
            return path;
        }
        private static string Claude(string home, string id) { return Put(home, Provider.Claude, id, ClaudeLine(id), false); }
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
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A); plan.Expires = DateTime.UtcNow.AddSeconds(-1);
            Assert(Delete(plan).Deleted == 0 && File.Exists(file), "Expired preview accepted.");
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
            string invalid = Home(); string path = Claude(invalid, A); File.WriteAllBytes(path, new byte[] { 0xff, 10 }); Blocked(invalid, Provider.Claude);
        }
        private static void Versions()
        {
            foreach (string version in new[] { "3.0.0", "2.0.0", "2.1.0-beta.1", "2.1" })
            { string home = Home(); Put(home, Provider.Claude, A, ClaudeLine(A).Replace("2.1.32", version), false); Blocked(home, Provider.Claude); }
            foreach (string version in new[] { "1.0.0", "0.130.0-alpha.5" })
            { string home = Home(); Put(home, Provider.Codex, A, CodexText(A).Replace("0.130.0", version), false); Blocked(home, Provider.Codex); }
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
            foreach (string name in new[] { "history.jsonl", "session_index.jsonl", "state_5.sqlite", "state_6.sqlite", "state_5.sqlite-wal", ".codex-global-state.json" })
            {
                string home = Home(); Codex(home, A, false); string path = Path.Combine(home, name); File.WriteAllText(path, Body);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Blocked(home, Provider.Codex);
                Assert(File.ReadAllText(path) == Body, "Shared bytes changed.");
            }
        }
        private static void Artifacts()
        {
            foreach (string relative in new[] { "file-history\\" + A, "todos", "projects\\fixture-project\\" + A + "\\subagents" })
            { string home = Home(); Claude(home, A); Directory.CreateDirectory(Path.Combine(home, relative)); Blocked(home, Provider.Claude); }
            string indexed = Home(); string file = Claude(indexed, A); File.WriteAllText(Path.Combine(Path.GetDirectoryName(file), "sessions-index.json"), Body); Blocked(indexed, Provider.Claude);
        }
        private static void UnknownLayout()
        {
            string home = Home(); Claude(home, A); File.WriteAllText(Path.Combine(home, "future-store.bin"), Body); Blocked(home, Provider.Claude);
        }
        private static void DuplicateIdentity()
        {
            string home = Home(); string file = Claude(home, A); string other = Path.Combine(home, "projects", "other"); Directory.CreateDirectory(other); File.Copy(file, Path.Combine(other, A + ".jsonl"));
            Blocked(home, Provider.Claude);
        }
        private static void Linked()
        {
            foreach (string alteration in new[] { "\"source\":{\"subagent\":{\"parent_thread_id\":\"" + B + "\"}}", "\"source\":\"cli\",\"forked_from_id\":\"" + B + "\"" })
            { string home = Home(); Codex(home, B, false); Put(home, Provider.Codex, A, CodexText(A).Replace("\"source\":\"cli\"", alteration), false); Blocked(home, Provider.Codex); }
            string tools = Home(); Put(tools, Provider.Codex, A, CodexText(A).Replace("\"type\":\"message\",\"role\":\"user\"", "\"type\":\"function_call\",\"name\":\"spawn_agent\",\"role\":\"user\""), false); Blocked(tools, Provider.Codex);
        }
        private static void Recent()
        {
            foreach (DateTime time in new[] { DateTime.UtcNow, DateTime.UtcNow.AddDays(1) }) { string home = Home(); string file = Claude(home, A); File.SetLastWriteTimeUtc(file, time); Blocked(home, Provider.Claude); }
        }
        private static void NestedBlocks()
        {
            foreach (string body in new[] { "[{\"type\":\"tool_use\",\"name\":\"Agent\",\"input\":{}}]", "[{\"type\":\"text\",\"text\":\"fixture\",\"future_link\":\"x\"}]", "[null]" })
            {
                string home = Home(); Put(home, Provider.Claude, A, ClaudeLine(A).Replace("\"" + Body + "\"", body), false); Blocked(home, Provider.Claude);
            }
            string codex = Home(); Put(codex, Provider.Codex, A, CodexText(A).Replace("input_text", "tool_result"), false); Blocked(codex, Provider.Codex);
            string textHome = Home(); Put(textHome, Provider.Claude, A, ClaudeLine(A).Replace("\"" + Body + "\"", "[{\"type\":\"text\",\"text\":\"fixture\"}]"), false);
            Assert(Scan(textHome, Provider.Claude).Sessions.Single().Eligible, "Text block fixture rejected.");
        }
        private static void MessageChain()
        {
            foreach (string text in new[] { ClaudeLine(A) + ClaudeLine(A), ClaudeLine(A).Replace("\"parentUuid\":null", "\"parentUuid\":\"" + B + "\"") })
            { string home = Home(); Put(home, Provider.Claude, A, text, false); Blocked(home, Provider.Claude); }
            string valid = Home(); string second = ClaudeLine(A).Replace("\"uuid\":\"" + C + "\"", "\"uuid\":\"" + B + "\"").Replace("\"parentUuid\":null", "\"parentUuid\":\"" + C + "\"");
            Put(valid, Provider.Claude, A, ClaudeLine(A) + second, false);
            Assert(Scan(valid, Provider.Claude).Sessions.Single().Eligible, "Valid local parent chain rejected.");
        }
        private static void Active()
        {
            string home = Home(); string file = Claude(home, A);
            var active = new HistoryEngine(delegate { return "Synthetic active provider"; });
            Assert(active.Scan(Provider.Claude, home, CancellationToken.None).Sessions.All(s => !s.Eligible), "Activity scan gate failed.");
            var plan = Plan(Scan(home, Provider.Claude), A);
            Assert(active.Delete(plan, "DELETE", CancellationToken.None).Deleted == 0 && File.Exists(file), "Activity deletion gate failed.");
            int checks = 0; var changing = new HistoryEngine(delegate { return ++checks > 1 ? "Provider started" : null; });
            Assert(changing.Delete(plan, "DELETE", CancellationToken.None).Deleted == 0 && File.Exists(file), "Late activity gate failed.");
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
            using (var child = Process.Start(start)) { if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Child interruption timed out."); } }
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
            string home = Home(); string file = Claude(home, A); var plan = Plan(Scan(home, Provider.Claude), A); var original = File.GetAccessControl(file);
            var denied = File.GetAccessControl(file);
            using (var identity = WindowsIdentity.GetCurrent()) denied.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.Delete, AccessControlType.Deny));
            bool applied = false;
            try
            {
                try { File.SetAccessControl(file, denied); applied = true; }
                catch (UnauthorizedAccessException) { throw new UnavailableException("Synthetic ACL setup was denied by the execution environment; delete-ACL enforcement was not validated."); }
                Assert(Delete(plan).Deleted == 0 && File.Exists(file), "Denied permission was ignored.");
            }
            finally { if (applied) File.SetAccessControl(file, original); }
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
            string home = Home(); Claude(home, A); Put(home, Provider.Claude, B, "{malformed}\n", false); Blocked(home, Provider.Claude);
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
                window.Show();
                Task task = window.ScanFixture(Provider.Claude, home);
                var timeout = Stopwatch.StartNew();
                while (!task.IsCompleted && timeout.ElapsedMilliseconds < 15000) { Application.DoEvents(); Thread.Sleep(5); }
                task.GetAwaiter().GetResult();
                Assert(window.VisibleSessions == 2, "GUI synthetic scan failed.");
                window.SetSearch(A); Assert(window.VisibleSessions == 1, "GUI ID filter failed.");
                window.SetSearch("missing"); Assert(window.VisibleSessions == 0, "GUI missing filter failed.");
                window.SetSearch("fixture-project"); Assert(window.VisibleSessions == 2, "GUI project filter failed.");
                var controls = AllControls(window).ToList();
                var grid = (DataGridView)controls.Single(c => c is DataGridView);
                var sessionRow = grid.Rows.Cast<DataGridViewRow>().First(r => r.Tag is SessionRecord);
                grid.CurrentCell = sessionRow.Cells[1]; Application.DoEvents();
                Assert(!controls.Any(c => c.Text.Contains(Body)), "GUI exposed body.");
                ((Button)controls.Single(c => c is Button && c.Text == "Select visible")).PerformClick(); Application.DoEvents();
                Assert(controls.Single(c => c is Button && c.Text == "Review deletion").Enabled, "Bulk selection did not enable review.");
                window.SetSearch("missing"); Assert(!controls.Single(c => c is Button && c.Text == "Review deletion").Enabled, "Changing filter retained selection.");
                window.SetSearch("fixture-project");
                using (var image = new System.Drawing.Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, window.Width, window.Height)); image.Save(Path.Combine(Path.GetDirectoryName(work), "synthetic-ui.png")); }
                window.Close();
            }
            using (var dialog = new ConfirmWindow(Plan(Scan(home, Provider.Claude), A)))
            {
                dialog.Show(); Application.DoEvents();
                var controls = AllControls(dialog).ToList();
                var input = (TextBox)controls.Single(c => c.AccessibleName == "Type DELETE to confirm");
                var remove = controls.Single(c => c is Button && c.Text == "Delete permanently");
                Assert(!remove.Enabled, "Deletion confirmation starts enabled.");
                input.Text = "delete"; Assert(!remove.Enabled, "Lowercase confirmation accepted by GUI.");
                input.Text = "DELETE"; Assert(remove.Enabled, "Exact confirmation did not enable deletion.");
                dialog.Close();
            }
        }
        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control control in root.Controls) { yield return control; foreach (Control child in AllControls(control)) yield return child; }
        }
        private static void Production()
        {
            string file = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "SessionPurge.exe"));
            var assembly = System.Reflection.Assembly.LoadFile(file);
            Assert(assembly.GetName().Name == "SessionPurge", "Production assembly identity mismatch.");
            var type = assembly.GetType("SessionPurge.MainWindow", true);
            using (var window = (Form)Activator.CreateInstance(type, true))
            {
                var property = type.GetProperty("StartsWithoutProvider", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert((bool)property.GetValue(window, null), "Production starts with a selected provider.");
                window.Show(); Application.DoEvents();
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
