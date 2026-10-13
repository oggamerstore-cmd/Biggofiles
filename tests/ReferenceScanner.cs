using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Biggofiles
{
    internal sealed class ReferenceScanner
    {
        public ScanResult Scan(string root, CancellationToken token, Action<ScanProgress> progress)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("Choose a drive or folder to scan.");
            root = Path.GetFullPath(root);
            uint attributes = NativeFiles.GetFileAttributesW(NativeFiles.Extended(root));
            if (attributes == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "This location is unavailable or access was denied.");
            if ((attributes & 0x10) == 0) throw new ArgumentException("Select a drive or folder, not a file.");
            var result = new ScanResult { Root = root };
            var pending = new Stack<string>(); pending.Push(root);
            var watch = Stopwatch.StartNew();
            long lastReport = -500;
            var top = new List<FileEntry>(101);
            while (pending.Count > 0 && !token.IsCancellationRequested)
            {
                string directory = pending.Pop();
                NativeFiles.FindData data;
                IntPtr handle = NativeFiles.FindFirstFileExW(NativeFiles.Extended(directory.TrimEnd('\\') + "\\*"), 1, out data, 0, IntPtr.Zero, 2);
                if (handle == NativeFiles.Invalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 2 && error != 18) result.Issues.Add(new ScanIssue { Path = directory, Reason = new Win32Exception(error).Message });
                    else result.FolderCount++;
                    Report(result, top, directory, watch, ref lastReport, progress);
                    continue;
                }
                result.FolderCount++;
                try
                {
                    do
                    {
                        if (token.IsCancellationRequested) break;
                        if (data.Name == "." || data.Name == "..") continue;
                        if ((data.Attributes & 0x10) != 0)
                        {
                            string child = directory.TrimEnd('\\') + "\\" + data.Name;
                            // Name-surrogate reparse points redirect traversal (junctions, mount points,
                            // symlinks). Cloud placeholders do not: enumerate their metadata normally.
                            if ((data.Attributes & 0x400) != 0 && (data.ReparseTag & 0x20000000) != 0)
                                result.Issues.Add(new ScanIssue { Path = child, Reason = "Directory link or mount point not followed (prevents loops and crossing drives)." });
                            else pending.Push(child);
                        }
                        else
                        {
                            long size = ((long)data.SizeHigh << 32) | data.SizeLow;
                            var entry = new FileEntry(directory, data.Name, size, NativeFiles.Ticks(data.WriteTime), data.Attributes);
                            result.Files.Add(entry); result.TotalBytes += size;
                            if (top.Count < 100 || size > top[top.Count - 1].Size)
                            {
                                int index = top.FindIndex(item => item.Size < size);
                                top.Insert(index < 0 ? top.Count : index, entry);
                                if (top.Count > 100) top.RemoveAt(100);
                            }
                        }
                        Report(result, top, directory, watch, ref lastReport, progress);
                    } while (NativeFiles.FindNextFileW(handle, out data));
                    int lastError = Marshal.GetLastWin32Error();
                    if (!token.IsCancellationRequested && lastError != 18 && lastError != 0)
                        result.Issues.Add(new ScanIssue { Path = directory, Reason = "Enumeration interrupted: " + new Win32Exception(lastError).Message });
                }
                finally { NativeFiles.FindClose(handle); }
            }
            result.Cancelled = token.IsCancellationRequested;
            FileQuery.SortLargest(result.Files);
            watch.Stop(); result.Elapsed = watch.Elapsed;
            return result;
        }

        private static void Report(ScanResult result, List<FileEntry> top, string directory, Stopwatch watch, ref long lastReport, Action<ScanProgress> progress)
        {
            if (progress == null || watch.ElapsedMilliseconds - lastReport < 350) return;
            lastReport = watch.ElapsedMilliseconds;
            progress(new ScanProgress { FileCount = result.Files.Count, FolderCount = result.FolderCount, TotalBytes = result.TotalBytes, LargestBytes = top.Count == 0 ? 0 : top[0].Size, IssueCount = result.Issues.Count, CurrentPath = directory, Elapsed = watch.Elapsed, Preview = top.ToArray() });
        }
    }
}
