using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace SessionPurge
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInfo
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Accessed;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Written;
            internal uint Volume;
            internal uint SizeHigh;
            internal uint SizeLow;
            internal uint Links;
            internal uint IndexHigh;
            internal uint IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder result, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(SafeFileHandle file);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool GetVolumeInformation(string root, StringBuilder name, uint nameLength, out uint serial, out uint maximum, out uint flags, StringBuilder filesystem, uint filesystemLength);
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int type, ref int data, uint size);
    }

    internal sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
        private readonly HashSet<string> held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly string Root;
        internal DirectoryLease(string root)
        {
            Root = SafeFileSystem.CanonicalRoot(root);
            try { Pin(Root); }
            catch { Dispose(); throw; }
        }
        internal void Pin(string directory)
        {
            string full = Path.GetFullPath(directory).TrimEnd('\\');
            string volume = Path.GetPathRoot(directory);
            var chain = new List<string>();
            string current = full;
            while (current.Length >= volume.Length)
            {
                chain.Add(current);
                string parent = Path.GetDirectoryName(current);
                if (parent == null || parent == current) break;
                current = parent;
            }
            chain.Reverse();
            foreach (string entry in chain)
            {
                if (held.Contains(entry)) continue;
                bool owned = entry.Equals(Root, StringComparison.OrdinalIgnoreCase) || entry.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase);
                var handle = SafeFileSystem.Open(entry, true, false, owned);
                handles.Add(handle);
                held.Add(entry);
            }
        }
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
    }

    internal sealed class LockedFile : IDisposable
    {
        internal readonly SafeFileHandle Handle;
        internal readonly FileStream Stream;
        internal readonly string Identity;
        internal readonly long Length;
        internal readonly DateTime Modified;
        internal LockedFile(string path, bool deletion)
            : this(path, deletion, null, false) { }
        internal LockedFile(string path, bool deletion, FileTransaction transaction, bool writing)
        {
            Handle = SafeFileSystem.Open(path, false, deletion, true, transaction, writing);
            try
            {
                Native.FileInfo info;
                if (!Native.GetFileInformationByHandle(Handle, out info)) throw new SafetyException("Cannot inspect file identity.");
                Identity = info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow;
                Length = ((long)info.SizeHigh << 32) | info.SizeLow;
                Modified = DateTime.FromFileTimeUtc(((long)info.Written.dwHighDateTime << 32) | (uint)info.Written.dwLowDateTime);
                Stream = new FileStream(Handle, writing ? FileAccess.ReadWrite : FileAccess.Read, 65536, false);
            }
            catch { Handle.Dispose(); throw; }
        }
        internal string Hash()
        {
            Stream.Position = 0;
            using (var hash = SHA256.Create()) { string result = Convert.ToBase64String(hash.ComputeHash(Stream)); Stream.Position = 0; return result; }
        }
        internal void Delete()
        {
            int disposition = 1;
            if (!Native.SetFileInformationByHandle(Handle, 4, ref disposition, 4)) throw new SafetyException("Windows refused deletion; no fallback was attempted.");
            Dispose();
        }
        public void Dispose() { if (Stream != null) Stream.Dispose(); Handle.Dispose(); }
    }

    internal static class SafeFileSystem
    {
        internal static string CanonicalRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 220 || path.StartsWith("\\") || path.IndexOf('/') >= 0 || path.IndexOf(':', 2) >= 0 || path.Length < 4 || path[1] != ':' || path[2] != '\\') throw new SafetyException("Choose an absolute local provider-home path on NTFS.");
            foreach (string part in path.Substring(3).TrimEnd('\\').Split('\\')) ValidatePart(part);
            string full = Path.GetFullPath(path).TrimEnd('\\');
            if (full.Length <= 3 || !Directory.Exists(full)) throw new SafetyException("The provider home does not exist or cannot be accessed.");
            uint serial, maximum, flags;
            var filesystem = new StringBuilder(32);
            if (!Native.GetVolumeInformation(Path.GetPathRoot(full), null, 0, out serial, out maximum, out flags, filesystem, 32) || filesystem.ToString() != "NTFS") throw new SafetyException("Only local NTFS volumes are supported.");
            return full;
        }
        internal static string Inside(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('/') >= 0) throw new SafetyException("Unsafe relative path.");
            foreach (string part in relative.Split('\\')) ValidatePart(part);
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || full.Length > 240) throw new SafetyException("Path is outside the supported boundary.");
            return full;
        }
        private static void ValidatePart(string part)
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new SafetyException("Unsafe path component.");
            foreach (char c in part) if (char.GetUnicodeCategory(c) == UnicodeCategory.Format || char.IsControl(c)) throw new SafetyException("Ambiguous Unicode path component.");
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '1' && stem[3] <= '9')) throw new SafetyException("Reserved Windows path component.");
        }
        internal static SafeFileHandle Open(string path, bool directory, bool deletion, bool requireOwner)
        { return Open(path, directory, deletion, requireOwner, null, false); }
        internal static SafeFileHandle Open(string path, bool directory, bool deletion, bool requireOwner, FileTransaction transaction, bool writing)
        {
            uint access = directory ? 0x20001u : 0x80020000u;
            if (deletion) access |= 0x10000;
            if (writing) access |= 0x40000000;
            uint share = directory ? 3u : (deletion || writing ? 0u : 1u);
            uint openFlags = 0x00200000u | (directory ? 0x02000000u : 0u);
            var handle = transaction == null ? Native.CreateFile(path, access, share, IntPtr.Zero, 3, openFlags, IntPtr.Zero) : transaction.Open(path, access, share, openFlags);
            if (handle.IsInvalid) { handle.Dispose(); throw new SafetyException("Path is inaccessible, in use, or lacks required permissions."); }
            try
            {
                Native.FileInfo info;
                if (Native.GetFileType(handle) != 1 || !Native.GetFileInformationByHandle(handle, out info)) throw new SafetyException("Unsupported file object.");
                uint prohibited = 0x400u | 0x40u | 0x1000u | 0x40000u | 0x400000u;
                if ((info.Attributes & prohibited) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1) || ((deletion || writing) && (info.Attributes & 1) != 0)) throw new SafetyException("Links, special files, offline files, and read-only deletion targets are unsupported.");
                var resolved = new StringBuilder(1024);
                uint length = Native.GetFinalPathNameByHandle(handle, resolved, 1024, 0);
                string actual = resolved.ToString();
                if (actual.StartsWith("\\\\?\\")) actual = actual.Substring(4);
                if (length == 0 || length >= 1024 || !actual.TrimEnd('\\').Equals(Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new SafetyException("Resolved path differs from the requested path.");
                if (requireOwner) CheckOwner(handle);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        private static void CheckOwner(SafeFileHandle handle)
        {
            IntPtr owner, group, dacl, sacl, descriptor;
            uint result = Native.GetSecurityInfo(handle, 1, 1, out owner, out group, out dacl, out sacl, out descriptor);
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    if (result != 0 || owner == IntPtr.Zero || !new SecurityIdentifier(owner).Equals(identity.User)) throw new SafetyException("Ownership is not the current Windows user.");
                }
            }
            finally { if (descriptor != IntPtr.Zero) Native.LocalFree(descriptor); }
        }
        internal static string SafeText(string value)
        {
            if (value == null) return "Unknown";
            var result = new StringBuilder();
            foreach (char c in value) if (!char.IsControl(c) && c != '\u202a' && c != '\u202b' && c != '\u202d' && c != '\u202e' && c != '\u202c' && (c < '\u2066' || c > '\u2069')) result.Append(c);
            return result.Length > 240 ? result.ToString(0, 240) : result.ToString();
        }
    }
}
