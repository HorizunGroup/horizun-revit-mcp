using System;
using System.IO;

namespace Horizun.Revit.Core
{
    public static class DwgCompanionRules
    {
        public static string Kind(string drawing, string candidate)
        {
            if (string.IsNullOrWhiteSpace(drawing) || string.IsNullOrWhiteSpace(candidate)) return null;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(drawing)),
                Path.GetDirectoryName(Path.GetFullPath(candidate)), StringComparison.OrdinalIgnoreCase)) return null;
            string root = Path.GetFileNameWithoutExtension(drawing);
            string stem = Path.GetFileNameWithoutExtension(candidate);
            if (!stem.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !stem.StartsWith(root + "-", StringComparison.OrdinalIgnoreCase) &&
                !stem.StartsWith(root + "_", StringComparison.OrdinalIgnoreCase)) return null;
            string ext = Path.GetExtension(candidate).ToLowerInvariant();
            if (ext == ".dwg" && !stem.Equals(root, StringComparison.OrdinalIgnoreCase)) return "xref";
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" ||
                ext == ".tif" || ext == ".tiff" ? "image" : null;
        }

        public static bool ValidImageHeader(string path, byte[] head)
        {
            if (head == null) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png") return head.Length >= 8 && head[0] == 137 && head[1] == 80 &&
                head[2] == 78 && head[3] == 71 && head[4] == 13 && head[5] == 10 && head[6] == 26 && head[7] == 10;
            if (ext == ".jpg" || ext == ".jpeg") return head.Length >= 3 && head[0] == 255 && head[1] == 216 && head[2] == 255;
            if (ext == ".bmp") return head.Length >= 2 && head[0] == 66 && head[1] == 77;
            if (ext == ".tif" || ext == ".tiff") return head.Length >= 4 &&
                ((head[0] == 73 && head[1] == 73 && head[2] == 42 && head[3] == 0) ||
                 (head[0] == 77 && head[1] == 77 && head[2] == 0 && head[3] == 42));
            return false;
        }
    }
}
