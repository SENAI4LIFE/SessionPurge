using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SessionPurge
{
    internal sealed class FileTransaction : IDisposable
    {
        [DllImport("KtmW32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr guid, uint options, uint isolation, uint flags, uint timeout, string description);
        [DllImport("KtmW32.dll", SetLastError = true)]
        private static extern bool CommitTransaction(SafeFileHandle transaction);
        [DllImport("KtmW32.dll", SetLastError = true)]
        private static extern bool RollbackTransaction(SafeFileHandle transaction);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileTransacted(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template, SafeFileHandle transaction, IntPtr miniVersion, IntPtr extended);
        private readonly SafeFileHandle handle;
        private bool committed;
        internal FileTransaction()
        {
            handle = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 120000, null);
            if (handle.IsInvalid) { handle.Dispose(); throw new SafetyException("Windows transactional NTFS is unavailable. Coordinated deletion was not started."); }
        }
        internal SafeFileHandle Open(string path, uint access, uint share, uint flags)
        { return CreateFileTransacted(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero, handle, IntPtr.Zero, IntPtr.Zero); }
        internal void Commit()
        {
            if (!CommitTransaction(handle)) throw new SafetyException("Windows refused the coordinated commit. No nontransactional fallback was attempted.");
            committed = true;
        }
        public void Dispose()
        {
            if (!committed && !handle.IsClosed) RollbackTransaction(handle);
            handle.Dispose();
        }
    }
}
