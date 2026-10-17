using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace SessionPurge
{
    internal static class ActivityGuard
    {
        internal static string Check()
        {
            var blocked = new HashSet<string>(new[] { "claude", "claude-code", "codex", "chatgpt", "node", "code", "code - insiders", "cursor", "windsurf", "wsl", "wslhost", "ubuntu", "debian", "bash", "zed", "idea64", "rider64", "jetbrains_client" }, StringComparer.OrdinalIgnoreCase);
            Process[] processes = null;
            try
            {
                processes = Process.GetProcesses();
                var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in processes)
                {
                    string name = process.ProcessName;
                    if (blocked.Contains(name)) hosts.Add(name.ToLowerInvariant());
                }
                return hosts.Count == 0 ? null : "Warning: possible provider hosts are running (" + string.Join(", ", hosts.ToArray()) + "). Sessions they are writing are blocked; close them before deleting if you can.";
            }
            catch { return "Warning: running processes could not be checked."; }
            finally { if (processes != null) foreach (var process in processes) process.Dispose(); }
        }
    }

    internal static class CodexCli
    {
        internal static string Locate()
        {
            var candidates = new List<string>();
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (directory.Trim().Length > 0) candidates.Add(Path.Combine(directory.Trim(), "codex.exe"));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "codex.exe"));
            try
            {
                string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(packages)) foreach (string package in Directory.GetDirectories(packages, "OpenAI.Codex_*")) candidates.AddRange(Directory.GetFiles(package, "codex-*-windows-msvc.exe"));
                string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
                if (Directory.Exists(npm)) candidates.AddRange(Directory.GetFiles(npm, "codex.exe", SearchOption.AllDirectories));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (string candidate in candidates)
            {
                try { if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return Path.GetFullPath(candidate); }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return null;
        }
        internal static string Delete(string id)
        {
            if (!HistoryReader.IsUuid(id)) return "Invalid session ID.";
            string executable = Locate();
            if (executable == null) return "The Codex CLI was not found.";
            var start = new ProcessStartInfo(executable, "delete --force " + id) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(start))
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    return "codex delete did not finish within two minutes.";
                }
                if (process.ExitCode == 0) return null;
                string message = (error.Result + " " + output.Result).Trim();
                return "codex delete failed (exit " + process.ExitCode + ")" + (message.Length == 0 ? "." : ": " + SessionNames.SafeText(message));
            }
        }
    }

    internal sealed class HistoryEngine
    {
        private readonly Func<string> activity;
        private readonly bool configuredHomesOnly;
        private readonly Func<bool> codexAvailable;
        private readonly Func<string, string> codexDelete;
        internal readonly Func<DateTime> Clock;
        internal HistoryEngine() { activity = ActivityGuard.Check; Clock = () => DateTime.UtcNow; configuredHomesOnly = true; codexAvailable = () => CodexCli.Locate() != null; codexDelete = CodexCli.Delete; }
        internal HistoryEngine(Func<string> guard, Func<DateTime> clock, Func<string, string> codex = null) { activity = guard; Clock = clock; configuredHomesOnly = false; codexAvailable = () => codex != null; codexDelete = codex; }
        internal static string ConfiguredHome(Provider provider)
        {
            string environment = Environment.GetEnvironmentVariable(provider == Provider.Claude ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME");
            return string.IsNullOrWhiteSpace(environment) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), provider == Provider.Claude ? ".claude" : ".codex") : environment;
        }
        private void CheckConfiguredHome(Provider provider, string path)
        {
            if (configuredHomesOnly && !string.Equals(path.TrimEnd('\\'), ConfiguredHome(provider).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Only the selected provider's configured home can be scanned or changed.");
        }
        private static readonly Regex MentionedId = new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);
        private static readonly string[] SessionFolders = { "file-history", "session-env", "tasks" };
        private sealed class Layout
        {
            internal readonly List<string> Entries = new List<string>();
            internal readonly List<string> Candidates = new List<string>();
            internal readonly HashSet<string> SessionFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> MentionedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal int Ignored;
            internal int Checked;
            internal string Blocked;
            internal string Text { get { return string.Join("\n", Entries.ToArray()); } }
        }
        internal Catalog Scan(Provider provider, string root, CancellationToken cancellation)
        {
            CheckConfiguredHome(provider, root);
            using (var lease = new DirectoryLease(root))
            {
                var catalog = new Catalog { Root = lease.Root, Provider = provider, ViaCodex = provider == Provider.Codex && codexAvailable() };
                var layout = Inventory(catalog, lease, cancellation);
                catalog.Inventory = layout.Text;
                string globalBlock = layout.Blocked;
                foreach (string relative in layout.Entries.Where(s => s.StartsWith("$store:", StringComparison.Ordinal)).Select(s => s.Substring(7)))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        string path = SafeFileSystem.Inside(catalog.Root, relative);
                        lease.Pin(Path.GetDirectoryName(path));
                        using (var file = new LockedFile(path, false)) catalog.Stores.Add(SharedHistory.Inspect(catalog, relative, file, cancellation));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        string reason = "Shared history " + SessionNames.SafeText(relative) + ": " + Reason(exception);
                        catalog.Notices.Add(reason); if (globalBlock == null) globalBlock = reason;
                    }
                }
                foreach (string relative in layout.Candidates)
                {
                    cancellation.ThrowIfCancellationRequested();
                    SessionRecord session = new SessionRecord { RelativePath = relative, Id = HistoryReader.FileId(provider, relative), Project = provider == Provider.Claude ? relative.Split('\\')[1] : "Unsupported transcript" };
                    try
                    {
                        string full = SafeFileSystem.Inside(catalog.Root, relative);
                        lease.Pin(Path.GetDirectoryName(full));
                        using (var file = new LockedFile(full, false))
                        {
                            session.Bytes = file.Length;
                            session.Modified = file.Modified;
                            session.Identity = file.Identity;
                            session = HistoryReader.Read(provider, relative, file, cancellation);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (exception is OperationCanceledException) throw;
                        session.Blocked = Reason(exception);
                    }
                    catalog.Sessions.Add(session);
                }
                var running = provider == Provider.Claude ? RunningClaudeSessions(catalog.Root) : new HashSet<string>();
                foreach (var session in catalog.Sessions)
                {
                    if (running == null) HistoryReader.Block(session, "Claude Code's list of running sessions could not be read, so the running session is unknown. Rescan.");
                    else if (running.Contains(session.Id)) HistoryReader.Block(session, "Running in Claude Code right now.");
                    if (layout.MentionedIds.Contains(session.Id)) HistoryReader.Block(session, "Other history files carry this session ID and would be left behind.");
                    if (provider == Provider.Codex && catalog.Sessions.Any(s => s != session && !s.LinksKnown)) HistoryReader.Block(session, "Another rollout could not be identified, so links between threads are unknown.");
                    if (provider == Provider.Claude && catalog.Sessions.Any(s => s != session && !s.LinksKnown && string.Equals(Path.GetDirectoryName(s.RelativePath), Path.GetDirectoryName(session.RelativePath), StringComparison.OrdinalIgnoreCase))) HistoryReader.Block(session, "Another transcript in this project could not be read completely, so links to this session are unknown.");
                    if (session.Modified > Clock().AddMinutes(-15)) HistoryReader.Block(session, "Recently modified or future-dated transcript; wait at least 15 minutes and rescan.");
                    if (globalBlock != null) HistoryReader.Block(session, globalBlock);
                }
                foreach (var group in catalog.Sessions.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    foreach (var session in group)
                    {
                        session.Blocked = "Duplicate session identity; ownership is ambiguous.";
                        session.NameIdentityVerified = false;
                        session.Name = "Untitled session";
                        session.NameSource = "Ambiguous session identity";
                    }
                foreach (var session in catalog.Sessions)
                {
                    string folder = Path.GetDirectoryName(session.RelativePath) + "\\" + session.Id;
                    bool hasFolder = provider == Provider.Claude && layout.SessionFolders.Contains(folder);
                    if (session.Delegates && !(hasFolder && Directory.Exists(Path.Combine(catalog.Root, folder, "subagents")))) HistoryReader.Block(session, "Started subagents whose transcripts are not in its session folder.");
                    if (provider != Provider.Claude || !HistoryReader.IsUuid(session.Id)) continue;
                    if (hasFolder) session.ArtifactRoots.Add(folder);
                    foreach (string name in SessionFolders) if (Directory.Exists(Path.Combine(catalog.Root, name, session.Id))) session.ArtifactRoots.Add(name + "\\" + session.Id);
                    if (!session.Eligible) continue;
                    try { Artifacts(catalog, session, false, cancellation); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { HistoryReader.Block(session, "A file or folder that belongs to this session cannot be deleted safely: " + Reason(exception)); }
                }
                var continued = new HashSet<string>(catalog.Sessions.SelectMany(s => s.ReferencedMessageIds.Where(id => !s.MessageIds.Contains(id))), StringComparer.OrdinalIgnoreCase);
                foreach (var session in catalog.Sessions)
                    session.Dependents = catalog.Sessions.Where(s => s != session && (s.LinkedIds.Contains(session.Id) || s.ReferencedMessageIds.Any(id => continued.Contains(id) && !s.MessageIds.Contains(id) && session.MessageIds.Contains(id)))).Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (bool changed = true; changed; )
                {
                    changed = false;
                    foreach (var session in catalog.Sessions.Where(s => s.Eligible))
                        if (catalog.Sessions.Any(s => !s.Eligible && session.Dependents.Contains(s.Id, StringComparer.OrdinalIgnoreCase)))
                        {
                            HistoryReader.Block(session, "Another session that builds on this one is blocked; resolve that first.");
                            changed = true;
                        }
                }
                if (provider == Provider.Codex) SessionNames.ReadCodexIndex(catalog, cancellation);
                if (globalBlock != null) catalog.Notices.Add(globalBlock);
                if (catalog.ViaCodex) catalog.Notices.Add("Codex threads are deleted with the Codex CLI (codex delete), which also removes Codex's own database records.");
                if (layout.Ignored > 0) catalog.Notices.Add(layout.Ignored + " other item(s) in the history folders are not recognized as transcripts and are left unchanged; sessions they name are blocked.");
                string warning = activity();
                if (warning != null) catalog.Notices.Add(warning);
                if (catalog.Inventory != Inventory(catalog, lease, cancellation).Text) throw new SafetyException("History files were added or removed during scanning. Rescan.");
                return catalog;
            }
        }
        private static HashSet<string> RunningClaudeSessions(string root)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string folder = Path.Combine(root, "sessions");
            if (!Directory.Exists(folder)) return live;
            try
            {
                foreach (string path in Directory.GetFiles(folder, "*.json"))
                {
                    if (new FileInfo(path).Length > 65536) return null;
                    string text;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
                    var row = StrictJson.Parse(text.Trim());
                    string id = StrictJson.Text(row, "sessionId");
                    object pid;
                    if (!row.TryGetValue("pid", out pid) || !(pid is double) || (double)pid < 1 || (double)pid > int.MaxValue) return null;
                    bool alive;
                    try { using (var process = Process.GetProcessById((int)(double)pid)) alive = !process.HasExited; }
                    catch (ArgumentException) { alive = false; }
                    catch (InvalidOperationException) { alive = false; }
                    catch (Win32Exception) { alive = true; }
                    if (!alive) continue;
                    if (!HistoryReader.IsUuid(id)) return null;
                    live.Add(id);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SafetyException) { return null; }
            return live;
        }
        private Layout Inventory(Catalog catalog, DirectoryLease lease, CancellationToken cancellation)
        {
            var layout = new Layout();
            var databases = new List<string>();
            foreach (string entry in Entries(catalog.Root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                string name = Path.GetFileName(entry);
                FileAttributes attributes = File.GetAttributes(entry);
                layout.Entries.Add(name + ":" + (int)attributes);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                bool history = directory && (catalog.Provider == Provider.Claude ? name == "projects" : name == "sessions" || name == "archived_sessions");
                if (history)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SafetyException("History directory is a link or junction.");
                    Walk(catalog, lease, name, 0, layout, cancellation);
                }
                else if (!directory && SharedHistory.Recognized(catalog.Provider, name))
                {
                    layout.Entries.Add("$store:" + name);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) layout.Blocked = "Shared history is a link; deletion is blocked.";
                }
                else if (catalog.Provider == Provider.Codex && !directory && (name.EndsWith(".sqlite", StringComparison.Ordinal) || name.Contains(".sqlite-"))) databases.Add(name);
            }
            if (!catalog.ViaCodex && databases.Count > 0 && layout.Blocked == null) layout.Blocked = "Codex keeps thread data in SQLite (" + SessionNames.SafeText(string.Join(", ", databases.Where(n => n.EndsWith(".sqlite", StringComparison.Ordinal)).DefaultIfEmpty(databases[0]).ToArray())) + "), and no Codex CLI with a delete command was found; deleting rollout files alone would leave that data behind.";
            if (!catalog.ViaCodex && catalog.Provider == Provider.Codex && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME"))) layout.Blocked = "CODEX_SQLITE_HOME points to an external Codex database, and no Codex CLI with a delete command was found.";
            layout.Entries.Sort(StringComparer.Ordinal);
            return layout;
        }
        private static void Walk(Catalog catalog, DirectoryLease lease, string relative, int depth, Layout layout, CancellationToken cancellation)
        {
            if (depth > 4 || layout.Entries.Count > 20000) throw new SafetyException("History tree exceeds supported depth or entry limits.");
            string directory = SafeFileSystem.Inside(catalog.Root, relative);
            lease.Pin(directory);
            bool claude = catalog.Provider == Provider.Claude;
            foreach (string entry in Entries(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                if (layout.Entries.Count >= 20000) throw new SafetyException("History tree exceeds 20,000 entries.");
                string name = Path.GetFileName(entry);
                string child = relative + "\\" + name;
                FileAttributes attributes = File.GetAttributes(entry);
                bool folder = (attributes & FileAttributes.Directory) != 0;
                layout.Entries.Add(child + ":" + (int)attributes);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    layout.Blocked = "A link or junction in the history folders makes ownership uncertain. Deletion is blocked.";
                    if (!folder && HistoryReader.Candidate(catalog.Provider, child)) layout.Candidates.Add(child);
                    continue;
                }
                if (folder)
                {
                    if (claude ? depth == 0 : relative.StartsWith("sessions", StringComparison.Ordinal) && depth < 3 && ValidDatePart(depth, name)) Walk(catalog, lease, child, depth + 1, layout, cancellation);
                    else if (claude && depth == 1 && HistoryReader.IsUuid(name)) { layout.SessionFolders.Add(child); Mention(entry, layout, cancellation, name); }
                    else { layout.Ignored++; Mention(entry, layout, cancellation, null); }
                    continue;
                }
                bool place = claude ? depth == 1 : (relative == "archived_sessions" ? depth == 0 : depth == 3);
                if (place && HistoryReader.Candidate(catalog.Provider, child)) layout.Candidates.Add(child);
                else if (place && SharedHistory.Recognized(catalog.Provider, child)) layout.Entries.Add("$store:" + child);
                else
                {
                    layout.Ignored++;
                    foreach (Match id in MentionedId.Matches(name)) layout.MentionedIds.Add(id.Value);
                }
            }
        }
        private static void Mention(string path, Layout layout, CancellationToken cancellation, string own)
        {
            foreach (Match id in MentionedId.Matches(Path.GetFileName(path))) if (!string.Equals(id.Value, own, StringComparison.OrdinalIgnoreCase)) layout.MentionedIds.Add(id.Value);
            foreach (string entry in Entries(path))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++layout.Checked > 50000) { layout.Blocked = "The history folders contain too many unrecognized items to check for session IDs."; return; }
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) layout.Blocked = "A link or junction in the history folders makes ownership uncertain. Deletion is blocked.";
                if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0) Mention(entry, layout, cancellation, own);
                else foreach (Match id in MentionedId.Matches(Path.GetFileName(entry))) if (!string.Equals(id.Value, own, StringComparison.OrdinalIgnoreCase)) layout.MentionedIds.Add(id.Value);
            }
        }
        private List<ArtifactFile> Artifacts(Catalog catalog, SessionRecord session, bool hash, CancellationToken cancellation)
        {
            var found = new List<ArtifactFile>();
            foreach (string root in session.ArtifactRoots) Collect(catalog, root, 0, found, hash, cancellation);
            return found;
        }
        private void Collect(Catalog catalog, string relative, int depth, List<ArtifactFile> found, bool hash, CancellationToken cancellation)
        {
            if (depth > 8 || found.Count > 20000) throw new SafetyException("Its session folders are deeper or larger than SessionPurge supports.");
            string path = SafeFileSystem.Inside(catalog.Root, relative);
            using (var handle = SafeFileSystem.Open(path, true, false, true))
            {
                Native.FileInfo info;
                if (!Native.GetFileInformationByHandle(handle, out info)) throw new SafetyException("A folder in its session folders cannot be inspected: " + SessionNames.SafeText(relative) + ".");
                DateTime modified = DateTime.FromFileTimeUtc(((long)info.Written.dwHighDateTime << 32) | (uint)info.Written.dwLowDateTime);
                if (modified > Clock().AddMinutes(-15)) throw new SafetyException("A folder in its session folders changed in the last 15 minutes: " + SessionNames.SafeText(relative) + ".");
                found.Add(new ArtifactFile { RelativePath = relative, Folder = true, Identity = info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow, Modified = modified });
            }
            foreach (string entry in Entries(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                string child = relative + "\\" + Path.GetFileName(entry);
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SafetyException("A link or junction is inside its session folders: " + SessionNames.SafeText(child) + ".");
                if ((attributes & FileAttributes.Directory) != 0) { Collect(catalog, child, depth + 1, found, hash, cancellation); continue; }
                using (var file = new LockedFile(SafeFileSystem.Inside(catalog.Root, child), false))
                {
                    if (file.Modified > Clock().AddMinutes(-15)) throw new SafetyException("A file in its session folders changed in the last 15 minutes: " + SessionNames.SafeText(child) + ".");
                    found.Add(new ArtifactFile { RelativePath = child, Identity = file.Identity, Bytes = file.Length, Modified = file.Modified, Fingerprint = hash ? file.Hash() : null });
                }
            }
        }
        private static IEnumerable<string> Entries(string directory)
        {
            return Directory.GetFileSystemEntries(directory).Where(entry => { string name = Path.GetFileName(entry); return name != "." && name != ".."; });
        }
        private static bool ValidDatePart(int depth, string value)
        {
            int number;
            return value.All(c => c >= '0' && c <= '9') && int.TryParse(value, out number) && (depth == 0 ? value.Length == 4 && number >= 2000 && number <= 9999 : value.Length == 2 && number >= 1 && number <= (depth == 1 ? 12 : 31));
        }
        internal DeletionPlan Plan(Catalog catalog, IEnumerable<SessionRecord> selection)
        {
            if (catalog == null) throw new SafetyException("Scan a provider first.");
            var chosen = selection.ToList();
            if (chosen.Count == 0 || chosen.Count > 500 || chosen.Distinct().Count() != chosen.Count) throw new SafetyException("Select between 1 and 500 distinct sessions.");
            foreach (var session in chosen) if (!catalog.Sessions.Contains(session) || !session.Eligible) throw new SafetyException("The selection includes a blocked or unknown session.");
            var ids = new HashSet<string>(chosen.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var session in chosen)
            {
                int missing = session.Dependents.Count(id => !ids.Contains(id));
                if (missing > 0) throw new SafetyException("Session " + session.Id + " has " + missing + " other session(s) that build on it. Select them too, or delete them first.");
            }
            var plan = new DeletionPlan { Catalog = catalog, ViaCodex = catalog.ViaCodex, Sessions = Order(chosen), Expires = Clock().AddMinutes(2), Warning = activity() };
            if (plan.ViaCodex) return plan;
            foreach (var session in plan.Sessions) plan.Artifacts.AddRange(Artifacts(catalog, session, true, CancellationToken.None));
            using (var lease = new DirectoryLease(catalog.Root))
            {
                foreach (var store in catalog.Stores)
                {
                    string path = SafeFileSystem.Inside(catalog.Root, store.RelativePath); lease.Pin(Path.GetDirectoryName(path));
                    using (var file = new LockedFile(path, false))
                    {
                        store.Verify(file); byte[] bytes; int count;
                        SharedHistory.Rewrite(catalog, store.RelativePath, file, ids, CancellationToken.None, out bytes, out count);
                        try
                        {
                            if (count > 0) using (var hash = SHA256.Create()) plan.StoreEdits.Add(new StoreEdit { Snapshot = store, RemovedRows = count, RemainingBytes = bytes.Length, Fingerprint = Convert.ToBase64String(hash.ComputeHash(bytes)) });
                        }
                        finally { Array.Clear(bytes, 0, bytes.Length); }
                    }
                }
            }
            return plan;
        }
        private static List<SessionRecord> Order(List<SessionRecord> chosen)
        {
            var remaining = chosen.OrderBy(s => s.RelativePath, StringComparer.Ordinal).ToList();
            var ordered = new List<SessionRecord>();
            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(s => !remaining.Any(o => o != s && s.Dependents.Contains(o.Id, StringComparer.OrdinalIgnoreCase)));
                if (next == null) throw new SafetyException("The selected sessions depend on each other in a cycle.");
                ordered.Add(next);
                remaining.Remove(next);
            }
            return ordered;
        }
        internal DeletionResult Delete(DeletionPlan plan, string confirmation, CancellationToken cancellation, Action<int> checkpoint = null)
        {
            var result = new DeletionResult();
            var files = new List<LockedFile>();
            try
            {
                if (confirmation != "DELETE" || plan == null || Clock() > plan.Expires) throw new SafetyException("Confirmation is missing or the preview expired.");
                CheckConfiguredHome(plan.Catalog.Provider, plan.Catalog.Root);
                if (plan.Sessions.Count == 0 || plan.Sessions.Any(s => !s.Eligible || !plan.Catalog.Sessions.Contains(s))) throw new SafetyException("Invalid deletion plan.");
                CheckRunning(plan);
                if (plan.ViaCodex) return DeleteWithCodex(plan, cancellation, checkpoint);
                if (plan.Transactional) return DeleteCoordinated(plan, cancellation, checkpoint);
                using (var lease = new DirectoryLease(plan.Catalog.Root))
                {
                    var removed = new List<string>();
                    CheckInventory(plan, lease, cancellation, removed);
                    var locked = LockTranscripts(plan, lease, null, files, cancellation);
                    CheckInventory(plan, lease, cancellation, removed);
                    for (int i = 0; i < plan.Sessions.Count; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (checkpoint != null) checkpoint(i);
                        CheckInventory(plan, lease, cancellation, removed);
                        CheckArtifacts(plan, cancellation);
                        CheckRunning(plan);
                        locked[plan.Sessions[i]].Delete();
                        result.Deleted++;
                        removed.Add(plan.Sessions[i].RelativePath);
                    }
                }
            }
            catch (OperationCanceledException) { result.Stopped = "Operation interrupted. Remaining selected files were preserved."; }
            catch (Exception exception) { result.Stopped = Reason(exception); }
            finally { foreach (var file in files) file.Dispose(); }
            return result;
        }
        private void CheckRunning(DeletionPlan plan)
        {
            if (plan.Catalog.Provider != Provider.Claude) return;
            var running = RunningClaudeSessions(plan.Catalog.Root);
            if (running == null) throw new SafetyException("Claude Code's list of running sessions could not be read. Nothing further was deleted.");
            if (plan.Sessions.Any(s => running.Contains(s.Id))) throw new SafetyException("A selected session is running in Claude Code now. Nothing further was deleted.");
        }
        private static List<string> Roots(Catalog catalog, SessionRecord session)
        {
            var roots = new List<string>();
            if (catalog.Provider != Provider.Claude || !HistoryReader.IsUuid(session.Id)) return roots;
            string folder = Path.GetDirectoryName(session.RelativePath) + "\\" + session.Id;
            if (Directory.Exists(Path.Combine(catalog.Root, folder))) roots.Add(folder);
            foreach (string name in SessionFolders) if (Directory.Exists(Path.Combine(catalog.Root, name, session.Id))) roots.Add(name + "\\" + session.Id);
            return roots;
        }
        private static void ListPaths(string root, string relative, List<string> paths)
        {
            paths.Add(relative + "\\");
            foreach (string entry in Entries(Path.Combine(root, relative)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string child = relative + "\\" + Path.GetFileName(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) ListPaths(root, child, paths);
                else paths.Add(child);
            }
        }
        private static void CheckArtifactPaths(DeletionPlan plan)
        {
            var paths = new List<string>();
            foreach (var session in plan.Sessions) foreach (string root in Roots(plan.Catalog, session)) ListPaths(plan.Catalog.Root, root, paths);
            if (!paths.SequenceEqual(plan.Artifacts.Select(a => a.RelativePath + (a.Folder ? "\\" : "")), StringComparer.OrdinalIgnoreCase)) throw new SafetyException("Session folders changed during deletion. Nothing was committed.");
        }
        private void CheckArtifacts(DeletionPlan plan, CancellationToken cancellation)
        {
            var current = new List<ArtifactFile>();
            foreach (var session in plan.Sessions)
            {
                session.ArtifactRoots.Clear();
                session.ArtifactRoots.AddRange(Roots(plan.Catalog, session));
                current.AddRange(Artifacts(plan.Catalog, session, false, cancellation));
            }
            if (current.Count != plan.Artifacts.Count || current.Zip(plan.Artifacts, (a, b) => a.RelativePath == b.RelativePath && a.Folder == b.Folder && a.Identity == b.Identity && a.Bytes == b.Bytes && a.Modified == b.Modified).Any(same => !same))
                throw new SafetyException("Session folders changed after the review. Review the deletion again.");
        }
        private DeletionResult DeleteCoordinated(DeletionPlan plan, CancellationToken cancellation, Action<int> checkpoint)
        {
            var result = new DeletionResult();
            var handles = new List<LockedFile>();
            var folders = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
            var buffers = new List<byte[]>();
            try
            {
                using (var lease = new DirectoryLease(plan.Catalog.Root))
                using (var transaction = new FileTransaction())
                {
                    var removed = new List<string>();
                    CheckInventory(plan, lease, cancellation, removed);
                    CheckArtifacts(plan, cancellation);
                    var ids = new HashSet<string>(plan.Sessions.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
                    var locked = LockTranscripts(plan, lease, transaction, handles, cancellation);
                    var writes = new Dictionary<LockedFile, byte[]>();
                    foreach (var store in plan.Catalog.Stores)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string path = SafeFileSystem.Inside(lease.Root, store.RelativePath); lease.Pin(Path.GetDirectoryName(path));
                        StoreEdit edit = plan.StoreEdits.SingleOrDefault(e => e.Snapshot == store);
                        var file = new LockedFile(path, false, edit == null ? null : transaction, edit != null); handles.Add(file); store.Verify(file);
                        byte[] bytes; int count;
                        SharedHistory.Rewrite(plan.Catalog, store.RelativePath, file, ids, cancellation, out bytes, out count); buffers.Add(bytes);
                        if (edit == null && count != 0 || edit != null && (edit.RemovedRows != count || edit.RemainingBytes != bytes.Length)) throw new SafetyException("The shared-history preview no longer matches. Nothing was committed.");
                        if (edit != null)
                        {
                            using (var hash = SHA256.Create()) if (Convert.ToBase64String(hash.ComputeHash(bytes)) != edit.Fingerprint) throw new SafetyException("The shared-history preview differs. Nothing was committed.");
                            writes.Add(file, bytes);
                        }
                    }
                    var artifactFiles = new List<LockedFile>();
                    foreach (var artifact in plan.Artifacts)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string path = SafeFileSystem.Inside(lease.Root, artifact.RelativePath);
                        if (!artifact.Folder) continue;
                        if (plan.Sessions.Any(s => s.ArtifactRoots.Contains(artifact.RelativePath, StringComparer.OrdinalIgnoreCase))) lease.Pin(Path.GetDirectoryName(path));
                        folders.Add(SafeFileSystem.Open(path, true, true, true, transaction));
                    }
                    foreach (var artifact in plan.Artifacts.Where(a => !a.Folder))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var file = new LockedFile(SafeFileSystem.Inside(lease.Root, artifact.RelativePath), true, transaction);
                        handles.Add(file);
                        artifactFiles.Add(file);
                        if (!file.Matches(artifact.Identity, artifact.Bytes, artifact.Modified, artifact.Fingerprint)) throw new SafetyException("A file in the session folders changed after the review. Nothing was committed.");
                    }
                    CheckInventory(plan, lease, cancellation, removed);
                    int step = 0;
                    foreach (var write in writes)
                    {
                        cancellation.ThrowIfCancellationRequested(); if (checkpoint != null) checkpoint(step++);
                        write.Key.Stream.Position = 0; write.Key.Stream.Write(write.Value, 0, write.Value.Length); write.Key.Stream.SetLength(write.Value.Length); write.Key.Stream.Flush(true);
                    }
                    foreach (var session in plan.Sessions)
                    {
                        cancellation.ThrowIfCancellationRequested(); if (checkpoint != null) checkpoint(step++);
                        locked[session].Delete();
                    }
                    foreach (var file in artifactFiles)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        file.Delete();
                    }
                    for (int i = folders.Count - 1; i >= 0; i--)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        SafeFileSystem.MarkDeleted(folders[i]);
                        folders[i].Dispose();
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (checkpoint != null) checkpoint(step);
                    CheckInventory(plan, lease, cancellation, removed);
                    CheckArtifactPaths(plan);
                    CheckRunning(plan);
                    if (Clock() > plan.Expires) throw new SafetyException("The preview expired before commit. Nothing was committed.");
                    transaction.Commit();
                    result.Deleted = plan.Sessions.Count;
                }
            }
            catch (OperationCanceledException) { result.Stopped = "Canceled before commit. The coordinated selection was rolled back."; }
            catch (Exception exception) { result.Stopped = Reason(exception); }
            finally
            {
                foreach (var folder in folders) folder.Dispose();
                foreach (var file in handles) file.Dispose();
                foreach (var bytes in buffers) Array.Clear(bytes, 0, bytes.Length);
            }
            return result;
        }
        private DeletionResult DeleteWithCodex(DeletionPlan plan, CancellationToken cancellation, Action<int> checkpoint)
        {
            var result = new DeletionResult();
            var deleted = new List<string>();
            var attempted = new List<string>();
            var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string expected;
                using (var lease = new DirectoryLease(plan.Catalog.Root))
                {
                    CheckInventory(plan, lease, cancellation, new List<string>());
                    expected = plan.Catalog.Inventory;
                    foreach (var store in plan.Catalog.Stores) retained[store.RelativePath] = Retained(plan.Catalog, lease, store.RelativePath, plan.Sessions.Select(s => s.Id));
                }
                for (int i = 0; i < plan.Sessions.Count; i++)
                {
                    var session = plan.Sessions[i];
                    cancellation.ThrowIfCancellationRequested();
                    if (checkpoint != null) checkpoint(i);
                    if (Clock() > plan.Expires) throw new SafetyException("The preview expired. Remaining selected threads were preserved.");
                    string path = SafeFileSystem.Inside(plan.Catalog.Root, session.RelativePath);
                    using (var lease = new DirectoryLease(plan.Catalog.Root))
                    {
                        var current = Inventory(plan.Catalog, lease, cancellation);
                        if (current.Blocked != null || current.Text != expected) throw new SafetyException("The Codex home changed unexpectedly. Remaining selected threads were preserved.");
                        CheckPeers(plan, lease, cancellation);
                        foreach (var peer in plan.Catalog.Sessions.Where(s => s.Eligible && !deleted.Contains(s.Id, StringComparer.OrdinalIgnoreCase)))
                        {
                            string peerPath = SafeFileSystem.Inside(plan.Catalog.Root, peer.RelativePath);
                            lease.Pin(Path.GetDirectoryName(peerPath));
                            using (var file = new LockedFile(peerPath, false))
                                if (!file.Matches(peer.Identity, peer.Bytes, peer.Modified, peer.Fingerprint)) throw new SafetyException("A rollout changed after scanning. Remaining selected threads were preserved.");
                        }
                    }
                    attempted.Add(session.Id);
                    string error = codexDelete(session.Id);
                    if (error != null)
                    {
                        if (!File.Exists(path)) { result.Deleted++; deleted.Add(session.Id); result.Notes.Add("codex delete reported an error after removing " + SessionNames.SafeText(session.RelativePath) + "."); }
                        throw new SafetyException(error);
                    }
                    if (File.Exists(path)) throw new SafetyException("codex delete reported success, but " + SessionNames.SafeText(session.RelativePath) + " still exists.");
                    result.Deleted++;
                    deleted.Add(session.Id);
                    using (var lease = new DirectoryLease(plan.Catalog.Root))
                    {
                        var after = Inventory(plan.Catalog, lease, cancellation);
                        var before = new HashSet<string>(expected.Split('\n'), StringComparer.Ordinal);
                        var now = new HashSet<string>(after.Entries, StringComparer.Ordinal);
                        string rollout = session.RelativePath;
                        Func<string, bool> allowed = row =>
                        {
                            int colon = row.LastIndexOf(':');
                            string entry = colon < 0 ? row : row.Substring(0, colon);
                            return entry.Equals(rollout, StringComparison.OrdinalIgnoreCase) || rollout.StartsWith(entry + "\\", StringComparison.OrdinalIgnoreCase);
                        };
                        if (!now.IsSubsetOf(before) || !before.Except(now).All(allowed)) throw new SafetyException("codex delete changed more than the selected thread's rollout. Remaining selected threads were preserved.");
                        expected = after.Text;
                    }
                }
            }
            catch (OperationCanceledException) { result.Stopped = "Operation interrupted. Remaining selected threads were preserved."; }
            catch (Exception exception) { result.Stopped = Reason(exception); }
            Verify(plan, attempted, deleted, retained, result);
            return result;
        }
        private static string Retained(Catalog catalog, DirectoryLease lease, string relative, IEnumerable<string> removed)
        {
            string path = SafeFileSystem.Inside(catalog.Root, relative);
            lease.Pin(Path.GetDirectoryName(path));
            using (var file = new LockedFile(path, false))
            {
                byte[] bytes; int count;
                SharedHistory.Rewrite(catalog, relative, file, new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase), CancellationToken.None, out bytes, out count);
                try { using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes)); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
            }
        }
        private void Verify(DeletionPlan plan, List<string> attempted, List<string> deleted, Dictionary<string, string> retained, DeletionResult result)
        {
            if (attempted.Count == 0) return;
            try
            {
                var later = Scan(plan.Catalog.Provider, plan.Catalog.Root, CancellationToken.None);
                var ids = new HashSet<string>(attempted, StringComparer.OrdinalIgnoreCase);
                int present = later.Sessions.Count(s => deleted.Contains(s.Id, StringComparer.OrdinalIgnoreCase));
                result.Notes.Add(present == 0 ? "Rescan confirmed that the deleted threads' rollout files are gone." : present + " deleted thread(s) still appear after a rescan.");
                int changed = plan.Catalog.Sessions.Count(s => s.Eligible && !ids.Contains(s.Id) && !later.Sessions.Any(l => l.RelativePath == s.RelativePath && l.Identity == s.Identity && l.Bytes == s.Bytes && l.Modified == s.Modified && l.Fingerprint == s.Fingerprint));
                if (changed > 0) result.Notes.Add(changed + " other rollout(s) changed or disappeared during deletion.");
                foreach (string notice in later.Notices.Where(n => n.StartsWith("Shared history ", StringComparison.Ordinal))) result.Notes.Add("Not verified: " + notice);
                using (var lease = new DirectoryLease(later.Root))
                    foreach (var store in later.Stores)
                    {
                        string before;
                        if (retained.TryGetValue(store.RelativePath, out before) && Retained(later, lease, store.RelativePath, plan.Sessions.Select(s => s.Id)) != before) result.Notes.Add(store.RelativePath + ": records of other threads changed during deletion.");
                    }
                using (var lease = new DirectoryLease(later.Root))
                    foreach (var store in later.Stores)
                    {
                        string path = SafeFileSystem.Inside(later.Root, store.RelativePath); lease.Pin(Path.GetDirectoryName(path));
                        using (var file = new LockedFile(path, false))
                        {
                            byte[] bytes; int count;
                            SharedHistory.Rewrite(later, store.RelativePath, file, ids, CancellationToken.None, out bytes, out count);
                            Array.Clear(bytes, 0, bytes.Length);
                            if (count > 0) result.Notes.Add(store.RelativePath + " still has " + count + " record(s) for deleted threads.");
                        }
                    }
                result.Notes.Add("Codex's SQLite records were removed by its own command; SessionPurge cannot read those databases to confirm it.");
            }
            catch (Exception exception) { result.Notes.Add("The verification rescan did not complete: " + Reason(exception)); }
        }
        private static Dictionary<SessionRecord, LockedFile> LockTranscripts(DeletionPlan plan, DirectoryLease lease, FileTransaction transaction, List<LockedFile> handles, CancellationToken cancellation)
        {
            var selected = new HashSet<SessionRecord>(plan.Sessions);
            var locked = new Dictionary<SessionRecord, LockedFile>();
            foreach (var session in plan.Catalog.Sessions.Where(s => s.Eligible).OrderBy(s => s.RelativePath, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                string path = SafeFileSystem.Inside(lease.Root, session.RelativePath);
                lease.Pin(Path.GetDirectoryName(path));
                bool deleting = selected.Contains(session);
                var file = new LockedFile(path, deleting, deleting ? transaction : null);
                handles.Add(file);
                if (deleting) locked.Add(session, file);
                if (!file.Matches(session.Identity, session.Bytes, session.Modified, session.Fingerprint)) throw new SafetyException("A transcript changed after scanning. Nothing was deleted; rescan before retrying.");
            }
            return locked;
        }
        private void CheckInventory(DeletionPlan plan, DirectoryLease lease, CancellationToken cancellation, List<string> removed)
        {
            var current = Inventory(plan.Catalog, lease, cancellation);
            string expected = string.Join("\n", plan.Catalog.Inventory.Split('\n').Where(row => !removed.Any(path => row.StartsWith(path + ":", StringComparison.Ordinal))).ToArray());
            if (current.Blocked != null || current.Text != expected) throw new SafetyException("The provider layout changed or now includes shared state. Rescan before deleting.");
            if (plan.Sessions.Any(s => current.MentionedIds.Contains(s.Id))) throw new SafetyException("Another history file now carries a selected session ID. Rescan before deleting.");
            CheckPeers(plan, lease, cancellation);
        }
        private static void CheckPeers(DeletionPlan plan, DirectoryLease lease, CancellationToken cancellation)
        {
            var selected = new HashSet<string>(plan.Sessions.Select(s => s.Id).Concat(plan.Sessions.SelectMany(s => s.MessageIds)), StringComparer.OrdinalIgnoreCase);
            foreach (var session in plan.Catalog.Sessions.Where(s => !s.Eligible))
            {
                cancellation.ThrowIfCancellationRequested();
                var links = HistoryReader.LaterLinks(SafeFileSystem.Inside(lease.Root, session.RelativePath), session);
                if (links == null || links.Overlaps(selected)) throw new SafetyException("Another transcript could not be checked or now builds on a selected session. Rescan before deleting.");
            }
        }
        internal static string Reason(Exception exception)
        {
            return exception is SafetyException ? exception.Message : "The operation could not be verified safely (" + exception.GetType().Name + "). No fallback was attempted.";
        }
    }
}
