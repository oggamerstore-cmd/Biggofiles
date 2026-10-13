using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Biggofiles
{
    internal static class NativeFiles
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
            public uint SizeHigh, SizeLow, ReparseTag, Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindFirstFileExW(string path, int level, out FindData data, int search, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FindNextFileW(IntPtr handle, out FindData data);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FindClose(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, System.Text.StringBuilder path, uint capacity, uint flags);
        internal static readonly IntPtr Invalid = new IntPtr(-1);
        internal static string Extended(string path) { return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path; }
        internal static long Ticks(System.Runtime.InteropServices.ComTypes.FILETIME time)
        {
            long value = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
            try { return DateTime.FromFileTimeUtc(value).Ticks; } catch (ArgumentOutOfRangeException) { return 0; }
        }
        internal static FileEntry ReadEntry(string path)
        {
            FindData data;
            IntPtr handle = FindFirstFileExW(Extended(path), 1, out data, 0, IntPtr.Zero, 0);
            if (handle == Invalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if ((data.Attributes & 0x10) != 0) throw new IOException("This item is now a folder. Scan again before continuing.");
                return new FileEntry(Path.GetDirectoryName(path), data.Name, ((long)data.SizeHigh << 32) | data.SizeLow, Ticks(data.WriteTime), data.Attributes);
            }
            finally { FindClose(handle); }
        }
        internal static string ResolvedPath(string path)
        {
            // Opening with zero desired access resolves aliases without reading file contents.
            using (var handle = CreateFileW(Extended(path), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var buffer = new System.Text.StringBuilder(32768);
                uint count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (count == 0 || count >= buffer.Capacity) throw new IOException("Could not verify the file's resolved location. Manage this item in Explorer.");
                string resolved = buffer.ToString();
                if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + resolved.Substring(8);
                return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved.Substring(4) : resolved;
            }
        }
    }

}
