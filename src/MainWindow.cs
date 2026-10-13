using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Biggofiles
{
    public sealed class MainController
    {
        public readonly Window Window;
        internal ScanResult Result;
        internal List<FileEntry> VisibleFiles = new List<FileEntry>();
        internal bool Scanning { get { return scanning; } }
        private string target;
        private bool scanning, closing, recycling;
        private int queryVersion;
        private CancellationTokenSource scanCancellation, queryCancellation;
        private volatile ScanProgress latestProgress;
        private readonly DispatcherTimer progressTimer, searchTimer;
        private readonly Dictionary<string, Button> driveButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly DataGrid grid;
        private readonly TextBox search;
        private readonly DriveScanner scanner = new DriveScanner();
        private readonly ThemePalette theme;
        private readonly Stopwatch scanClock = new Stopwatch();
        private ScanProgress displayedProgress;
        internal bool IsLightTheme { get { return theme.IsLight; } }
        private bool suggestionsOnly;
        internal Action<FileEntry> RevealAction = ExplorerService.Reveal;

        public MainController()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Biggofiles.MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            theme = new ThemePalette(Window.Resources);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Biggofiles.App.ico"))
            { if (stream != null) Window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(stream); }
            grid = Get<DataGrid>("Files"); search = Get<TextBox>("Search");
            progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            progressTimer.Tick += delegate { UpdateProgress(); };
            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            searchTimer.Tick += async delegate { searchTimer.Stop(); await ApplyFiltersAsync(); };
            Get<Button>("Minimize").Click += delegate { Window.WindowState = WindowState.Minimized; };
            Get<Button>("Maximize").Click += delegate { Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; };
            Get<Button>("CloseWindow").Click += delegate { Window.Close(); };
            Get<Button>("RefreshDrives").Click += delegate { LoadDrives(); };
            Get<Button>("Browse").Click += delegate { ChooseFolder(); };
            Get<Button>("Scan").Click += async delegate { if (scanning) { scanCancellation.Cancel(); Get<Button>("Scan").Content = "Stopping…"; Get<Button>("Scan").IsEnabled = false; } else await StartScanAsync(target); };
            Get<Button>("Export").Click += async delegate { await ExportAsync(); };
            Get<Button>("About").Click += delegate { ShowAbout(); };
            Get<Button>("Theme").Click += delegate { SetLightTheme(!theme.IsLight); };
            Get<Button>("Coverage").Click += delegate { ShowCoverage(); };
            Get<Button>("SizeInfo").Click += delegate { ShowSizeInfo(); };
            Get<Button>("Suggestions").Click += async delegate { suggestionsOnly = !suggestionsOnly; UpdateMode(); await ApplyFiltersAsync(); };
            Get<Button>("FreshScan").Click += async delegate { if (!scanning) await StartScanAsync(target, true); };
            Get<Button>("CopyPath").Click += delegate { var file = grid.SelectedItem as FileEntry; if (file != null) Guard(delegate { Clipboard.SetText(file.FullPath); SetStatus("Path copied to clipboard"); }); };
            Get<Button>("Reveal").Click += delegate { RevealSelected(); };
            Get<Button>("Recycle").Click += async delegate { await RecycleSelectedAsync(); };
            search.TextChanged += delegate { Get<TextBlock>("SearchHint").Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ScheduleFilter(); };
            foreach (string name in new[] { "KindFilter", "SizeFilter", "Sort" }) Get<ComboBox>(name).SelectionChanged += delegate { ScheduleFilter(); };
            grid.SelectionChanged += delegate { UpdateSelection(); };
            grid.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e)
            {
                var row = ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) as DataGridRow;
                if (row == null || !(row.Item is FileEntry)) return;
                grid.SelectedItem = row.Item; RevealSelected(); e.Handled = true;
            };
            Window.PreviewKeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; }
                if (e.Key == Key.Escape && scanning) { scanCancellation.Cancel(); e.Handled = true; }
                if (e.Key == Key.F5 && !scanning && !recycling && target != null) { await StartScanAsync(target, Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; }
            };
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (recycling) { e.Cancel = true; return; }
                closing = true; if (scanCancellation != null) scanCancellation.Cancel(); if (queryCancellation != null) queryCancellation.Cancel(); progressTimer.Stop(); searchTimer.Stop();
            };
            Window.SizeChanged += delegate { Get<ScrollViewer>("DriveScroll").MaxHeight = Math.Max(80, Math.Min(260, Window.ActualHeight - 600)); };
            LoadDrives();
        }

        internal T Get<T>(string name) where T : class { return Window.FindName(name) as T; }
        private Brush Brush(string color) { return theme.Get(color); }
        internal void SetLightTheme(bool light)
        {
            theme.SetLight(light);
            Get<Button>("Theme").Content = light ? "Switch to dark theme" : "Switch to light theme";
            Get<Button>("Theme").ToolTip = light ? "Current theme: light" : "Current theme: dark (default)";
        }
        private void SetText(string name, string value) { Get<TextBlock>(name).Text = value; }
        private void SetStatus(string value) { SetText("Status", value); }

        private void LoadDrives()
        {
            var panel = Get<StackPanel>("DriveList"); panel.Children.Clear(); driveButtons.Clear();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType == DriveType.CDRom) continue;
                    string path = drive.RootDirectory.FullName;
                    long total = drive.TotalSize, free = drive.AvailableFreeSpace;
                    var content = new StackPanel();
                    var heading = new TextBlock { Text = (String.IsNullOrEmpty(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel) + " (" + drive.Name.TrimEnd('\\') + ")", FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                    content.Children.Add(heading);
                    content.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, total), Value = total - free, Height = 4, Background = Brush("#344039"), Foreground = Brush(total > 0 && (double)free / total < .1 ? "#E9AB89" : "#ADCD82"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 12, 0, 8) });
                    content.Children.Add(new TextBlock { Text = Format.Bytes(free) + " free of " + Format.Bytes(total), FontSize = 10, Foreground = Brush("#8F9F9A") });
                    var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 7), Padding = new Thickness(13), ToolTip = path };
                    button.Click += delegate { SelectTarget(path); };
                    panel.Children.Add(button); driveButtons[path] = button;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
            if (target != null) HighlightTarget();
        }

        internal void SelectTarget(string path)
        {
            if (scanning || recycling) return;
            target = path; SetText("TargetPath", path); Get<TextBlock>("TargetPath").ToolTip = path; Get<Button>("Scan").IsEnabled = true; HighlightTarget();
            if (Result != null && !String.Equals(Result.Root, path, StringComparison.OrdinalIgnoreCase)) SetStatus("Ready to scan " + path + " · results still show " + Result.Root);
        }
        private void HighlightTarget()
        {
            foreach (var pair in driveButtons)
            {
                bool chosen = String.Equals(pair.Key, target, StringComparison.OrdinalIgnoreCase);
                pair.Value.Background = Brush(chosen ? "#263326" : "#1B242C"); pair.Value.BorderBrush = Brush(chosen ? "#738E4D" : "#303B43");
            }
        }
        private void ChooseFolder()
        {
            Guard(delegate { string chosen = FolderPicker.Pick(new WindowInteropHelper(Window).Handle); if (chosen != null) SelectTarget(chosen); });
        }

        internal async Task StartScanAsync(string path, bool forceFull = false)
        {
            if (scanning || recycling || String.IsNullOrWhiteSpace(path)) return;
            SelectTarget(path); scanning = true; Result = null; queryVersion++;
            if (queryCancellation != null) queryCancellation.Cancel(); searchTimer.Stop();
            search.Clear(); foreach (string name in new[] { "KindFilter", "SizeFilter", "Sort" }) Get<ComboBox>(name).SelectedIndex = 0;
            VisibleFiles = new List<FileEntry>(); grid.ItemsSource = null; latestProgress = null; displayedProgress = null; scanClock.Restart();
            SetBusy(true); UpdateSelection(); SetEmpty("Looking through your files…", "Reading names and sizes. Your file contents stay untouched.");
            SetText("TotalSize", "0 B"); SetText("FileCount", "0"); SetText("LargestSize", "0 B"); SetText("LargestName", "Finding your biggest files"); SetText("FolderCount", "Starting scan…");
            SetStatus("Scanning " + path); Get<Button>("Scan").Content = "Stop scan";
            scanCancellation = new CancellationTokenSource(); var cancellation = scanCancellation;
            progressTimer.Start();
            try
            {
                var result = await Task.Run(() => scanner.Scan(path, cancellation.Token, p => latestProgress = p, forceFull));
                if (closing) return;
                progressTimer.Stop(); latestProgress = null; scanClock.Stop();
                Result = result; SetSummary(result.Files.Count, result.FolderCount, result.TotalBytes, result.LargestBytes);
                SetText("LargestName", result.Files.Count == 0 ? "No files found" : result.Files[0].Name);
                Get<Button>("Coverage").Content = result.Issues.Count > 0 ? result.Issues.Count.ToString("N0") + " scan notes  ↗" : "Scan details  ↗";
                await ApplyFiltersAsync(true);
            }
            catch (Exception ex) { if (!closing) { grid.ItemsSource = null; VisibleFiles.Clear(); SetEmpty("This location couldn't be scanned.", ex.Message); SetStatus("Scan failed · choose another location or try again"); } }
            finally
            {
                progressTimer.Stop(); scanClock.Stop(); latestProgress = null; scanning = false; if (scanCancellation == cancellation) scanCancellation = null; cancellation.Dispose();
                if (!closing) { SetBusy(false); Get<Button>("Scan").Content = "Scan again  →"; UpdateSelection(); LoadDrives(); }
            }
        }
        private void SetBusy(bool busy)
        {
            Get<StackPanel>("DriveList").IsEnabled = !busy; Get<Button>("Browse").IsEnabled = !busy; Get<Button>("RefreshDrives").IsEnabled = !busy;
            Get<Grid>("Filters").IsEnabled = !busy; Get<Button>("Export").IsEnabled = !busy && VisibleFiles.Count > 0;
            Get<Button>("Suggestions").IsEnabled = !busy && Result != null; Get<Button>("FreshScan").IsEnabled = !busy && target != null;
            Get<Button>("Scan").IsEnabled = target != null && !recycling;
        }
        private void UpdateProgress()
        {
            var progress = latestProgress; latestProgress = null;
            if (!scanning) return;
            var status = progress ?? displayedProgress;
            if (status != null) SetStatus(status.Stage + " · " + status.FileCount.ToString("N0") + " files · " + Format.Duration(scanClock.Elapsed));
            if (progress == null) return;
            displayedProgress = progress;
            SetSummary(progress.FileCount, progress.FolderCount, progress.TotalBytes, progress.LargestBytes);
            Get<TextBlock>("Status").ToolTip = progress.CurrentPath;
            if (progress.Preview.Length > 0)
            {
                foreach (var entry in progress.Preview) entry.SizeFraction = progress.LargestBytes == 0 ? 0 : (double)entry.Size / progress.LargestBytes * 100;
                grid.ItemsSource = progress.Preview; Get<StackPanel>("EmptyState").Visibility = Visibility.Collapsed;
                SetText("LargestName", progress.Preview[0].Name);
            }
        }
        private void SetSummary(int count, int folders, long size, long largest)
        {
            SetText("TotalSize", Format.Bytes(size)); SetText("FileCount", count.ToString("N0")); SetText("LargestSize", Format.Bytes(largest)); SetText("FolderCount", folders.ToString("N0") + " folders explored");
        }
        private void SetEmpty(string title, string detail)
        {
            SetText("EmptyTitle", title); SetText("EmptyDetail", detail); Get<StackPanel>("EmptyState").Visibility = Visibility.Visible;
            Get<TextBlock>("EmptyFoot").Visibility = Visibility.Collapsed;
        }
        private void ScheduleFilter() { if (scanning || Result == null) return; searchTimer.Stop(); searchTimer.Start(); }
        private string Selected(string name) { var item = Get<ComboBox>(name).SelectedItem as ComboBoxItem; return item == null ? "" : item.Content.ToString(); }

        internal async Task ApplyFiltersAsync(bool finishingScan = false)
        {
            if (Result == null || (scanning && !finishingScan) || closing) return;
            int version = ++queryVersion;
            if (queryCancellation != null) queryCancellation.Cancel();
            var cancellation = new CancellationTokenSource(); queryCancellation = cancellation;
            var source = Result; var selected = grid.SelectedItem as FileEntry;
            string text = search.Text, kind = Selected("KindFilter"), sort = Selected("Sort");
            long[] thresholds = { 0, 100L * 1024 * 1024 + 1, 1024L * 1024 * 1024 + 1, 10L * 1024 * 1024 * 1024 + 1 };
            long minimum = thresholds[Math.Max(0, Get<ComboBox>("SizeFilter").SelectedIndex)];
            SetStatus("Updating results…");
            try
            {
                bool review = suggestionsOnly;
                var filtered = await Task.Run(() => FileQuery.Apply(source.Files, text, kind, minimum, sort, cancellation.Token, review));
                if (closing || version != queryVersion) return;
                VisibleFiles = filtered; grid.ItemsSource = VisibleFiles;
                if (selected != null && VisibleFiles.Contains(selected)) grid.SelectedItem = selected;
                Get<StackPanel>("EmptyState").Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (filtered.Count == 0) SetEmpty(review ? "No cleanup candidates found." : source.Files.Count == 0 ? "No files found." : "No matching files.", review ? "No older large personal files match these filters.\nWindows and app files are excluded." : source.Files.Count == 0 ? "Try a different location, or review the scan notes." : "Try a different search, file type, or size filter.");
                Get<Button>("Export").IsEnabled = filtered.Count > 0;
                SetStatus((source.Cancelled ? "Stopped · partial results" : source.Method) + " · " + filtered.Count.ToString("N0") + " of " + source.Files.Count.ToString("N0") + " files · " + Format.Duration(source.Elapsed));
                Get<TextBlock>("Status").ToolTip = source.Root + (source.Issues.Count > 0 ? " · " + source.Issues.Count + " skipped or inaccessible locations; see scan details." : "");
                UpdateSelection();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closing) SetStatus("Could not filter files: " + ex.Message); }
            finally { if (queryCancellation == cancellation) queryCancellation = null; cancellation.Dispose(); }
        }

        private void UpdateSelection()
        {
            var file = grid.SelectedItem as FileEntry; bool enabled = file != null && Result != null && !scanning && !recycling;
            Get<Button>("CopyPath").IsEnabled = file != null && !recycling; Get<Button>("Reveal").IsEnabled = file != null && !recycling;
            string reason = file == null ? "" : FileSafety.BlockReason(file);
            Get<Button>("Recycle").IsEnabled = enabled && reason == null; Get<Button>("Recycle").ToolTip = reason ?? "Move this file to the Windows Recycle Bin";
            SetText("SelectionName", file == null ? "Select a file to take a closer look" : file.Name);
            SetText("SelectionDetail", file == null ? "Double-click a file to open its folder." : reason ?? CleanupAdvisor.Reason(file, DateTime.UtcNow) ?? (file.SizeText + " · " + file.FullPath));
            Get<TextBlock>("SelectionDetail").ToolTip = file == null ? null : (reason ?? CleanupAdvisor.Reason(file, DateTime.UtcNow) ?? "") + "\n" + file.FullPath;
        }
        private void RevealSelected()
        {
            var file = grid.SelectedItem as FileEntry; if (file == null || recycling) return;
            Guard(delegate { RevealAction(file); });
        }
        private void UpdateMode()
        {
            Get<Button>("Suggestions").Content = suggestionsOnly ? "← All files" : "Review cleanup";
            Get<Button>("Suggestions").Background = Brush(suggestionsOnly ? "#34432B" : "#222C36");
            SetText("MainHeading", suggestionsOnly ? "Back up. Review. Make room." : "Your space, sorted.");
            SetText("MainSubtitle", suggestionsOnly ? "Candidates based on size and age. Backup status is not verified." : "All accessible files, largest first. Double-click to open a folder.");
        }
        private async Task ExportAsync()
        {
            if (VisibleFiles.Count == 0 || scanning) return;
            var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "Biggofiles-" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv", AddExtension = true };
            if (dialog.ShowDialog(Window) != true) return;
            var files = VisibleFiles; Get<Button>("Export").IsEnabled = false;
            try { await Task.Run(() => CsvExport.Write(dialog.FileName, files)); if (!scanning) SetStatus("Exported " + files.Count.ToString("N0") + " files"); }
            catch (Exception ex) { ShowMessage("Export couldn't finish", ex.Message); }
            finally { Get<Button>("Export").IsEnabled = !scanning && VisibleFiles.Count > 0; }
        }

        private async Task RecycleSelectedAsync()
        {
            var file = grid.SelectedItem as FileEntry; if (file == null || Result == null || scanning || recycling) return;
            string reason = FileSafety.BlockReason(file); if (reason != null) { ShowMessage("This file needs extra care", reason); return; }
            string suggestion = CleanupAdvisor.Reason(file, DateTime.UtcNow);
            if (suggestion != null && !ShowMessage("Verify your backup first", suggestion + "\n\nBiggofiles cannot verify a backup or know whether you still need this file. Continue only after checking a separate backup copy.", "I've verified a backup")) return;
            if (!ShowMessage("Move this file to the Recycle Bin?", file.Name + "\n" + file.FullPath + "\n\n" + file.SizeText + " · Review the file before removing it. You can restore it from the Recycle Bin.\n\nSpace is only released after the Recycle Bin is emptied. Biggofiles never empties it for you.", "Move to Recycle Bin")) return;
            recycling = true; SetBusy(true); UpdateSelection(); SetStatus("Moving file to the Recycle Bin…");
            try
            {
                IntPtr owner = new WindowInteropHelper(Window).Handle;
                await RecycleService.RecycleAsync(file, owner);
                var source = Result;
                var updated = await Task.Run(() =>
                {
                    var result = new ScanResult { Root = source.Root, Cancelled = source.Cancelled, FolderCount = source.FolderCount, Elapsed = source.Elapsed, Method = source.Method, IndexNote = source.IndexNote, FoldersRead = source.FoldersRead, ReusedFolders = source.ReusedFolders };
                    result.Issues.AddRange(source.Issues);
                    foreach (var entry in source.Files) if (!Object.ReferenceEquals(entry, file)) { result.Files.Add(entry); result.TotalBytes += entry.Size; }
                    FileQuery.SortLargest(result.Files); return result;
                });
                Result = updated; SetSummary(updated.Files.Count, updated.FolderCount, updated.TotalBytes, updated.LargestBytes); SetText("LargestName", updated.Files.Count > 0 ? updated.Files[0].Name : "No files found");
                await ApplyFiltersAsync(); SetStatus("Moved to Recycle Bin · restore there if needed · empty it yourself to reclaim space");
            }
            catch (Exception ex) { SetStatus("File was not recycled · scan again to refresh"); ShowMessage("The file wasn't recycled", ex.Message); }
            finally { recycling = false; SetBusy(false); UpdateSelection(); }
        }

        private void ShowAbout()
        {
            var dialog = CreateDialog("Small app. More room.", 540);
            var body = (StackPanel)dialog.Content;
            body.Children.Add(new TextBlock { Text = "Biggofiles  /  v1.0", Foreground = Brush("#D5F58A"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
            body.Children.Add(new TextBlock { Text = "Made by OG STUDIO.\n\nA lightweight way to find your largest files and make room for what matters. Built for Windows. Free and open source under the MIT License.\n\nYour scan stays on this computer. No accounts, analytics, uploads, or background services.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A7B5C1"), LineHeight = 22 });
            var links = new WrapPanel { Margin = new Thickness(0, 24, 0, 20) };
            var site = new Button { Content = "OG STUDIO  ↗", Margin = new Thickness(0, 0, 8, 0) }; site.Click += delegate { OpenLink("https://ogstudioapps.com/bf/"); };
            var privacy = new Button { Content = "Privacy policy  ↗" }; privacy.Click += delegate { OpenLink("https://ogstudioapps.com/bf/pp.html"); };
            links.Children.Add(site); links.Children.Add(privacy); body.Children.Add(links);
            body.Children.Add(new TextBlock { Text = "© 2026 OG STUDIO  ·  Made with purpose.", Foreground = Brush("#788F7B"), FontSize = 11 });
            AddClose(body, dialog); dialog.ShowDialog();
        }
        private void OpenLink(string url) { Guard(delegate { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }); }
        private void ShowSizeInfo()
        {
            ShowMessage("Why the storage totals differ", "The drive card shows capacity and free space reported by Windows. Capacity minus free space is the space in use.\n\nListed file size adds the sizes of files discovered by this scan. It is not total drive usage or the amount you would recover by deleting them.\n\nProtected or inaccessible folders, restore-point storage, filesystem metadata, and extra data streams are not fully counted. Scan notes show locations that could not be listed.\n\nCompression, sparse or cloud-only files, shared hard links, and allocation rounding also make file sizes differ from space used on disk. Both numbers can change while apps are running.\n\nA difference between these totals is not a measured amount of missing files.");
        }
        private void ShowCoverage()
        {
            var text = new StringBuilder();
            if (Result != null) text.AppendLine("Location: " + Result.Root + "\n" + (Result.Cancelled ? "Stopped early: these results are incomplete." : Result.Method + " finished.") + "\n" + Result.Files.Count.ToString("N0") + " files · " + Result.FolderCount.ToString("N0") + " folders · " + Result.Issues.Count.ToString("N0") + " scan notes\n" + Result.FoldersRead.ToString("N0") + " folders read; " + Result.ReusedFolders.ToString("N0") + " reused.\n" + Result.IndexNote + "\n");
            text.AppendLine("Hidden and system files are included. Files are listed by their logical size, using 1 KB = 1,024 bytes. No file contents are opened.\n");
            text.AppendLine("Windows may deny access to protected folders and file-system metadata. Directory links and mounted folders are not followed, so a scan cannot loop or wander onto another drive. Cloud placeholder metadata is included when available.\n");
            text.AppendLine("Listed file size is not drive usage or reclaimable space. Protected storage, restore points, filesystem metadata, allocation rounding, compressed and sparse files, cloud-only files, and hard links can make these totals differ. Hard-link paths appear separately. Alternate data streams and NTFS internal metadata are not measured. Files may change after scanning.\n");
            if (Result != null && Result.Issues.Count > 0)
            {
                text.AppendLine("SCAN NOTES" + (Result.Issues.Count > 200 ? " (first 200; export for the full list)" : ""));
                foreach (var issue in Result.Issues.Take(200)) text.AppendLine(issue.Path + "\n" + issue.Reason + "\n");
            }
            var dialog = CreateDialog("Scan coverage", 700); var body = (StackPanel)dialog.Content;
            body.Children.Add(new TextBox { Text = text.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 365, FontSize = 12 });
            if (Result != null && Result.Issues.Count > 0)
            {
                var export = new Button { Content = "Save all scan notes", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 15, 0, 0) };
                export.Click += delegate
                {
                    var save = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt", FileName = "Biggofiles-scan-notes.txt" };
                    if (save.ShowDialog(dialog) == true) Guard(delegate { File.WriteAllLines(save.FileName, Result.Issues.Select(i => i.Path + "\r\n" + i.Reason + "\r\n"), Encoding.UTF8); });
                }; body.Children.Add(export);
            }
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                var admin = new Button { Content = "Open an administrator instance", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), ToolTip = "Windows will ask permission. Administrator access can expose more folders, but cannot guarantee a complete scan." };
                admin.Click += delegate { Guard(delegate { Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location) { UseShellExecute = true, Verb = "runas" }); }); };
                body.Children.Add(admin);
            }
            AddClose(body, dialog); dialog.ShowDialog();
        }

        private Window CreateDialog(string title, double width)
        {
            var dialog = new Window { Title = "Biggofiles", Owner = Window, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#192129"), Foreground = Brush("#EDF2F4"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13, ShowInTaskbar = false, Resources = Window.Resources };
            dialog.SourceInitialized += delegate { SetTitleTheme(dialog, !theme.IsLight); };
            var body = new StackPanel { Margin = new Thickness(28) }; body.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 19), TextWrapping = TextWrapping.Wrap }); dialog.Content = body; return dialog;
        }
        private void AddClose(StackPanel body, Window dialog) { var button = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 85, Margin = new Thickness(0, 22, 0, 0) }; button.Click += delegate { dialog.Close(); }; body.Children.Add(button); }
        internal bool ShowMessage(string title, string message, string confirm = null)
        {
            var dialog = CreateDialog(title, 560); var body = (StackPanel)dialog.Content;
            body.Children.Add(new ScrollViewer { MaxHeight = Math.Max(160, Math.Min(420, SystemParameters.WorkArea.Height - 230)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A9BAC6"), LineHeight = 22 } });
            if (confirm == null) AddClose(body, dialog);
            else
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
                var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) }; cancel.Click += delegate { dialog.DialogResult = false; };
                var yes = new Button { Content = confirm, Style = (Style)Window.Resources["Primary"] }; yes.Click += delegate { dialog.DialogResult = true; };
                buttons.Children.Add(cancel); buttons.Children.Add(yes); body.Children.Add(buttons);
            }
            return dialog.ShowDialog() == true;
        }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { ShowMessage("Couldn't complete that action", ex.Message); } }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private static void SetTitleTheme(Window window, bool dark) { try { int enabled = dark ? 1 : 0; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, 4); } catch (DllNotFoundException) { } }
    }

    internal static class Program
    {
        [STAThread] public static int Main(string[] args)
        {
            try
            {
                var app = new Application(); var controller = new MainController();
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { e.Handled = true; controller.ShowMessage("Something went wrong", e.Exception.Message + "\n\nTry scanning again."); };
                return app.Run(controller.Window);
            }
            catch (Exception ex) { MessageBox.Show(ex.ToString(), "Biggofiles could not start", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        }
    }
}

