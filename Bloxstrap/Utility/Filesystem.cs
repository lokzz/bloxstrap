using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Bloxstrap.Utility
{
    internal static class Filesystem
    {
        internal static long GetFreeDiskSpace(string p)
        {
            try {
                if (!Path.IsPathRooted(p) || !Path.IsPathFullyQualified(p)) {
                    return -1;
                }
                var root = Path.GetPathRoot(p);
                if (root != null) {
                    var drive = new DriveInfo(root);
                    return drive.AvailableFreeSpace;
                } else {
                    throw new ArgumentException("Invalid path", nameof(p));
                }
            } catch (ArgumentException e) {
                App.Logger.WriteLine("Filesystem::BadPath", $"The path: {e} does not contain valid drive info.");
                return -1;
            }
        }

        internal static void AssertReadOnly(string filePath)
        {
            var fileInfo = new FileInfo(filePath);

            if (!fileInfo.Exists || !fileInfo.IsReadOnly)
                return;

            fileInfo.IsReadOnly = false;
            App.Logger.WriteLine("Filesystem::AssertReadOnly", $"The following file was set as read-only: {filePath}");
        }
    }
}
