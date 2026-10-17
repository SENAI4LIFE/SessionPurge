using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
            var unsupported = Named(Provider.Claude, ClaudePrompt(A, "Review build configuration").Replace("2.1.32", "9.0.0"));
            Assert(!unsupported.Eligible && unsupported.Name == old.Name, "Display adapter affected version safety.");
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
            foreach (string text in new[] { IndexLine(A, "Earlier valid title") + "{broken}\n", IndexLine(A, "Injected").TrimEnd('\n'), IndexLine(A, "Injected").Replace("\"updated_at\"", "\"path\""), "\ufeff" + IndexLine(A, "Injected"), IndexLine(A, "Injected").Replace("\"thread_name\"", "\"id\"") })
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
            Assert(!session.Eligible && session.Name == "Untitled session", "Partial parse exposed an unverified label.");
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
        private static void WaitUi(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
            Assert(task.IsCompleted, "UI operation timed out."); task.GetAwaiter().GetResult(); Application.DoEvents();
        }
        private static void GuiNamesAndLayout()
        {
            string home = Home(); Put(home, Provider.Claude, A, ClaudePrompt(A, "Improve dashboard navigation"), false); Claude(home, B);
            using (var window = new MainWindow(Engine))
            {
                window.Show(); WaitUi(window.ScanFixture(Provider.Claude, home));
                var controls = AllControls(window).ToList();
                var toggle = (CheckBox)controls.Single(c => c.AccessibleName == "Show session names");
                Assert(!toggle.Checked, "Names visible by default.");
                window.SetSearch("dashboard"); Assert(window.VisibleSessions == 0, "Hidden names leaked through search.");
                toggle.Checked = true; Assert(window.VisibleSessions == 1, "Name search did not find the prompt.");
                window.SetSearch("");
                var grid = (DataGridView)controls.Single(c => c is DataGridView);
                var row = grid.Rows.Cast<DataGridViewRow>().First(r => r.Tag is SessionRecord && ((SessionRecord)r.Tag).Id == A);
                grid.CurrentCell = row.Cells[1]; Application.DoEvents();
                Assert(controls.Single(c => c.AccessibleName == "Session detail title").Text == "Improve dashboard navigation", "Detail title did not reflect the selected name.");
                toggle.Checked = false;
                Assert(!controls.Any(c => c.Text.Contains("Improve dashboard navigation")), "Hiding names left a title visible.");
                var split = (SplitContainer)controls.Single(c => c is SplitContainer);
                window.Size = new Size(1360, 920); Application.DoEvents();
                Assert(split.Orientation == Orientation.Vertical && split.Panel1.Width >= 650, "Wide layout hides list columns.");
                window.Size = new Size(980, 850); Application.DoEvents();
                var detailTab = (Button)controls.Single(c => c is Button && c.Text == "Session details");
                var listTab = (Button)controls.Single(c => c is Button && c.Text == "Sessions");
                Assert(detailTab.Visible && listTab.Visible && grid.Visible && grid.Height > 130, "Compact session navigation is unusable.");
                detailTab.PerformClick(); Application.DoEvents();
                Assert(!grid.Visible && controls.Single(c => c.AccessibleName == "Session file details").Visible, "Compact detail navigation failed.");
                listTab.PerformClick(); Application.DoEvents(); Assert(grid.Visible, "Compact list navigation failed.");
                window.Size = new Size(900, 760); Application.DoEvents(); Assert(grid.Visible && grid.Height >= 130, "Minimum-size session list cannot show a row.");
                AssertFits(controls.Single(c => c.AccessibleName == "Provider home directory"));
                AssertFits(controls.Single(c => c is Button && c.Text == "Review deletion"));
                using (var compact = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(compact, new Rectangle(0, 0, window.Width, window.Height)); compact.Save(Path.Combine(Path.GetDirectoryName(work), "synthetic-ui-compact.png")); }
                window.Size = new Size(1360, 920); Application.DoEvents();
                toggle.Checked = true;
                AssertFits(controls.Single(c => c.AccessibleName == "Provider home directory"));
                using (var image = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new Rectangle(0, 0, window.Width, window.Height)); image.Save(Path.Combine(Path.GetDirectoryName(work), "synthetic-ui-names.png")); }
                window.Close();
            }
            var plan = Plan(Scan(home, Provider.Claude), A);
            using (var dialog = new ConfirmWindow(plan))
            {
                dialog.Show(); Application.DoEvents();
                var controls = AllControls(dialog).ToList();
                ((TextBox)controls.Single(c => c.AccessibleName == "Type DELETE to confirm")).Text = "DELETE";
                var remove = controls.Single(c => c is Button && c.Text == "Delete permanently"); Assert(remove.Enabled, "Valid confirmation disabled.");
                plan.Expires = DateTime.UtcNow.AddSeconds(-1);
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (remove.Enabled && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
                Assert(!remove.Enabled, "Expired confirmation remains enabled."); dialog.Close();
            }
        }
        private static void AssertFits(Control control)
        {
            Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
            {
                Rectangle area = parent.RectangleToScreen(parent.ClientRectangle);
                Assert(bounds.Top >= area.Top && bounds.Bottom <= area.Bottom && bounds.Left >= area.Left && bounds.Right <= area.Right, "A visible control is clipped by its parent.");
            }
        }
    }
}
