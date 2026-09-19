using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// WHICH ANALYSIS A PAGE BELONGS TO. MEASURED (campaign 7, M106): every page of horizun_cad_networks re-read
    /// the drawing and re-ran the whole analysis - 1.5-2.2 s a page, 30 pages for one listing - to return four rows
    /// of a result that had not changed. A page that names the fingerprint of the analysis it continues can be cut
    /// from that analysis instead, and only then: the key is the document, the CAD instance, where it is placed, the
    /// bytes of its file and every argument except the paging ones. Any change to a document drops every entry
    /// (QueryCacheLifecycle), so a repointed or reloaded link is read again - and refused, if it now differs.
    /// </summary>
    public static class CadNetworkCache
    {
        /// <summary>
        /// The kept analyses. Their validity rides on QueryCacheLifecycle's epoch, which every document change,
        /// open, close, save, synchronisation and view activation advances: the key carries it, so an analysis made
        /// before any of those can never be found again.
        /// </summary>
        public static readonly BoundedReadCache Kept = new BoundedReadCache(8, 16 * 1024 * 1024, TimeSpan.FromMinutes(10));

        /// <summary>The arguments that choose a PAGE of an analysis, not the analysis.</summary>
        public static readonly string[] PagingKeys =
            { "page_offset", "page_limit", "lists", "expect_analysis_fingerprint", "idempotency_key" };

        public static string Key(string documentPath, string documentTitle, long instanceId, string transformFingerprint,
                                 string fileSha256, JObject request, long modelEpoch = 0)
        {
            if (request == null || string.IsNullOrEmpty(fileSha256) || string.IsNullOrEmpty(transformFingerprint)) return null;
            var analysis = (JObject)request.DeepClone();
            foreach (string k in PagingKeys) analysis.Remove(k);
            const char sep = '\u001f';
            string raw = string.Join(sep.ToString(), new[]
            {
                documentPath ?? "", documentTitle ?? "", instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                transformFingerprint, fileSha256, modelEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                analysis.ToString(Formatting.None)
            });
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return "netc:" + BitConverter.ToString(h, 0, 16).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>What a reply served from an earlier analysis says about itself.</summary>
        public static JObject HitBlock(string key) => new JObject
        {
            ["state"] = "hit",
            ["key"] = key,
            ["means"] = "this page was cut from the analysis whose fingerprint the request named - the same reading, not " +
                        "a new one. A model change of any kind drops it; a page without expect_analysis_fingerprint is " +
                        "always read afresh."
        };
    }
}
