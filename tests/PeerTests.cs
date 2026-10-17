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
    }
}
