using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Biggofiles
{
    internal sealed class JournalState
    {
        public ulong Id;
        public long FirstUsn, NextUsn, LowestValidUsn;
        public bool Covers(ulong id, long usn) { return Id == id && usn >= FirstUsn && usn >= LowestValidUsn && usn <= NextUsn; }
    }
    internal sealed class JournalDelta
    {
        public readonly HashSet<FileIdentity> ParentIds = new HashSet<FileIdentity>();
        public readonly HashSet<FileIdentity> FileIds = new HashSet<FileIdentity>();
        public readonly HashSet<FileIdentity> SecurityDirectoryIds = new HashSet<FileIdentity>();
        public readonly Dictionary<FileIdentity, FileIdentity> UnclosedFiles = new Dictionary<FileIdentity, FileIdentity>();
        public readonly HashSet<FileIdentity> ClosedFiles = new HashSet<FileIdentity>();
        public bool RequiresFullScan;
        public bool UnsupportedRecords;
    }
    internal interface IChangeJournal : IDisposable
    {
        JournalState Query();
        JournalDelta Read(long from, JournalState until, CancellationToken token, bool pendingOnly = false);
    }
    internal sealed class UsnJournal : IChangeJournal
    {
        private readonly SafeFileHandle handle;
        private uint readCode;
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inSize, IntPtr output, uint outSize, out uint returned, IntPtr overlapped);
        private UsnJournal(SafeFileHandle handle) { this.handle = handle; }
        public static IChangeJournal TryOpen(string volumeRoot)
        {
            // A filesystem root handle supports the unprivileged journal API on Windows 10+.
            // A raw volume handle would unnecessarily require administrator access.
            if (String.IsNullOrEmpty(volumeRoot) || volumeRoot.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            try
            {
                if (!String.Equals(new DriveInfo(volumeRoot).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) return null;
                var handle = BulkDirectory.Open(volumeRoot, 0);
                if (handle.IsInvalid) { handle.Dispose(); return null; }
                return new UsnJournal(handle);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        public JournalState Query()
        {
            IntPtr output = Marshal.AllocHGlobal(80);
            try
            {
                uint returned;
                if (!DeviceIoControl(handle, 0x900f4, IntPtr.Zero, 0, output, 80, out returned, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (returned < 56) throw new IOException("Incomplete journal state.");
                return new JournalState { Id = unchecked((ulong)Marshal.ReadInt64(output)), FirstUsn = Marshal.ReadInt64(output, 8), NextUsn = Marshal.ReadInt64(output, 16), LowestValidUsn = Marshal.ReadInt64(output, 24) };
            }
            finally { Marshal.FreeHGlobal(output); }
        }
        public JournalDelta Read(long from, JournalState until, CancellationToken token, bool pendingOnly = false)
        {
            if (!until.Covers(until.Id, from)) throw new IOException("The saved journal checkpoint is no longer available.");
            var result = new JournalDelta();
            IntPtr input = Marshal.AllocHGlobal(40), output = Marshal.AllocHGlobal(1024 * 1024);
            try
            {
                Marshal.Copy(new byte[40], 0, input, 40); Marshal.WriteInt32(input, 8, -1); Marshal.WriteInt64(input, 32, unchecked((long)until.Id));
                long cursor = from; bool first = true;
                // Even an unchanged journal must be read once to prove access to its records.
                while (first || cursor < until.NextUsn)
                {
                    token.ThrowIfCancellationRequested(); first = false; Marshal.WriteInt64(input, cursor); uint returned;
                    uint code = readCode == 0 ? 0x900bbu : readCode;
                    bool ok = DeviceIoControl(handle, code, input, 40, output, 1024 * 1024, out returned, IntPtr.Zero);
                    if (!ok && readCode == 0) { code = 0x903ab; ok = DeviceIoControl(handle, code, input, 40, output, 1024 * 1024, out returned, IntPtr.Zero); }
                    if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); readCode = code;
                    if (returned < 8) throw new IOException("Invalid change-journal response.");
                    long next = Marshal.ReadInt64(output);
                    int offset = 8;
                    while (offset < returned)
                    {
                        token.ThrowIfCancellationRequested();
                        if (returned - offset < 8) throw new IOException("Truncated journal record.");
                        IntPtr item = IntPtr.Add(output, offset); int length = Marshal.ReadInt32(item); int version = (ushort)Marshal.ReadInt16(item, 4);
                        if (length < 8 || length > returned - offset) throw new IOException("Invalid journal record length.");
                        if (version != 2 && version != 3) { result.RequiresFullScan = true; result.UnsupportedRecords = true; offset += length; continue; }
                        int idWidth = version == 2 ? 8 : 16, usnOffset = 8 + idWidth * 2;
                        if (length < (version == 2 ? 60 : 76)) throw new IOException("Truncated journal metadata.");
                        long usn = Marshal.ReadInt64(item, usnOffset);
                        if (usn >= from && usn < until.NextUsn)
                        {
                            var id = new FileIdentity(unchecked((ulong)Marshal.ReadInt64(item, 8)), idWidth == 16 ? unchecked((ulong)Marshal.ReadInt64(item, 16)) : 0);
                            var parent = new FileIdentity(unchecked((ulong)Marshal.ReadInt64(item, 8 + idWidth)), idWidth == 16 ? unchecked((ulong)Marshal.ReadInt64(item, 16 + idWidth)) : 0);
                            uint reason = unchecked((uint)Marshal.ReadInt32(item, usnOffset + 16));
                            // Directory ACL changes invalidate their entire subtree.
                            // A file ACL change already dirties its containing folder.
                            if (!pendingOnly && (reason & 0x800) != 0 && (Marshal.ReadInt32(item, usnOffset + 28) & 16) != 0) result.SecurityDirectoryIds.Add(id);
                            if (!pendingOnly) { result.FileIds.Add(id); result.ParentIds.Add(parent); }
                            if ((reason & 0x80000000) != 0) { result.UnclosedFiles.Remove(id); if (!pendingOnly) result.ClosedFiles.Add(id); }
                            else if ((reason & 7) != 0) { result.UnclosedFiles[id] = parent; result.ClosedFiles.Remove(id); }
                        }
                        offset += length;
                    }
                    if (next <= cursor && cursor < until.NextUsn) throw new IOException("The journal stopped before the requested checkpoint.");
                    cursor = next;
                }
                if (!Query().Covers(until.Id, from)) throw new IOException("Change history expired while it was being read.");
                return result;
            }
            finally { Marshal.FreeHGlobal(input); Marshal.FreeHGlobal(output); }
        }
        public void Dispose() { handle.Dispose(); }
    }
}
