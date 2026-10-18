using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SessionPurge
{
    internal static class OpenFiles
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess
        {
            internal int ProcessId;
            internal System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessInfo
        {
            internal UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string ServiceName;
            internal int ApplicationType;
            internal uint AppStatus;
            internal uint SessionId;
            [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
        }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint processCount, UniqueProcess[] processes, uint serviceCount, string[] services);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] processes, ref uint reasons);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint session);

        internal static string Check(IEnumerable<string> paths)
        {
            string[] files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) return null;
            uint session;
            int error = RmStartSession(out session, 0, new StringBuilder(33));
            if (error != 0) return Failure(error);
            try
            {
                error = RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
                if (error != 0) return Failure(error);
                var found = new ProcessInfo[0];
                uint count = 0, needed, reasons = 0;
                for (int attempt = 0; ; attempt++)
                {
                    error = RmGetList(session, out needed, ref count, found, ref reasons);
                    if (error == 0) break;
                    if (error != 234 || attempt == 4) return Failure(error);
                    found = new ProcessInfo[needed + 4];
                    count = (uint)found.Length;
                }
                int self = Process.GetCurrentProcess().Id;
                var holders = found.Take((int)count).Where(p => p.Process.ProcessId != self).Select(p => Describe(p)).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                return holders.Length == 0 ? null : "Files of this session are open in " + string.Join(", ", holders) + ". Close them and rescan.";
            }
            finally { RmEndSession(session); }
        }
        private static string Describe(ProcessInfo info)
        {
            string name = null;
            try { using (var process = Process.GetProcessById(info.Process.ProcessId)) name = process.ProcessName; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return SessionNames.SafeText(string.IsNullOrEmpty(name) ? info.AppName : name) + " (PID " + info.Process.ProcessId + ")";
        }
        private static string Failure(int error)
        {
            return "Windows Restart Manager could not check whether this session's files are open (error " + error + "). Rescan to try again.";
        }
    }
}
