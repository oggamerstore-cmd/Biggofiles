using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Biggofiles
{
    public static class FileSafety
    {
        private static readonly string[] ProtectedFolders = { "Windows", "Windows.old", "WINNT", "Program Files", "Program Files (x86)", "ProgramData", "Boot", "EFI", "Recovery", "$Recycle.Bin", "System Volume Information", "$Extend", "$Windows.~BT", "$Windows.~WS", "Config.Msi", "MSOCache", "WindowsApps", "WpSystem", "WUDownloadCache" };
        private static readonly string[] ProtectedNames = { "pagefile.sys", "swapfile.sys", "hiberfil.sys", "bootmgr", "bootmgr.efi", "bootnxt", "bootsect.bak", "ntldr", "ntdetect.com", "boot.ini", "BCD", "$MFT", "$MFTMirr", "$LogFile", "$Bitmap", "$Boot", "$Secure", "$UpCase", "$Volume", "$AttrDef" };
        private static readonly string[] ProtectedRoots = { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        [DllImport("sfc.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SfcIsFileProtected(IntPtr rpc, string path);
        public static string BlockReason(FileEntry entry)
        {
            if ((entry.Attributes & 0x4) != 0) return "System file · review in Explorer; recycling is disabled here.";
            if ((entry.Attributes & 0x400) != 0) return "Link or cloud placeholder · manage this item in Explorer.";
            string path = entry.FullPath;
            string relative = path.Substring(Path.GetPathRoot(path).Length);
            foreach (string folder in ProtectedFolders) if (relative.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) return "Protected Windows, recovery, or application storage · deletion disabled.";
            if (path.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("\\Application Data\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Protected profile and application data · deletion disabled.";
            foreach (string name in ProtectedNames) if (String.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return "Protected Windows or boot file · deletion disabled.";
            if (entry.Name.StartsWith("ntuser.", StringComparison.OrdinalIgnoreCase) || entry.Name.StartsWith("usrclass.dat", StringComparison.OrdinalIgnoreCase)) return "Protected user registry · deletion disabled.";
            if (entry.Extension == ".sys" || entry.Extension == ".dll" || entry.Extension == ".drv" || entry.Extension == ".ocx" || entry.Extension == ".efi") return "Protected system or application component · deletion disabled.";
            foreach (string reserved in ProtectedRoots) if (IsWithin(path, reserved)) return "Protected Windows or application storage · deletion disabled.";
            return null;
        }
        private static bool IsWithin(string path, string parent) { return !String.IsNullOrEmpty(parent) && path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        public static void ValidateUnchanged(FileEntry original)
        {
            var current = NativeFiles.ReadEntry(original.FullPath);
            string reason = BlockReason(current); if (reason != null) throw new IOException(reason);
            string resolved = NativeFiles.ResolvedPath(original.FullPath);
            reason = BlockReason(new FileEntry(Path.GetDirectoryName(resolved), Path.GetFileName(resolved), current.Size, current.ModifiedUtcTicks, current.Attributes));
            if (reason != null) throw new IOException(reason);
            if (SfcIsFileProtected(IntPtr.Zero, resolved)) throw new IOException("Windows Resource Protection identifies this as a required system file. Deletion is disabled.");
            using (var handle = BulkDirectory.Open(resolved, 0))
            {
                if (handle.IsInvalid) throw new IOException("Could not verify this file's protection status.");
                if (!original.Identity.IsEmpty)
                {
                    ulong serial; var identity = BulkDirectory.Identify(handle, out serial);
                    if (!identity.Equals(original.Identity)) throw new IOException("This file was replaced since the scan. Scan again before recycling it.");
                }
                IntPtr info = Marshal.AllocHGlobal(24);
                try
                {
                    if (!BulkDirectory.GetFileInformationByHandleEx(handle, 1, info, 24)) throw new IOException("Could not verify this file's link count. Manage the file in Explorer.");
                    if (Marshal.ReadInt32(info, 16) > 1) throw new IOException("This file has multiple hard links and may be shared with Windows or an application. Deletion is disabled.");
                }
                finally { Marshal.FreeHGlobal(info); }
            }
            if (current.Size != original.Size || current.ModifiedUtcTicks != original.ModifiedUtcTicks || current.Attributes != original.Attributes)
                throw new IOException("This file changed since the scan. Scan again and review the updated file before recycling it.");
        }
    }

    internal static class ExplorerService
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] private static extern void SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint mask, out uint attributes);
        [DllImport("shell32.dll", PreserveSig = false)] private static extern void SHOpenFolderAndSelectItems(IntPtr folder, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] children, uint flags);
        [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
        internal static void Reveal(FileEntry file)
        {
            if (NativeFiles.GetFileAttributesW(NativeFiles.Extended(file.FullPath)) == UInt32.MaxValue)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.DirectoryPath) { UseShellExecute = true });
                return;
            }
            IntPtr folder = IntPtr.Zero, item = IntPtr.Zero;
            try
            {
                uint attributes;
                SHParseDisplayName(file.DirectoryPath, IntPtr.Zero, out folder, 0, out attributes);
                SHParseDisplayName(file.FullPath, IntPtr.Zero, out item, 0, out attributes);
                SHOpenFolderAndSelectItems(folder, 1, new[] { ILFindLastID(item) }, 0);
            }
            finally { if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item); if (folder != IntPtr.Zero) Marshal.FreeCoTaskMem(folder); }
        }
    }

    internal static class FolderPicker
    {
        internal static string Pick(IntPtr owner)
        {
            var dialog = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")));
            IShellItem item = null;
            try
            {
                uint options; dialog.GetOptions(out options); dialog.SetOptions(options | 0x20 | 0x40 | 0x800 | 0x02000000);
                dialog.SetTitle("Choose a folder to scan"); dialog.SetOkButtonLabel("Select folder");
                int hr = dialog.Show(owner); if (hr == unchecked((int)0x800704C7)) return null; Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out item); string path; item.GetDisplayName(0x80058000, out path); return path;
            }
            finally { if (item != null) Marshal.ReleaseComObject(item); Marshal.ReleaseComObject(dialog); }
        }
    }

    internal static class RecycleService
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        internal static Task RecycleAsync(FileEntry file, IntPtr owner)
        {
            var completion = new TaskCompletionSource<bool>();
            var thread = new Thread(delegate()
            {
                try { Recycle(file, owner); completion.SetResult(true); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
        }
        internal static void Recycle(FileEntry file, IntPtr owner)
        {
            FileSafety.ValidateUnchanged(file);
            IFileOperation operation = null; IShellItem item = null;
            var sink = new RecycleGuard(file);
            try
            {
                Guid iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(file.FullPath, IntPtr.Zero, ref iid, out item);
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
                // Recycle, record undo, fail early. The app already obtained confirmation.
                // Keep native permanent-delete warnings; the sink also vetoes such callbacks.
                operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00100000 | 0x00004000 | 0x00000010 | 0x00000400 | 0x00000004);
                operation.SetOwnerWindow(owner);
                operation.DeleteItem(item, sink);
                try { operation.PerformOperations(); }
                catch (COMException)
                {
                    if (sink.ValidationFailure != null) throw new IOException(sink.ValidationFailure);
                    if (sink.PermanentDeleteBlocked) throw new IOException("Windows did not offer recycling for this file. The operation was blocked to prevent permanent deletion.");
                    throw;
                }
                bool aborted; operation.GetAnyOperationsAborted(out aborted);
                if (sink.ValidationFailure != null) throw new IOException(sink.ValidationFailure);
                if (sink.PermanentDeleteBlocked) throw new IOException("Windows could not recycle this file. Permanent deletion was blocked. Manage the file in Explorer if you want to proceed.");
                if (aborted || !sink.Succeeded) throw new IOException("The operation was cancelled or Windows could not move this file to the Recycle Bin. The file may be in use, protected, or on a drive without a Recycle Bin.");
            }
            finally { if (item != null) Marshal.ReleaseComObject(item); if (operation != null) Marshal.ReleaseComObject(operation); GC.KeepAlive(sink); }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class RecycleGuard : IFileOperationProgressSink
    {
        internal bool Succeeded, PermanentDeleteBlocked;
        internal string ValidationFailure;
        private readonly FileEntry expected;
        internal RecycleGuard(FileEntry expected = null) { this.expected = expected; }
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            if ((flags & 0x80) == 0) { PermanentDeleteBlocked = true; return unchecked((int)0x80004004); }
            // Recheck at the Shell's final callback as well as before scheduling.
            try { if (expected != null) FileSafety.ValidateUnchanged(expected); return 0; }
            catch (Exception ex) { ValidationFailure = ex.Message; return unchecked((int)0x80004004); }
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created) { Succeeded = result >= 0 && created != null; return 0; }
        public int StartOperations() { return 0; }
        public int FinishOperations(int result) { return 0; }
        public int PreRenameItem(uint flags, IShellItem item, string name) { return 0; }
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem created) { return 0; }
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) { return 0; }
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem created) { return 0; }
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) { return 0; }
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem created) { return 0; }
        public int PreNewItem(uint flags, IShellItem destination, string name) { return 0; }
        public int PostNewItem(uint flags, IShellItem destination, string name, string template, uint attributes, int result, IShellItem created) { return 0; }
        public int UpdateProgress(uint total, uint done) { return 0; }
        public int ResetTimer() { return 0; }
        public int PauseTimer() { return 0; }
        public int ResumeTimer() { return 0; }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem item, uint hint, out int order);
    }
    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index); void GetFileTypeIndex(out uint index);
        void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie);
        void SetOptions(uint options); void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder); void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder); void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title); void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item); void AddPlace(IShellItem item, int alignment); void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result); void SetClientGuid(ref Guid guid); void ClearClientData(); void SetFilter(IntPtr filter);
        void GetResults(out IntPtr results); void GetSelectedItems(out IntPtr results);
    }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie); void Unadvise(uint cookie);
        void SetOperationFlags(uint flags); void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item); void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink); void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink); void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink); void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink); void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
        void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations(); [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem created);
        [PreserveSig] int UpdateProgress(uint total, uint done); [PreserveSig] int ResetTimer(); [PreserveSig] int PauseTimer(); [PreserveSig] int ResumeTimer();
    }
}
