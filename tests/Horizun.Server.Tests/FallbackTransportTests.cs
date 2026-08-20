// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE WHOLE HOP, as values rather than as source text.
//
// A structured signal that is dropped in serialization is indistinguishable from
// one that was never granted, and the client then silently stops falling back to
// Python - a failure that presents as "the bridge just says no" and gets
// diagnosed as a model problem. Asserting that PipeServer.cs *contains a line*
// would not catch it; building a CommandResult, putting it through the real
// envelope, and reading the real MCP result does.
//
// Both halves of the hop are the SHIPPED code: PipeEnvelope is what the add-in
// writes to the pipe, McpResult is what the server hands the client.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class FallbackTransportTests
    {
        /// <summary>The real hop: command result -> pipe envelope -> MCP tool result.</summary>
        private static JObject Hop(CommandResult result)
            => McpResult.FromPluginReply(PipeEnvelope.Of("req-1", result), null);

        private static CommandResult GrantedBatch() => FallbackDecision.Refuse(
            "1 element plan(s) are invalid. Nothing was created.",
            FallbackDecision.Decide(new[]
            {
                new ActionOutcome { Index = 0 },
                new ActionOutcome { Index = 1, Error = "unsupported kind 'sprinkler_head'",
                                    UnsupportedReason = FallbackSignal.ReasonUnsupportedKind }
            }, writeStarted: false));

        private static byte[] BuildPng(uint width, uint height, byte bitDepth,
            byte colorType, byte interlace, byte[] decoded,
            byte[] palette = null, string extraChunkType = null, byte[] extraChunkData = null,
            Tuple<string, byte[]>[] extraChunks = null)
        {
            using (var compressed = new MemoryStream())
            {
                using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true))
                    z.Write(decoded, 0, decoded.Length);
                using (var png = new MemoryStream())
                {
                    png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                    var ihdr = new byte[13];
                    WriteBe32(ihdr, 0, width); WriteBe32(ihdr, 4, height);
                    ihdr[8] = bitDepth; ihdr[9] = colorType; ihdr[12] = interlace;
                    WritePngChunk(png, "IHDR", ihdr);
                    if (palette != null) WritePngChunk(png, "PLTE", palette);
                    if (extraChunkType != null) WritePngChunk(png, extraChunkType, extraChunkData ?? Array.Empty<byte>());
                    if (extraChunks != null)
                        foreach (Tuple<string, byte[]> chunk in extraChunks)
                            WritePngChunk(png, chunk.Item1, chunk.Item2);
                    WritePngChunk(png, "IDAT", compressed.ToArray());
                    WritePngChunk(png, "IEND", Array.Empty<byte>());
                    return png.ToArray();
                }
            }
        }

        private static void WritePngChunk(Stream stream, string type, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            byte[] length = new byte[4]; WriteBe32(length, 0, (uint)data.Length);
            stream.Write(length, 0, length.Length);
            stream.Write(typeBytes, 0, typeBytes.Length);
            stream.Write(data, 0, data.Length);
            uint crc = 0xffffffff;
            foreach (byte value in typeBytes) crc = CrcStep(crc, value);
            foreach (byte value in data) crc = CrcStep(crc, value);
            byte[] crcBytes = new byte[4]; WriteBe32(crcBytes, 0, crc ^ 0xffffffff);
            stream.Write(crcBytes, 0, crcBytes.Length);
        }

        private static uint CrcStep(uint crc, byte value)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            return crc;
        }

        private static void WriteBe32(byte[] bytes, int at, uint value)
        {
            bytes[at] = (byte)(value >> 24); bytes[at + 1] = (byte)(value >> 16);
            bytes[at + 2] = (byte)(value >> 8); bytes[at + 3] = (byte)value;
        }

        private static byte[] Be32Values(params uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) WriteBe32(bytes, i * 4, values[i]);
            return bytes;
        }

        private static CommandResult MixedBatch() => FallbackDecision.Refuse(
            "2 element plan(s) are invalid. Nothing was created.",
            FallbackDecision.Decide(new[]
            {
                new ActionOutcome { Index = 1, Error = "unsupported kind 'sprinkler_head'",
                                    UnsupportedReason = FallbackSignal.ReasonUnsupportedKind },
                new ActionOutcome { Index = 2, Error = "profile needs at least three points" }
            }, writeStarted: false));

        // ---- the pipe half ------------------------------------------------------

        [Fact]
        public void The_envelope_carries_the_signal_beside_the_error()
        {
            JObject envelope = PipeEnvelope.Of("req-1", GrantedBatch());

            Assert.False((bool)envelope["success"]);
            Assert.NotNull(envelope["fallback"]);
            Assert.True((bool)envelope["fallback"]["allowed"]);
            Assert.False((bool)envelope["fallback"]["write_started"]);
            Assert.Equal("horizun_execute_python", (string)envelope["fallback"]["recommended_tool"]);
            Assert.NotNull(envelope["capability_gaps"]);
            // Structure beside the human message, never inside it.
            Assert.DoesNotContain("recommended_tool", (string)envelope["error"]);
        }

        [Fact]
        public void An_ordinary_failure_carries_no_fallback_on_the_wire()
        {
            JObject envelope = PipeEnvelope.Of("req-1", CommandResult.Fail("units must be mm, m or feet."));

            Assert.Null(envelope["fallback"]);
            Assert.Null(envelope["capability_gaps"]);
        }

        [Fact]
        public void A_successful_command_carries_no_fallback_either()
        {
            JObject envelope = PipeEnvelope.Of("req-1", CommandResult.Ok(new JObject { ["created"] = 3 }));

            Assert.True((bool)envelope["success"]);
            Assert.Null(envelope["fallback"]);
            Assert.Null(envelope["capability_gaps"]);
        }

        // ---- the whole hop ------------------------------------------------------

        [Fact]
        public void A_granted_fallback_reaches_structuredContent_intact()
        {
            JObject mcp = Hop(GrantedBatch());

            Assert.True((bool)mcp["isError"]);
            JObject structured = (JObject)mcp["structuredContent"];
            Assert.NotNull(structured);

            JObject fallback = (JObject)structured["fallback"];
            Assert.True((bool)fallback["allowed"]);
            Assert.Equal("unsupported_kind", (string)fallback["reason"]);
            Assert.False((bool)fallback["write_started"]);
            Assert.Equal("horizun_execute_python", (string)fallback["recommended_tool"]);

            // A client can decide on that field alone. The prose is for the human.
            Assert.Single((JArray)structured["capability_gaps"]);
            Assert.Equal(1, (int)structured["capability_gaps"][0]["index"]);
        }

        /// <summary>
        /// THE MIXED BATCH over the wire: the gaps must arrive, the permission must not.
        /// A client reading only `allowed` gets the right answer, and a client that wants
        /// detail can see exactly which action has no typed path.
        /// </summary>
        [Fact]
        public void A_mixed_batch_transports_the_gaps_without_transporting_permission()
        {
            JObject mcp = Hop(MixedBatch());

            JObject structured = (JObject)mcp["structuredContent"];
            Assert.False((bool)structured["fallback"]["allowed"]);
            Assert.Equal("mixed_capability_and_invalid_input", (string)structured["fallback"]["reason"]);

            JArray gaps = (JArray)structured["capability_gaps"];
            Assert.Single(gaps);
            Assert.Equal(1, (int)gaps[0]["index"]);
        }

        [Fact]
        public void A_write_that_may_have_started_transports_an_explicit_refusal()
        {
            CommandResult afterWrite = FallbackDecision.Refuse(
                "Committed, but verification failed.",
                FallbackDecision.Decide(new[]
                {
                    new ActionOutcome { Index = 0, Error = "unsupported kind 'x'",
                                        UnsupportedReason = FallbackSignal.ReasonUnsupportedKind }
                }, writeStarted: true));

            JObject structured = (JObject)Hop(afterWrite)["structuredContent"];

            Assert.False((bool)structured["fallback"]["allowed"]);
            Assert.True((bool)structured["fallback"]["write_started"]);
            Assert.Contains("SECOND write", (string)structured["fallback"]["what_this_means"]);
        }

        [Fact]
        public void An_ordinary_failure_reaches_the_client_as_plain_text_with_no_structure()
        {
            JObject mcp = Hop(CommandResult.Fail("units must be mm, m or feet."));

            Assert.True((bool)mcp["isError"]);
            // Absence is the answer: a client that finds no block must not fall back.
            Assert.Null(mcp["structuredContent"]);
            Assert.Contains("units must be mm", (string)mcp["content"][0]["text"]);
        }

        [Fact]
        public void The_human_message_survives_alongside_the_structure()
        {
            string text = (string)Hop(GrantedBatch())["content"][0]["text"];

            Assert.Contains("Nothing was created", text);
            // The block is repeated in the text so a human reading a log sees it too.
            Assert.Contains("--- fallback ---", text);
            Assert.Contains("capability_gaps", text);
        }

        /// <summary>
        /// The success path must not acquire a fallback by accident, and its payload
        /// must arrive unchanged - that payload is where the Python evidence fields live.
        /// </summary>
        [Fact]
        public void A_python_result_payload_crosses_the_hop_unchanged()
        {
            var payload = new JObject
            {
                ["mode"] = "sync",
                ["executed"] = true,
                ["evidence_status"] = "self_reported_verified",
                ["script_reported_status"] = "verified",
                ["host_verified"] = false
            };

            JObject mcp = Hop(CommandResult.Ok(payload));
            JObject structured = (JObject)mcp["structuredContent"];

            Assert.False((bool)mcp["isError"]);
            Assert.Equal("self_reported_verified", (string)structured["evidence_status"]);
            Assert.False((bool)structured["host_verified"]);
            // The word the typed commands own must not appear as this path's state.
            Assert.NotEqual("verified", (string)structured["evidence_status"]);
            Assert.Null(structured["fallback"]);
        }

        [Fact]
        public void A_missing_image_is_an_error_but_keeps_capture_diagnostics_and_fallback_structure()
        {
            var data = new JObject
            {
                ["captured"] = true,
                ["image_path"] = "missing.png",
                ["sha256"] = "abc"
            };
            var fallback = new JObject
            {
                ["allowed"] = false,
                ["reason"] = "write_may_have_started",
                ["write_started"] = true
            };
            var gaps = new JArray { new JObject { ["index"] = 2 } };

            JObject result = McpResult.ImageAttachmentError(
                data, "image could not be attached", "FileNotFound", fallback, gaps);

            Assert.True((bool)result["isError"]);
            Assert.Equal("abc", (string)result["structuredContent"]["sha256"]);
            Assert.False((bool)result["structuredContent"]["image_attachment"]["attached"]);
            Assert.False((bool)result["structuredContent"]["fallback"]["allowed"]);
            Assert.Equal(2, (int)result["structuredContent"]["capability_gaps"][0]["index"]);
        }

        [Fact]
        public void The_real_attachment_path_rejects_a_missing_file_and_preserves_structure()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.png");
            var data = new JObject { ["captured"] = true, ["image_path"] = path, ["sha256"] = "kept" };

            JObject result = McpResult.AttachImageIfAny(data, "capture diagnostics");

            Assert.True((bool)result["isError"]);
            Assert.Equal("kept", (string)result["structuredContent"]["sha256"]);
            Assert.False((bool)result["structuredContent"]["image_attachment"]["attached"]);
            Assert.Contains("not there", (string)result["structuredContent"]["image_attachment"]["error"]);
        }

        [Fact]
        public void The_real_attachment_path_checks_file_length_before_reading_or_base64()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-image-limit-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.SetLength(McpResult.MaxImageFileBytes + 1);

                var data = new JObject { ["captured"] = true, ["image_path"] = path, ["diagnostic"] = "kept" };
                JObject result = McpResult.AttachImageIfAny(data, "capture diagnostics");

                Assert.True((bool)result["isError"]);
                Assert.Equal("kept", (string)result["structuredContent"]["diagnostic"]);
                Assert.False((bool)result["structuredContent"]["image_attachment"]["attached"]);
                Assert.Contains("limit", (string)result["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);
                Assert.Single((JArray)result["content"]); // no base64 image block was constructed
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void The_real_attachment_path_reports_a_corrupt_image_as_an_attachment_error()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-corrupt-image-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
                var data = new JObject { ["image_path"] = path, ["capture_diagnostic"] = "kept" };

                JObject result = McpResult.AttachImageIfAny(data, "capture diagnostics");

                Assert.True((bool)result["isError"]);
                Assert.Equal("kept", (string)result["structuredContent"]["capture_diagnostic"]);
                Assert.Contains("corrupt", (string)result["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);
                Assert.Single((JArray)result["content"]);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void The_real_attachment_path_enforces_the_encoded_response_budget_before_reading()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-image-response-budget-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.SetLength(McpResult.MaxImageFileBytes);
                var data = new JObject
                {
                    ["image_path"] = path,
                    // Base64 for a 16 MiB image is below the response cap by itself;
                    // this diagnostic consumes the remainder and forces the aggregate cap.
                    ["capture_diagnostic"] = new string('d', 3 * 1024 * 1024)
                };

                JObject result = McpResult.AttachImageIfAny(data, "capture diagnostics");

                Assert.True((bool)result["isError"]);
                Assert.Contains("response budget",
                    (string)result["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);
                Assert.Single((JArray)result["content"]);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Bounded_image_read_refuses_growth_without_resizing_past_the_checked_length()
        {
            using (var stream = new MemoryStream(new byte[] { 1, 2, 3 }))
            {
                InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
                    McpResult.ReadExactlyBounded(stream, expectedLength: 2, maxBytes: 2));
                Assert.Contains("grew", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Signature_and_footer_without_png_chunks_is_still_corrupt()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-fake-envelope-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(path, new byte[]
                {
                    137, 80, 78, 71, 13, 10, 26, 10,
                    0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130
                });
                var data = new JObject { ["image_path"] = path, ["diagnostic"] = "kept" };

                JObject result = McpResult.AttachImageIfAny(data, "capture diagnostics");

                Assert.True((bool)result["isError"]);
                Assert.Equal("kept", (string)result["structuredContent"]["diagnostic"]);
                Assert.Contains("corrupt", (string)result["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Jpeg_is_not_accepted_by_the_png_capture_attachment_seam()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-fake-envelope-" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                File.WriteAllBytes(path, new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
                JObject result = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = path, ["diagnostic"] = "kept" }, "capture diagnostics");

                Assert.True((bool)result["isError"]);
                Assert.Equal("kept", (string)result["structuredContent"]["diagnostic"]);
                Assert.Contains("Only PNG", (string)result["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Png_decoded_budget_cannot_wrap_for_extreme_uint_dimensions()
        {
            // These dimensions made (rowBytes + 1) * height wrap to a small,
            // positive Int64 value before the parser compared it with the cap.
            Assert.False(McpResult.TryComputePngDecodedByteCount(
                width: 1_073_758_206u,
                height: 4_294_901_768u,
                channels: 4,
                bitDepth: 8,
                out long wrappedCandidate));
            Assert.Equal(0, wrappedCandidate);

            Assert.True(McpResult.TryComputePngDecodedByteCount(
                width: 1, height: 1, channels: 4, bitDepth: 8,
                out long onePixel));
            Assert.Equal(5, onePixel); // RGBA byte quartet plus the row filter byte.
        }

        [Fact]
        public void Png_rejects_invalid_scanline_filters_and_truncated_adam7_data()
        {
            string filterPath = Path.Combine(Path.GetTempPath(), "hz-bad-filter-" + Guid.NewGuid().ToString("N") + ".png");
            string adam7Path = Path.Combine(Path.GetTempPath(), "hz-bad-adam7-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(filterPath, BuildPng(1, 1, 8, 6, 0,
                    new byte[] { 5, 0, 0, 0, 0 }));
                JObject badFilter = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = filterPath }, "capture diagnostics");
                Assert.True((bool)badFilter["isError"]);
                Assert.Contains("filter", (string)badFilter["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);

                File.WriteAllBytes(adam7Path, BuildPng(8, 8, 8, 6, 1, new byte[] { 0 }));
                JObject truncatedAdam7 = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = adam7Path }, "capture diagnostics");
                Assert.True((bool)truncatedAdam7["isError"]);
                Assert.Contains("scanline", (string)truncatedAdam7["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { File.Delete(filterPath); } catch { }
                try { File.Delete(adam7Path); } catch { }
            }
        }

        [Fact]
        public void Png_rejects_forbidden_palettes_and_invalid_chunk_type_bits()
        {
            string grayPath = Path.Combine(Path.GetTempPath(), "hz-gray-palette-" + Guid.NewGuid().ToString("N") + ".png");
            string indexedPath = Path.Combine(Path.GetTempPath(), "hz-indexed-palette-" + Guid.NewGuid().ToString("N") + ".png");
            string reservedPath = Path.Combine(Path.GetTempPath(), "hz-reserved-chunk-" + Guid.NewGuid().ToString("N") + ".png");
            string transparencyPath = Path.Combine(Path.GetTempPath(), "hz-alpha-trns-" + Guid.NewGuid().ToString("N") + ".png");
            string compressedMetadataPath = Path.Combine(Path.GetTempPath(), "hz-iccp-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(grayPath, BuildPng(1, 1, 8, 0, 0,
                    new byte[] { 0, 0 }, palette: new byte[] { 0, 0, 0 }));
                JObject gray = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = grayPath }, "capture diagnostics");
                Assert.True((bool)gray["isError"]);
                Assert.Contains("PLTE", (string)gray["structuredContent"]["image_attachment"]["error"]);

                File.WriteAllBytes(indexedPath, BuildPng(1, 1, 1, 3, 0,
                    new byte[] { 0, 0 }, palette: new byte[9])); // depth 1 allows at most two entries
                JObject indexed = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = indexedPath }, "capture diagnostics");
                Assert.True((bool)indexed["isError"]);
                Assert.Contains("indexed-color", (string)indexed["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);

                File.WriteAllBytes(reservedPath, BuildPng(1, 1, 8, 6, 0,
                    new byte[] { 0, 0, 0, 0, 0 }, extraChunkType: "abca"));
                JObject reserved = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = reservedPath }, "capture diagnostics");
                Assert.True((bool)reserved["isError"]);
                Assert.Contains("reserved bit", (string)reserved["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);

                File.WriteAllBytes(transparencyPath, BuildPng(1, 1, 8, 6, 0,
                    new byte[] { 0, 0, 0, 0, 0 }, extraChunkType: "tRNS", extraChunkData: new byte[] { 0 }));
                JObject transparency = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = transparencyPath }, "capture diagnostics");
                Assert.True((bool)transparency["isError"]);
                Assert.Contains("ancillary", (string)transparency["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);

                File.WriteAllBytes(compressedMetadataPath, BuildPng(1, 1, 8, 6, 0,
                    new byte[] { 0, 0, 0, 0, 0 }, extraChunkType: "iCCP", extraChunkData: new byte[] { 65, 0, 0, 1 }));
                JObject compressedMetadata = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = compressedMetadataPath }, "capture diagnostics");
                Assert.True((bool)compressedMetadata["isError"]);
                Assert.Contains("ancillary", (string)compressedMetadata["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { File.Delete(grayPath); } catch { }
                try { File.Delete(indexedPath); } catch { }
                try { File.Delete(reservedPath); } catch { }
                try { File.Delete(transparencyPath); } catch { }
                try { File.Delete(compressedMetadataPath); } catch { }
            }
        }

        [Fact]
        public void Png_accepts_only_canonical_srgb_gamma_and_chromaticity_coexistence()
        {
            string canonicalPath = Path.Combine(Path.GetTempPath(), "hz-canonical-srgb-" + Guid.NewGuid().ToString("N") + ".png");
            string conflictPath = Path.Combine(Path.GetTempPath(), "hz-conflict-srgb-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(canonicalPath, BuildPng(1, 1, 8, 6, 0,
                    new byte[] { 0, 0, 0, 0, 0 }, extraChunks: new[]
                    {
                        Tuple.Create("gAMA", Be32Values(45455)),
                        Tuple.Create("cHRM", Be32Values(31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000)),
                        Tuple.Create("sRGB", new byte[] { 0 })
                    }));
                JObject canonical = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = canonicalPath }, "capture diagnostics");
                Assert.False((bool)canonical["isError"],
                    (string)canonical["structuredContent"]?["image_attachment"]?["error"]);

                File.WriteAllBytes(conflictPath, BuildPng(1, 1, 8, 6, 0,
                    new byte[] { 0, 0, 0, 0, 0 }, extraChunks: new[]
                    {
                        Tuple.Create("gAMA", Be32Values(100000)),
                        Tuple.Create("sRGB", new byte[] { 0 })
                    }));
                JObject conflict = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = conflictPath }, "capture diagnostics");
                Assert.True((bool)conflict["isError"]);
                Assert.Contains("conflict", (string)conflict["structuredContent"]["image_attachment"]["error"],
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { File.Delete(canonicalPath); } catch { }
                try { File.Delete(conflictPath); } catch { }
            }
        }

        [Fact]
        public void A_structurally_valid_png_still_attaches()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-valid-image-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(path, Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
                JObject result = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = path }, "capture diagnostics");

                Assert.False((bool)result["isError"]);
                Assert.Equal("image", (string)result["content"][1]["type"]);
                Assert.False(string.IsNullOrEmpty((string)result["content"][1]["data"]));
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Even_a_structurally_valid_jpeg_is_rejected_because_capture_contract_is_png()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-valid-image-" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                File.WriteAllBytes(path, Convert.FromBase64String(
                    "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsL" +
                    "EBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU" +
                    "FBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcI" +
                    "CQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRol" +
                    "JicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ip" +
                    "qrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAA" +
                    "AAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLR" +
                    "ChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaX" +
                    "mJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEA" +
                    "PwD50ooor8MP9Uz/2Q=="));
                JObject result = McpResult.AttachImageIfAny(
                    new JObject { ["image_path"] = path }, "capture diagnostics");

                Assert.True((bool)result["isError"]);
                Assert.Contains("Only PNG", (string)result["structuredContent"]?["image_attachment"]?["error"],
                    StringComparison.OrdinalIgnoreCase);
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }
}
