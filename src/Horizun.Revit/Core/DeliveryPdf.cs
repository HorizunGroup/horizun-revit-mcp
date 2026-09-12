using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UglyToad.PdfPig;
namespace Horizun.Revit.Core
{
    public static class DeliveryPdf
    {
        public static void WriteManifestVerified(string path, JObject manifest, bool overwrite)
        {
            // Compare the actual serialized document, not a reparsed JToken:
            // Json.NET otherwise promotes ISO date strings to Date tokens.
            string expected = manifest.ToString(Newtonsoft.Json.Formatting.Indented);
            using (var writer = new StreamWriter(new FileStream(path,
                overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write),
                new System.Text.UTF8Encoding(false)))
                writer.Write(expected);
            if (!string.Equals(expected, File.ReadAllText(path), StringComparison.Ordinal))
                throw new IOException("Manifest reread mismatch.");
        }
        public static string[] Paths(string output, bool combine, IEnumerable<long> ids)
        {
            long[] list=ids.ToArray();
            if (list.Length==0 || list.Distinct().Count()!=list.Length) throw new ArgumentException("PDF view IDs must be nonempty and unique.");
            return combine ? new[]{output} : list.Select((id,i)=>Path.Combine(Path.GetDirectoryName(output),
                Path.GetFileNameWithoutExtension(output)+"-"+(i+1).ToString("D3")+"-"+id+".pdf")).ToArray();
        }
        public static JObject Inspect(string path, int expectedPages)
        {
            using(var stream=File.OpenRead(path))
            using(var sha=SHA256.Create())
            using(var pdf=PdfDocument.Open(path))
            {
                var pages=new JArray();
                foreach(var page in pdf.GetPages())
                    pages.Add(new JObject { ["page"]=page.Number,["width_points"]=page.Width,["height_points"]=page.Height });
                return new JObject { ["path"]=path,["bytes"]=stream.Length,
                    ["sha256"]=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant(),
                    ["pages"]=pdf.NumberOfPages,["expected_pages"]=expectedPages,["page_count_verified"]=pdf.NumberOfPages==expectedPages,
                    ["page_geometry"]=pages,["visual_approval"]="not_performed" };
            }
        }
    }
}
