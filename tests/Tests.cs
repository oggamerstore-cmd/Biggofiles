using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Biggofiles
{
    internal static class Tests
    {
        private static int passed, failed;
        private static string root, project;
        [STAThread] public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--benchmark")
            {
                var scanner = new DriveScanner(); var timer = Stopwatch.StartNew(); string lastStage = null; long lastPrint = 0; int updates = 0;
                Action<ScanProgress> report = p => { updates++; if (lastStage != p.Stage || timer.ElapsedMilliseconds - lastPrint > 5000) { lastStage = p.Stage; lastPrint = timer.ElapsedMilliseconds; Console.WriteLine("  " + p.Stage + ": " + p.FileCount + " files at " + Format.Duration(timer.Elapsed)); } };
                var data = scanner.Scan(args[1], CancellationToken.None, report);
                Console.WriteLine("First: Files=" + data.Files.Count + " Folders=" + data.FolderCount + " Notes=" + data.Issues.Count + " Time=" + Format.Duration(timer.Elapsed) + " Logical=" + Format.Bytes(data.TotalBytes));
                Console.WriteLine("Indexed scan memory: WorkingSet=" + Format.Bytes(Process.GetCurrentProcess().WorkingSet64) + " Managed=" + Format.Bytes(GC.GetTotalMemory(false)));
                Console.WriteLine("Progress snapshots: " + updates);
                Console.WriteLine("First index: " + data.IndexNote);
                timer.Restart(); var again = scanner.Scan(args[1], CancellationToken.None, report);
                Console.WriteLine("Second: Files=" + again.Files.Count + " Time=" + Format.Duration(timer.Elapsed) + " Method=" + again.Method + " Read=" + again.FoldersRead + " Reused=" + again.ReusedFolders);
                Console.WriteLine("Second index: " + again.IndexNote);
                timer.Restart(); var legacy = new ReferenceScanner().Scan(args[1], CancellationToken.None, null);
                Console.WriteLine("Original scanner: Files=" + legacy.Files.Count + " Time=" + Format.Duration(timer.Elapsed));
                var indexedPaths = Fingerprint(again).ToArray(); var freshPaths = Fingerprint(legacy).ToArray();
                Console.WriteLine("Path/size comparison: " + indexedPaths.SequenceEqual(freshPaths));
                Console.WriteLine("Entries only in indexed/fresh scan (including live changes): " + indexedPaths.Except(freshPaths).Count() + "/" + freshPaths.Except(indexedPaths).Count());
                Console.WriteLine("WorkingSet=" + Format.Bytes(Process.GetCurrentProcess().WorkingSet64) + " Managed=" + Format.Bytes(GC.GetTotalMemory(false)));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--benchmark-ui") return BenchmarkInterface(args[1]);
            project = Path.GetFullPath(args[0]);
            root = Path.Combine(project, ".test-work", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                Run("Recursive scan includes hidden, system, zero-byte, Unicode and extensionless files", ScanAll);
                Run("Files over 4 GB keep their full 64-bit size", LargeFile);
                Run("Long paths beyond 260 characters are scanned", LongPaths);
                Run("Directory junction loops are reported and not followed", JunctionLoop);
                Run("Access-denied folders are reported while siblings are scanned", AccessDenied);
                Run("Hard-link paths are both listed", HardLinks);
                Run("Cancellation returns sorted partial results and closes handles", Cancellation);
                Run("Unavailable roots fail; empty folders succeed", EmptyAndInvalid);
                Run("Search, category, size and sorting produce correct results", Filters);
                Run("CSV protects formula-like filenames and preserves Unicode and quotes", Export);
                Run("File actions reject changed, missing, system and reparse files", Safety);
                Run("Replaced files and changes at the final recycling callback are blocked", ReplacedFile);
                Run("Permanent-delete callbacks are vetoed", RecycleVeto);
                Run("Windows Recycle Bin receives a disposable test file", ActualRecycle);
                Run("Million-file query stays correct with deterministic ordering", MillionFiles);
                Run("Million-file progress stays bounded and matches complete results", MillionFileProgress);
                bool nativeJournal = NativeJournalAvailable();
                Run("Native scans match fresh enumeration through edits, moves and deletes", () => JournalRefresh("native", nativeJournal));
                Run("Controlled journal refresh handles edits, moves, replacements and hard links", () => JournalRefresh("controlled", true, new FakeJournal()));
                Run("Without a journal, repeat scans stay accurate through every file change", () => JournalRefresh("unavailable", false, null, true));
                Run("Unclosed writers are refreshed even when journal reasons coalesce", OpenWriter);
                Run("Changes during a first scan are replayed on refresh", DuringScan);
                Run("Journal reset, overflow, security changes and failures force a fresh scan", JournalFallback);
                Run("Native permission changes hide and restore affected trees", () => PermissionRefresh("native", nativeJournal));
                Run("Controlled permission changes invalidate only affected cached trees", () => PermissionRefresh("controlled", true, new FakeJournal()));
                Run("Without a journal, permission changes never reuse inaccessible descendants", () => PermissionRefresh("unavailable", false, null, true));
                Run("Backup-first suggestions exclude protected and recent files", Suggestions);
                Run("Boot, profile, application and hard-link files cannot be recycled", ExtendedProtection);
                Run("Native WPF interface scans, filters, scrolls and renders", UserInterface);
            }
            catch (Exception ex) { failed++; Console.WriteLine("FATAL " + ex); }
            finally
            {
                // The only recursive cleanup target is the unique fixture directory created above.
                string expected = Path.Combine(project, ".test-work") + Path.DirectorySeparatorChar;
                if (root.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                {
                    foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(root, true);
                }
            }
            Console.WriteLine("\nRESULT: " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test) { var timer = Stopwatch.StartNew(); try { test(); passed++; Console.WriteLine("PASS " + name + " (" + timer.ElapsedMilliseconds + " ms)"); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); } }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        private static string FileOfSize(string folder, string name, long size) { string path = Path.Combine(folder, name); using (var stream = File.Create(path)) stream.SetLength(size); return path; }
        private static ScanResult Scan(string folder) { return new DriveScanner().Scan(folder, CancellationToken.None, null); }
        private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected the operation to fail safely."); }

        private static void ScanAll()
        {
            string folder = Folder("basics"), nested = Path.Combine(folder, "Nested"); Directory.CreateDirectory(nested);
            FileOfSize(folder, "empty", 0); FileOfSize(nested, "影片 résumé.mp4", 54321); FileOfSize(folder, "medium.zip", 2048);
            string hidden = FileOfSize(nested, ".hidden.dat", 71); File.SetAttributes(hidden, FileAttributes.Hidden);
            string system = FileOfSize(folder, "system.dat", 123); File.SetAttributes(system, FileAttributes.System);
            using (var locked = new FileStream(FileOfSize(folder, "locked.bin", 300), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = Scan(folder); Assert(result.Files.Count == 6, "Expected all six files, including locked metadata."); Assert(result.TotalBytes == 56863, "Total mismatch: " + result.TotalBytes);
                Assert(result.Files[0].Name == "影片 résumé.mp4" && result.Files.Last().Size == 0, "Largest-first order incorrect."); Assert(result.Issues.Count == 0 && result.FolderCount == 2, "Unexpected coverage result.");
            }
        }
        private static void LargeFile()
        {
            string folder = Folder("large"), path = Path.Combine(folder, "large-sparse.bin");
            using (var stream = File.Create(path)) { uint returned; Assert(DeviceIoControl(stream.SafeFileHandle.DangerousGetHandle(), 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero), "Cannot mark fixture sparse."); stream.SetLength(5L * 1024 * 1024 * 1024 + 97); }
            var result = Scan(folder); Assert(result.Files.Single().Size == 5368709217, "64-bit size was truncated.");
        }
        private static void LongPaths()
        {
            string folder = Folder("long"), current = folder;
            for (int i = 0; i < 8; i++) { current = Path.Combine(current, new String('a', 35) + i); Directory.CreateDirectory(current); }
            string path = FileOfSize(current, "long-路径.txt", 87); Assert(path.Length > 300, "Fixture too short.");
            var result = Scan(folder); Assert(result.Files.Single().FullPath == path, "Long file missing."); Assert(NativeFiles.ReadEntry(path).Size == 87, "Long path metadata revalidation failed.");
        }
        private static void JunctionLoop()
        {
            string folder = Folder("junction"), link = Path.Combine(folder, "loop"); FileOfSize(folder, "one.txt", 1);
            CreateJunction(link, folder);
            try { var result = Scan(folder); Assert(result.Files.Count == 1 && result.FolderCount == 1, "Junction was traversed."); Assert(result.Issues.Count == 1 && result.Issues[0].Path == link, "Skipped junction wasn't reported."); }
            finally { Directory.Delete(link); }
        }
        private static void AccessDenied()
        {
            string folder = Folder("permissions"), denied = Path.Combine(folder, "denied"); Directory.CreateDirectory(denied); FileOfSize(denied, "secret.txt", 1); FileOfSize(folder, "readable.txt", 2);
            var acl = Directory.GetAccessControl(denied);
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(rule); Directory.SetAccessControl(denied, acl);
            try { var result = Scan(folder); Assert(result.Files.Count == 1 && result.Files[0].Name == "readable.txt", "Denied folder handling failed."); Assert(result.Issues.Any(i => i.Path == denied), "Permission gap was not reported."); }
            finally { acl.RemoveAccessRuleSpecific(rule); Directory.SetAccessControl(denied, acl); }
        }
        private static void HardLinks()
        {
            string folder = Folder("links"), first = FileOfSize(folder, "original.bin", 19); Assert(CreateHardLinkW(Path.Combine(folder, "alias.bin"), first, IntPtr.Zero), "Hard-link fixture failed.");
            var result = Scan(folder); Assert(result.Files.Count == 2 && result.TotalBytes == 38, "Both paths must be represented in logical totals.");
        }
        private static void Cancellation()
        {
            string folder = Folder("cancel"), child = Path.Combine(folder, "child"); Directory.CreateDirectory(child); for (int i = 0; i < 150; i++) FileOfSize(child, "file-" + i + ".txt", i);
            using (var cancellation = new CancellationTokenSource())
            {
                var result = new DriveScanner().Scan(folder, cancellation.Token, p => cancellation.Cancel());
                Assert(result.Cancelled && result.Files.Count < 150, "Cancellation didn't retain a partial result.");
                Assert(result.Files.Zip(result.Files.Skip(1), (a, b) => a.Size >= b.Size).All(x => x), "Partial results unsorted.");
            }
            using (var cancellation = new CancellationTokenSource()) { cancellation.Cancel(); Assert(new DriveScanner().Scan(folder, cancellation.Token, null).Files.Count == 0, "Pre-cancel ignored."); }
            Directory.Move(folder, folder + "-moved");
        }
        private static void EmptyAndInvalid() { Assert(Scan(Folder("empty")).Files.Count == 0, "Empty folder failed."); Throws(() => Scan(Path.Combine(root, "not-real"))); Throws(() => Scan(FileOfSize(Folder("invalid"), "file", 1))); }
        private static void Filters()
        {
            var files = new List<FileEntry> { new FileEntry("D:\\Alpha", "B.mp4", 400, 4, 0), new FileEntry("D:\\Beta", "a.zip", 500, 2, 0), new FileEntry("D:\\Alpha", "c.txt", 10, 8, 0), new FileEntry("D:\\Zero", "zero", 0, 1, 0) };
            FileQuery.SortLargest(files);
            Assert(FileQuery.Apply(files, "ALPHA", "All types", 0, "Largest first", CancellationToken.None).Count == 2, "Path search failed.");
            Assert(FileQuery.Apply(files, "b.MP4", "Video", 100, "Largest first", CancellationToken.None).Single().Size == 400, "Combined filters failed.");
            Assert(FileQuery.Apply(files, "", "All types", 0, "Smallest first", CancellationToken.None).First().Size == 0, "Ascending order failed.");
            Assert(FileQuery.Apply(files, "", "All types", 0, "Recently modified", CancellationToken.None).First().Name == "c.txt", "Modified sort failed.");
            Assert(FileQuery.Apply(files, "", "All types", 0, "Name A–Z", CancellationToken.None).First().Name == "a.zip", "Name sort failed.");
            Assert(FileQuery.Apply(files, "", "Archives", 501, "Largest first", CancellationToken.None).Count == 0, "Minimum filter failed.");
            using (var cts = new CancellationTokenSource()) { cts.Cancel(); Throws(() => FileQuery.Apply(files, "", "All types", 0, "Largest first", cts.Token)); }
        }
        private static void Export()
        {
            string path = Path.Combine(Folder("csv"), "export.csv");
            var files = new[] { new FileEntry(root, "=1+1.csv", 4, DateTime.UtcNow.Ticks, 0), new FileEntry(root, "comma, 日本.txt", 5, 0, 0) };
            CsvExport.Write(path, files); string csv = File.ReadAllText(path);
            Assert(csv.Contains("\"'=1+1.csv\"") && csv.Contains("comma, 日本.txt") && CsvExport.Cell("quote\"") == "\"quote\"\"\"", "CSV escaping failed.");
            Assert(CsvExport.Cell("  @SUM(A1)").StartsWith("\"'"), "Formula-prefix whitespace bypass.");
        }
        private static void Safety()
        {
            string folder = Folder("safety"), path = FileOfSize(folder, "changing.bin", 3); var entry = NativeFiles.ReadEntry(path); FileSafety.ValidateUnchanged(entry);
            File.WriteAllText(path, "changed contents"); Throws(() => FileSafety.ValidateUnchanged(entry));
            File.Delete(path); Throws(() => FileSafety.ValidateUnchanged(entry));
            Assert(FileSafety.BlockReason(new FileEntry(folder, "system", 0, 0, 4)) != null, "System file allowed.");
            Assert(FileSafety.BlockReason(new FileEntry(folder, "link", 0, 0, 0x400)) != null, "Reparse file allowed.");
            Assert(FileSafety.BlockReason(new FileEntry(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "example", 0, 0, 0)) != null, "Windows path allowed.");
            Assert(FileSafety.BlockReason(new FileEntry(folder, "personal", 0, 0, 0)) == null, "Personal file blocked.");
            string alias = Path.Combine(folder, "windows-alias"); CreateJunction(alias, Environment.SystemDirectory);
            try { var aliased = NativeFiles.ReadEntry(Path.Combine(alias, "kernel32.dll")); Throws(() => FileSafety.ValidateUnchanged(aliased)); }
            finally { Directory.Delete(alias); }
        }
        private static void RecycleVeto() { var sink = new RecycleGuard(); Assert(sink.PreDeleteItem(0, null) < 0 && sink.PermanentDeleteBlocked, "Permanent deletion wasn't vetoed."); Assert(new RecycleGuard().PreDeleteItem(0x80, null) == 0, "Recycle callback rejected."); }
        private static void ReplacedFile()
        {
            string folder = Folder("replaced"), path = FileOfSize(folder, "replace.bin", 12);
            var original = Scan(folder).Files.Single(); Assert(!original.Identity.IsEmpty, "Replacement test needs a file identity.");
            File.Move(path, Path.Combine(folder, "original.bin")); FileOfSize(folder, "replace.bin", original.Size);
            File.SetLastWriteTimeUtc(path, new DateTime(original.ModifiedUtcTicks, DateTimeKind.Utc)); File.SetAttributes(path, (FileAttributes)original.Attributes);
            Throws(() => FileSafety.ValidateUnchanged(original));
            var replacementGuard = new RecycleGuard(original);
            Assert(replacementGuard.PreDeleteItem(0x80, null) < 0 && replacementGuard.ValidationFailure != null, "Replacement bypassed the final Shell check.");
            var current = Scan(folder).Files.Single(f => f.Name == "replace.bin");
            FileSafety.ValidateUnchanged(current); File.WriteAllText(path, "modified just before callback");
            var changedGuard = new RecycleGuard(current);
            Assert(changedGuard.PreDeleteItem(0x80, null) < 0 && changedGuard.ValidationFailure != null && File.Exists(path), "Final callback did not reject a changed file safely.");
        }
        private static void ActualRecycle()
        {
            string path = FileOfSize(Folder("recycle"), "Biggofiles-disposable-recycle-test-" + Guid.NewGuid().ToString("N") + ".txt", 11);
            var scanner = new DriveScanner(); scanner.Scan(Path.GetDirectoryName(path), CancellationToken.None, null);
            RecycleService.Recycle(NativeFiles.ReadEntry(path), IntPtr.Zero); Assert(!File.Exists(path), "Test file still exists after recycle.");
            var after = scanner.Scan(Path.GetDirectoryName(path), CancellationToken.None, null);
            Assert(after.Files.Count == 0, "Recycling wasn't reflected by the next scan: " + after.Method + " / " + after.IndexNote);
            // The Shell may change an ACL during recycling. That legitimately requires
            // a full rebuild under the scanner's conservative security-change policy.
        }
        private static void MillionFiles()
        {
            var files = new List<FileEntry>(1000000); var random = new Random(1701);
            for (int i = 0; i < 1000000; i++) files.Add(new FileEntry("D:\\PerformanceFixture", "file-" + i.ToString("D7") + (i % 2 == 0 ? ".mp4" : ".txt"), random.Next(), i, 0));
            FileQuery.SortLargest(files); var all = FileQuery.Apply(files, "", "All types", 0, "Largest first", CancellationToken.None);
            Assert(all.Count == 1000000, "Results were capped."); Assert(all.Zip(all.Skip(1), (a, b) => a.Size >= b.Size).All(x => x), "Million-file order wrong.");
            var matches = FileQuery.Apply(files, "file-0000", "Video", 0, "Smallest first", CancellationToken.None);
            Assert(matches.Count == 500, "Search over large result set incorrect.");
            Console.WriteLine("  Managed memory with million-file fixtures: " + Format.Bytes(GC.GetTotalMemory(false)));
        }
        private static string[] Fingerprint(ScanResult result) { return result.Files.Select(f => f.FullPath + "|" + f.Size + "|" + f.Attributes).OrderBy(x => x, StringComparer.Ordinal).ToArray(); }
        private static void SameAsFresh(string folder, ScanResult result) { Assert(Fingerprint(result).SequenceEqual(Fingerprint(new ReferenceScanner().Scan(folder, CancellationToken.None, null))), "Incremental index differs from independent recursive enumeration."); }
        private static bool NativeJournalAvailable()
        {
            string volume = Path.GetPathRoot(NativeFiles.ResolvedPath(root));
            using (var journal = UsnJournal.TryOpen(volume))
            {
                if (journal == null) { Console.WriteLine("  Native journal unavailable on " + volume + "; verifying full-scan fallback. Controlled journal tests still require fast refresh."); return false; }
                try
                {
                    // Probe Windows directly, independently of the scanner's cache/fallback decision.
                    var state = journal.Query();
                    var retained = journal.Read(Math.Max(state.FirstUsn, state.LowestValidUsn), state, CancellationToken.None, true);
                    Assert(!retained.UnsupportedRecords, "Native journal probe found an unsupported record format.");
                    journal.Read(state.NextUsn, journal.Query(), CancellationToken.None);
                    Console.WriteLine("  Native journal readable on " + volume + "; native tests require fast refresh."); return true;
                }
                catch (Win32Exception ex)
                {
                    // Expected capability limits only. Bad parameters, malformed records and other
                    // unexpected failures must fail the suite instead of silently reducing coverage.
                    if (!IsJournalUnavailable(ex.NativeErrorCode)) throw;
                    Console.WriteLine("  Native journal unavailable on " + volume + " (Windows error " + ex.NativeErrorCode + ": " + ex.Message + "); verifying full-scan fallback. Controlled journal tests still require fast refresh."); return false;
                }
            }
        }
        private static bool IsJournalUnavailable(int error)
        {
            return error == 1 || error == 5 || error == 50 || error == 1178 || error == 1179;
        }
        private static DriveScanner RefreshScanner(FakeJournal fake, bool withoutJournal)
        {
            var scanner = new DriveScanner();
            if (fake != null) scanner.JournalFactory = p => fake;
            else if (withoutJournal) scanner.JournalFactory = p => new FakeJournal { QueryError = 1179 };
            return scanner;
        }
        private static void AssertRefresh(ScanResult result, bool expectJournal)
        {
            string expected = expectJournal ? "Journal refresh" : "Full scan";
            Assert(result.Method == expected, "Expected " + expected + ", got " + result.Method + ": " + result.IndexNote);
            if (!expectJournal) Assert(result.ReusedFolders == 0, "Unavailable journal reused cached folders.");
        }
        private static FileIdentity IdentityOf(string path)
        {
            using (var handle = BulkDirectory.Open(path, 0)) { ulong serial; var id = BulkDirectory.Identify(handle, out serial); Assert(!id.IsEmpty, "Fixture has no file identity: " + path); return id; }
        }
        private static void RecordChange(FakeJournal fake, string parent, string path, bool security = false)
        {
            if (fake == null) return;
            var delta = new JournalDelta(); delta.ParentIds.Add(IdentityOf(parent));
            if (path != null) { var id = IdentityOf(path); delta.FileIds.Add(id); if (security) delta.SecurityDirectoryIds.Add(id); }
            fake.Append(delta);
        }
        private static void JournalRefresh(string mode, bool expectJournal, FakeJournal fake = null, bool withoutJournal = false)
        {
            string folder = Folder("journal-" + mode), one = Path.Combine(folder, "one"), two = Path.Combine(folder, "two"); Directory.CreateDirectory(one); Directory.CreateDirectory(two);
            string changing = FileOfSize(one, "change.bin", 10), deleted = FileOfSize(two, "delete.bin", 20);
            FileOfSize(one, "move.txt", 30); Directory.CreateDirectory(Path.Combine(one, "nested")); FileOfSize(Path.Combine(one, "nested"), "nested.txt", 40);
            var scanner = RefreshScanner(fake, withoutJournal); scanner.Scan(folder, CancellationToken.None, null);
            var unchanged = scanner.Scan(folder, CancellationToken.None, null);
            AssertRefresh(unchanged, expectJournal);
            if (expectJournal) Assert(unchanged.FoldersRead == 0, "Unchanged folders were needlessly rescanned."); SameAsFresh(folder, unchanged);
            RecordChange(fake, one, changing); RecordChange(fake, two, deleted); RecordChange(fake, one, Path.Combine(one, "move.txt"));
            using (var stream = new FileStream(changing, FileMode.Open)) stream.SetLength(120);
            File.Delete(deleted); FileOfSize(two, "new 日本.txt", 57); File.Move(Path.Combine(one, "move.txt"), Path.Combine(two, "renamed.txt"));
            RecordChange(fake, two, Path.Combine(two, "new 日本.txt")); RecordChange(fake, two, Path.Combine(two, "renamed.txt"));
            var edited = scanner.Scan(folder, CancellationToken.None, null); AssertRefresh(edited, expectJournal); SameAsFresh(folder, edited);
            RecordChange(fake, folder, one);
            Directory.Move(one, Path.Combine(folder, "renamed-directory"));
            RecordChange(fake, folder, Path.Combine(folder, "renamed-directory"));
            var renamed = scanner.Scan(folder, CancellationToken.None, null); AssertRefresh(renamed, expectJournal); SameAsFresh(folder, renamed);
            RecordChange(fake, folder, two);
            Directory.Delete(two, true); Directory.CreateDirectory(two); FileOfSize(two, "replacement.txt", 8);
            RecordChange(fake, folder, two);
            var replaced = scanner.Scan(folder, CancellationToken.None, null); AssertRefresh(replaced, expectJournal); SameAsFresh(folder, replaced);
            string alias = Path.Combine(folder, "hardlink.bin"); string original = Path.Combine(folder, "renamed-directory", "change.bin"); Assert(CreateHardLinkW(alias, original, IntPtr.Zero), "Hardlink creation failed.");
            RecordChange(fake, folder, alias);
            var linked = scanner.Scan(folder, CancellationToken.None, null); AssertRefresh(linked, expectJournal); SameAsFresh(folder, linked);
            using (var stream = new FileStream(original, FileMode.Open)) stream.SetLength(900);
            RecordChange(fake, Path.GetDirectoryName(original), original);
            var resized = scanner.Scan(folder, CancellationToken.None, null); AssertRefresh(resized, expectJournal); SameAsFresh(folder, resized);
            var forced = scanner.Scan(folder, CancellationToken.None, null, true); AssertRefresh(forced, false); SameAsFresh(folder, forced);
        }
        private static void OpenWriter()
        {
            string folder = Folder("open-writer"), path = FileOfSize(folder, "stream.bin", 5); var scanner = new DriveScanner(); scanner.Scan(folder, CancellationToken.None, null);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(51); stream.Flush(true); var first = scanner.Scan(folder, CancellationToken.None, null); Assert(first.Files.Single().Size == 51, "Open writer's first update missed.");
                stream.SetLength(93); stream.Flush(true); var second = scanner.Scan(folder, CancellationToken.None, null); Assert(second.Files.Single().Size == 93, "Open writer's coalesced update missed.");
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(123); stream.Flush(true); var another = new DriveScanner(); another.Scan(folder, CancellationToken.None, null);
                stream.SetLength(321); stream.Flush(true); Assert(another.Scan(folder, CancellationToken.None, null).Files.Single().Size == 321, "Writer already open before the baseline scan was missed.");
            }
        }
        private static void DuringScan()
        {
            string folder = Folder("during"), child = Path.Combine(folder, "child"); Directory.CreateDirectory(child); FileOfSize(child, "original", 4);
            var scanner = new DriveScanner(); bool changed = false;
            scanner.Scan(folder, CancellationToken.None, p => { if (!changed && p.FileCount > 0) { changed = true; FileOfSize(folder, "created-during-scan", 9); } });
            var refreshed = scanner.Scan(folder, CancellationToken.None, null); SameAsFresh(folder, refreshed); Assert(refreshed.Files.Count == 2, "Change during baseline was lost.");
        }
        private sealed class FakeJournal : IChangeJournal
        {
            public JournalState State = new JournalState { Id = 1, NextUsn = 100 };
            public bool Fail, Security;
            public int QueryError;
            private readonly List<KeyValuePair<long, JournalDelta>> changes = new List<KeyValuePair<long, JournalDelta>>();
            public void Append(JournalDelta delta) { changes.Add(new KeyValuePair<long, JournalDelta>(State.NextUsn++, delta)); }
            public JournalState Query() { if (QueryError != 0) throw new Win32Exception(QueryError); return new JournalState { Id = State.Id, FirstUsn = State.FirstUsn, NextUsn = State.NextUsn, LowestValidUsn = State.LowestValidUsn }; }
            public JournalDelta Read(long from, JournalState until, CancellationToken token, bool pendingOnly = false)
            {
                if (Fail) throw new IOException("Injected read failure");
                var result = new JournalDelta { RequiresFullScan = Security };
                foreach (var change in changes)
                {
                    token.ThrowIfCancellationRequested();
                    if (change.Key < from || change.Key >= until.NextUsn || pendingOnly) continue;
                    result.ParentIds.UnionWith(change.Value.ParentIds); result.FileIds.UnionWith(change.Value.FileIds); result.SecurityDirectoryIds.UnionWith(change.Value.SecurityDirectoryIds);
                }
                return result;
            }
            public void Dispose() { }
        }
        private static void JournalFallback()
        {
            string folder = Folder("fallback"); FileOfSize(folder, "first", 3); var fake = new FakeJournal(); var scanner = new DriveScanner { JournalFactory = p => fake };
            scanner.Scan(folder, CancellationToken.None, null); fake.State.Id++;
            Assert(scanner.Scan(folder, CancellationToken.None, null).Method == "Full scan", "Reset journal reused stale index.");
            fake.State.FirstUsn = 101; fake.State.NextUsn = 200;
            Assert(scanner.Scan(folder, CancellationToken.None, null).Method == "Full scan", "Expired checkpoint reused stale index.");
            fake.Fail = true; FileOfSize(folder, "new", 5);
            var failed = scanner.Scan(folder, CancellationToken.None, null); Assert(failed.Method == "Full scan" && failed.Files.Count == 2, "Read failure didn't rebuild.");
            fake.Fail = false; scanner.Scan(folder, CancellationToken.None, null); fake.Security = true;
            Assert(scanner.Scan(folder, CancellationToken.None, null).Method == "Full scan", "Security change didn't rebuild.");
            scanner.JournalFactory = p => null;
            Assert(scanner.Scan(folder, CancellationToken.None, null).Method == "Full scan", "Missing journal didn't rebuild.");
        }
        private static void Suggestions()
        {
            DateTime now = DateTime.UtcNow; long old = now.AddDays(-200).Ticks;
            Assert(CleanupAdvisor.Reason(new FileEntry("D:\\Personal", "video.mp4", 500L * 1024 * 1024, old, 0), now) != null, "Older large media wasn't suggested.");
            Assert(CleanupAdvisor.Reason(new FileEntry("D:\\Personal", "video.mp4", 500L * 1024 * 1024, now.Ticks, 0), now) == null, "Recent file suggested.");
            Assert(CleanupAdvisor.Reason(new FileEntry("C:\\ProgramData", "archive.zip", 900L * 1024 * 1024, old, 0), now) == null, "Protected file suggested.");
            Assert(CleanupAdvisor.Reason(new FileEntry("D:\\User\\Downloads", "setup.exe", 90L * 1024 * 1024, old, 0), now) != null, "Downloaded installer wasn't suggested.");
            Assert(CleanupAdvisor.Reason(new FileEntry("D:\\DownloadsArchive", "setup.exe", 90L * 1024 * 1024, old, 0), now) == null, "Unrelated application folder suggested.");
        }
        private static void PermissionRefresh(string mode, bool expectJournal, FakeJournal fake = null, bool withoutJournal = false)
        {
            string folder = Folder("permission-refresh-" + mode), branch = Path.Combine(folder, "branch"), nested = Path.Combine(branch, "nested"), sibling = Path.Combine(folder, "sibling");
            Directory.CreateDirectory(nested); Directory.CreateDirectory(sibling);
            FileOfSize(branch, "one", 1); FileOfSize(nested, "two", 2); FileOfSize(sibling, "three", 3);
            var scanner = RefreshScanner(fake, withoutJournal); scanner.Scan(folder, CancellationToken.None, null);
            var acl = Directory.GetAccessControl(branch);
            var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(deny); Directory.SetAccessControl(branch, acl);
            try
            {
                RecordChange(fake, folder, branch, true);
                var denied = scanner.Scan(folder, CancellationToken.None, null);
                Assert(denied.Files.Count == 1 && denied.Files[0].Name == "three", "Permission change retained inaccessible descendants.");
                Assert(denied.Issues.Any(i => i.Path == branch), "Denied subtree was not reported.");
                AssertRefresh(denied, expectJournal);
                if (expectJournal) Assert(denied.ReusedFolders > 0, "Permission change rebuilt unrelated cached folders.");
                SameAsFresh(folder, denied);
            }
            finally { acl.RemoveAccessRuleSpecific(deny); Directory.SetAccessControl(branch, acl); }
            RecordChange(fake, folder, branch, true);
            var restored = scanner.Scan(folder, CancellationToken.None, null);
            Assert(restored.Files.Count == 3, "Newly accessible subtree was not restored."); AssertRefresh(restored, expectJournal); SameAsFresh(folder, restored);

            string unrelated = Folder("unrelated-permission-" + mode); var unrelatedAcl = Directory.GetAccessControl(unrelated);
            unrelatedAcl.AddAccessRule(deny); Directory.SetAccessControl(unrelated, unrelatedAcl);
            try { RecordChange(fake, root, unrelated, true); var unchanged = scanner.Scan(folder, CancellationToken.None, null); AssertRefresh(unchanged, expectJournal); if (expectJournal) Assert(unchanged.FoldersRead == 0, "Unrelated permission change reread cached folders."); SameAsFresh(folder, unchanged); }
            finally { unrelatedAcl.RemoveAccessRuleSpecific(deny); Directory.SetAccessControl(unrelated, unrelatedAcl); }

            // An ancestor outside the selected root can change effective access.
            var nestedScanner = RefreshScanner(fake, withoutJournal); nestedScanner.Scan(nested, CancellationToken.None, null);
            var allow = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory, AccessControlType.Allow);
            acl.AddAccessRule(allow); Directory.SetAccessControl(branch, acl);
            try { RecordChange(fake, folder, branch, true); var rebuilt = nestedScanner.Scan(nested, CancellationToken.None, null); AssertRefresh(rebuilt, false); SameAsFresh(nested, rebuilt); }
            finally { acl.RemoveAccessRuleSpecific(allow); Directory.SetAccessControl(branch, acl); }
        }
        private static void ExtendedProtection()
        {
            foreach (string path in new[] { @"C:\Boot\BCD", @"D:\Windows\example.dat", @"C:\ProgramData\database.bin", @"C:\Users\example\AppData\Local\profile.db", @"C:\Users\example\NTUSER.DAT", @"D:\pagefile.sys", @"C:\EFI\boot\loader.efi", @"D:\Recovery\restore.wim" })
                Assert(FileSafety.BlockReason(new FileEntry(Path.GetDirectoryName(path), Path.GetFileName(path), 1, 0, 0)) != null, "Required file unprotected: " + path);
            string folder = Folder("protected-hardlink"), original = FileOfSize(folder, "one.txt", 3); Assert(CreateHardLinkW(Path.Combine(folder, "two.txt"), original, IntPtr.Zero), "Hardlink fixture failed.");
            Throws(() => FileSafety.ValidateUnchanged(NativeFiles.ReadEntry(original)));
        }
        private static void MillionFileProgress()
        {
            var preview = new ScanPreview(); var all = new List<FileEntry>(); long total = 0;
            var timer = Stopwatch.StartNew();
            for (int batch = 0; batch < 1000; batch++)
            {
                var record = new DirectoryRecord();
                for (int i = 0; i < 1000; i++)
                {
                    int number = batch * 1000 + i;
                    var file = new FileEntry("D:\\Synthetic", "progress-" + number, (number * 7919L) % 1000003, 0, 0);
                    record.Files.Add(file); all.Add(file); total += file.Size;
                }
                preview.Add(record);
                var snapshot = preview.Snapshot("D:\\Synthetic", timer.Elapsed, "Scanning");
                Assert(snapshot.FileCount == all.Count && snapshot.TotalBytes == total, "Live count or size lost files.");
                Assert(snapshot.Preview.Length == 100, "Progress size grows with the file count.");
            }
            var final = preview.Snapshot("D:\\Synthetic", timer.Elapsed, "Sorting results");
            FileQuery.SortLargest(all);
            Assert(final.Preview.SequenceEqual(all.Take(100)), "Bounded progress differs from a full independent sort.");
            Assert(final.FileCount == 1000000 && final.FolderCount == 1000, "Progress counts are incomplete.");
            Console.WriteLine("  1,000 progress snapshots over 1,000,000 records: " + Format.Duration(timer.Elapsed));
        }
        private static int BenchmarkInterface(string location)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var controller = new MainController();
            controller.Window.ShowInTaskbar = false; controller.Window.Left = -20000; controller.Window.Top = -20000; controller.Window.WindowStartupLocation = WindowStartupLocation.Manual;
            controller.Window.Show(); Pump();
            for (int run = 1; run <= 2; run++)
            {
                var timer = Stopwatch.StartNew(); long lastTick = 0, longestGap = 0, lastPrint = 0; int ticks = 0;
                var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                heartbeat.Tick += delegate { longestGap = Math.Max(longestGap, timer.ElapsedMilliseconds - lastTick); lastTick = timer.ElapsedMilliseconds; ticks++; };
                heartbeat.Start(); var scan = controller.StartScanAsync(location);
                while (!scan.IsCompleted && timer.Elapsed.TotalMinutes < 5)
                {
                    Pump(); Thread.Sleep(5);
                    if (timer.ElapsedMilliseconds - lastPrint > 5000) { lastPrint = timer.ElapsedMilliseconds; Console.WriteLine("  UI: " + controller.Get<TextBlock>("Status").Text); }
                }
                heartbeat.Stop(); Assert(scan.IsCompleted, "UI drive benchmark timed out."); scan.GetAwaiter().GetResult();
                var result = controller.Result; Assert(result != null && !result.Cancelled && result.Files.Count == controller.VisibleFiles.Count, "UI scan did not finish with all results bound.");
                Console.WriteLine("UI run " + run + ": files=" + result.Files.Count + " folders=" + result.FolderCount + " notes=" + result.Issues.Count + " total=" + Format.Duration(timer.Elapsed) + " scanner=" + Format.Duration(result.Elapsed) + " method=" + result.Method + " read=" + result.FoldersRead + " reused=" + result.ReusedFolders + " heartbeatTicks=" + ticks + " largestGapMs=" + longestGap);
                Console.WriteLine("  WorkingSet=" + Format.Bytes(Process.GetCurrentProcess().WorkingSet64) + " Managed=" + Format.Bytes(GC.GetTotalMemory(false)));
                Console.WriteLine("  Index: " + result.IndexNote);
            }
            controller.Window.Close(); app.Shutdown(); return 0;
        }
        private static void UserInterface()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var controller = new MainController();
            Assert(!controller.IsLightTheme && controller.Window.Title.Contains("v1.0"), "Default theme or visible version is incorrect.");
            controller.Window.ShowInTaskbar = false; controller.Window.Left = -20000; controller.Window.Top = -20000; controller.Window.WindowStartupLocation = WindowStartupLocation.Manual;
            controller.Window.Show(); Pump();
            string images = Path.Combine(project, "docs"); Directory.CreateDirectory(images);
            Render(controller.Window, Path.Combine(images, "empty-state.png"));
            string folder = Folder("ui");
            string[] names = { "The Northern Coast — final cut.mp4", "Project archive 2026.zip", "Studio session masters.wav", "Brand system.psd", "Windows backup.vhdx", "Product walkthrough.mov", "Research collection.pdf", "Summer photographs.zip", "Ambient textures.flac", "Portfolio images.tiff", "Read me.txt" };
            long[] sizes = { 12884901888, 8321499136, 5261334937, 2716566814, 1846835937, 1275072348, 534773760, 335544320, 144703488, 73400320, 12400 };
            for (int i = 0; i < names.Length; i++)
            {
                using (var stream = File.Create(Path.Combine(folder, names[i]))) { uint returned; DeviceIoControl(stream.SafeFileHandle.DangerousGetHandle(), 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero); stream.SetLength(sizes[i]); }
                File.SetLastWriteTimeUtc(Path.Combine(folder, names[i]), DateTime.UtcNow.AddDays(-200));
            }
            Wait(controller.StartScanAsync(folder)); Pump();
            Assert(controller.Result.Files.Count == 11 && controller.VisibleFiles[0].Name == names[0], "UI scan didn't bind correctly.");
            var grid = controller.Get<DataGrid>("Files"); grid.SelectedIndex = 0; Pump();
            Assert(controller.Get<Button>("Recycle").IsEnabled && controller.Get<Button>("Reveal").IsEnabled, "Selection actions not enabled.");
            var completeResult = controller.Result; controller.Result = null; grid.SelectedIndex = 1;
            Assert(!controller.Get<Button>("Recycle").IsEnabled, "Recycling enabled without a completed scan result.");
            controller.Result = completeResult; grid.SelectedIndex = 0;
            FileEntry revealed = null; controller.RevealAction = file => revealed = file;
            grid.SelectedIndex = 1; var clickedRow = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow; Assert(clickedRow != null, "Clicked row not realized.");
            var doubleClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = clickedRow };
            grid.RaiseEvent(doubleClick); Assert(revealed != null && revealed.Name == names[0], "Double-click did not reveal the clicked row's file.");
            revealed = null; grid.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = grid });
            Assert(revealed == null, "Double-click on grid background incorrectly opened a selected file.");
            Render(controller.Window, Path.Combine(images, "biggofiles.png"));
            var priorSelection = grid.SelectedItem; var priorItems = grid.ItemsSource;
            controller.Get<Button>("Theme").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle();
            Assert(controller.IsLightTheme && ((SolidColorBrush)controller.Window.Background).Color == (Color)ColorConverter.ConvertFromString("#F6F7F4"), "Light theme did not reach the window.");
            Assert(Object.ReferenceEquals(priorItems, grid.ItemsSource) && Object.ReferenceEquals(priorSelection, grid.SelectedItem), "Theme switching reset scan results or selection.");
            Render(controller.Window, Path.Combine(images, "light-theme.png"));
            VerifyDialog(controller, "SizeInfo", images, "size-explanation.png", "Why the storage totals differ");
            VerifyDialog(controller, "About", images, "about.png", "Biggofiles");
            controller.Get<Button>("Theme").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle();
            Assert(!controller.IsLightTheme && ((SolidColorBrush)controller.Window.Background).Color == (Color)ColorConverter.ConvertFromString("#11161C"), "Dark theme did not restore.");
            controller.Get<Button>("Suggestions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle();
            Assert(controller.VisibleFiles.Count > 0 && controller.VisibleFiles.Count < 11, "Suggestions didn't filter the file list."); Render(controller.Window, Path.Combine(images, "cleanup-review.png"));
            controller.Get<Button>("Suggestions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(); Assert(controller.VisibleFiles.Count == 11, "All-files mode wasn't restored.");
            controller.Get<TextBox>("Search").Text = "Northern"; Wait(controller.ApplyFiltersAsync()); Assert(controller.VisibleFiles.Count == 1, "UI search failed.");
            controller.Get<TextBox>("Search").Text = "nonexistent"; Wait(controller.ApplyFiltersAsync()); Assert(controller.VisibleFiles.Count == 0 && controller.Get<StackPanel>("EmptyState").Visibility == Visibility.Visible, "Empty filter state failed.");
            controller.Get<TextBox>("Search").Clear(); Wait(controller.ApplyFiltersAsync());
            controller.Get<ComboBox>("Sort").SelectedIndex = 1; Wait(controller.ApplyFiltersAsync()); Assert(controller.VisibleFiles[0].Size == 12400, "UI sort failed.");
            controller.Get<ComboBox>("KindFilter").SelectedIndex = 1; Wait(controller.ApplyFiltersAsync()); Assert(controller.VisibleFiles.Count == 2, "UI kind filter failed.");
            controller.Get<ComboBox>("Sort").SelectedIndex = 0; controller.Get<ComboBox>("KindFilter").SelectedIndex = 0; Wait(controller.ApplyFiltersAsync());
            // Verify row virtualization with a much larger bound collection.
            var originalResult = controller.Result;
            var bulk = new ScanResult { Root = folder };
            for (int i = 0; i < 100000; i++) bulk.Files.Add(new FileEntry(folder, "virtual-row-" + i + ".bin", 100000 - i, DateTime.UtcNow.Ticks, 0));
            FileQuery.SortLargest(bulk.Files); controller.Result = bulk; Wait(controller.ApplyFiltersAsync()); Pump();
            grid.ScrollIntoView(bulk.Files[99999]); Pump(); int rows = CountRows(grid);
            Assert(rows < 100, "DataGrid created too many rows: " + rows); Console.WriteLine("  Realized rows for 100,000 files: " + rows);
            controller.Result = originalResult; Wait(controller.ApplyFiltersAsync()); grid.ScrollIntoView(originalResult.Files[0]); Settle();
            controller.Window.Width = 1000; controller.Window.Height = 660; Settle(); Render(controller.Window, Path.Combine(images, "minimum-window.png"));
            Point themePosition = controller.Get<Button>("Theme").TranslatePoint(new Point(0, 0), controller.Window);
            Assert(themePosition.Y + controller.Get<Button>("Theme").ActualHeight < controller.Window.ActualHeight, "Theme switch is clipped at minimum height.");
            controller.Window.Close(); app.Shutdown();
        }
        private static int CountRows(DependencyObject element) { int count = element is DataGridRow ? 1 : 0; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) count += CountRows(VisualTreeHelper.GetChild(element, i)); return count; }
        private static void VerifyDialog(MainController controller, string button, string images, string file, string expected)
        {
            Exception error = null; bool opened = false;
            controller.Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = controller.Window.OwnedWindows.Cast<Window>().FirstOrDefault();
                try
                {
                    Assert(dialog != null, "Dialog did not open."); opened = true; dialog.UpdateLayout();
                    Assert(((SolidColorBrush)dialog.Background).Color == Colors.White, "Dialog ignored the light theme.");
                    string text = String.Join(" ", FindText(dialog)); Assert(text.Contains(expected), "Dialog content or branding is missing.");
                    Render(dialog, Path.Combine(images, file));
                }
                catch (Exception ex) { error = ex; }
                finally { if (dialog != null) dialog.Close(); }
            }));
            controller.Get<Button>(button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(opened && error == null, "Dialog check failed: " + error);
        }
        private static IEnumerable<string> FindText(DependencyObject element)
        {
            var block = element as TextBlock; if (block != null) yield return block.Text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) foreach (var text in FindText(VisualTreeHelper.GetChild(element, i))) yield return text;
        }
        private static void Wait(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30); while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            Assert(task.IsCompleted, "UI operation timed out."); task.GetAwaiter().GetResult();
        }
        private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        private static void Settle() { for (int i = 0; i < 12; i++) { Pump(); Thread.Sleep(20); } }
        private static void Render(Window window, string path)
        {
            window.UpdateLayout(); Pump(); var target = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); target.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target)); using (var stream = File.Create(path)) encoder.Save(stream);
        }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(IntPtr file, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private static void CreateJunction(string path, string destination)
        {
            Directory.CreateDirectory(path); byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + destination), print = Encoding.Unicode.GetBytes(destination);
            int length = 16 + substitute.Length + 2 + print.Length + 2; IntPtr buffer = Marshal.AllocHGlobal(length);
            IntPtr handle = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            try
            {
                Assert(handle != new IntPtr(-1), "Couldn't open junction fixture.");
                Marshal.Copy(new byte[length], 0, buffer, length); Marshal.WriteInt32(buffer, unchecked((int)0xA0000003)); Marshal.WriteInt16(buffer, 4, (short)(length - 8)); Marshal.WriteInt16(buffer, 8, 0); Marshal.WriteInt16(buffer, 10, (short)substitute.Length); Marshal.WriteInt16(buffer, 12, (short)(substitute.Length + 2)); Marshal.WriteInt16(buffer, 14, (short)print.Length);
                Marshal.Copy(substitute, 0, IntPtr.Add(buffer, 16), substitute.Length); Marshal.Copy(print, 0, IntPtr.Add(buffer, 18 + substitute.Length), print.Length);
                uint returned; Assert(DeviceIoControl(handle, 0x900A4, buffer, (uint)length, IntPtr.Zero, 0, out returned, IntPtr.Zero), "Couldn't create junction: " + Marshal.GetLastWin32Error());
            }
            finally { if (handle != new IntPtr(-1)) CloseHandle(handle); Marshal.FreeHGlobal(buffer); }
        }
    }
}
