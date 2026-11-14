using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SessionPurge.Tests
{
    internal static partial class TestMain
    {
        // Same synthetic rollout that tests\fixtures\compressed-rollout.jsonl.zst holds, compressed by
        // the reference Zstandard encoder at level 19 with a content checksum.
        private static readonly string Other = "44444444-4444-4444-8444-444444444444";
        private static string CompressedText()
        {
            var text = new StringBuilder("{\"timestamp\":\"2026-01-01T12:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + A + "\",\"timestamp\":\"2026-01-01T12:00:00Z\",\"cwd\":\"fixture-project\",\"cli_version\":\"0.130.0\",\"source\":\"cli\"}}\n");
            for (long i = 0; i < 1200; i++)
                text.Append("{\"timestamp\":\"2026-01-01T12:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + Body + " row " + i + " of " + (i * 2654435761L % 4294967296L).ToString("x8") + "\"}]}}\n");
            return text.ToString();
        }
        private static byte[] CompressedFixture()
        {
            return File.ReadAllBytes(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tests", "fixtures", "compressed-rollout.jsonl.zst")));
        }
        private static string PutCompressed(string home, string id, byte[] frame)
        {
            string path = Path.Combine(home, "sessions", "2026", "01", "01", "rollout-2026-01-01T12-00-00-" + id + ".jsonl.zst");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, frame);
            File.SetLastWriteTimeUtc(path, Now.AddDays(-10));
            return path;
        }
        // Builds a frame from raw (type 0) and run-length (type 1) blocks.
        private static byte[] Frame(bool single, bool checksum, params object[] blocks)
        {
            var content = new List<byte>();
            foreach (object block in blocks)
                if (block is string) content.AddRange(Encoding.ASCII.GetBytes((string)block));
                else { var run = (Tuple<byte, int>)block; content.AddRange(Enumerable.Repeat(run.Item1, run.Item2)); }
            var frame = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD, (byte)((single ? 0xA0 : 0x00) | (checksum ? 0x04 : 0)) };
            if (single) frame.AddRange(BitConverter.GetBytes(content.Count));
            else frame.Add(0x18); // 2^(10+3) = 8 KiB window
            for (int i = 0; i < blocks.Length; i++)
            {
                bool last = i == blocks.Length - 1;
                var text = blocks[i] as string;
                int header = (last ? 1 : 0) | (text != null ? 0 : 1 << 1) | (text != null ? text.Length : ((Tuple<byte, int>)blocks[i]).Item2) << 3;
                frame.AddRange(new[] { (byte)header, (byte)(header >> 8), (byte)(header >> 16) });
                if (text != null) frame.AddRange(Encoding.ASCII.GetBytes(text)); else frame.Add(((Tuple<byte, int>)blocks[i]).Item1);
            }
            if (checksum) frame.AddRange(BitConverter.GetBytes((uint)Zstd.XxHash64(content.ToArray(), 0, content.Count)));
            return frame.ToArray();
        }
        private static void ZstdDecoding()
        {
            Assert(Zstd.XxHash64(new byte[0], 0, 0) == 0xEF46DB3751D8E999UL && Zstd.XxHash64(Encoding.ASCII.GetBytes("abc"), 0, 3) == 0x44BC2CF5AD770999UL, "XXH64 does not match the reference values.");
            byte[] fixture = CompressedFixture();
            Assert(Encoding.UTF8.GetString(Zstd.Decompress(fixture, 1 << 20, CancellationToken.None)) == CompressedText(), "The reference-encoded frame did not decode to its original text.");
            var run = Tuple.Create((byte)'x', 5000);
            Assert(Encoding.ASCII.GetString(Zstd.Decompress(Frame(true, true, "ab", run, "c\n"), 1 << 20, CancellationToken.None)) == "ab" + new string('x', 5000) + "c\n", "Raw and run-length blocks did not decode.");
            var skippable = new byte[] { 0x50, 0x2A, 0x4D, 0x18, 3, 0, 0, 0, 1, 2, 3 };
            byte[] joined = Frame(false, false, "first\n").Concat(skippable).Concat(Frame(true, true, "second\n")).ToArray();
            Assert(Encoding.ASCII.GetString(Zstd.Decompress(joined, 1 << 20, CancellationToken.None)) == "first\nsecond\n", "Concatenated and skippable frames did not decode.");
            Refused(() => Zstd.Decompress(fixture, CompressedText().Length - 1, CancellationToken.None));
            Refused(() => Zstd.Decompress(Frame(false, false, Tuple.Create((byte)'x', 9000)), 1 << 20, CancellationToken.None));
            byte[] badChecksum = Frame(true, true, "abc\n"); badChecksum[badChecksum.Length - 1] ^= 1;
            Refused(() => Zstd.Decompress(badChecksum, 1 << 20, CancellationToken.None));
            var dictionary = Frame(true, false, "abc\n"); dictionary[4] |= 1;
            Refused(() => Zstd.Decompress(dictionary.Take(5).Concat(new byte[] { 7 }).Concat(dictionary.Skip(5)).ToArray(), 1 << 20, CancellationToken.None));
            byte[] prefixed = new byte[] { 0x50, 0x2A, 0x4D, 0x18, 0, 0, 0, 0 }.Concat(fixture).ToArray();
            Assert(Zstd.IsFrame(prefixed) && Zstd.Decompress(prefixed, 1 << 20, CancellationToken.None).SequenceEqual(Encoding.UTF8.GetBytes(CompressedText())), "A leading skippable frame was refused.");
            Refused(() => Zstd.Decompress(new byte[] { 0x50, 0x2A, 0x4D, 0x18, 0, 0, 0, 0 }, 1 << 20, CancellationToken.None));
            // Malformed frames: too few bits for both weight states, a Huffman tree without a pair of
            // weight-one symbols, and a compressed block larger than its window.
            foreach (string hex in new[] { "28b52ffd20106500000201020410881f01ffff0100", "28b52ffd20084500008200018122ff0100", "28b52ffd20011d0000086100", "28b52ffd000085000036000380100100010001000202020100" })
                Refused(() => Zstd.Decompress(Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(2 * i, 2), 16)).ToArray(), 1 << 20, CancellationToken.None));
            foreach (int length in new[] { 0, 3, 4, 5, 9, fixture.Length / 2, fixture.Length - 1 }) Refused(() => Zstd.Decompress(fixture.Take(length).ToArray(), 1 << 20, CancellationToken.None));
            // A changed byte is either refused or falls in bits the format ignores (unused header bit,
            // padding after a table description) and then still yields exactly the original text.
            byte[] original = Zstd.Decompress(fixture, 1 << 20, CancellationToken.None);
            for (int i = 4; i < fixture.Length; i += 5)
                foreach (byte flip in new byte[] { 0x01, 0x5A, 0x80 })
                {
                    var damaged = (byte[])fixture.Clone();
                    damaged[i] ^= flip;
                    byte[] decoded = null;
                    try { decoded = Zstd.Decompress(damaged, 1 << 20, CancellationToken.None); }
                    catch (SafetyException) { }
                    Assert(decoded == null || decoded.SequenceEqual(original), "A damaged frame decoded to different text at byte " + i + ".");
                }
        }
        private static void CompressedRollouts()
        {
            string home = Home(); string compressed = PutCompressed(home, A, CompressedFixture()); string plain = Codex(home, B, false);
            var catalog = Scan(home, Provider.Codex);
            var session = catalog.Sessions.Single(s => s.Id == A);
            Assert(catalog.Sessions.Count == 2 && catalog.Sessions.All(s => s.Eligible), "A compressed rollout was not listed as eligible: " + string.Join("; ", catalog.Sessions.Select(s => s.Blocked).ToArray()));
            Assert(session.Rows == 1201 && session.Project == "fixture-project" && session.Bytes == new FileInfo(compressed).Length && !catalog.Notices.Any(n => n.Contains("not recognized")), "Compressed rollout metadata is wrong.");
            Assert(Delete(Plan(catalog, A)).Deleted == 1 && !File.Exists(compressed) && File.Exists(plain), "The compressed rollout was not deleted on its own.");

            string viaCli = Home(); compressed = PutCompressed(viaCli, A, CompressedFixture()); Codex(viaCli, B, false);
            File.WriteAllText(Path.Combine(viaCli, "state_5.sqlite"), Body);
            var calls = new List<string>();
            var engine = new HistoryEngine(delegate { return null; }, delegate { return Now; }, CodexStub(viaCli, calls, null));
            catalog = engine.Scan(Provider.Codex, viaCli, CancellationToken.None);
            var result = engine.Delete(engine.Plan(catalog, catalog.Sessions.Where(s => s.Id == A)), "DELETE", CancellationToken.None);
            Assert(result.Stopped == null && result.Deleted == 1 && calls.SequenceEqual(new[] { A }) && !File.Exists(compressed) && result.Notes.Any(n => n.Contains("Rescan confirmed")), "Codex CLI deletion of a compressed rollout failed: " + result.Describe());

            string damaged = Home(); byte[] broken = CompressedFixture(); broken[broken.Length / 2] ^= 0x5A; PutCompressed(damaged, A, broken); Codex(damaged, B, false);
            catalog = Scan(damaged, Provider.Codex);
            Assert(catalog.Sessions.Single(s => s.Id == A).Blocked.Contains("Zstandard") && catalog.Sessions.Single(s => s.Id == B).Blocked.Contains("links between threads are unknown"), "A damaged compressed rollout was not refused, or did not protect other threads.");

            string twin = Home(); PutCompressed(twin, A, CompressedFixture()); Codex(twin, A, false);
            Assert(Scan(twin, Provider.Codex).Sessions.All(s => s.Blocked != null && s.Blocked.Contains("Duplicate session identity")), "Plain and compressed copies of one thread were not refused.");
        }
        private static void ScopedUnknownLinks()
        {
            string home = Home(); string other = Codex(home, B, false); Put(home, Provider.Codex, A, "{broken}\n" + CodexText(A), false);
            var catalog = Scan(home, Provider.Codex);
            Assert(!catalog.Sessions.Single(s => s.Id == A).Eligible && catalog.Sessions.Single(s => s.Id == B).Eligible, "An unreadable rollout that never names a thread still blocked it.");
            Assert(Delete(Plan(catalog, B)).Deleted == 1 && !File.Exists(other), "A thread beside an unrelated unreadable rollout was not deleted.");
            string named = Home(); Codex(named, B, false); Codex(named, C, false); Put(named, Provider.Codex, A, "{broken " + B.ToUpperInvariant() + "}\n" + CodexText(A), false);
            catalog = Scan(named, Provider.Codex);
            Assert(catalog.Sessions.Single(s => s.Id == B).Blocked.Contains("mentions this thread") && catalog.Sessions.Single(s => s.Id == C).Eligible, "Only the thread an unreadable rollout names should be blocked.");
            string escaped = Home(); Codex(escaped, B, false); Put(escaped, Provider.Codex, A, "{broken \"\\u0032\"}\n" + CodexText(A), false);
            Assert(Scan(escaped, Provider.Codex).Sessions.Single(s => s.Id == B).Blocked.Contains("links between threads are unknown"), "An escaped character that could hide a thread ID did not block every thread.");
            string rewritten = Home(); Codex(rewritten, B, false);
            string placeholder = "{broken " + C + "}\n" + CodexText(A);
            string unreadable = Put(rewritten, Provider.Codex, A, placeholder, false);
            catalog = Scan(rewritten, Provider.Codex);
            Assert(catalog.Sessions.Single(s => s.Id == B).Eligible, "Rewrite fixture should start eligible.");
            DateTime stamp = File.GetLastWriteTimeUtc(unreadable);
            File.WriteAllText(unreadable, placeholder.Replace(C, B), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(unreadable, stamp);
            Assert(Delete(Plan(catalog, B)).Deleted == 0 && File.Exists(Path.Combine(rewritten, "sessions", "2026", "01", "01", "rollout-2026-01-01T12-00-00-" + B + ".jsonl")), "A same-size, same-time rewrite that names the selected thread did not stop deletion.");
            string invalid = Home(); string target = Codex(invalid, B, false);
            string bad = Put(invalid, Provider.Codex, A, "", false);
            byte[] bytes = Encoding.UTF8.GetBytes("{broken " + C + "}\n" + CodexText(A)); bytes[3] = 0xFF;
            File.WriteAllBytes(bad, bytes); File.SetLastWriteTimeUtc(bad, Now.AddDays(-10));
            catalog = Scan(invalid, Provider.Codex);
            Assert(catalog.Sessions.Single(s => s.Id == B).Eligible, "An invalid UTF-8 rollout that never names a thread still blocked it.");
            stamp = File.GetLastWriteTimeUtc(bad);
            File.WriteAllBytes(bad, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(C, B)).Select((b, i) => i == 3 ? (byte)0xFF : b).ToArray());
            File.SetLastWriteTimeUtc(bad, stamp);
            Assert(Delete(Plan(catalog, B)).Deleted == 0 && File.Exists(target), "A rewrite of an invalid UTF-8 rollout that names the selected thread did not stop deletion.");
            string empty = Home(); Codex(empty, B, false); string blank = Put(empty, Provider.Codex, A, "", false);
            Assert(Scan(empty, Provider.Codex).Sessions.Single(s => s.Id == B).Eligible, "An empty rollout blocked every thread.");
            using (new FileStream(blank, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                Assert(Scan(empty, Provider.Codex).Sessions.Single(s => s.Id == B).Eligible, "An empty rollout held open by a writer blocked every thread.");
            string literal = Home(); Codex(literal, B, false); Put(literal, Provider.Codex, A, "{broken \"\\\\u0032\"}\n" + CodexText(A), false);
            Assert(Scan(literal, Provider.Codex).Sessions.Single(s => s.Id == B).Eligible, "An escaped backslash followed by text was treated as a hidden escape.");
            string live = Home(); Codex(live, B, false); Codex(live, C, false);
            string running = Put(live, Provider.Codex, A, CodexChild(A, B), false);
            using (var writer = new FileStream(running, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                catalog = Scan(live, Provider.Codex);
                Assert(!catalog.Sessions.Single(s => s.Id == A).Eligible && catalog.Sessions.Single(s => s.Id == B).Blocked.Contains("mentions this thread") && catalog.Sessions.Single(s => s.Id == C).Eligible, "A rollout held open by a running thread blocked unrelated threads or hid its parent: " + string.Join("; ", catalog.Sessions.Select(s => s.Blocked).ToArray()));
                byte[] appended = Encoding.UTF8.GetBytes(CodexText(A).Split('\n')[1] + "\n");
                writer.Seek(0, SeekOrigin.End); writer.Write(appended, 0, appended.Length); writer.Flush();
                var result = Delete(Plan(catalog, C));
                Assert(result.Deleted == 1, "Deleting a thread beside a growing running rollout failed: " + result.Describe());
                byte[] naming = Encoding.UTF8.GetBytes("{\"type\":\"event_msg\",\"payload\":{\"thread_id\":" + Quote(B) + "}}\n");
                writer.Write(naming, 0, naming.Length); writer.Flush();
                string another = Codex(live, Other, false);
                catalog = Scan(live, Provider.Codex);
                writer.Write(naming, 0, naming.Length); writer.Flush();
                Assert(Delete(Plan(catalog, Other)).Deleted == 1 && !File.Exists(another), "Appended records that name an unselected thread stopped deletion.");
            }
            string large = Home(); string big = Put(large, Provider.Codex, A, CodexText(A) + "{\"timestamp\":\"2026-01-01T12:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64," + new string('A', 6 * 1024 * 1024) + "\"}]}}\n", false);
            Codex(large, B, false);
            catalog = Scan(large, Provider.Codex);
            Assert(catalog.Sessions.All(s => s.Eligible) && catalog.Sessions.Single(s => s.Id == A).Rows == 3, "A rollout with a 6 MiB record was refused: " + string.Join("; ", catalog.Sessions.Select(s => s.Blocked).ToArray()));
            Assert(Delete(Plan(catalog, A, B)).Deleted == 2 && !File.Exists(big), "A rollout with a large record was not deleted.");
        }
    }
}
