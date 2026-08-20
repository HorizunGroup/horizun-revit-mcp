using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Makes file-backed async results durable before the terminal job event is
    /// appended. The MCP server may restart and the original Revit temp capture
    /// may be cleaned before anybody polls; a job result cannot depend on that race.
    /// </summary>
    public static class AsyncResultPayload
    {
        public const long MaxImageBytes = 16L * 1024 * 1024;

        public static string Serialize(object data, string jobId)
        {
            JToken token = data == null ? JValue.CreateNull() : JToken.FromObject(data);
            if (token is JObject obj && obj["image_path"]?.Type == JTokenType.String)
                obj["image_path"] = PreserveImage((string)obj["image_path"], jobId);
            return token.ToString(Formatting.None);
        }

        private static string PreserveImage(string sourcePath, string jobId)
        {
            if (!IsSafeJobId(jobId))
                throw new InvalidDataException("async capture has an invalid durable job id");
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                !string.Equals(Path.GetExtension(sourcePath), ".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("async capture did not return a PNG path");

            string directory = Path.Combine(HorizunPaths.JobsDir(), "attachments");
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, jobId + ".png");
            string temp = Path.Combine(directory, "." + jobId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read | FileShare.Delete))
                {
                    long length = input.Length;
                    if (length <= 0 || length > MaxImageBytes)
                        throw new InvalidDataException("async capture is empty or exceeds the 16 MiB durable limit");
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long remaining = length;
                        while (remaining > 0)
                        {
                            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read == 0) throw new EndOfStreamException("async capture ended while it was copied");
                            output.Write(buffer, 0, read);
                            remaining -= read;
                        }
                        if (input.ReadByte() != -1 || input.Length != length)
                            throw new InvalidDataException("async capture changed while it was copied");
                        output.Flush(true);
                    }
                }

                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
                temp = null;
                return destination;
            }
            finally { if (temp != null) try { File.Delete(temp); } catch { } }
        }

        private static bool IsSafeJobId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
                value != Path.GetFileName(value)) return false;
            foreach (char c in value)
                if (!(char.IsLetterOrDigit(c) || c == '-')) return false;
            return true;
        }
    }
}
