using System;
using System.Collections.Generic;
using System.Linq;

namespace SessionPurge
{
    internal enum Provider { Claude, Codex }

    internal sealed class SafetyException : Exception
    {
        internal SafetyException(string message) : base(message) { }
    }

    internal sealed class SessionRecord
    {
        internal string Id = "Unknown";
        internal string Project = "Unknown project";
        internal string RelativePath;
        internal string Version = "Unknown";
        internal string Name = "Untitled session";
        internal string NameSource = "No usable title or user prompt";
        internal bool NameIdentityVerified;
        internal string Blocked;
        internal long Bytes;
        internal DateTime Modified;
        internal string Fingerprint;
        internal string Identity;
        internal int Rows;
        internal List<string> Structure = new List<string>();
        internal HashSet<string> MessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> ReferencedMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool Eligible { get { return Blocked == null; } }
    }

    internal sealed class Catalog
    {
        internal string Root;
        internal Provider Provider;
        internal string Inventory;
        internal List<SessionRecord> Sessions = new List<SessionRecord>();
        internal List<string> Notices = new List<string>();
        internal List<StoreSnapshot> Stores = new List<StoreSnapshot>();
    }

    internal sealed class DeletionPlan
    {
        internal Catalog Catalog;
        internal List<SessionRecord> Sessions;
        internal DateTime Expires;
        internal List<StoreEdit> StoreEdits = new List<StoreEdit>();
        internal long Bytes { get { return Sessions.Sum(s => s.Bytes); } }
        internal string Describe()
        {
            return "PERMANENT DELETION\r\n\r\n" + Sessions.Count + " selected transcript file(s), " + Bytes.ToString("N0") + " bytes.\r\n\r\n" +
                string.Join("\r\n", Sessions.Select(s => s.RelativePath + "  |  " + s.Bytes.ToString("N0") + " bytes").ToArray()) +
                (StoreEdits.Count == 0 ? "\r\n\r\nNo shared history records need removal.\r\n" : "\r\n\r\nSHARED HISTORY UPDATES\r\n" + string.Join("\r\n", StoreEdits.Select(e => e.Snapshot.RelativePath + "  |  remove " + e.RemovedRows + " record(s)  |  " + e.Snapshot.Bytes.ToString("N0") + " -> " + e.RemainingBytes.ToString("N0") + " bytes").ToArray()) + "\r\nUnselected JSONL rows and retained project-index values are preserved.\r\n") +
                "No directories, settings, authentication, or source projects will be changed.\r\n" +
                "Deletion bypasses the Recycle Bin. It cannot be undone here. It is not forensic secure erasure.\r\n" +
                (Catalog.Stores.Count == 0 ? "Each file is an independent operation. An interruption can leave a partially completed selection. Rescan before retrying.\r\n" : "Transcript deletion and shared-record updates commit together using transactional NTFS. Before commit, interruption rolls back the selection. Windows manages its own filesystem transaction journal.\r\n") +
                "All file identities, contents, permissions, activity, and storage layout will be checked again.\r\n\r\nType DELETE to confirm.";
        }
    }

    internal sealed class DeletionResult
    {
        internal int Deleted;
        internal string Stopped;
        internal bool Coordinated;
        internal string Describe()
        {
            return Deleted + " selected file(s) permanently deleted." + (Stopped == null ? "" : "\r\nStopped safely: " + Stopped) +
                "\r\nRescan to see the current state. " + (Coordinated ? "No application backup or temporary conversation file was created. Windows manages its filesystem transaction journal." : "No application recovery journal or conversation copy was created.");
        }
    }
}
