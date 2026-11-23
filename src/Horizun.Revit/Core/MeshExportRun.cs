using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class MeshExportRun
    {
        internal delegate string MeshPayload(Stream destination, out JObject summary);
        private const string Scope = "horizun_export:mesh_json";

        internal static CommandResult Run(JObject request, string output, JObject options, string document,
            ConfirmationStore confirmations, MeshPayload payload, Func<string, string> readHash)
        {
            var notes = ReliabilityDefaults.Mesh(request).ToArray();
            bool dry = request.Value<bool?>("dry_run") ?? true;
            var result = new JObject { ["format"] = "mesh_json", ["dry_run"] = dry, ["output_path"] = output,
                ["write_started"] = false, ["units"] = "metres", ["coordinate_system"] = "host_internal" };
            CommandResult Failure(string message) => AssumptionNotes.MarkFailure(
                CommandResult.FailWithDetail(message, result), "SUPUESTO used; ", notes);

            string expectedHash;
            JObject summary;
            try { expectedHash = payload(Stream.Null, out summary); }
            catch (Exception ex) { return Failure("Could not read mesh geometry: " + ex.Message); }
            result["geometry"] = summary;
            result["geometry_sha256"] = expectedHash;
            var bound = new JObject { ["request"] = options, ["geometry_sha256"] = expectedHash };
            string hash = ConfirmationStore.PlanHash(bound, "request", "geometry_sha256");
            if (dry)
            {
                result["confirmation_token"] = confirmations.Issue(Scope, document, hash).Token;
                return AssumptionNotes.StampResult(CommandResult.Ok(result), notes);
            }
            if (string.IsNullOrWhiteSpace(request.Value<string>("idempotency_key")))
                return Failure("mesh_json apply requires a new idempotency_key.");
            var confirmation = confirmations.Validate(request.Value<string>("confirmation_token"), Scope, document, hash);
            if (!confirmation.Ok) return Failure(confirmation.Message);

            string temporary = output + ".horizun-" + Guid.NewGuid().ToString("N") + ".tmp";
            bool moved = false;
            try
            {
                string written;
                result["write_started"] = true;
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    written = payload(stream, out _);
                if (written != expectedHash)
                    throw new IOException("Geometry differed from the confirmed mesh.");
                string error = ExportDeliveryRules.VerifyMeshFile(temporary, summary.Value<long>("triangles"), expectedHash);
                if (error != null) throw new IOException(error);
                File.Move(temporary, output);
                moved = true;
                bool verified = readHash(output) == expectedHash;
                result["file_verified"] = verified;
                result["bytes"] = new FileInfo(output).Length;
                if (!verified) return Failure("Mesh file exists but its final hash differs; inspect it.");
                return AssumptionNotes.StampResult(CommandResult.Ok(result), notes);
            }
            catch (Exception ex)
            {
                result["file_moved"] = moved;
                result["temporary_path"] = temporary;
                return Failure("mesh_json failed: " + ex.Message + ". Any new file is named in the evidence; no existing file was replaced.");
            }
        }
    }
}
