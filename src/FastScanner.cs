using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Biggofiles
{
    internal struct FileIdentity : IEquatable<FileIdentity>
    {
        public ulong Low, High;
        public FileIdentity(ulong low, ulong high) { Low = low; High = high; }
        public bool IsEmpty { get { return Low == 0 && High == 0; } }
        public bool Equals(FileIdentity other) { return Low == other.Low && High == other.High; }
        public override bool Equals(object other) { return other is FileIdentity && Equals((FileIdentity)other); }
        public override int GetHashCode() { return Low.GetHashCode() ^ High.GetHashCode(); }
    }
    internal sealed class ChildDirectory
    {
        public string Path;
        public FileIdentity Identity;
    }
    internal sealed class DirectoryRecord
    {
        public string Path;
        public FileIdentity Identity;
        public readonly List<FileEntry> Files = new List<FileEntry>();
        public readonly List<ChildDirectory> Children = new List<ChildDirectory>();
        public readonly List<ScanIssue> Issues = new List<ScanIssue>();
        public bool Complete = true;
    }
    internal static class BulkDirectory
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, IntPtr data, uint size);
        internal static SafeFileHandle Open(string path, uint access) { return CreateFileW(NativeFiles.Extended(path), access, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero); }
        internal static FileIdentity Identify(SafeFileHandle file, out ulong serial)
        {
            IntPtr memory = Marshal.AllocHGlobal(24);
            try
            {
                if (!GetFileInformationByHandleEx(file, 18, memory, 24)) throw new Win32Exception(Marshal.GetLastWin32Error());
                serial = unchecked((ulong)Marshal.ReadInt64(memory));
                return new FileIdentity(unchecked((ulong)Marshal.ReadInt64(memory, 8)), unchecked((ulong)Marshal.ReadInt64(memory, 16)));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        internal static DirectoryRecord Read(string path, FileIdentity expected, CancellationToken token)
        {
            var record = new DirectoryRecord { Path = path, Identity = expected };
            using (var handle = Open(path, 1))
            {
                if (handle.IsInvalid) return Failure(record, Marshal.GetLastWin32Error());
                try { ulong serial; record.Identity = Identify(handle, out serial); }
                catch (Win32Exception) { /* Some non-NTFS providers do not expose file IDs. */ }
                const int capacity = 65536;
                IntPtr buffer = Marshal.AllocHGlobal(capacity);
                bool first = true;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!GetFileInformationByHandleEx(handle, first ? 20 : 19, buffer, capacity))
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == 18) return record;
                            if (first && (error == 1 || error == 50 || error == 87)) return ReadCompatible(path, record.Identity, token);
                            return Failure(record, error);
                        }
                        first = false; int offset = 0;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            if (offset < 0 || offset > capacity - 88) throw new IOException("Windows returned an invalid directory record.");
                            IntPtr item = IntPtr.Add(buffer, offset);
                            int next = Marshal.ReadInt32(item), nameLength = Marshal.ReadInt32(item, 60);
                            if (nameLength < 0 || (nameLength & 1) != 0 || nameLength > capacity - offset - 88 || (next != 0 && (next < 88 + nameLength || next > capacity - offset))) throw new IOException("Windows returned an invalid directory record length.");
                            string name = Marshal.PtrToStringUni(IntPtr.Add(item, 88), nameLength / 2);
                            if (name != "." && name != "..")
                            {
                                uint attributes = unchecked((uint)Marshal.ReadInt32(item, 56));
                                var id = new FileIdentity(unchecked((ulong)Marshal.ReadInt64(item, 72)), unchecked((ulong)Marshal.ReadInt64(item, 80)));
                                if ((attributes & 16) != 0)
                                    AddDirectory(record, name, attributes, unchecked((uint)Marshal.ReadInt32(item, 68)), id);
                                else
                                {
                                    long ticks; try { ticks = DateTime.FromFileTimeUtc(Marshal.ReadInt64(item, 24)).Ticks; } catch (ArgumentOutOfRangeException) { ticks = 0; }
                                    var entry = new FileEntry(path, name, Marshal.ReadInt64(item, 40), ticks, attributes) { Identity = id };
                                    if (entry.Size < 0) throw new IOException("Windows returned a negative file size.");
                                    record.Files.Add(entry);
                                }
                            }
                            if (next == 0) break; offset += next;
                        }
                    }
                    token.ThrowIfCancellationRequested(); return record;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        private static DirectoryRecord Failure(DirectoryRecord record, int error)
        {
            record.Complete = false; record.Issues.Add(new ScanIssue { Path = record.Path, Reason = new Win32Exception(error).Message }); return record;
        }
        private static void AddDirectory(DirectoryRecord record, string name, uint attributes, uint tag, FileIdentity id)
        {
            string path = record.Path.TrimEnd('\\') + "\\" + name;
            if ((attributes & 0x400) != 0 && (tag & 0x20000000) != 0)
                record.Issues.Add(new ScanIssue { Path = path, Reason = "Directory link or mount point not followed (prevents loops and crossing drives)." });
            else record.Children.Add(new ChildDirectory { Path = path, Identity = id });
        }
        private static DirectoryRecord ReadCompatible(string path, FileIdentity id, CancellationToken token)
        {
            var record = new DirectoryRecord { Path = path, Identity = id }; NativeFiles.FindData data;
            IntPtr handle = NativeFiles.FindFirstFileExW(NativeFiles.Extended(path.TrimEnd('\\') + "\\*"), 1, out data, 0, IntPtr.Zero, 2);
            if (handle == NativeFiles.Invalid) { int error = Marshal.GetLastWin32Error(); return error == 2 || error == 18 ? record : Failure(record, error); }
            try
            {
                do
                {
                    token.ThrowIfCancellationRequested(); if (data.Name == "." || data.Name == "..") continue;
                    if ((data.Attributes & 16) != 0) AddDirectory(record, data.Name, data.Attributes, data.ReparseTag, new FileIdentity());
                    else record.Files.Add(new FileEntry(path, data.Name, ((long)data.SizeHigh << 32) | data.SizeLow, NativeFiles.Ticks(data.WriteTime), data.Attributes));
                } while (NativeFiles.FindNextFileW(handle, out data));
                int error = Marshal.GetLastWin32Error(); return error == 18 ? record : Failure(record, error);
            }
            finally { NativeFiles.FindClose(handle); }
        }
    }

    // Keep one index per scanner instance. It exists only while the application is open.
    // No unvalidated, on-disk cache can accidentally be presented as a new scan.
    public sealed class DriveScanner
    {
        private Dictionary<string, DirectoryRecord> index;
        private string indexedRoot, indexedResolvedRoot, identityContext;
        private FileIdentity rootIdentity;
        private ulong volumeSerial, journalId;
        private long checkpoint;
        private bool valid;
        private readonly Dictionary<FileIdentity, FileIdentity> activeFiles = new Dictionary<FileIdentity, FileIdentity>();
        internal Func<string, IChangeJournal> JournalFactory = UsnJournal.TryOpen;

        public ScanResult Scan(string root, CancellationToken token, Action<ScanProgress> progress) { return Scan(root, token, progress, false); }
        public ScanResult Scan(string root, CancellationToken token, Action<ScanProgress> progress, bool forceFull)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("Choose a drive or folder to scan.");
            root = Path.GetFullPath(root); if (root.Length > Path.GetPathRoot(root).Length) root = root.TrimEnd('\\');
            uint attributes = NativeFiles.GetFileAttributesW(NativeFiles.Extended(root));
            if (attributes == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "This location is unavailable or access was denied.");
            if ((attributes & 16) == 0) throw new ArgumentException("Choose a drive or folder, not a file.");
            var timer = Stopwatch.StartNew(); var result = new ScanResult { Root = root };
            var preview = new ScanPreview();
            string resolved; FileIdentity currentId; ulong currentSerial;
            try { resolved = NativeFiles.ResolvedPath(root); using (var handle = BulkDirectory.Open(root, 0)) currentId = BulkDirectory.Identify(handle, out currentSerial); }
            catch (Exception ex) { if (!(ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException)) throw; resolved = root; currentId = new FileIdentity(); currentSerial = 0; }
            string context = WindowsIdentity.GetCurrent().User.Value + ":" + new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            bool reuse = !forceFull && valid && root == indexedRoot && resolved == indexedResolvedRoot && currentId.Equals(rootIdentity) && currentSerial == volumeSerial && context == identityContext;
            using (var journal = JournalFactory(Path.GetPathRoot(resolved)))
            {
                JournalState start = null; JournalDelta changes = null;
                if (progress != null) progress(preview.Snapshot(root, timer.Elapsed, "Checking change history"));
                try
                {
                    if (journal != null) start = journal.Query();
                    if (reuse && start != null)
                    {
                        if (!start.Covers(journalId, checkpoint)) { reuse = false; result.IndexNote = "Change history was reset or expired; rebuilt the index."; }
                        else
                        {
                            changes = journal.Read(checkpoint, start, token);
                            if (changes.RequiresFullScan || AncestorSecurityChanged(resolved, changes.SecurityDirectoryIds)) { reuse = false; result.IndexNote = "Ancestor access or unsupported changes detected; rebuilt the index."; }
                        }
                    }
                    else reuse = false;
                    // Seed in-flight writes that started before this process's baseline.
                    // USN reasons are coalesced while a file handle remains open.
                    if (!reuse && start != null)
                    {
                        changes = journal.Read(Math.Max(start.FirstUsn, start.LowestValidUsn), start, token, true);
                        if (changes.UnsupportedRecords) { start = null; result.IndexNote = "Unsupported journal records; used a full scan."; }
                    }
                }
                catch (OperationCanceledException) { result.Cancelled = true; result.Elapsed = timer.Elapsed; return result; }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException)) throw;
                    reuse = false; start = null; result.IndexNote = "Change history could not be verified; used a full scan.";
                }
                if (start == null && result.IndexNote.Length == 0) result.IndexNote = "Fast refresh needs a readable NTFS change journal. This scan reads every accessible folder.";
                if (!reuse) { index = new Dictionary<string, DirectoryRecord>(StringComparer.Ordinal); activeFiles.Clear(); }
                var activeParents = new HashSet<FileIdentity>(activeFiles.Values);
                if (changes != null)
                {
                    foreach (var closed in changes.ClosedFiles) activeFiles.Remove(closed);
                    foreach (var pair in changes.UnclosedFiles) activeFiles[pair.Key] = pair.Value;
                }
                activeParents.UnionWith(activeFiles.Values);
                valid = false;
                var pending = new Queue<ChildDirectory>(); var queued = new HashSet<string>(StringComparer.Ordinal);
                if (!reuse) Enqueue(pending, queued, root, currentId);
                else
                {
                    // A permission change can expose or hide a whole tree, even
                    // without changes to each descendant. Drop that tree before
                    // rereading it; unrelated security updates need no full scan.
                    var changedTrees = index.Values.Where(d => changes.SecurityDirectoryIds.Contains(d.Identity)).ToArray();
                    foreach (var tree in changedTrees)
                    {
                        RemoveSubtree(tree.Path); Enqueue(pending, queued, tree.Path, tree.Identity);
                    }
                    var dirty = new HashSet<FileIdentity>(changes.ParentIds);
                    // Retain directories with unclosed changes. An open writer can change a file
                    // repeatedly without a distinct USN record for every write.
                    dirty.UnionWith(activeParents);
                    foreach (var directory in index.Values)
                    {
                        bool touched = dirty.Contains(directory.Identity) || changes.FileIds.Contains(directory.Identity) || !directory.Complete;
                        if (!touched) foreach (var file in directory.Files) if (changes.FileIds.Contains(file.Identity)) { touched = true; break; }
                        if (touched) Enqueue(pending, queued, directory.Path, directory.Identity);
                    }
                }
                int read = 0; long lastReport = -500;
                result.Method = reuse ? "Journal refresh" : "Full scan";
                string stage = reuse ? "Checking changed folders" : "Scanning";
                if (progress != null) progress(preview.Snapshot(root, timer.Elapsed, stage));
                try
                {
                    while (pending.Count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var batch = new List<ChildDirectory>();
                        while (pending.Count > 0 && batch.Count < 64)
                        {
                            var next = pending.Dequeue(); queued.Remove(next.Path);
                            if (!reuse || next.Path == root || index.ContainsKey(next.Path) || HasParent(next.Path)) batch.Add(next);
                        }
                        var records = new DirectoryRecord[batch.Count];
                        Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, i => records[i] = BulkDirectory.Read(batch[i].Path, batch[i].Identity, token));
                        for (int batchIndex = 0; batchIndex < batch.Count; batchIndex++)
                        {
                            var work = batch[batchIndex];
                            if (reuse && work.Path != root && !index.ContainsKey(work.Path) && !HasParent(work.Path)) continue;
                            DirectoryRecord old; index.TryGetValue(work.Path, out old);
                            var fresh = records[batchIndex]; read++;
                            if (old != null)
                            {
                                var currentChildren = new Dictionary<string, FileIdentity>(StringComparer.Ordinal);
                                foreach (var child in fresh.Children) currentChildren[child.Path] = child.Identity;
                                foreach (var child in old.Children)
                                {
                                    FileIdentity now;
                                    if (!currentChildren.TryGetValue(child.Path, out now) || !now.Equals(child.Identity)) RemoveSubtree(child.Path);
                                }
                            }
                            index[work.Path] = fresh;
                            if (progress != null) preview.Add(fresh);
                            foreach (var child in fresh.Children) if (!index.ContainsKey(child.Path)) Enqueue(pending, queued, child.Path, child.Identity);
                        }
                        if (progress != null && batch.Count > 0 && timer.ElapsedMilliseconds - lastReport >= 350)
                        {
                            progress(preview.Snapshot(batch[batch.Count - 1].Path, timer.Elapsed, stage));
                            lastReport = timer.ElapsedMilliseconds;
                        }
                    }
                }
                catch (OperationCanceledException) { result.Cancelled = true; }
                if (progress != null) progress(preview.Snapshot(root, timer.Elapsed, "Sorting results"));
                Summarize(result);
                result.FoldersRead = read; result.ReusedFolders = Math.Max(0, index.Count - read);
                result.Cancelled |= token.IsCancellationRequested;
                // The checkpoint precedes directory reads. Changes made during this scan are
                // therefore replayed on the next refresh rather than silently skipped.
                if (!result.Cancelled && start != null && !currentId.IsEmpty && index.Values.All(d => !d.Identity.IsEmpty && d.Files.All(f => !f.Identity.IsEmpty)))
                {
                    try
                    {
                        var end = journal.Query();
                        if (end.Covers(start.Id, start.NextUsn))
                        {
                            valid = true; checkpoint = start.NextUsn; journalId = start.Id;
                            indexedRoot = root; indexedResolvedRoot = resolved; rootIdentity = currentId; volumeSerial = currentSerial; identityContext = context;
                        }
                        else result.IndexNote += " Change history expired during this scan; the next scan will rebuild.";
                    }
                    catch (Exception ex) { if (!(ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException)) throw; result.IndexNote += " Change history could not be verified after this scan; the next scan will rebuild."; }
                }
                else if (!result.Cancelled && start != null) result.IndexNote += " Some file or folder identities were unavailable; the next scan will rebuild.";
                if (valid) result.IndexNote = (result.IndexNote.Length == 0 ? "" : result.IndexNote + " ") + "Index held in memory. Scan again refreshes changed folders using the NTFS journal. A fresh scan is available with Shift+F5.";
                timer.Stop(); result.Elapsed = timer.Elapsed; return result;
            }
        }
        private bool HasParent(string path)
        {
            string parent = Path.GetDirectoryName(path);
            if (parent == null) return false; DirectoryRecord record;
            if (!index.TryGetValue(parent, out record)) return false;
            return record.Children.Any(c => c.Path == path);
        }
        private static bool AncestorSecurityChanged(string path, HashSet<FileIdentity> changes)
        {
            if (changes.Count == 0) return false;
            string ancestor = Path.GetDirectoryName(path);
            while (!String.IsNullOrEmpty(ancestor))
            {
                // Failure to validate an ancestor must never silently reuse data.
                using (var handle = BulkDirectory.Open(ancestor, 0))
                {
                    ulong serial; var identity = BulkDirectory.Identify(handle, out serial);
                    if (identity.IsEmpty || changes.Contains(identity)) return true;
                }
                ancestor = Path.GetDirectoryName(ancestor);
            }
            return false;
        }
        private static void Enqueue(Queue<ChildDirectory> queue, HashSet<string> queued, string path, FileIdentity id) { if (queued.Add(path)) queue.Enqueue(new ChildDirectory { Path = path, Identity = id }); }
        private void RemoveSubtree(string path)
        {
            var stack = new Stack<string>(); stack.Push(path);
            while (stack.Count > 0) { string next = stack.Pop(); DirectoryRecord record; if (!index.TryGetValue(next, out record)) continue; foreach (var child in record.Children) stack.Push(child.Path); index.Remove(next); }
        }
        private void Summarize(ScanResult result)
        {
            result.Files.Capacity = index.Values.Sum(d => d.Files.Count);
            foreach (var record in index.Values)
            {
                if (record.Complete) result.FolderCount++;
                result.Files.AddRange(record.Files); result.Issues.AddRange(record.Issues);
                foreach (var file in record.Files) result.TotalBytes += file.Size;
            }
            FileQuery.SortLargest(result.Files);
        }
    }
}
