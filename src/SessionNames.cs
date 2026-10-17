using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SessionPurge
{
    internal sealed class SessionNames
    {
        private readonly Provider provider;
        private readonly string id;
        private readonly HashSet<string> messageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<string, string>> summaries = new List<KeyValuePair<string, string>>();
        private string customTitle;
        private string providerTitle;
        private string firstPrompt;
        private string nickname;
        private bool identity;
        private bool uncertain;
        private static readonly Regex Sensitive = new Regex(@"(?i)(secret|password|passwd|credential|authorization|bearer\s|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|private[_ -]?key|sk-[a-z0-9_-]{8}|gh[pousr]_[a-z0-9]|github_pat_|AKIA[A-Z0-9]|-----BEGIN|[a-z0-9+/=_-]{40,}|https?://[^\s/]*@)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        internal SessionNames(Provider value, string sessionId) { provider = value; id = sessionId; }
        internal void Observe(Dictionary<string, object> row, int ordinal)
        {
            if (provider == Provider.Claude) Claude(row);
            else Codex(row, ordinal);
        }
        private void Claude(Dictionary<string, object> row)
        {
            string type = StrictJson.Text(row, "type");
            string owner = StrictJson.Text(row, "sessionId");
            if (row.ContainsKey("sessionId") && !string.Equals(owner, id, StringComparison.OrdinalIgnoreCase)) { uncertain = true; return; }
            if (type == "summary")
            {
                string leaf = StrictJson.Text(row, "leafUuid");
                if (HistoryReader.IsUuid(leaf)) summaries.Add(new KeyValuePair<string, string>(leaf, Label(StrictJson.Text(row, "summary"))));
                return;
            }
            if (!string.Equals(owner, id, StringComparison.OrdinalIgnoreCase)) return;
            if (type == "custom-title") { identity = true; customTitle = Label(StrictJson.Text(row, "customTitle")); return; }
            if (type == "ai-title") { identity = true; providerTitle = Label(StrictJson.Text(row, "aiTitle")); return; }
            if (type != "user" && type != "assistant") return;
            string uuid = StrictJson.Text(row, "uuid");
            if (!HistoryReader.IsUuid(uuid) || !messageIds.Add(uuid)) { uncertain = true; return; }
            identity = true;
            var message = StrictJson.Object(row, "message");
            if (type != "user" || message == null || StrictJson.Text(message, "role") != "user" || Flag(row, "isMeta") || Flag(row, "isCompactSummary") || Flag(row, "isSidechain") || row.ContainsKey("toolUseResult")) return;
            if (firstPrompt == null) firstPrompt = Prompt(message, "text");
        }
        private void Codex(Dictionary<string, object> row, int ordinal)
        {
            string type = StrictJson.Text(row, "type");
            var payload = StrictJson.Object(row, "payload");
            if (payload == null) return;
            if (ordinal == 1)
            {
                identity = type == "session_meta" && string.Equals(StrictJson.Text(payload, "id"), id, StringComparison.OrdinalIgnoreCase);
                if (!identity) uncertain = true;
                else nickname = Label(StrictJson.Text(payload, "agent_nickname"));
                return;
            }
            if (type == "session_meta") { uncertain = true; return; }
            if (type == "event_msg" && StrictJson.Text(payload, "type") == "thread_name_updated")
            {
                if (string.Equals(StrictJson.Text(payload, "thread_id"), id, StringComparison.OrdinalIgnoreCase)) providerTitle = Label(StrictJson.Text(payload, "thread_name"));
                return;
            }
            if (firstPrompt != null || !identity) return;
            if (type == "response_item" && StrictJson.Text(payload, "type") == "message" && StrictJson.Text(payload, "role") == "user") firstPrompt = Prompt(payload, "input_text");
            if (type == "event_msg" && StrictJson.Text(payload, "type") == "user_message") firstPrompt = Label(StrictJson.Text(payload, "message"));
        }
        private static bool Flag(Dictionary<string, object> row, string key)
        {
            object value;
            return row.TryGetValue(key, out value) && (!(value is bool) || (bool)value);
        }
        private static string Prompt(Dictionary<string, object> message, string textType)
        {
            object content;
            if (!message.TryGetValue("content", out content)) return null;
            if (content is string) return Label((string)content);
            var blocks = content as List<object>;
            if (blocks == null) return null;
            var text = new StringBuilder();
            foreach (object value in blocks)
            {
                var block = value as Dictionary<string, object>;
                if (block == null || StrictJson.Text(block, "type") != textType || block.Keys.Any(k => k != "type" && k != "text")) return null;
                string part = StrictJson.Text(block, "text");
                if (part == null || text.Length + part.Length > 4096) return null;
                if (text.Length > 0) text.Append(' ');
                text.Append(part);
            }
            return Label(text.ToString());
        }
        internal void Apply(SessionRecord session)
        {
            session.NameIdentityVerified = identity && !uncertain;
            if (!session.NameIdentityVerified) return;
            string summary = null;
            foreach (var value in summaries) if (messageIds.Contains(value.Key)) summary = value.Value;
            session.Name = customTitle ?? providerTitle ?? summary ?? nickname ?? firstPrompt ?? "Untitled session";
            session.NameSource = customTitle != null ? "Custom title" : providerTitle != null ? "Provider title" : summary != null ? "Conversation summary" : nickname != null ? "Subagent nickname" : firstPrompt != null ? "First user prompt" : "No usable title or user prompt";
        }
        internal static string Label(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return null;
            try { if (Sensitive.IsMatch(value)) return null; }
            catch (RegexMatchTimeoutException) { return null; }
            var clean = new StringBuilder();
            bool space = false;
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) { space = clean.Length > 0; continue; }
                if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format || c == '<' || c == '>') return null;
                if (space) { clean.Append(' '); space = false; }
                clean.Append(c);
            }
            string result = clean.ToString();
            if (result.Length < 3 || result[0] == '/' || result[0] == '[' || result[0] == '#' || !result.Any(char.IsLetterOrDigit)) return null;
            if (result.Length > 120)
            {
                int limit = 119;
                if (char.IsHighSurrogate(result[limit - 1])) limit--;
                result = result.Substring(0, limit).TrimEnd() + "\u2026";
            }
            return result;
        }
        internal static void ReadCodexIndex(Catalog catalog, CancellationToken cancellation)
        {
            string path = SafeFileSystem.Inside(catalog.Root, "session_index.jsonl");
            if (!File.Exists(path)) return;
            try
            {
                using (var file = new LockedFile(path, false))
                {
                    if (file.Length == 0 || file.Length > 8 * 1024 * 1024) throw new SafetyException("Unsupported name index size.");
                    var bytes = file.ReadAll(cancellation);
                    try
                    {
                        string text = StrictJson.Utf8.GetString(bytes);
                        if (text[0] == '\ufeff' || text[text.Length - 1] != '\n') throw new SafetyException("Incomplete name index.");
                        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        using (var reader = new StringReader(text))
                        {
                            string line;
                            int rows = 0;
                            while ((line = reader.ReadLine()) != null)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                if (++rows > 20000) throw new SafetyException("Name index limit exceeded.");
                                var row = StrictJson.Parse(line);
                                string owner = StrictJson.Text(row, "id");
                                if (HistoryReader.IsUuid(owner)) names[owner] = Label(StrictJson.Text(row, "thread_name"));
                            }
                        }
                        foreach (var session in catalog.Sessions)
                        {
                            string name;
                            if (session.NameIdentityVerified && names.TryGetValue(session.Id, out name) && name != null) { session.Name = name; session.NameSource = "Session index title"; }
                        }
                    }
                    finally { Array.Clear(bytes, 0, bytes.Length); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { catalog.Notices.Add("Session-name index unavailable or unsupported; transcript labels were used. Shared metadata remains unchanged."); }
        }
        internal static string SafeText(string value)
        {
            if (value == null) return "Unknown";
            var result = new StringBuilder();
            foreach (char c in value) if (!char.IsControl(c) && c != '‪' && c != '‫' && c != '‭' && c != '‮' && c != '‬' && (c < '⁦' || c > '⁩')) result.Append(c);
            return result.Length > 240 ? result.ToString(0, 240) : result.ToString();
        }
    }
}
