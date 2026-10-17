using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SessionPurge.Tests
{
    internal static partial class TestMain
    {
        private static string Quote(string value) { return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\""; }
        private static string ClaudePrompt(string id, string text) { return ClaudeLine(id).Replace(Quote(Body), Quote(text)); }
        private static string Title(string type, string key, string owner, string value)
        {
            return "{\"type\":" + Quote(type) + ",\"sessionId\":" + Quote(owner) + "," + Quote(key) + ":" + Quote(value) + "}\n";
        }
        private static string IndexLine(string id, string title)
        {
            return "{\"id\":" + Quote(id) + ",\"thread_name\":" + Quote(title) + ",\"updated_at\":\"2026-01-02T10:00:00Z\"}\n";
        }
        private static string InlineName(string owner, string name)
        {
            return "{\"timestamp\":\"2026-01-02T10:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_name_updated\",\"thread_id\":" + Quote(owner) + ",\"thread_name\":" + Quote(name) + "}}\n";
        }
        private static SessionRecord Named(Provider provider, string text)
        {
            string home = Home(); Put(home, provider, A, text, false); return Scan(home, provider).Sessions.Single();
        }
        private static void ClaudeNames()
        {
            string transcript = ClaudePrompt(A, "Build the dashboard") + Title("custom-title", "customTitle", A, "Original dashboard") + Title("ai-title", "aiTitle", A, "Provider dashboard") + Title("custom-title", "customTitle", A, "Dashboard accessibility");
            var session = Named(Provider.Claude, transcript);
            Assert(session.Name == "Dashboard accessibility" && session.NameSource == "Custom title", "Claude title precedence incorrect.");
            Assert(session.Eligible, "Verified title records should be supported.");
            Assert(Named(Provider.Claude, transcript).Name == session.Name, "Name is nondeterministic.");
        }
        private static void SummaryNames()
        {
            string valid = ClaudePrompt(A, "Improve keyboard navigation") + "{\"type\":\"summary\",\"leafUuid\":" + Quote(C) + ",\"summary\":\"Keyboard navigation review\"}\n";
            Assert(Named(Provider.Claude, valid).Name == "Keyboard navigation review", "Owned summary ignored.");
            string foreign = valid.Replace("\"leafUuid\":" + Quote(C), "\"leafUuid\":" + Quote(B));
            Assert(Named(Provider.Claude, foreign).Name == "Improve keyboard navigation", "Unowned summary displayed.");
        }
        private static void PromptNames()
        {
            string actual = ClaudePrompt(A, "Improve the settings screen").Replace("\"uuid\":" + Quote(C), "\"uuid\":" + Quote(B));
            foreach (string first in new[] { "<system-reminder>internal text</system-reminder>", "[Request interrupted by user]", "# AGENTS.md instructions", "/rename", "<local-command-stdout>output</local-command-stdout>", "<environment_context>internal</environment_context>" })
                Assert(Named(Provider.Claude, ClaudePrompt(A, first) + actual).Name == "Improve the settings screen", "Internal prompt became a name.");
            foreach (string flag in new[] { "isMeta", "isCompactSummary", "isSidechain" })
            {
                string meta = ClaudePrompt(A, "Internal bookkeeping");
                meta = flag == "isSidechain" ? meta.Replace("\"isSidechain\":false", "\"isSidechain\":true") : meta.Replace("\"type\":\"user\"", "\"" + flag + "\":true,\"type\":\"user\"");
                Assert(Named(Provider.Claude, meta + actual).Name == "Improve the settings screen", "Flagged prompt became a name.");
            }
            string assistant = ClaudePrompt(A, "Assistant-only content").Replace("\"type\":\"user\"", "\"type\":\"assistant\"").Replace("\"role\":\"user\"", "\"role\":\"assistant\"");
            Assert(Named(Provider.Claude, assistant + actual).Name == "Improve the settings screen", "Assistant content became a name.");
            string codex = CodexText(A).Replace(Quote(Body), Quote("Review the settings screen"));
            Assert(Named(Provider.Codex, codex).Name == "Review the settings screen", "Codex user prompt fallback failed.");
            Assert(Named(Provider.Codex, codex.Replace("\"role\":\"user\"", "\"role\":\"system\"")).Name == "Untitled session", "Codex system message became a name.");
        }
        private static void TitleFallback()
        {
            string text = ClaudePrompt(A, "Inspect the window layout") + Title("custom-title", "customTitle", A, "Old label") + Title("ai-title", "aiTitle", A, "Window layout review");
            Assert(Named(Provider.Claude, text + Title("custom-title", "customTitle", A, " ")).Name == "Window layout review", "Empty latest rename resurrected a stale title.");
            Assert(Named(Provider.Claude, text + "{\"type\":\"custom-title\",\"sessionId\":" + Quote(A) + ",\"customTitle\":42}\n").Name == "Window layout review", "Malformed latest rename resurrected a stale title.");
            Assert(Named(Provider.Claude, ClaudeLine(A)).Name == "Untitled session", "Sensitive-only fixture invented a name.");
        }
        private static void NamesKeepBoundary()
        {
            var old = Named(Provider.Claude, ClaudePrompt(A, "Review build configuration"));
            Assert(old.Eligible && old.Name == "Review build configuration", "Existing safe profile regressed.");
            var unsupported = Named(Provider.Claude, ClaudePrompt(A, "Review build configuration").Replace("\"isSidechain\":false", "\"isSidechain\":false,\"forkedFrom\":{\"sessionId\":\"" + B + "\"}"));
            Assert(!unsupported.Eligible && unsupported.Name == old.Name, "Display adapter affected link safety.");
            string home = Home(); Codex(home, A, false); File.WriteAllText(Path.Combine(home, "session_index.jsonl"), IndexLine(A, "Build investigation"));
            var catalog = Scan(home, Provider.Codex);
            Assert(catalog.Sessions.Single().Name == "Build investigation" && catalog.Sessions.Single().Eligible, "Verified index should be supported.");
            Assert(Plan(catalog, A).StoreEdits.Single().RemovedRows == 1, "Index rows omitted from deletion preview.");
        }
        private static void CodexIndexNames()
        {
            string home = Home(); Codex(home, A, false); Codex(home, B, true);
            string index = IndexLine(A, "Earlier name").Replace("2026-01-02", "2026-02-02") + IndexLine(B, "Archived review") + IndexLine(A, "Latest appended name");
            string path = Path.Combine(home, "session_index.jsonl"); File.WriteAllText(path, index, new UTF8Encoding(false));
            var catalog = Scan(home, Provider.Codex);
            Assert(catalog.Sessions.Single(s => s.Id == A).Name == "Latest appended name", "Index timestamp incorrectly overrode append order.");
            Assert(catalog.Sessions.Single(s => s.Id == B).Name == "Archived review", "Index mapped the wrong session.");
            Assert(File.ReadAllText(path) == index, "Index was changed.");
            File.AppendAllText(path, IndexLine(A, ""));
            Assert(Scan(home, Provider.Codex).Sessions.Single(s => s.Id == A).Name == "Untitled session", "Empty latest index resurrected old name.");
        }
        private static void CodexInlineNames()
        {
            var session = Named(Provider.Codex, CodexText(A) + InlineName(A, "Inline session title"));
            Assert(session.Name == "Inline session title" && session.Eligible, "Verified inline name missing or blocked.");
            Assert(Named(Provider.Codex, CodexText(A) + InlineName(B, "Wrong session")).Name == "Untitled session", "Wrong-owner inline title displayed.");
        }
        private static void InvalidNameIndex()
        {
            string tolerant = Home(); Codex(tolerant, A, false); WriteStore(tolerant, "session_index.jsonl", IndexLine(A, "Index title with new fields").Replace("\"updated_at\"", "\"future_field\":1,\"path\""));
            var named = Scan(tolerant, Provider.Codex).Sessions.Single();
            Assert(named.Eligible && named.Name == "Index title with new fields", "Index rows with unfamiliar fields were not tolerated.");
            foreach (string text in new[] { IndexLine(A, "Earlier valid title") + "{broken}\n", IndexLine(A, "Injected").TrimEnd('\n'), "\ufeff" + IndexLine(A, "Injected"), IndexLine(A, "Injected").Replace("\"thread_name\"", "\"id\"") })
            {
                string home = Home(); Put(home, Provider.Codex, A, CodexText(A).Replace(Quote(Body), Quote("Original user request")), false);
                string path = Path.Combine(home, "session_index.jsonl"); File.WriteAllText(path, text, new UTF8Encoding(false));
                byte[] before = File.ReadAllBytes(path);
                var session = Scan(home, Provider.Codex).Sessions.Single();
                Assert(session.Name == "Original user request" && !session.Eligible, "Invalid index changed fallback or safety.");
                Assert(File.ReadAllBytes(path).SequenceEqual(before), "Invalid index was rewritten.");
            }
        }
        private static void NameOwnership()
        {
            Assert(Named(Provider.Claude, ClaudePrompt(A, "Valid request") + Title("custom-title", "customTitle", B, "Wrong owner")).Name == "Untitled session", "Foreign transcript owner was accepted.");
            string home = Home(); Codex(home, A, false); File.WriteAllText(Path.Combine(home, "session_index.jsonl"), IndexLine(B, "Foreign index title"));
            Assert(Scan(home, Provider.Codex).Sessions.Single().Name == "Untitled session", "Foreign index name was associated by proximity.");
            string duplicate = Home(); string file = Claude(duplicate, A); string other = Path.Combine(duplicate, "projects", "other"); Directory.CreateDirectory(other); File.Copy(file, Path.Combine(other, A + ".jsonl"));
            Assert(Scan(duplicate, Provider.Claude).Sessions.All(s => !s.NameIdentityVerified && s.Name == "Untitled session"), "Ambiguous session identities kept labels.");
        }
        private static void NameIndexSafety()
        {
            string home = Home(); Codex(home, A, false); string path = Path.Combine(home, "session_index.jsonl"); File.WriteAllText(path, IndexLine(A, "Index title"));
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert(Scan(home, Provider.Codex).Sessions.Single().Name == "Untitled session", "Locked index was read.");
            string outside = Path.Combine(work, "index-link-" + Guid.NewGuid().ToString("N")); Assert(CreateHardLink(outside, path, IntPtr.Zero), "Fixture hard link failed.");
            Assert(Scan(home, Provider.Codex).Sessions.Single().Name == "Untitled session", "Hard-linked index was accepted.");
            Assert(File.ReadAllText(outside) == IndexLine(A, "Index title"), "Hard-linked index changed.");
        }
        private static void NamePrivacy()
        {
            foreach (string text in new[] { Body, "password: fixture-value", "api_key=fixture-value", "Authorization: Bearer fixture-value", "-----BEGIN PRIVATE KEY-----", new string('a', 50), "http://fixture:fixture@example.invalid", "Normal\u202e reversed text", "<task-notification>output</task-notification>" }) Assert(SessionNames.Label(text) == null, "Unsafe label accepted.");
            Assert(SessionNames.Label("Dashboard & settings") == "Dashboard & settings", "Literal ampersand changed.");
        }
        private static void NameFormatting()
        {
            Assert(SessionNames.Label("  Review\n  the\t dashboard  ") == "Review the dashboard", "Whitespace normalization incorrect.");
            string label = SessionNames.Label(new string('x', 30) + " " + new string('y', 30) + " " + new string('z', 30) + " " + new string('w', 30));
            Assert(label != null && label.Length <= 120 && label.EndsWith("\u2026"), "Label length not bounded.");
            Assert(SessionNames.Label("  ") == null && SessionNames.Label("!!!") == null, "Empty or meaningless label accepted.");
        }
        private static void PartialName()
        {
            string home = Home(); Put(home, Provider.Claude, A, ClaudePrompt(A, "A valid request") + Title("custom-title", "customTitle", A, "A private label") + "{broken}\n", false);
            var session = Scan(home, Provider.Claude).Sessions.Single();
            Assert(!session.Eligible && session.Blocked.Contains("could not be read") && session.Name == "A private label", "Unreadable record hid verified titles or allowed deletion.");
        }
        private static void NamingDoesNotReadCredentials()
        {
            string home = Home(); Codex(home, A, false);
            string auth = Path.Combine(home, "auth.json"); string settings = Path.Combine(home, "config.toml"); File.WriteAllText(auth, Body); File.WriteAllText(settings, Body);
            using (var lockedAuth = new FileStream(auth, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var lockedSettings = new FileStream(settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(Scan(home, Provider.Codex).Sessions.Single().Eligible, "Naming tried to open unrelated private files.");
            Assert(File.ReadAllText(auth) == Body && File.ReadAllText(settings) == Body, "Private fixture files changed.");
        }
        private static void GuiNamesAndLayout()
        {
            string home = Home(); Put(home, Provider.Claude, A, ClaudePrompt(A, "Improve dashboard navigation"), false); Claude(home, B);
            using (var window = new MainWindow(Engine))
            {
                ShowHidden(window); Await(window.ScanFixture(Provider.Claude, home));
                Size standard = window.Size;
                Size minimum = window.MinimumSize;
                Assert(Screen.AllScreens.Any(s => minimum.Width <= s.WorkingArea.Width && minimum.Height <= s.WorkingArea.Height && standard.Width <= s.WorkingArea.Width && standard.Height <= s.WorkingArea.Height), "Window does not fit a screen work area.");
                var toggle = Find<CheckBox>(window, "showNames");
                var grid = Find<DataGridView>(window, "sessions");
                var details = Find<TextBox>(window, "details");
                var title = grid.Columns["Name"];
                Assert(toggle.Checked && title.Visible && title.HeaderText == "Session" && title.DisplayIndex == 1 && grid.Columns["Project"].DisplayIndex == 2, "Session titles are not shown first by default.");
                var titled = grid.Rows.Cast<DataGridViewRow>().First(r => ((SessionRecord)r.Tag).Id == A);
                Assert(Convert.ToString(titled.Cells["Name"].Value) == "Improve dashboard navigation" && Convert.ToString(titled.Cells["Project"].Value) != Convert.ToString(titled.Cells["Name"].Value), "The project path was shown instead of the session title.");
                Assert(grid.Rows.Cast<DataGridViewRow>().Any(r => Convert.ToString(r.Cells["Name"].Value) == "Untitled session"), "A session without a title lost its fallback name.");
                toggle.Checked = false; window.SetSearch("dashboard"); Assert(window.VisibleSessions == 0, "Hidden names leaked through search.");
                toggle.Checked = true; Assert(window.VisibleSessions == 1 && grid.Columns["Name"].Visible, "Name search did not find the prompt.");
                window.SetSearch("");
                var row = grid.Rows.Cast<DataGridViewRow>().First(r => ((SessionRecord)r.Tag).Id == A);
                grid.CurrentCell = row.Cells["Id"]; Application.DoEvents();
                Assert(details.Text.StartsWith("Improve dashboard navigation"), "Details did not show the selected name.");
                toggle.Checked = false;
                Assert(!AllControls(window).Any(c => c.Text.Contains("Improve dashboard navigation")), "Hiding names left a title visible.");
                Assert(!grid.Rows.Cast<DataGridViewRow>().SelectMany(r => r.Cells.Cast<DataGridViewCell>()).Any(c => Convert.ToString(c.Value).Contains("Improve dashboard navigation")), "Hiding names left a title in the list.");
                string[] essential = { "providers", "home", "scan", "cancel", "search", "age", "showNames", "selectVisible", "reviewDeletion", "status" };
                bool unclamped = window.MinimumSize == window.LogicalToDeviceUnits(new Size(960, 600)) && standard == window.LogicalToDeviceUnits(new Size(1280, 820));
                foreach (Size size in new[] { standard, window.MinimumSize })
                {
                    window.Size = size; Application.DoEvents();
                    Assert(grid.Visible && details.Visible && grid.Height >= 130 && grid.Width >= 400 && details.Width >= 200, "Session list or details unusable at " + size + ".");
                    Assert(!unclamped || !window.HorizontalScroll.Visible && !window.VerticalScroll.Visible, "Window scrolls at " + size + ".");
                    foreach (string name in essential) AssertFits(Find<Control>(window, name));
                    Render(window);
                }
                window.MinimumSize = Size.Empty;
                window.Size = window.LogicalToDeviceUnits(new Size(640, 360)); Application.DoEvents();
                Assert(window.HorizontalScroll.Visible && window.VerticalScroll.Visible, "A small screen cannot scroll to every control.");
                foreach (string name in essential) AssertFits(Find<Control>(window, name), Find<TableLayoutPanel>(window, "layout"));
                window.Size = standard; toggle.Checked = true; Application.DoEvents();
                Assert(!unclamped || !grid.Controls.OfType<HScrollBar>().Any(bar => bar.Visible), "Session columns need horizontal scrolling at the default size.");
                Render(window);
                toggle.Checked = false;
                Find<ComboBox>(window, "providers").SelectedIndex = 1;
                Assert(!toggle.Checked && !grid.Columns["Name"].Visible, "Switching providers showed names again after they were hidden.");
                window.Close();
            }
            DateTime now = Now;
            var engine = new HistoryEngine(delegate { return null; }, delegate { return now; });
            var catalog = engine.Scan(Provider.Claude, home, CancellationToken.None);
            var plan = engine.Plan(catalog, catalog.Sessions.Where(s => s.Id == A));
            using (var dialog = new ConfirmWindow(plan, engine.Clock))
            {
                ShowHidden(dialog);
                var input = Find<TextBox>(dialog, "confirmation");
                var remove = Find<Button>(dialog, "deletePermanently");
                input.Text = "DELETE"; Assert(remove.Enabled, "Valid confirmation disabled.");
                now = plan.Expires; input.Text = ""; input.Text = "DELETE"; Assert(remove.Enabled, "Confirmation disabled before expiry.");
                now = plan.Expires.AddTicks(1);
                Pump(() => !remove.Enabled);
                dialog.Close();
            }
        }
        private static void AssertFits(Control control, Control boundary = null)
        {
            Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
            for (Control parent = control.Parent; parent != null; parent = parent == boundary ? null : parent.Parent)
            {
                Rectangle area = parent.RectangleToScreen(parent.ClientRectangle);
                Assert(bounds.Top >= area.Top && bounds.Bottom <= area.Bottom && bounds.Left >= area.Left && bounds.Right <= area.Right, "A visible control is clipped by its parent.");
            }
        }
    }
}
