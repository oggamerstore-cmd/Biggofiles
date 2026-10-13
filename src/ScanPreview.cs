using System;
using System.Collections.Generic;

namespace Biggofiles
{
    // Only the small heap is copied for a progress update. Never walk or sort the
    // growing directory index from the progress path: that makes a scan quadratic.
    internal sealed class ScanPreview
    {
        private const int Limit = 100;
        private readonly List<FileEntry> heap = new List<FileEntry>(Limit);
        private int files, folders, issues;
        private long bytes;
        internal void Add(DirectoryRecord record)
        {
            if (record.Complete) folders++;
            issues += record.Issues.Count;
            foreach (var file in record.Files)
            {
                files++; bytes += file.Size;
                if (heap.Count < Limit)
                {
                    heap.Add(file); int child = heap.Count - 1;
                    while (child > 0)
                    {
                        int parent = (child - 1) / 2;
                        if (Compare(heap[parent], heap[child]) <= 0) break;
                        Swap(parent, child); child = parent;
                    }
                }
                else if (Compare(file, heap[0]) > 0)
                {
                    heap[0] = file; int parent = 0;
                    while (parent * 2 + 1 < heap.Count)
                    {
                        int child = parent * 2 + 1;
                        if (child + 1 < heap.Count && Compare(heap[child + 1], heap[child]) < 0) child++;
                        if (Compare(heap[parent], heap[child]) <= 0) break;
                        Swap(parent, child); parent = child;
                    }
                }
            }
        }
        private static int Compare(FileEntry a, FileEntry b)
        {
            int size = a.Size.CompareTo(b.Size);
            return size != 0 ? size : StringComparer.OrdinalIgnoreCase.Compare(b.Name, a.Name);
        }
        private void Swap(int a, int b) { var item = heap[a]; heap[a] = heap[b]; heap[b] = item; }
        internal ScanProgress Snapshot(string path, TimeSpan elapsed, string stage)
        {
            var preview = heap.ToArray(); Array.Sort(preview, (a, b) => Compare(b, a));
            return new ScanProgress { FileCount = files, FolderCount = folders, IssueCount = issues, TotalBytes = bytes,
                LargestBytes = preview.Length == 0 ? 0 : preview[0].Size, Preview = preview,
                CurrentPath = path, Elapsed = elapsed, Stage = stage };
        }
    }
}
