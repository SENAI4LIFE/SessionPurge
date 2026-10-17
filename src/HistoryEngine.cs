using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
                return hosts.Count == 0 ? null : "Possible provider hosts are running: " + string.Join(", ", hosts.ToArray()) + ". Close them and rescan before deleting.";
            }
            catch { return "Process activity could not be established. Deletion is blocked."; }
            finally { if (processes != null) foreach (var process in processes) process.Dispose(); }
        }
    }

    internal sealed class HistoryEngine
    {
        private readonly Func<string> activity;
        private readonly bool configuredHomesOnly;
        internal HistoryEngine() { activity = ActivityGuard.Check; configuredHomesOnly = true; }
        internal HistoryEngine(Func<string> guard) { activity = guard; configuredHomesOnly = false; }
        internal static string ConfiguredHome(Provider provider)
        {
            string environment = Environment.GetEnvironmentVariable(provider == Provider.Claude ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME");
            return string.IsNullOrWhiteSpace(environment) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), provider == Provider.Claude ? ".claude" : ".codex") : environment;
        }
        private void CheckConfiguredHome(Provider provider, string path)
        {
            if (configuredHomesOnly && !string.Equals(path.TrimEnd('\\'), ConfiguredHome(provider).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Only the selected provider's configured home can be scanned or changed.");
        }
        private static readonly HashSet<string> ExcludedFiles = new HashSet<string>(new[] { "auth.json", ".credentials.json", "config.toml", "settings.json", "settings.local.json", ".claude.json", "preferences.json", "version.json", "update-check.json", "last-update-check.json", "AGENTS.md", "CLAUDE.md", "package.json", "package-lock.json", ".gitignore" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ExcludedDirectories = new HashSet<string>(new[] { "skills", "rules", "plugins", "commands", "agents", "vendor_imports" }, StringComparer.OrdinalIgnoreCase);
        internal Catalog Scan(Provider provider, string root, CancellationToken cancellation)
        {
            CheckConfiguredHome(provider, root);
            using (var lease = new DirectoryLease(root))
            {
                var catalog = new Catalog { Root = lease.Root, Provider = provider };
                string globalBlock;
                List<string> candidates;
                catalog.Inventory = Inventory(catalog, lease, cancellation, out globalBlock, out candidates);
                foreach (string relative in catalog.Inventory.Split('\n').Where(s => s.StartsWith("$store:", StringComparison.Ordinal)).Select(s => s.Substring(7)))
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
                        string reason = "Shared history " + SafeFileSystem.SafeText(relative) + ": " + Reason(exception);
                        catalog.Notices.Add(reason); if (globalBlock == null) globalBlock = reason;
                    }
                }
                foreach (string relative in candidates)
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
                    if (session.Blocked == null && globalBlock != null) session.Blocked = globalBlock;
                    if (session.Blocked == null && session.Modified > DateTime.UtcNow.AddMinutes(-15)) session.Blocked = "Recently modified or future-dated transcript; wait at least 15 minutes and rescan.";
                    catalog.Sessions.Add(session);
                }
                foreach (var group in catalog.Sessions.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    foreach (var session in group)
                    {
                        session.Blocked = "Duplicate session identity; ownership is ambiguous.";
                        session.NameIdentityVerified = false;
                        session.Name = "Untitled session";
                        session.NameSource = "Ambiguous session identity";
                    }
                if (provider == Provider.Codex) SessionNames.ReadCodexIndex(catalog, cancellation);
                if (catalog.Sessions.Any(s => s.Blocked != null) && globalBlock == null)
                {
                    const string uncertain = "Another transcript in this home has unsupported or uncertain ownership. Coordinated deletion is blocked.";
                    foreach (var session in catalog.Sessions) if (session.Blocked == null) session.Blocked = uncertain;
                    catalog.Notices.Add(uncertain);
                }
                if (globalBlock != null) catalog.Notices.Add(globalBlock);
                string running = activity();
                if (running != null)
                {
                    catalog.Notices.Add(running);
                    foreach (var session in catalog.Sessions) if (session.Blocked == null) session.Blocked = running;
                }
                string laterBlock;
                List<string> laterCandidates;
                if (catalog.Inventory != Inventory(catalog, lease, cancellation, out laterBlock, out laterCandidates)) throw new SafetyException("History changed during scanning. Wait for the provider to exit and rescan.");
                return catalog;
            }
        }
        private static string Inventory(Catalog catalog, DirectoryLease lease, CancellationToken cancellation, out string blocked, out List<string> candidates)
        {
            var entries = new List<string>();
            candidates = new List<string>();
            blocked = null;
            foreach (string entry in Directory.GetFileSystemEntries(catalog.Root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                string name = Path.GetFileName(entry);
                FileAttributes attributes = File.GetAttributes(entry);
                entries.Add(name + ":" + (int)attributes);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                bool history = directory && (catalog.Provider == Provider.Claude ? name == "projects" : name == "sessions" || name == "archived_sessions");
                if (history)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SafetyException("History directory is a link or junction.");
                    Walk(catalog, lease, name, 0, entries, candidates, cancellation, ref blocked);
                }
                else if (!directory && SharedHistory.Recognized(catalog.Provider, name))
                {
                    entries.Add("$store:" + name);
                    var info = new FileInfo(entry); entries.Add(name + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) blocked = "Shared history is a link; deletion is blocked.";
                }
                else if (directory ? !ExcludedDirectories.Contains(name) : !ExcludedFiles.Contains(name))
                {
                    string reason = catalog.Provider == Provider.Codex && (name.EndsWith(".sqlite", StringComparison.Ordinal) || name.Contains(".sqlite-")) ? "Codex SQLite state requires a verified database adapter; this database version is unsupported: " : "Unsupported related store or layout: ";
                    if (blocked == null) blocked = reason + SafeFileSystem.SafeText(name) + ". No files in this home can be deleted safely.";
                }
            }
            if (catalog.Provider == Provider.Codex && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME"))) blocked = "An external Codex SQLite home is configured. Coordinated deletion is unsupported.";
            entries.Sort(StringComparer.Ordinal);
            return string.Join("\n", entries.ToArray());
        }
        private static void Walk(Catalog catalog, DirectoryLease lease, string relative, int depth, List<string> entries, List<string> candidates, CancellationToken cancellation, ref string blocked)
        {
            if (depth > 4 || entries.Count > 20000) throw new SafetyException("History tree exceeds supported depth or entry limits.");
            string directory = SafeFileSystem.Inside(catalog.Root, relative);
            lease.Pin(directory);
            foreach (string entry in Directory.GetFileSystemEntries(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                if (entries.Count >= 20000) throw new SafetyException("History tree exceeds 20,000 entries.");
                string child = relative + "\\" + Path.GetFileName(entry);
                FileAttributes attributes = File.GetAttributes(entry);
                entries.Add(child + ":" + (int)attributes);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    blocked = "A history link or junction makes ownership uncertain. Deletion is blocked.";
                    if ((attributes & FileAttributes.Directory) == 0 && HistoryReader.Candidate(catalog.Provider, child)) candidates.Add(child);
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    bool allowed = catalog.Provider == Provider.Claude ? depth == 0 : relative.StartsWith("sessions", StringComparison.Ordinal) && depth < 3 && ValidDatePart(depth, Path.GetFileName(entry));
                    if (allowed) Walk(catalog, lease, child, depth + 1, entries, candidates, cancellation, ref blocked);
                    else blocked = "Session artifacts or unsupported history subdirectories require a coordinated operation. This home is browse-only.";
                }
                else
                {
                    var info = new FileInfo(entry);
                    entries.Add(child + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks);
                    bool layout = catalog.Provider == Provider.Claude ? depth == 1 : (relative == "archived_sessions" ? depth == 0 : depth == 3);
                    if (layout && HistoryReader.Candidate(catalog.Provider, child)) candidates.Add(child);
                    else if (layout && SharedHistory.Recognized(catalog.Provider, child)) entries.Add("$store:" + child);
                    else blocked = "Unsupported history file: " + SafeFileSystem.SafeText(child) + ". Deletion is blocked.";
                }
            }
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
            var plan = new DeletionPlan { Catalog = catalog, Sessions = chosen.OrderBy(s => s.RelativePath, StringComparer.Ordinal).ToList(), Expires = DateTime.UtcNow.AddMinutes(2) };
            var ids = new HashSet<string>(chosen.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
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
        internal DeletionResult Delete(DeletionPlan plan, string confirmation, CancellationToken cancellation)
        {
            return Delete(plan, confirmation, cancellation, null);
        }
        internal DeletionResult Delete(DeletionPlan plan, string confirmation, CancellationToken cancellation, Action<int> checkpoint)
        {
            var result = new DeletionResult();
            var files = new List<LockedFile>();
            try
            {
                if (confirmation != "DELETE" || plan == null || DateTime.UtcNow > plan.Expires) throw new SafetyException("Confirmation is missing or the preview expired.");
                CheckConfiguredHome(plan.Catalog.Provider, plan.Catalog.Root);
                if (plan.Sessions.Count == 0 || plan.Sessions.Any(s => !s.Eligible || !plan.Catalog.Sessions.Contains(s))) throw new SafetyException("Invalid deletion plan.");
                string active = activity();
                if (active != null) throw new SafetyException(active);
                if (plan.Catalog.Stores.Count != 0) return DeleteCoordinated(plan, cancellation, checkpoint);
                using (var lease = new DirectoryLease(plan.Catalog.Root))
                {
                    var removed = new List<string>();
                    var locked = new Dictionary<SessionRecord, LockedFile>();
                    var selected = new HashSet<SessionRecord>(plan.Sessions);
                    CheckInventory(plan, lease, cancellation, removed);
                    foreach (var session in plan.Catalog.Sessions.OrderBy(s => s.RelativePath, StringComparer.Ordinal))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string path = SafeFileSystem.Inside(lease.Root, session.RelativePath);
                        lease.Pin(Path.GetDirectoryName(path));
                        var file = new LockedFile(path, selected.Contains(session));
                        files.Add(file);
                        locked.Add(session, file);
                        if (file.Identity != session.Identity || file.Length != session.Bytes || file.Modified != session.Modified || file.Hash() != session.Fingerprint) throw new SafetyException("A transcript changed since scanning. Ownership must be verified by a new scan; nothing further was deleted.");
                    }
                    CheckInventory(plan, lease, cancellation, removed);
                    active = activity();
                    if (active != null) throw new SafetyException(active);
                    for (int i = 0; i < plan.Sessions.Count; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (checkpoint != null) checkpoint(i);
                        CheckInventory(plan, lease, cancellation, removed);
                        active = activity();
                        if (active != null) throw new SafetyException(active);
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
        private DeletionResult DeleteCoordinated(DeletionPlan plan, CancellationToken cancellation, Action<int> checkpoint)
        {
            var result = new DeletionResult { Coordinated = true };
            var handles = new List<LockedFile>();
            var buffers = new List<byte[]>();
            try
            {
                using (var lease = new DirectoryLease(plan.Catalog.Root))
                using (var transaction = new FileTransaction())
                {
                    var removed = new List<string>();
                    CheckInventory(plan, lease, cancellation, removed);
                    var selected = new HashSet<SessionRecord>(plan.Sessions);
                    var ids = new HashSet<string>(plan.Sessions.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
                    var deletes = new List<LockedFile>();
                    var writes = new Dictionary<LockedFile, byte[]>();
                    foreach (var session in plan.Catalog.Sessions.OrderBy(s => s.RelativePath, StringComparer.Ordinal))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string path = SafeFileSystem.Inside(lease.Root, session.RelativePath); lease.Pin(Path.GetDirectoryName(path));
                        bool deleting = selected.Contains(session);
                        var file = new LockedFile(path, deleting, deleting ? transaction : null, false); handles.Add(file);
                        if (file.Identity != session.Identity || file.Length != session.Bytes || file.Modified != session.Modified || file.Hash() != session.Fingerprint) throw new SafetyException("A transcript changed since scanning. Nothing was committed.");
                        if (deleting) deletes.Add(file);
                    }
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
                    CheckInventory(plan, lease, cancellation, removed);
                    int step = 0;
                    foreach (var write in writes)
                    {
                        cancellation.ThrowIfCancellationRequested(); if (checkpoint != null) checkpoint(step++);
                        write.Key.Stream.Position = 0; write.Key.Stream.Write(write.Value, 0, write.Value.Length); write.Key.Stream.SetLength(write.Value.Length); write.Key.Stream.Flush(true);
                    }
                    foreach (var file in deletes)
                    {
                        cancellation.ThrowIfCancellationRequested(); if (checkpoint != null) checkpoint(step++);
                        file.Delete();
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (checkpoint != null) checkpoint(step);
                    CheckInventory(plan, lease, cancellation, removed);
                    string active = activity(); if (active != null) throw new SafetyException(active);
                    if (DateTime.UtcNow > plan.Expires) throw new SafetyException("The preview expired before commit. Nothing was committed.");
                    transaction.Commit();
                    result.Deleted = plan.Sessions.Count;
                }
            }
            catch (OperationCanceledException) { result.Stopped = "Canceled before commit. The coordinated selection was rolled back."; }
            catch (Exception exception) { result.Stopped = Reason(exception); }
            finally { foreach (var file in handles) file.Dispose(); foreach (var bytes in buffers) Array.Clear(bytes, 0, bytes.Length); }
            return result;
        }
        private static void CheckInventory(DeletionPlan plan, DirectoryLease lease, CancellationToken cancellation, List<string> removed)
        {
            string blocked;
            List<string> candidates;
            string current = Inventory(plan.Catalog, lease, cancellation, out blocked, out candidates);
            string expected = string.Join("\n", plan.Catalog.Inventory.Split('\n').Where(row => !removed.Any(path => row.StartsWith(path + ":", StringComparison.Ordinal))).ToArray());
            if (blocked != null || current != expected) throw new SafetyException("The provider layout changed or now includes shared state. Rescan before deleting.");
        }
        internal static string Reason(Exception exception)
        {
            return exception is SafetyException ? exception.Message : "The operation could not be verified safely (" + exception.GetType().Name + "). No fallback was attempted.";
        }
    }
}
