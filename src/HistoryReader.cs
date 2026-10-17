using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SessionPurge
{
    internal static class HistoryReader
    {
        private static readonly Regex Uuid = new Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);
        private static readonly Regex Rollout = new Regex("^rollout-[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}-[0-9]{2}-[0-9]{2}-(?<id>[0-9a-fA-F-]{36})\\.jsonl$", RegexOptions.CultureInvariant);
        internal static bool IsUuid(string value) { return value != null && Uuid.IsMatch(value); }
        internal static string FileId(Provider provider, string path)
        {
            return provider == Provider.Claude ? Path.GetFileNameWithoutExtension(path) : Rollout.Match(Path.GetFileName(path)).Groups["id"].Value;
        }
        internal static bool Candidate(Provider provider, string path)
        {
            string name = Path.GetFileName(path);
            return provider == Provider.Claude ? name.EndsWith(".jsonl", StringComparison.Ordinal) && IsUuid(Path.GetFileNameWithoutExtension(name)) : Rollout.IsMatch(name) && IsUuid(Rollout.Match(name).Groups["id"].Value);
        }
        internal static void Block(SessionRecord session, string reason) { if (session.Blocked == null) session.Blocked = reason; }
        internal static SessionRecord Read(Provider provider, string relative, LockedFile file, System.Threading.CancellationToken cancellation)
        {
            var session = new SessionRecord { RelativePath = relative, Bytes = file.Length, Modified = file.Modified, Identity = file.Identity };
            session.Id = FileId(provider, relative);
            session.Project = provider == Provider.Claude ? relative.Split('\\')[1] : "Unknown project";
            if (file.Length == 0 || file.Length > 32 * 1024 * 1024) throw new SafetyException("Empty transcript or transcript larger than 32 MiB.");
            session.Fingerprint = file.Hash();
            var bytes = file.ReadAll(cancellation);
            try
            {
                string content = StrictJson.Utf8.GetString(bytes);
                if (content[0] == '﻿') Block(session, "The transcript starts with a byte-order mark.");
                if (content[content.Length - 1] != '\n') Block(session, "The last record is incomplete; the transcript may still be in use.");
                var names = new SessionNames(provider, session.Id);
                string[] lines = content.Split('\n');
                int unreadable = 0;
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++session.Rows > 100000) { Block(session, "The transcript has more than 100,000 records."); unreadable++; break; }
                    if (lines[i].TrimEnd('\r').Length == 0) { Block(session, "The transcript contains blank lines."); continue; }
                    Dictionary<string, object> row;
                    try { row = StrictJson.Parse(lines[i]); }
                    catch (SafetyException) { unreadable++; continue; }
                    names.Observe(row, session.Rows);
                    if (provider == Provider.Claude) ReadClaude(session, row);
                    else ReadCodex(session, row, session.Rows);
                }
                if (lines.Length > 0 && lines[lines.Length - 1].Trim().Length > 0)
                {
                    try
                    {
                        var partial = StrictJson.Parse(lines[lines.Length - 1]);
                        if (provider == Provider.Claude) ReadClaude(session, partial);
                        else ReadCodex(session, partial, session.Rows + 1);
                        Links(session, partial);
                        session.LinkedIds.Remove(session.Id);
                    }
                    catch (SafetyException) { unreadable++; }
                }
                if (unreadable > 0) Block(session, unreadable + " record(s) could not be read.");
                if (provider == Provider.Claude) session.LinksKnown = unreadable == 0;
                else if (unreadable > 0) session.LinksKnown = false;
                names.Apply(session);
                if (session.ReferencedMessageIds.Any(id => !session.MessageIds.Contains(id))) Block(session, "Messages refer to another transcript (resumed or forked conversation).");
                return session;
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        private static void ReadClaude(SessionRecord session, Dictionary<string, object> row)
        {
            string type = StrictJson.Text(row, "type") ?? "record";
            if (row.ContainsKey("sessionId") && !string.Equals(StrictJson.Text(row, "sessionId"), session.Id, StringComparison.OrdinalIgnoreCase))
            {
                Block(session, "Contains records that belong to another session.");
                Links(session, Value(row, "sessionId"));
            }
            string version = StrictJson.Text(row, "version");
            if (!string.IsNullOrEmpty(version)) session.Version = version;
            if (Equals(Value(row, "isSidechain"), true)) Block(session, "Contains subagent (sidechain) records.");
            foreach (string key in new[] { "parentSessionId", "forkedFromSessionId", "forkedFrom", "agentId", "teamName" })
                if (Value(row, key) != null) Block(session, "Linked to another session (fork, team or agent).");
            foreach (string key in new[] { "parentSessionId", "forkedFromSessionId", "forkedFrom" }) Links(session, Value(row, key));
            session.LinkedIds.Remove(session.Id);
            string uuid = StrictJson.Text(row, "uuid");
            if (uuid != null) session.MessageIds.Add(uuid);
            foreach (string key in new[] { "parentUuid", "logicalParentUuid", "sourceToolAssistantUUID" }) Reference(session, StrictJson.Text(row, key));
            if (type == "summary") Reference(session, StrictJson.Text(row, "leafUuid"));
            if (type == "file-history-snapshot")
            {
                Reference(session, StrictJson.Text(row, "messageId"));
                var snapshot = StrictJson.Object(row, "snapshot");
                if (snapshot != null) Reference(session, StrictJson.Text(snapshot, "messageId"));
            }
            if (type == "assistant")
            {
                var message = StrictJson.Object(row, "message");
                var blocks = message == null ? null : Value(message, "content") as List<object>;
                if (blocks != null && blocks.OfType<Dictionary<string, object>>().Any(b => StrictJson.Text(b, "type") == "tool_use" && Delegation(StrictJson.Text(b, "name")))) session.Delegates = true;
            }
            Structure(session, row, type);
        }
        private static void ReadCodex(SessionRecord session, Dictionary<string, object> row, int ordinal)
        {
            string type = StrictJson.Text(row, "type") ?? "record";
            var payload = StrictJson.Object(row, "payload");
            if (ordinal == 1)
            {
                if (type != "session_meta" || payload == null || !string.Equals(StrictJson.Text(payload, "id"), session.Id, StringComparison.OrdinalIgnoreCase)) Block(session, "The first record does not identify this session.");
                else
                {
                    string cwd = StrictJson.Text(payload, "cwd");
                    if (!string.IsNullOrWhiteSpace(cwd)) session.Project = SessionNames.SafeText(cwd);
                    string version = StrictJson.Text(payload, "cli_version");
                    if (!string.IsNullOrEmpty(version)) session.Version = version;
                    session.LinksKnown = true;
                    foreach (string key in new[] { "parent_thread_id", "forked_from_id", "source", "history_base" }) Links(session, Value(payload, key));
                    session.LinkedIds.Remove(session.Id);
                    string rollout = StrictJson.Text(payload, "rollout_id");
                    if (rollout != null && !string.Equals(rollout, session.Id, StringComparison.OrdinalIgnoreCase)) Block(session, "The thread is split across several rollout files.");
                }
            }
            else if (type == "session_meta")
            {
                Block(session, "Contains a second session header.");
                session.LinksKnown = false;
                if (payload != null) foreach (object value in payload.Values) Links(session, value);
                session.LinkedIds.Remove(session.Id);
            }
            Structure(session, row, type);
        }
        internal static HashSet<string> LaterLinks(string path, SessionRecord session)
        {
            try
            {
                var probe = new SessionRecord();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    Native.FileInfo info;
                    if (!Native.GetFileInformationByHandle(stream.SafeFileHandle, out info) || (info.Attributes & 0x400u) != 0 || info.Links != 1) return null;
                    if (session.Identity != null && session.Identity != info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow) return null;
                    long length = ((long)info.SizeHigh << 32) | info.SizeLow;
                    DateTime written = DateTime.FromFileTimeUtc(((long)info.Written.dwHighDateTime << 32) | (uint)info.Written.dwLowDateTime);
                    if (length == session.Bytes && written == session.Modified) return probe.LinkedIds;
                    if (length < session.Bytes || length - session.Bytes > 32 * 1024 * 1024 || session.Fingerprint == null) return null;
                    using (var hash = SHA256.Create())
                    {
                        var buffer = new byte[65536];
                        long left = session.Bytes;
                        while (left > 0)
                        {
                            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                            if (read == 0) return null;
                            hash.TransformBlock(buffer, 0, read, null, 0);
                            left -= read;
                        }
                        hash.TransformFinalBlock(buffer, 0, 0);
                        if (Convert.ToBase64String(hash.Hash) != session.Fingerprint) return null;
                    }
                    long start = session.Bytes;
                    while (start > 0 && session.Bytes - start < 1048576) { stream.Position = start - 1; if (stream.ReadByte() == 10) break; start--; }
                    stream.Position = start;
                    string line;
                    while ((line = Line(stream)) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        Links(probe, StrictJson.Parse(line));
                    }
                }
                return probe.LinkedIds;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SafetyException) { return null; }
            catch (ArgumentException) { return null; }
        }
        private static void Structure(SessionRecord session, Dictionary<string, object> row, string type)
        {
            DateTimeOffset time;
            if (session.Structure.Count < 200 && StrictJson.Time(row, "timestamp", out time)) session.Structure.Add(time.UtcDateTime.ToString("u") + "  " + SessionNames.SafeText(type) + "  |  content hidden");
        }
        private static string Line(Stream stream)
        {
            var buffer = new List<byte>();
            int value;
            while ((value = stream.ReadByte()) >= 0 && value != 10) { buffer.Add((byte)value); if (buffer.Count > 1048576) throw new SafetyException("Record too long."); }
            return value == 10 || buffer.Count > 0 ? StrictJson.Utf8.GetString(buffer.ToArray()) : null;
        }
        private static void Reference(SessionRecord session, string id) { if (id != null) session.ReferencedMessageIds.Add(id); }
        private static void Links(SessionRecord session, object value)
        {
            var text = value as string;
            if (IsUuid(text)) session.LinkedIds.Add(text);
            var map = value as Dictionary<string, object>;
            if (map != null) foreach (object child in map.Values) Links(session, child);
            var list = value as List<object>;
            if (list != null) foreach (object child in list) Links(session, child);
        }
        private static object Value(Dictionary<string, object> row, string key)
        {
            object value;
            return row.TryGetValue(key, out value) ? value : null;
        }
        private static bool Delegation(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return new[] { "agent", "task", "teamcreate", "teamdelete", "sendmessage", "taskcreate", "taskupdate" }.Contains(name.Split('.').Last().ToLowerInvariant());
        }
    }
}
