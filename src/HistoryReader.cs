using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
        internal static SessionRecord Read(Provider provider, string relative, LockedFile file, System.Threading.CancellationToken cancellation)
        {
            var session = new SessionRecord { RelativePath = relative, Bytes = file.Length, Modified = file.Modified, Identity = file.Identity };
            session.Id = FileId(provider, relative);
            session.Project = provider == Provider.Claude ? relative.Split('\\')[1] : "Unknown project";
            if (file.Length == 0 || file.Length > 32 * 1024 * 1024) throw new SafetyException("Empty transcript or transcript larger than 32 MiB.");
            session.Fingerprint = file.Hash();
            file.Stream.Position = 0;
            var bytes = new byte[(int)file.Length];
            try
            {
                int offset = 0;
                while (offset < bytes.Length) { int count = file.Stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new SafetyException("Transcript changed while reading."); offset += count; }
                string content = new UTF8Encoding(false, true).GetString(bytes);
                if (content.Length == 0 || content[0] == '\ufeff' || content[content.Length - 1] != '\n') throw new SafetyException("Only complete UTF-8 JSONL without a BOM is supported.");
                using (var reader = new StringReader(content))
                {
                    var names = new SessionNames(provider, session.Id);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (++session.Rows > 100000 || line.Length == 0) throw new SafetyException("Blank rows or excessive row count are unsupported.");
                        var row = StrictJson.Parse(line);
                        names.Observe(row, session.Rows);
                        if (session.Blocked == null)
                        {
                            try
                            {
                                if (provider == Provider.Claude) ReadClaude(session, row);
                                else ReadCodex(session, row);
                            }
                            catch (SafetyException exception) { session.Blocked = exception.Message; }
                        }
                    }
                    names.Apply(session);
                }
                if (session.Version == "Unknown" && session.Blocked == null) session.Blocked = "No supported provider version was found.";
                if (session.Blocked == null && session.ReferencedMessageIds.Any(id => !session.MessageIds.Contains(id))) session.Blocked = "Claude metadata refers to a message outside the verified transcript.";
                return session;
            }
            finally { Array.Clear(bytes, 0, bytes.Length); file.Stream.Position = 0; }
        }
        private static void ReadClaude(SessionRecord session, Dictionary<string, object> row)
        {
            string type = StrictJson.Text(row, "type");
            if (type == "file-history-snapshot")
            {
                OnlyKeys(row, new[] { "type", "messageId", "snapshot", "isSnapshotUpdate" });
                string snapshotMessageId = StrictJson.Text(row, "messageId");
                var snapshot = StrictJson.Object(row, "snapshot");
                if (!IsUuid(snapshotMessageId) || snapshot == null || !string.Equals(StrictJson.Text(snapshot, "messageId"), snapshotMessageId, StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Claude snapshot message identity is ambiguous.");
                OnlyKeys(snapshot, new[] { "messageId", "trackedFileBackups", "timestamp" });
                if (StrictJson.Object(snapshot, "trackedFileBackups") == null) throw new SafetyException("Unknown Claude snapshot structure.");
                session.ReferencedMessageIds.Add(snapshotMessageId);
                return;
            }
            if (type == "queue-operation")
            {
                OnlyKeys(row, new[] { "type", "operation", "timestamp", "sessionId", "content" });
                string operation = StrictJson.Text(row, "operation");
                if (!string.Equals(StrictJson.Text(row, "sessionId"), session.Id, StringComparison.OrdinalIgnoreCase) || operation != "enqueue" && operation != "dequeue" && operation != "remove") throw new SafetyException("Claude queued record has unknown ownership or operation.");
                return;
            }
            if (type == "custom-title" || type == "ai-title")
            {
                OnlyKeys(row, new[] { "type", "sessionId", "customTitle", "aiTitle" });
                if (!string.Equals(StrictJson.Text(row, "sessionId"), session.Id, StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Claude title belongs to a different session.");
                return;
            }
            if (type == "summary")
            {
                OnlyKeys(row, new[] { "type", "leafUuid", "summary" });
                if (!session.MessageIds.Contains(StrictJson.Text(row, "leafUuid"))) throw new SafetyException("Claude summary is not linked to a verified message in this transcript.");
                return;
            }
            OnlyKeys(row, new[] { "type", "sessionId", "version", "uuid", "parentUuid", "logicalParentUuid", "isSidechain", "userType", "cwd", "timestamp", "message", "requestId", "gitBranch", "slug", "isMeta", "isCompactSummary", "isApiErrorMessage", "toolUseResult", "sourceToolAssistantUUID", "parentSessionId", "forkedFromSessionId", "agentId", "teamName", "permissionMode", "todos", "thinkingMetadata", "data", "toolUseID", "parentToolUseID", "subtype", "content", "level", "compactMetadata", "stopReason", "preventedContinuation", "durationMs", "costUSD", "numTurns", "promptId", "origin", "promptSource", "entrypoint" });
            if (type != "user" && type != "assistant" && type != "system" && type != "progress") throw new SafetyException("Unsupported Claude record type; related-session ownership is not established.");
            if (!string.Equals(StrictJson.Text(row, "sessionId"), session.Id, StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Transcript contains an ambiguous session owner.");
            string version = StrictJson.Text(row, "version");
            Version parsed;
            if (version == null || !Version.TryParse(version, out parsed) || parsed.Major != 2 || parsed.Minor != 1 || parsed.Build < 0 || parsed.Revision >= 0) throw new SafetyException("Unsupported Claude version; a stable 2.1.x envelope is required.");
            session.Version = version;
            object sidechain;
            if (row.TryGetValue("isSidechain", out sidechain) && (!(sidechain is bool) || (bool)sidechain)) throw new SafetyException("Subagent or ambiguous sidechain ownership is unsupported.");
            foreach (string key in new[] { "parentSessionId", "forkedFromSessionId", "agentId", "teamName" })
                if (row.ContainsKey(key) && row[key] != null) throw new SafetyException("Related-session metadata requires an unsupported coordinated operation.");
            foreach (string key in new[] { "sourceToolAssistantUUID", "logicalParentUuid" })
                if (row.ContainsKey(key) && row[key] != null && !session.MessageIds.Contains(StrictJson.Text(row, key))) throw new SafetyException("A Claude message refers outside the verified transcript.");
            string messageId = StrictJson.Text(row, "uuid");
            if (!IsUuid(messageId) || session.MessageIds.Contains(messageId)) throw new SafetyException("Message identity is missing, duplicate, or invalid.");
            object parent;
            if (!row.TryGetValue("parentUuid", out parent) || parent != null && (!(parent is string) || !session.MessageIds.Contains((string)parent))) throw new SafetyException("Message parent is missing or outside the verified transcript.");
            session.MessageIds.Add(messageId);
            string timestamp = StrictJson.Text(row, "timestamp");
            DateTimeOffset time;
            if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) throw new SafetyException("Message timestamp is missing or invalid.");
            if (type == "system" || type == "progress")
            {
                if (session.Structure.Count < 200) session.Structure.Add(time.UtcDateTime.ToString("u") + "  " + type + "  |  content hidden");
                return;
            }
            var message = StrictJson.Object(row, "message");
            object body;
            if (message == null || StrictJson.Text(message, "role") != type || !message.TryGetValue("content", out body) || (!(body is string) && !(body is List<object>))) throw new SafetyException("Unsupported message structure.");
            OnlyKeys(message, new[] { "role", "content", "id", "model", "stop_reason", "stop_sequence", "usage", "type", "container", "context_management" });
            if (body is List<object>) ContentBlocks((List<object>)body, false);
            if (session.Structure.Count < 200) session.Structure.Add(time.UtcDateTime.ToString("u") + "  " + type + "  |  content hidden");
        }
        private static void ReadCodex(SessionRecord session, Dictionary<string, object> row)
        {
            OnlyKeys(row, new[] { "timestamp", "type", "payload" });
            string type = StrictJson.Text(row, "type");
            DateTimeOffset time;
            if (!DateTimeOffset.TryParse(StrictJson.Text(row, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) throw new SafetyException("Record timestamp is missing or invalid.");
            var payload = StrictJson.Object(row, "payload");
            if (payload == null) throw new SafetyException("Unsupported Codex payload.");
            if (session.Rows == 1)
            {
                OnlyKeys(payload, new[] { "id", "timestamp", "cwd", "originator", "cli_version", "source", "model_provider", "base_instructions", "instructions", "git", "forked_from_id", "parent_thread_id", "history_base", "rollout_id", "thread_source" });
                if (type != "session_meta" || !string.Equals(StrictJson.Text(payload, "id"), session.Id, StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Filename and session metadata do not identify one session.");
                string version = StrictJson.Text(payload, "cli_version");
                Version parsed;
                if (version == null || !Version.TryParse(version, out parsed) || parsed.Major != 0 || parsed.Minor < 1 || parsed.Build < 0 || parsed.Revision >= 0) throw new SafetyException("Unsupported Codex version; a stable 0.x.y envelope is required.");
                session.Version = version;
                string cwd = StrictJson.Text(payload, "cwd");
                if (string.IsNullOrWhiteSpace(cwd)) throw new SafetyException("Project metadata is missing.");
                if (!DateTimeOffset.TryParse(StrictJson.Text(payload, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) throw new SafetyException("Session metadata timestamp is invalid.");
                object instructions;
                if (payload.TryGetValue("instructions", out instructions) && instructions != null && !(instructions is string)) throw new SafetyException("Unknown instruction envelope.");
                object baseInstructions;
                if (payload.TryGetValue("base_instructions", out baseInstructions) && baseInstructions != null)
                {
                    var instructionBlock = baseInstructions as Dictionary<string, object>;
                    if (instructionBlock == null || StrictJson.Text(instructionBlock, "text") == null) throw new SafetyException("Unknown base-instruction envelope.");
                    OnlyKeys(instructionBlock, new[] { "text" });
                }
                session.Project = SafeFileSystem.SafeText(cwd);
                foreach (string key in new[] { "forked_from_id", "parent_thread_id", "history_base", "rollout_id", "thread_source" })
                    if (payload.ContainsKey(key) && payload[key] != null) throw new SafetyException("Linked or immutable rollouts require an unsupported coordinated operation.");
                object source;
                if (payload.TryGetValue("source", out source) && (!(source is string) || ((string)source != "cli" && (string)source != "vscode"))) throw new SafetyException("Subagent or unknown session source.");
                return;
            }
            if (type != "response_item" && type != "event_msg" && type != "turn_context" && type != "compacted") throw new SafetyException("Unsupported Codex record type.");
            if (type == "response_item")
            {
                string itemType = StrictJson.Text(payload, "type");
                if (itemType != "message")
                {
                    string tool = StrictJson.Text(payload, "name");
                    if ((itemType == "function_call" || itemType == "custom_tool_call") && Delegation(tool, true)) throw new SafetyException("Codex delegation requires verified linked-session support.");
                    if (itemType != "function_call" && itemType != "function_call_output" && itemType != "custom_tool_call" && itemType != "custom_tool_call_output" && itemType != "reasoning" && itemType != "web_search_call" && itemType != "local_shell_call") throw new SafetyException("Unknown Codex response-item type.");
                    OnlyKeys(payload, new[] { "type", "id", "call_id", "name", "arguments", "output", "input", "status", "summary", "content", "encrypted_content", "action", "namespace" });
                    return;
                }
                OnlyKeys(payload, new[] { "type", "role", "content", "id", "phase", "end_turn" });
                string role = StrictJson.Text(payload, "role");
                object body;
                if (StrictJson.Text(payload, "type") != "message" || (role != "user" && role != "assistant" && role != "developer" && role != "system") || !payload.TryGetValue("content", out body) || !(body is List<object>)) throw new SafetyException("Only simple Codex message items are supported; tools and linked items are browse-only.");
                ContentBlocks((List<object>)body, true);
            }
            if (type == "event_msg")
            {
                string eventType = StrictJson.Text(payload, "type");
                if (eventType == "thread_name_updated")
                {
                    OnlyKeys(payload, new[] { "type", "thread_id", "thread_name" });
                    if (!string.Equals(StrictJson.Text(payload, "thread_id"), session.Id, StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Codex title event refers to a different session.");
                    return;
                }
                OnlyKeys(payload, new[] { "type", "message", "images", "local_images", "text_elements", "last_agent_message", "turn_id", "collaboration_mode_kind", "model_context_window", "info", "rate_limits" });
                if (eventType != "user_message" && eventType != "agent_message" && eventType != "task_started" && eventType != "task_complete" && eventType != "token_count") throw new SafetyException("Unsupported event or uncertain related-session event.");
            }
            if (type == "turn_context")
            {
                OnlyKeys(payload, new[] { "turn_id", "cwd", "current_date", "timezone", "approval_policy", "approvals_reviewer", "sandbox_policy", "model", "model_provider", "personality", "collaboration_mode", "effort", "reasoning_effort", "summary", "user_instructions", "developer_instructions", "base_instructions", "output_schema", "final_output_json_schema", "truncation_policy", "auto_compact_token_limit", "token_budget", "is_review_mode" });
            }
            if (type == "compacted")
            {
                OnlyKeys(payload, new[] { "message", "replacement_history" });
                if (payload.ContainsKey("replacement_history") && payload["replacement_history"] != null) throw new SafetyException("Compaction with embedded replacement history needs a linked-record adapter.");
            }
            if (session.Structure.Count < 200) session.Structure.Add(time.UtcDateTime.ToString("u") + "  " + type + "  |  payload hidden");
        }
        private static void OnlyKeys(Dictionary<string, object> value, string[] allowed)
        {
            foreach (string key in value.Keys) if (!allowed.Contains(key, StringComparer.Ordinal)) throw new SafetyException("Unknown schema fields require review; deletion is blocked.");
        }
        private static void ContentBlocks(List<object> blocks, bool codex)
        {
            foreach (object item in blocks)
            {
                var block = item as Dictionary<string, object>;
                if (block == null) throw new SafetyException("Unknown content block structure.");
                string type = StrictJson.Text(block, "type");
                if (codex ? type == "input_text" || type == "output_text" : type == "text")
                {
                    OnlyKeys(block, new[] { "type", "text" });
                    if (StrictJson.Text(block, "text") == null) throw new SafetyException("Malformed text content block.");
                }
                else if (codex ? type == "input_image" : type == "tool_use" || type == "tool_result" || type == "thinking" || type == "redacted_thinking" || type == "image" || type == "document")
                {
                    if (type == "tool_use" && Delegation(StrictJson.Text(block, "name"), false)) throw new SafetyException("Claude delegation requires verified related-artifact support.");
                    OnlyKeys(block, new[] { "type", "id", "name", "input", "tool_use_id", "content", "is_error", "thinking", "signature", "data", "source", "image_url", "detail", "cache_control", "citations", "title", "context" });
                }
                else throw new SafetyException("Unknown content block; ownership cannot be verified.");
            }
        }
        private static bool Delegation(string name, bool codex)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string tool = name.Split('.').Last().ToLowerInvariant();
            return codex ? new[] { "spawn_agent", "send_input", "wait", "close_agent", "resume_agent" }.Contains(tool) : new[] { "agent", "task", "teamcreate", "teamdelete", "sendmessage", "taskcreate", "taskupdate" }.Contains(tool);
        }
    }
}
