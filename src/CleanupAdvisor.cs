using System;

namespace Biggofiles
{
    public static class CleanupAdvisor
    {
        public static string Reason(FileEntry file, DateTime utcNow)
        {
            if (FileSafety.BlockReason(file) != null || file.ModifiedUtcTicks == 0 || (file.Attributes & (0x1000 | 0x400000 | 0x40000)) != 0) return null;
            double days = (utcNow - new DateTime(file.ModifiedUtcTicks, DateTimeKind.Utc)).TotalDays;
            bool download = (file.DirectoryPath.TrimEnd('\\') + "\\").IndexOf("\\Downloads\\", StringComparison.OrdinalIgnoreCase) >= 0;
            if (download && days >= 30 && file.Size >= 25L * 1024 * 1024 && (file.Extension == ".exe" || file.Extension == ".msi" || file.Extension == ".msix"))
                return "Downloaded installer, unchanged for 30+ days. Confirm installation is complete and keep a verified backup before removal.";
            if (file.Kind == "Archives" && days >= 90 && file.Size >= 100L * 1024 * 1024)
                return "Large archive or disk image, unchanged for 90+ days. Verify its contents exist in a separate, readable backup before removal.";
            if ((file.Kind == "Video" || file.Kind == "Audio" || file.Kind == "Images") && days >= 180 && file.Size >= 250L * 1024 * 1024)
                return "Large media file, unchanged for 180+ days. Copy it to a separate backup and verify the copy before deciding to remove it.";
            if (download && days >= 180 && file.Size >= 100L * 1024 * 1024 && file.Kind == "Documents")
                return "Large older download. Review its purpose and verify a separate backup before removal.";
            return null;
        }
    }
}
