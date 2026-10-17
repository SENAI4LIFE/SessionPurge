using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace SessionPurge
{
    internal static class SharedHistory
    {
        internal static bool Recognized(Provider provider, string relative)
        {
            return relative == "history.jsonl" || provider == Provider.Codex && relative == "session_index.jsonl" || provider == Provider.Claude && relative.StartsWith("projects\\", StringComparison.Ordinal) && relative.Split('\\').Length == 3 && Path.GetFileName(relative) == "sessions-index.json";
        }
        internal static StoreSnapshot Inspect(Catalog catalog, string relative, LockedFile file, CancellationToken cancellation)
        {
            byte[] ignored;
            int count;
            Rewrite(catalog, relative, file, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellation, out ignored, out count);
            Array.Clear(ignored, 0, ignored.Length);
            return new StoreSnapshot { RelativePath = relative, Identity = file.Identity, Fingerprint = file.Hash(), Bytes = file.Length, Modified = file.Modified };
        }
        internal static void Rewrite(Catalog catalog, string relative, LockedFile file, HashSet<string> ids, CancellationToken cancellation, out byte[] result, out int removed)
        {
            if (file.Length > 32 * 1024 * 1024) throw new SafetyException("Shared history exceeds the 32 MiB supported limit.");
            byte[] original = file.ReadAll(cancellation);
            try
            {
                string text = StrictJson.Utf8.GetString(original);
                if (text.Length > 0 && text[0] == '﻿') throw new SafetyException("Shared history contains an unsupported byte-order mark.");
                if (relative.EndsWith("sessions-index.json", StringComparison.Ordinal))
                {
                    result = RewriteProjectIndex(catalog, relative, text, ids, cancellation, out removed);
                    return;
                }
                if (text.Length > 0 && text[text.Length - 1] != '\n') throw new SafetyException("Shared JSONL is incomplete; deletion is blocked.");
                removed = 0;
                int start = 0, rows = 0;
                using (var output = new MemoryStream())
                {
                    for (int end = 0; end < original.Length; end++)
                    {
                        if (original[end] != 10) continue;
                        cancellation.ThrowIfCancellationRequested();
                        if (++rows > 100000) throw new SafetyException("Shared JSONL exceeds 100,000 rows.");
                        string line = StrictJson.Utf8.GetString(original, start, end - start);
                        var row = StrictJson.Parse(line);
                        string owner = Owner(catalog.Provider, relative, row);
                        if (ids.Contains(owner)) removed++;
                        else output.Write(original, start, end - start + 1);
                        start = end + 1;
                    }
                    result = output.ToArray();
                }
            }
            finally { Array.Clear(original, 0, original.Length); }
        }
        private static string Owner(Provider provider, string relative, Dictionary<string, object> row)
        {
            string owner = StrictJson.Text(row, relative == "session_index.jsonl" ? "id" : provider == Provider.Claude ? "sessionId" : "session_id");
            return HistoryReader.IsUuid(owner) ? owner : null;
        }
        private static byte[] RewriteProjectIndex(Catalog catalog, string relative, string text, HashSet<string> ids, CancellationToken cancellation, out int removed)
        {
            var root = StrictJson.Parse(text);
            object version, entries;
            if (!root.TryGetValue("version", out version) || !(version is double) || (double)version != 1 || !root.TryGetValue("entries", out entries) || !(entries is List<object>)) throw new SafetyException("Only Claude project index version 1 is supported.");
            var chosen = new List<bool>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            removed = 0;
            foreach (object value in (List<object>)entries)
            {
                cancellation.ThrowIfCancellationRequested();
                var entry = value as Dictionary<string, object>;
                if (entry == null) throw new SafetyException("Malformed Claude project index entry.");
                string owner = StrictJson.Text(entry, "sessionId");
                if (!HistoryReader.IsUuid(owner) || !seen.Add(owner)) throw new SafetyException("Ambiguous Claude project index identity.");
                object fullPath;
                if (entry.TryGetValue("fullPath", out fullPath))
                {
                    string expected = SafeFileSystem.Inside(catalog.Root, Path.GetDirectoryName(relative) + "\\" + owner + ".jsonl");
                    if (!(fullPath is string) || !expected.Equals(((string)fullPath).Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Claude project index contains a contradictory transcript path. Metadata paths are never followed.");
                }
                object sidechain;
                if (entry.TryGetValue("isSidechain", out sidechain) && (!(sidechain is bool) || (bool)sidechain)) throw new SafetyException("Claude project index contains unsupported sidechain ownership.");
                if (ids.Contains(owner) && !catalog.Sessions.Any(s => string.Equals(s.Id, owner, StringComparison.OrdinalIgnoreCase) && string.Equals(s.RelativePath, Path.GetDirectoryName(relative) + "\\" + owner + ".jsonl", StringComparison.OrdinalIgnoreCase))) throw new SafetyException("A selected session is indexed under a different project. Ownership is ambiguous.");
                bool drop = ids.Contains(owner); chosen.Add(drop); if (drop) removed++;
            }
            if (removed == 0) return StrictJson.Utf8.GetBytes(text);
            int position = 0, arrayStart = -1, arrayEnd = -1;
            SkipWhite(text, ref position); position++;
            while (position < text.Length)
            {
                SkipWhite(text, ref position);
                if (text[position] == '}') break;
                int keyStart = position;
                SkipValue(text, ref position);
                var keyObject = StrictJson.Parse("{" + text.Substring(keyStart, position - keyStart) + ":null}");
                string key = keyObject.Keys.Single();
                SkipWhite(text, ref position); position++; SkipWhite(text, ref position);
                int start = position; SkipValue(text, ref position);
                if (key == "entries") { arrayStart = start; arrayEnd = position; break; }
                SkipWhite(text, ref position); if (text[position] == ',') position++;
            }
            if (arrayStart < 0) throw new SafetyException("Claude index array could not be located safely.");
            position = arrayStart + 1;
            var retained = new List<string>(); int ordinal = 0;
            while (position < arrayEnd - 1)
            {
                cancellation.ThrowIfCancellationRequested(); SkipWhite(text, ref position);
                if (text[position] == ']') break;
                int start = position; SkipValue(text, ref position);
                if (ordinal >= chosen.Count) throw new SafetyException("Claude index token count differs.");
                if (!chosen[ordinal++]) retained.Add(text.Substring(start, position - start));
                SkipWhite(text, ref position); if (text[position] == ',') position++;
            }
            if (ordinal != chosen.Count) throw new SafetyException("Claude index token count differs.");
            return StrictJson.Utf8.GetBytes(text.Substring(0, arrayStart + 1) + string.Join(",", retained.ToArray()) + text.Substring(arrayEnd - 1));
        }
        private static void SkipWhite(string text, ref int position) { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        private static void SkipValue(string text, ref int position)
        {
            bool quoted = false, escape = false; int depth = 0;
            for (; position < text.Length; position++)
            {
                char c = text[position];
                if (quoted) { if (escape) escape = false; else if (c == '\\') escape = true; else if (c == '"') { quoted = false; if (depth == 0) { position++; return; } } continue; }
                if (c == '"') { quoted = true; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') { if (depth == 0) return; if (--depth == 0) { position++; return; } }
                else if (depth == 0 && (c == ',' || c == ':' || char.IsWhiteSpace(c))) return;
            }
        }
    }
}
