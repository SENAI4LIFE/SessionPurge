using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SessionPurge.Tests
{
    internal static partial class TestMain
    {
        private static void PeerContentRace()
        {
            string home = Home(); string selected = Claude(home, A); string peer = Claude(home, B);
            var plan = Plan(Scan(home, Provider.Claude), A);
            DateTime stamp = File.GetLastWriteTimeUtc(peer);
            File.WriteAllText(peer, ClaudeLine(B).Replace("\"sessionId\":\"" + B, "\"sessionId\":\"" + A), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(peer, stamp);
            var result = Delete(plan);
            Assert(result.Deleted == 0 && result.Stopped != null && File.Exists(selected) && File.Exists(peer), "Same-metadata peer ownership mutation did not block deletion.");
        }
        private static void PeerWriter()
        {
            string home = Home(); string selected = Claude(home, A); string peer = Claude(home, B);
            var plan = Plan(Scan(home, Provider.Claude), A);
            using (var writer = new FileStream(peer, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                Assert(Delete(plan).Deleted == 0 && File.Exists(selected), "An active peer writer did not block preflight.");
        }
        private static void PeerHeld()
        {
            string home = Home(); string selected = Claude(home, A); string peer = Claude(home, B);
            var plan = Plan(Scan(home, Provider.Claude), A);
            bool refused = false;
            var result = Engine.Delete(plan, "DELETE", CancellationToken.None, delegate
            {
                try { using (var writer = new FileStream(peer, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
                catch (IOException) { refused = true; }
            });
            Assert(refused && result.Deleted == 1 && !File.Exists(selected) && File.Exists(peer), "Peer lock was released before deletion completed.");
        }
        private static DeletionPlan SharedPeers(out string selected, out string peer, out string store)
        {
            string home = Home(); selected = Claude(home, A); peer = Claude(home, B); store = Path.Combine(home, "history.jsonl");
            WriteStore(home, "history.jsonl", HistoryRow(Provider.Claude, A) + HistoryRow(Provider.Claude, B));
            return Plan(Scan(home, Provider.Claude), A);
        }
        private static void CoordinatedPeers()
        {
            string selected, peer, store, history = HistoryRow(Provider.Claude, A) + HistoryRow(Provider.Claude, B);
            var plan = SharedPeers(out selected, out peer, out store);
            DateTime stamp = File.GetLastWriteTimeUtc(peer);
            File.WriteAllText(peer, ClaudeLine(B).Replace("\"sessionId\":\"" + B, "\"sessionId\":\"" + A), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(peer, stamp);
            Assert(Delete(plan).Deleted == 0 && File.Exists(selected) && File.ReadAllText(store) == history, "Coordinated deletion ignored a same-metadata peer mutation.");
            plan = SharedPeers(out selected, out peer, out store);
            using (var writer = new FileStream(peer, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                Assert(Delete(plan).Deleted == 0 && File.Exists(selected) && File.ReadAllText(store) == history, "Coordinated deletion ignored an active peer writer.");
            plan = SharedPeers(out selected, out peer, out store);
            int checkpoints = 0, refusals = 0;
            var result = Engine.Delete(plan, "DELETE", CancellationToken.None, delegate
            {
                checkpoints++;
                try { using (var writer = new FileStream(peer, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
                catch (IOException) { refusals++; }
            });
            Assert(checkpoints == 3 && refusals == checkpoints && result.Deleted == 1 && !File.Exists(selected) && File.Exists(peer) && File.ReadAllText(store) == HistoryRow(Provider.Claude, B), "Coordinated peer lock was released before commit.");
        }
    }
}
