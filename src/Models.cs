using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Biggofiles
{
    public sealed class FileEntry
    {
        public string DirectoryPath { get; private set; }
        public string Name { get; private set; }
        public long Size { get; private set; }
        public long ModifiedUtcTicks { get; private set; }
        public uint Attributes { get; private set; }
        public string Extension { get; private set; }
        public string FullPath { get { return DirectoryPath.TrimEnd('\\') + "\\" + Name; } }
        public string SizeText { get { return Format.Bytes(Size); } }
        public string ModifiedText { get { return ModifiedUtcTicks == 0 ? "Unknown" : new DateTime(ModifiedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, yyyy"); } }
        public string Kind { get { return FileKinds.Get(Extension); } }
        public string TypeText { get { return Extension.Length == 0 ? "FILE" : Extension.TrimStart('.').ToUpperInvariant(); } }
        public string Glyph { get { return FileKinds.Glyph(Kind); } }
        public double SizeFraction { get; set; }
        internal FileIdentity Identity;
        public string Protection { get { return FileSafety.BlockReason(this) == null ? "" : "Protected"; } }

        public FileEntry(string directory, string name, long size, long ticks, uint attributes)
        {
            DirectoryPath = directory; Name = name; Size = size; ModifiedUtcTicks = ticks; Attributes = attributes;
            int dot = name.LastIndexOf('.');
            Extension = dot < 0 ? "" : name.Substring(dot).ToLowerInvariant();
        }
    }

    public static class Format
    {
        public static string Bytes(long value)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            double number = value; int unit = 0;
            while (number >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
            return number.ToString(unit == 0 ? "N0" : "0.##", CultureInfo.CurrentCulture) + " " + units[unit];
        }
        public static string Duration(TimeSpan value) { return value.TotalSeconds < 60 ? value.TotalSeconds.ToString("0.0") + "s" : value.ToString(@"h\:mm\:ss"); }
    }

    public static class FileKinds
    {
        private static readonly Dictionary<string, string> Kinds = Build();
        private static Dictionary<string, string> Build()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Add(map, "Video", ".mp4 .mkv .mov .avi .webm .m4v .wmv .mpeg .mpg .ts .mts");
            Add(map, "Audio", ".mp3 .wav .flac .aac .m4a .ogg .aiff .wma .opus");
            Add(map, "Images", ".png .jpg .jpeg .gif .webp .bmp .tiff .tif .heic .raw .psd .ai .svg .avif");
            Add(map, "Archives", ".zip .7z .rar .tar .gz .bz2 .xz .iso .img .vhd .vhdx .wim .cab");
            Add(map, "Documents", ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .rtf .md .csv .epub .odt .log");
            Add(map, "Apps & code", ".exe .msi .dll .sys .appx .msix .js .ts .cs .py .cpp .c .h .java .json .html .css .pdb");
            return map;
        }
        private static void Add(Dictionary<string, string> map, string kind, string extensions) { foreach (string extension in extensions.Split(' ')) map[extension] = kind; }
        public static string Get(string extension) { string kind; return Kinds.TryGetValue(extension, out kind) ? kind : "Other"; }
        public static string Glyph(string kind)
        {
            switch (kind) { case "Video": return "\uE714"; case "Audio": return "\uE8D6"; case "Images": return "\uEB9F"; case "Archives": return "\uE7B8"; case "Apps & code": return "\uE943"; default: return "\uE8A5"; }
        }
    }

    public sealed class ScanIssue
    {
        public string Path { get; set; }
        public string Reason { get; set; }
    }

    public sealed class ScanProgress
    {
        public int FileCount, FolderCount, IssueCount;
        public long TotalBytes, LargestBytes;
        public string CurrentPath;
        public string Stage = "Scanning";
        public TimeSpan Elapsed;
        public FileEntry[] Preview = new FileEntry[0];
    }

    public sealed class ScanResult
    {
        public string Root;
        public readonly List<FileEntry> Files = new List<FileEntry>();
        public readonly List<ScanIssue> Issues = new List<ScanIssue>();
        public long TotalBytes;
        public int FolderCount;
        public TimeSpan Elapsed;
        public bool Cancelled;
        public string Method = "Full scan";
        public string IndexNote = "";
        public int FoldersRead;
        public int ReusedFolders;
        public long LargestBytes { get { return Files.Count == 0 ? 0 : Files[0].Size; } }
    }

    public static class FileQuery
    {
        public static List<FileEntry> Apply(IList<FileEntry> files, string search, string kind, long minimum, string sort, CancellationToken token, bool suggestionsOnly = false)
        {
            search = (search ?? "").Trim();
            var result = new List<FileEntry>();
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < files.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var file = files[i];
                if (suggestionsOnly && CleanupAdvisor.Reason(file, now) == null) continue;
                if (file.Size < minimum || (kind != "All types" && file.Kind != kind)) continue;
                if (search.Length > 0 && file.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && file.DirectoryPath.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(file);
            }
            token.ThrowIfCancellationRequested();
            // The scanner already sorts descending; filtering preserves that order.
            if (sort == "Smallest first") result.Reverse();
            else if (sort == "Recently modified") result.Sort((a, b) => b.ModifiedUtcTicks.CompareTo(a.ModifiedUtcTicks));
            else if (sort == "Name A–Z") result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            token.ThrowIfCancellationRequested();
            return result;
        }
        public static void SortLargest(List<FileEntry> files)
        {
            files.Sort((a, b) => { int order = b.Size.CompareTo(a.Size); return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); });
            long largest = files.Count == 0 ? 0 : files[0].Size;
            foreach (var file in files) file.SizeFraction = largest == 0 ? 0 : (double)file.Size / largest * 100;
        }
    }

    public static class CsvExport
    {
        public static string Cell(string text)
        {
            text = text ?? "";
            // A filename is untrusted spreadsheet input. Neutralize formula prefixes.
            string trimmed = text.TrimStart();
            if (trimmed.Length > 0 && "=+-@\t\r\n".IndexOf(trimmed[0]) >= 0) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        public static void Write(string path, IList<FileEntry> files)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Name,Full path,Bytes,Type,Modified UTC");
                foreach (var file in files) writer.WriteLine(Cell(file.Name) + "," + Cell(file.FullPath) + "," + file.Size.ToString(CultureInfo.InvariantCulture) + "," + Cell(file.Kind) + "," + Cell(file.ModifiedUtcTicks == 0 ? "" : new DateTime(file.ModifiedUtcTicks, DateTimeKind.Utc).ToString("o")));
            }
        }
    }
}
