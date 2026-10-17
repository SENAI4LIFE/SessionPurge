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
        internal HashSet<string> LinkedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool LinksKnown;
        internal List<string> ArtifactRoots = new List<string>();
        internal bool Delegates;
        internal List<string> Dependents = new List<string>();
        internal List<string> Structure = new List<string>();
        internal HashSet<string> MessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> ReferencedMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool Eligible { get { return Blocked == null; } }
    }

    internal sealed class Catalog
    {
        internal string Root;
        internal Provider Provider;
        internal bool ViaCodex;
        internal string Inventory;
        internal List<SessionRecord> Sessions = new List<SessionRecord>();
        internal List<string> Notices = new List<string>();
        internal List<StoreSnapshot> Stores = new List<StoreSnapshot>();
    }

    internal sealed class StoreSnapshot
    {
        internal string RelativePath;
        internal string Identity;
        internal string Fingerprint;
        internal long Bytes;
        internal DateTime Modified;
        internal void Verify(LockedFile file)
        {
            if (!file.Matches(Identity, Bytes, Modified, Fingerprint)) throw new SafetyException("A shared history file changed after scanning. Rescan; nothing was committed.");
        }
    }

    internal sealed class StoreEdit
    {
        internal StoreSnapshot Snapshot;
        internal int RemovedRows;
        internal long RemainingBytes;
        internal string Fingerprint;
    }

    internal sealed class ArtifactFile
    {
        internal string RelativePath;
        internal bool Folder;
        internal string Identity;
        internal long Bytes;
        internal DateTime Modified;
        internal string Fingerprint;
    }

    internal sealed class DeletionPlan
    {
        internal Catalog Catalog;
        internal List<SessionRecord> Sessions;
        internal DateTime Expires;
        internal string Warning;
        internal bool ViaCodex;
        internal List<ArtifactFile> Artifacts = new List<ArtifactFile>();
        internal List<StoreEdit> StoreEdits = new List<StoreEdit>();
        internal bool Transactional { get { return !ViaCodex && (Catalog.Stores.Count != 0 || Artifacts.Count != 0); } }
        internal long Bytes { get { return Sessions.Sum(s => s.Bytes) + Artifacts.Sum(a => a.Bytes); } }
        internal int Files { get { return Sessions.Count + Artifacts.Count(a => !a.Folder); } }
        internal string Describe()
        {
            var lines = new List<string>();
            if (Warning != null) { lines.Add(Warning); lines.Add(""); }
            lines.Add("Transcripts to delete:");
            lines.AddRange(Sessions.Select(s => "  " + s.RelativePath + "  (" + s.Bytes.ToString("N0") + " bytes)"));
            if (Artifacts.Count > 0)
            {
                lines.Add("");
                lines.Add("Session folders and files deleted with them:");
                lines.AddRange(Artifacts.Select(a => "  " + a.RelativePath + (a.Folder ? "\\" : "  (" + a.Bytes.ToString("N0") + " bytes)")));
            }
            lines.Add("");
            if (ViaCodex)
            {
                lines.Add("Codex deletes each thread with its own command, subagent threads first, in this order:");
                lines.AddRange(Sessions.Select(s => "  codex delete --force " + s.Id));
                lines.Add("Codex removes its rollout file and its own database and index records. SessionPurge then checks that each rollout file is gone and rescans the home.");
                lines.Add("Each command is separate. An interruption can leave part of the selection deleted; rescan before retrying.");
            }
            else
            {
                if (StoreEdits.Count == 0) lines.Add("No shared history records to remove.");
                else
                {
                    lines.Add("Shared history updates (unselected records are kept):");
                    lines.AddRange(StoreEdits.Select(e => "  " + e.Snapshot.RelativePath + ": remove " + e.RemovedRows + " record(s), " + e.Snapshot.Bytes.ToString("N0") + " -> " + e.RemainingBytes.ToString("N0") + " bytes"));
                }
                lines.Add("");
                lines.Add(Transactional ? "All changes commit together in one NTFS transaction. An interruption before commit changes nothing." : "Files are deleted one at a time. An interruption can leave part of the selection deleted; rescan before retrying.");
            }
            lines.Add("Every file is verified again before deletion. Settings and source projects are not changed. This is not secure erasure.");
            return string.Join("\r\n", lines.ToArray());
        }
    }

    internal sealed class DeletionResult
    {
        internal int Deleted;
        internal string Stopped;
        internal List<string> Notes = new List<string>();
        internal string Describe()
        {
            return Deleted + " selected session(s) permanently deleted." + (Stopped == null ? "" : "\r\nStopped safely: " + Stopped) + (Notes.Count == 0 ? "" : "\r\n" + string.Join("\r\n", Notes.ToArray())) + "\r\nRescan to see the current state.";
        }
    }
}
