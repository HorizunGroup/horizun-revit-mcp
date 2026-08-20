// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// HOW A PLUGIN REPLY BECOMES AN MCP TOOL RESULT. Extracted from Program.cs for
// the same reason ServerInstructions was: it is a contract surface, and while it
// sat as three private helpers the only way to assert anything about it was to
// read the source.
//
// What travels here decides whether a client can act. The human text is what a
// person reads; structuredContent is what a program branches on. The fallback
// block in particular must survive this hop intact - a signal dropped in
// translation looks exactly like a signal that was never granted, and the client
// then silently stops falling back to Python, which is the failure nobody
// notices because it presents as "the model just says no".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpResult
    {
        // Capture is a convenience transport, not an unbounded file-transfer endpoint.
        // The response cap includes the already-rendered text/structured payload plus
        // base64 expansion; both checks happen before ReadAllBytes/ToBase64String.
        internal const long MaxImageFileBytes = 16L * 1024 * 1024;
        internal const long MaxImageResponseBytes = 24L * 1024 * 1024;
        internal const long MaxDecodedImageBytes = 256L * 1024 * 1024;
        internal const uint MaxImageDimension = 8192;
        internal const int LargeStructuredTextThresholdBytes = 512 * 1024;

        public static JObject Text(string text, bool isError)
        {
            return new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
                ["isError"] = isError
            };
        }

        public static JObject Structured(JToken data, string text)
            => Structured(data, text, null, null);

        /// <summary>
        /// A success, with the fallback verdict merged into structuredContent beside the
        /// payload when there is one. The payload is never rewritten - the verdict is
        /// extra information about what the call FOUND, and a client reads one object.
        /// </summary>
        public static JObject Structured(JToken data, string text, JObject fallback, JArray capabilityGaps)
        {
            // The TEXT of a success stays exactly the payload. On the error path the
            // block is also rendered as prose, because a human reading a log has
            // nothing else; here that would splice a second JSON document into a
            // string clients parse as one, and every parser downstream would break on
            // precisely the replies that carry the new signal. structuredContent is
            // where a success publishes structure - that is what it is for.
            JObject structured = data is JObject o ? (JObject)o.DeepClone() : null;
            // Tool results normally repeat the payload as human text and as
            // structuredContent. That is useful for small answers but doubles a large
            // async result and can turn an otherwise valid <=32 MiB response into an
            // undeliverable one. Preserve the exact machine-readable value and replace
            // only the redundant prose copy once it is materially large.
            string rendered = text;
            if (structured != null)
            {
                int structuredBytes = Encoding.UTF8.GetByteCount(
                    structured.ToString(Formatting.None));
                if (structuredBytes > LargeStructuredTextThresholdBytes)
                    rendered = "Large structured result (" + structuredBytes +
                               " UTF-8 bytes) is available in structuredContent.";
            }
            JObject result = Text(rendered, false);

            if (structured == null && (fallback != null || capabilityGaps != null)) structured = new JObject();
            if (structured == null) return result;

            if (fallback != null) structured["fallback"] = fallback.DeepClone();
            if (capabilityGaps != null) structured["capability_gaps"] = capabilityGaps.DeepClone();

            result["structuredContent"] = structured;
            return result;
        }

        /// <summary>
        /// A failure, with the fallback decision and any per-action capability gaps
        /// carried as STRUCTURE as well as prose. No fallback and no gaps gives exactly
        /// the plain text error it always did.
        /// </summary>
        public static JObject Error(string text, JObject fallback, JArray capabilityGaps)
            => Error(text, fallback, capabilityGaps, null);

        /// <summary>
        /// A failure, with the fallback decision, per-action capability gaps AND a structured
        /// diagnostic (an atomic plan's rollback trace) carried as STRUCTURE as well as prose.
        /// The diagnostic's fields are spread at the top of structuredContent so a client reads
        /// $s.transaction_group_started / $s.rollback_status directly - the shape a live probe
        /// asserts against instead of parsing the sentence.
        /// </summary>
        public static JObject Error(string text, JObject fallback, JArray capabilityGaps, JObject detail)
        {
            if (fallback == null && capabilityGaps == null && detail == null) return Text(text, true);

            var structured = new JObject();
            string rendered = text;

            // The diagnostic first, spread flat: transaction_group_started, rollback_status,
            // execution_trace and the rest become top-level fields of structuredContent.
            if (detail != null)
            {
                foreach (var prop in detail.Properties())
                    structured[prop.Name] = prop.Value.DeepClone();
            }

            if (fallback != null)
            {
                rendered += Environment.NewLine + Environment.NewLine +
                            "--- fallback ---" + Environment.NewLine + fallback.ToString(Formatting.Indented);
                structured["fallback"] = fallback.DeepClone();
            }
            // The gaps ride along even when the grant was refused - a mixed batch is
            // precisely where a caller needs to see which entries have no typed path and
            // which are theirs to fix.
            if (capabilityGaps != null)
            {
                rendered += Environment.NewLine + Environment.NewLine +
                            "--- capability_gaps (actions with no typed path) ---" + Environment.NewLine +
                            capabilityGaps.ToString(Formatting.Indented);
                structured["capability_gaps"] = capabilityGaps.DeepClone();
            }

            JObject result = Text(rendered, true);
            result["structuredContent"] = structured;
            return result;
        }

        /// <summary>
        /// The Revit command succeeded but the server could not deliver the image that
        /// was the tool's primary output. This is a tool error, while the original data
        /// and machine-readable verdict remain available for diagnosis.
        /// </summary>
        public static JObject ImageAttachmentError(JToken data, string text, string reason,
                                                   JObject fallback, JArray capabilityGaps)
        {
            JObject structured = data is JObject o ? (JObject)o.DeepClone() : new JObject();
            structured["image_attachment"] = new JObject
            {
                ["attached"] = false,
                ["error"] = reason
            };
            if (fallback != null) structured["fallback"] = fallback.DeepClone();
            if (capabilityGaps != null) structured["capability_gaps"] = capabilityGaps.DeepClone();

            JObject result = Text(text, true);
            result["structuredContent"] = structured;
            return result;
        }

        /// <summary>
        /// Production image attachment seam. It performs metadata and output-budget checks
        /// before allocating the file or its base64 representation. Length and bytes come
        /// from one open handle; the bounded reader never allocates beyond the cap and
        /// probes for growth before accepting the file.
        /// </summary>
        internal static JObject AttachImageIfAny(JToken data, string text,
                                                  JObject fallback = null,
                                                  JArray capabilityGaps = null)
        {
            string path = data is JObject obj ? (string)obj["image_path"] : null;
            if (string.IsNullOrEmpty(path))
                return Structured(data, text, fallback, capabilityGaps);

            try
            {
                if (!File.Exists(path))
                    return ImageAttachmentError(data,
                        text + "\n\n[the image could not be attached: " + path + " is not there]",
                        path + " is not there", fallback, capabilityGaps);

                byte[] bytes;
                JObject baseResult;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long length = stream.Length;
                    if (length < 0 || length > MaxImageFileBytes)
                        return ImageAttachmentError(data,
                            text + "\n\n[the image could not be attached: file size " + length +
                            " bytes exceeds the " + MaxImageFileBytes + " byte limit]",
                            "Image file is " + length + " bytes; limit is " + MaxImageFileBytes + " bytes.",
                            fallback, capabilityGaps);

                    baseResult = Structured(data, text, fallback, capabilityGaps);
                    long baseBytes = System.Text.Encoding.UTF8.GetByteCount(baseResult.ToString(Formatting.None));
                    long encodedBytes = ((length + 2L) / 3L) * 4L;
                    // Reserve the JSON wrapper for the image block. The constant is deliberately
                    // conservative; the exact mime/path do not approach it.
                    const int imageJsonOverheadBytes = 256;
                    if (baseBytes > MaxImageResponseBytes ||
                        encodedBytes > MaxImageResponseBytes - baseBytes - imageJsonOverheadBytes)
                        return ImageAttachmentError(data,
                            text + "\n\n[the image could not be attached: the MCP response would exceed its output budget]",
                            "Image attachment would exceed the " + MaxImageResponseBytes +
                            " byte MCP response budget after base64 expansion.", fallback, capabilityGaps);

                    bytes = ReadExactlyBounded(stream, length, MaxImageFileBytes);
                }

                // The typed capture contract exports PNG only. Do not turn this seam into
                // a generic image decoder: accepting a format requires proving its decoded
                // structure, not recognizing a header and footer.
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    return ImageAttachmentError(data,
                        text + "\n\n[the image could not be attached: only PNG capture output is supported]",
                        "Only PNG output from horizun_capture_view can be attached.",
                        fallback, capabilityGaps);
                const string mime = "image/png";
                string structureError;
                if (!HasValidImageStructure(bytes, mime, out structureError))
                    return ImageAttachmentError(data,
                        text + "\n\n[the image could not be attached: invalid " + mime + " structure]",
                        "Invalid or corrupt " + mime + " image: " + structureError,
                        fallback, capabilityGaps);
                baseResult["content"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = text },
                    new JObject
                    {
                        ["type"] = "image",
                        ["data"] = Convert.ToBase64String(bytes),
                        ["mimeType"] = mime
                    }
                };
                baseResult["isError"] = false;
                return baseResult;
            }
            catch (Exception ex)
            {
                return ImageAttachmentError(data,
                    text + "\n\n[the image could not be attached: " + ex.Message + "]",
                    ex.GetType().Name + ": " + ex.Message, fallback, capabilityGaps);
            }
        }

        /// <summary>
        /// Read exactly the length obtained from the already-open handle. Allocation is
        /// bounded before it happens; an early EOF or even one extra byte is a changed file,
        /// never a reason to resize the buffer.
        /// </summary>
        internal static byte[] ReadExactlyBounded(Stream stream, long expectedLength, long maxBytes)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (expectedLength < 0 || expectedLength > maxBytes || expectedLength > int.MaxValue)
                throw new InvalidDataException("Image length " + expectedLength + " exceeds the " + maxBytes + " byte limit.");

            var bytes = new byte[(int)expectedLength];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0)
                    throw new InvalidDataException("Image ended after " + offset + " bytes; expected " + expectedLength + ".");
                offset += read;
            }
            if (stream.ReadByte() != -1)
                throw new InvalidDataException("Image grew beyond its checked length while it was read; attachment was refused.");
            if (stream.CanSeek && stream.Length != expectedLength)
                throw new InvalidDataException("Image length changed while it was read; attachment was refused.");
            return bytes;
        }

        private static bool HasValidImageStructure(byte[] bytes, string mime, out string error)
            => HasValidPngStructure(bytes, out error);

        private static bool HasValidPngStructure(byte[] bytes, out string error)
        {
            error = null;
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes == null || bytes.Length < 8) { error = "missing PNG signature"; return false; }
            for (int i = 0; i < signature.Length; i++)
                if (bytes[i] != signature[i]) { error = "bad PNG signature"; return false; }

            int at = 8;
            bool ihdr = false, idat = false, iend = false, plte = false, idatEnded = false;
            int colorType = -1, bitDepth = -1, interlace = -1;
            long expectedDecoded = -1;
            List<PngPassLayout> scanLayout = null;
            var ancillaryState = new PngAncillaryState();
            using (var compressed = new MemoryStream())
            {
                while (at < bytes.Length)
                {
                    if (bytes.Length - at < 12) { error = "truncated PNG chunk header"; return false; }
                    uint length = ReadBe32(bytes, at);
                    if (length > int.MaxValue || length > (uint)(bytes.Length - at - 12))
                    { error = "PNG chunk length exceeds the file"; return false; }
                    int dataLength = (int)length;
                    int typeAt = at + 4;
                    int dataAt = at + 8;
                    int crcAt = dataAt + dataLength;
                    uint type = ReadBe32(bytes, typeAt);
                    for (int letter = 0; letter < 4; letter++)
                    {
                        byte value = bytes[typeAt + letter];
                        if (!((value >= (byte)'A' && value <= (byte)'Z') ||
                              (value >= (byte)'a' && value <= (byte)'z')))
                        { error = "PNG chunk type is not four ASCII letters"; return false; }
                    }
                    // PNG reserves bit 5 of the third chunk-type byte; it must be zero
                    // (uppercase) even for otherwise-unknown ancillary chunks.
                    if ((bytes[typeAt + 2] & 0x20) != 0)
                    { error = "PNG chunk type has a nonzero reserved bit"; return false; }
                    if (PngCrc(bytes, typeAt, 4 + dataLength) != ReadBe32(bytes, crcAt))
                    { error = "PNG chunk CRC mismatch"; return false; }

                    const uint IHDR = 0x49484452, PLTE = 0x504c5445, IDAT = 0x49444154, IEND = 0x49454e44;
                    if (!ihdr && type != IHDR) { error = "IHDR is not the first PNG chunk"; return false; }
                    if (type == IHDR)
                    {
                        if (ihdr || dataLength != 13) { error = "invalid or duplicate IHDR"; return false; }
                        uint width = ReadBe32(bytes, dataAt), height = ReadBe32(bytes, dataAt + 4);
                        bitDepth = bytes[dataAt + 8]; colorType = bytes[dataAt + 9]; interlace = bytes[dataAt + 12];
                        if (width == 0 || height == 0 || width > MaxImageDimension || height > MaxImageDimension ||
                            bytes[dataAt + 10] != 0 || bytes[dataAt + 11] != 0 || interlace > 1)
                        { error = "invalid IHDR dimensions or methods"; return false; }
                        int channels;
                        switch (colorType)
                        {
                            case 0: channels = 1; if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8 && bitDepth != 16) { error = "invalid grayscale bit depth"; return false; } break;
                            case 2: channels = 3; if (bitDepth != 8 && bitDepth != 16) { error = "invalid RGB bit depth"; return false; } break;
                            // Revit's typed capture path exports direct-color PNG. An
                            // indexed image would require reconstructing every filtered
                            // scanline to prove each pixel index exists in PLTE; it is not
                            // part of this attachment contract, so reject it explicitly.
                            case 3: error = "indexed-color PNG is not supported by the capture attachment"; return false;
                            case 4: channels = 2; if (bitDepth != 8 && bitDepth != 16) { error = "invalid grayscale-alpha bit depth"; return false; } break;
                            case 6: channels = 4; if (bitDepth != 8 && bitDepth != 16) { error = "invalid RGBA bit depth"; return false; } break;
                            default: error = "invalid PNG color type"; return false;
                        }
                        if (!TryCreatePngScanLayout(width, height, channels, bitDepth, interlace,
                                out scanLayout, out expectedDecoded))
                        { error = "decoded PNG exceeds the safety budget"; return false; }
                        ihdr = true;
                    }
                    else if (type == PLTE)
                    {
                        int paletteEntries = dataLength / 3;
                        if (plte || idat || dataLength == 0 || dataLength > 768 || dataLength % 3 != 0 ||
                            colorType == 0 || colorType == 4 ||
                            (colorType == 3 && paletteEntries > (1 << bitDepth)))
                        { error = "invalid PLTE chunk"; return false; }
                        plte = true;
                    }
                    else if (type == IDAT)
                    {
                        if (idatEnded) { error = "non-contiguous IDAT chunks"; return false; }
                        compressed.Write(bytes, dataAt, dataLength);
                        idat = true;
                    }
                    else
                    {
                        if (idat) idatEnded = true;
                        if (type == IEND)
                        {
                            if (dataLength != 0 || !idat || colorType == 3 && !plte)
                            { error = "invalid IEND or missing required PNG chunks"; return false; }
                            iend = true;
                            at = crcAt + 4;
                            if (at != bytes.Length) { error = "bytes follow IEND"; return false; }
                            break;
                        }
                        // Capture is not a generic PNG metadata tunnel. Accept only a
                        // small, non-compressed ancillary set with explicit size/order
                        // rules; iCCP/zTXt/iTXt and format-specific chunks such as tRNS
                        // are rejected instead of handing a second decompression surface
                        // or contradictory colour model to the client decoder.
                        if ((bytes[typeAt] & 0x20) == 0)
                        { error = "unknown critical PNG chunk"; return false; }
                        if (!ValidatePngAncillary(bytes, type, dataAt, dataLength, plte, idat,
                                colorType, bitDepth, ancillaryState, out error)) return false;
                    }
                    at = crcAt + 4;
                }

                if (!ihdr || !idat || !iend || compressed.Length == 0)
                { error = "missing IHDR, IDAT or IEND"; return false; }
                try
                {
                    compressed.Position = 0;
                    using (var z = new ZLibStream(compressed, CompressionMode.Decompress, true))
                        if (!ValidatePngScanlines(z, scanLayout, expectedDecoded, out error)) return false;
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                { error = "invalid PNG compressed data: " + ex.Message; return false; }
            }
            return true;
        }

        internal static bool TryComputePngDecodedByteCount(
            uint width, uint height, int channels, int bitDepth, out long expectedDecoded)
        {
            expectedDecoded = 0;
            if (width == 0 || height == 0 || channels <= 0 || bitDepth == 0)
                return false;

            // Width * channels * bitDepth is bounded well below Int64.MaxValue for
            // PNG's uint dimensions, but multiplying a scanline by uint height is
            // not. Check against the decoded-image budget before multiplying so a
            // wrapped Int64 value can never make an enormous declaration look small.
            long rowBits = (long)width * channels * bitDepth;
            long rowStride = ((rowBits + 7) / 8) + 1; // one filter byte per row
            if (rowStride <= 0 || rowStride > MaxDecodedImageBytes)
                return false;
            if ((long)height > MaxDecodedImageBytes / rowStride)
                return false;

            expectedDecoded = rowStride * (long)height;
            return expectedDecoded > 0;
        }

        private sealed class PngPassLayout
        {
            public int RowBytes;
            public uint Rows;
        }

        private static bool TryCreatePngScanLayout(uint width, uint height, int channels,
            int bitDepth, int interlace, out List<PngPassLayout> layout, out long expectedDecoded)
        {
            layout = new List<PngPassLayout>(interlace == 0 ? 1 : 7);
            expectedDecoded = 0;
            int[] startX = interlace == 0 ? new[] { 0 } : new[] { 0, 4, 0, 2, 0, 1, 0 };
            int[] startY = interlace == 0 ? new[] { 0 } : new[] { 0, 0, 4, 0, 2, 0, 1 };
            int[] stepX = interlace == 0 ? new[] { 1 } : new[] { 8, 8, 4, 4, 2, 2, 1 };
            int[] stepY = interlace == 0 ? new[] { 1 } : new[] { 8, 8, 8, 4, 4, 2, 2 };

            for (int pass = 0; pass < startX.Length; pass++)
            {
                if (width <= (uint)startX[pass] || height <= (uint)startY[pass]) continue;
                uint passWidth = (width - (uint)startX[pass] + (uint)stepX[pass] - 1) / (uint)stepX[pass];
                uint passHeight = (height - (uint)startY[pass] + (uint)stepY[pass] - 1) / (uint)stepY[pass];
                long rowBits = (long)passWidth * channels * bitDepth;
                long rowBytes = ((rowBits + 7) / 8) + 1;
                if (rowBytes <= 1 || rowBytes > int.MaxValue || rowBytes > MaxDecodedImageBytes ||
                    (long)passHeight > (MaxDecodedImageBytes - expectedDecoded) / rowBytes)
                    return false;
                expectedDecoded += rowBytes * (long)passHeight;
                layout.Add(new PngPassLayout { RowBytes = (int)rowBytes, Rows = passHeight });
            }
            return layout.Count > 0 && expectedDecoded > 0 && expectedDecoded <= MaxDecodedImageBytes;
        }

        private static bool ValidatePngScanlines(Stream decoded, List<PngPassLayout> layout,
            long expectedDecoded, out string error)
        {
            error = null;
            if (decoded == null || layout == null || layout.Count == 0)
            { error = "missing PNG scanline layout"; return false; }
            int largestRow = 0;
            foreach (PngPassLayout pass in layout) if (pass.RowBytes > largestRow) largestRow = pass.RowBytes;
            byte[] row = new byte[largestRow];
            long total = 0;
            foreach (PngPassLayout pass in layout)
            {
                for (uint y = 0; y < pass.Rows; y++)
                {
                    int offset = 0;
                    while (offset < pass.RowBytes)
                    {
                        int read = decoded.Read(row, offset, pass.RowBytes - offset);
                        if (read <= 0) { error = "PNG decompressed data ended inside a scanline"; return false; }
                        offset += read;
                    }
                    if (row[0] > 4) { error = "PNG scanline uses an invalid filter"; return false; }
                    total += pass.RowBytes;
                    if (total > expectedDecoded || total > MaxDecodedImageBytes)
                    { error = "decoded PNG exceeds its declared dimensions or safety budget"; return false; }
                }
            }
            if (total != expectedDecoded || decoded.ReadByte() != -1)
            { error = "PNG decompressed size does not match IHDR"; return false; }
            return true;
        }

        private sealed class PngAncillaryState
        {
            public readonly HashSet<uint> Seen = new HashSet<uint>();
            public uint? Gamma;
            public uint[] Chromaticities;
            public bool Srgb;
        }

        private static bool ValidatePngAncillary(byte[] bytes, uint type, int dataAt, int dataLength,
            bool afterPalette, bool afterImageData, int colorType, int bitDepth,
            PngAncillaryState state, out string error)
        {
            error = null;
            const uint cHRM = 0x6348524d, gAMA = 0x67414d41, sBIT = 0x73424954,
                       sRGB = 0x73524742, pHYs = 0x70485973, tIME = 0x74494d45,
                       tEXt = 0x74455874;
            if (type != cHRM && type != gAMA && type != sBIT && type != sRGB &&
                type != pHYs && type != tIME && type != tEXt)
            { error = "unsupported PNG ancillary chunk"; return false; }
            if (!state.Seen.Add(type) && type != tEXt)
            { error = "duplicate PNG ancillary chunk"; return false; }

            if (type == cHRM)
            {
                if (afterPalette || afterImageData || dataLength != 32)
                { error = "invalid cHRM chunk"; return false; }
                var values = new uint[8];
                for (int i = 0; i < 8; i++)
                {
                    values[i] = ReadBe32(bytes, dataAt + 4 * i);
                    if (values[i] > 100000)
                    { error = "invalid cHRM chromaticity"; return false; }
                }
                if (state.Srgb && !IsCanonicalSrgbChromaticity(values))
                { error = "cHRM conflicts with sRGB"; return false; }
                state.Chromaticities = values;
                return true;
            }
            if (type == gAMA)
            {
                uint gamma = dataLength == 4 ? ReadBe32(bytes, dataAt) : 0;
                if (afterPalette || afterImageData || dataLength != 4 || gamma == 0)
                { error = "invalid gAMA chunk"; return false; }
                if (state.Srgb && gamma != 45455)
                { error = "gAMA conflicts with sRGB"; return false; }
                state.Gamma = gamma;
                return true;
            }
            if (type == sRGB)
            {
                if (afterPalette || afterImageData || dataLength != 1 || bytes[dataAt] > 3)
                { error = "invalid sRGB chunk"; return false; }
                if (state.Gamma.HasValue && state.Gamma.Value != 45455 ||
                    state.Chromaticities != null && !IsCanonicalSrgbChromaticity(state.Chromaticities))
                { error = "sRGB conflicts with gAMA or cHRM"; return false; }
                state.Srgb = true;
                return true;
            }
            if (type == sBIT)
            {
                int expected = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 4 ? 2 : colorType == 6 ? 4 : 0;
                if (afterPalette || afterImageData || expected == 0 || dataLength != expected)
                { error = "invalid sBIT chunk"; return false; }
                for (int i = 0; i < dataLength; i++)
                    if (bytes[dataAt + i] == 0 || bytes[dataAt + i] > bitDepth)
                    { error = "invalid sBIT precision"; return false; }
                return true;
            }
            if (type == pHYs)
            {
                if (afterImageData || dataLength != 9 || bytes[dataAt + 8] > 1)
                { error = "invalid pHYs chunk"; return false; }
                return true;
            }
            if (type == tIME)
            {
                if (dataLength != 7 || bytes[dataAt + 5] > 23 || bytes[dataAt + 6] > 60)
                { error = "invalid tIME chunk"; return false; }
                int year = (bytes[dataAt] << 8) | bytes[dataAt + 1];
                try { _ = new DateTime(year, bytes[dataAt + 2], bytes[dataAt + 3], bytes[dataAt + 4], bytes[dataAt + 5], Math.Min(bytes[dataAt + 6], (byte)59)); }
                catch { error = "invalid tIME date"; return false; }
                return true;
            }

            // tEXt is uncompressed. Keep metadata bounded and require a legal PNG
            // keyword so even allowed metadata cannot dominate the MCP response.
            if (dataLength < 2 || dataLength > 4096)
            { error = "invalid or oversized tEXt chunk"; return false; }
            int separator = -1;
            for (int i = 0; i < dataLength; i++)
                if (bytes[dataAt + i] == 0) { separator = i; break; }
            if (separator < 1 || separator > 79)
            { error = "invalid tEXt keyword"; return false; }
            bool previousSpace = false;
            for (int i = 0; i < separator; i++)
            {
                byte value = bytes[dataAt + i];
                bool printable = value >= 32 && value <= 126 || value >= 161;
                if (!printable || value == 32 && (i == 0 || i == separator - 1 || previousSpace))
                { error = "invalid tEXt keyword"; return false; }
                previousSpace = value == 32;
            }
            for (int i = separator + 1; i < dataLength; i++)
                if (bytes[dataAt + i] == 0)
                { error = "invalid tEXt text"; return false; }
            return true;
        }

        private static bool IsCanonicalSrgbChromaticity(uint[] values)
        {
            uint[] canonical = { 31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000 };
            if (values == null || values.Length != canonical.Length) return false;
            for (int i = 0; i < canonical.Length; i++) if (values[i] != canonical[i]) return false;
            return true;
        }

        private static bool HasValidJpegStructure(byte[] bytes, out string error)
        {
            error = null;
            if (bytes == null || bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            { error = "missing JPEG SOI"; return false; }
            int at = 2;
            bool sof = false, sos = false, eoi = false, quantization = false, codingTable = false;
            long entropyBytes = 0;
            bool inScan = false;
            HashSet<byte> frameComponentIds = null;
            while (at < bytes.Length)
            {
                if (inScan && bytes[at] != 0xff) { entropyBytes++; at++; continue; }
                if (bytes[at] != 0xff) { error = "data outside a JPEG scan"; return false; }
                while (at < bytes.Length && bytes[at] == 0xff) at++;
                if (at >= bytes.Length) { error = "truncated JPEG marker"; return false; }
                byte marker = bytes[at++];
                if (inScan && marker == 0x00) { entropyBytes++; continue; }
                if (marker >= 0xd0 && marker <= 0xd7) { if (!inScan) { error = "restart marker outside scan"; return false; } continue; }
                if (marker == 0xd9)
                {
                    eoi = true;
                    if (at != bytes.Length) { error = "bytes follow JPEG EOI"; return false; }
                    break;
                }
                inScan = false;
                if (marker == 0xd8 || marker == 0x01) { error = "unexpected standalone JPEG marker"; return false; }
                if (at + 2 > bytes.Length) { error = "truncated JPEG segment length"; return false; }
                int segmentLength = (bytes[at] << 8) | bytes[at + 1];
                if (segmentLength < 2 || at + segmentLength > bytes.Length)
                { error = "JPEG segment exceeds the file"; return false; }
                int dataAt = at + 2;
                int dataLength = segmentLength - 2;
                if (IsSof(marker))
                {
                    if (sof || dataLength < 6) { error = "invalid or duplicate JPEG SOF"; return false; }
                    int precision = bytes[dataAt];
                    int height = (bytes[dataAt + 1] << 8) | bytes[dataAt + 2];
                    int width = (bytes[dataAt + 3] << 8) | bytes[dataAt + 4];
                    int components = bytes[dataAt + 5];
                    if ((precision != 8 && precision != 12) || width == 0 || height == 0 ||
                        components < 1 || components > 4 || dataLength != 6 + 3 * components ||
                        (long)width * height * components > MaxDecodedImageBytes)
                    { error = "invalid JPEG frame dimensions/components"; return false; }
                    var componentIds = new HashSet<byte>();
                    for (int c = 0; c < components; c++)
                    {
                        int componentAt = dataAt + 6 + 3 * c;
                        byte sampling = bytes[componentAt + 1];
                        int horizontal = sampling >> 4, vertical = sampling & 0x0f;
                        if (!componentIds.Add(bytes[componentAt]) || horizontal < 1 || horizontal > 4 ||
                            vertical < 1 || vertical > 4 || bytes[componentAt + 2] > 3)
                        { error = "invalid JPEG frame component"; return false; }
                    }
                    frameComponentIds = componentIds;
                    sof = true;
                }
                if (marker == 0xdb)
                {
                    if (!ValidDqt(bytes, dataAt, dataLength)) { error = "invalid JPEG quantization table"; return false; }
                    quantization = true;
                }
                if (marker == 0xc4)
                {
                    if (!ValidDht(bytes, dataAt, dataLength)) { error = "invalid JPEG Huffman table"; return false; }
                    codingTable = true;
                }
                if (marker == 0xcc)
                {
                    if (dataLength == 0 || dataLength % 2 != 0) { error = "invalid JPEG arithmetic table"; return false; }
                    codingTable = true;
                }
                if (marker == 0xda)
                {
                    if (!sof || !quantization || !codingTable ||
                        !ValidSos(bytes, dataAt, dataLength, frameComponentIds))
                    { error = "invalid SOS or SOS before a valid JPEG frame"; return false; }
                    sos = true;
                    inScan = true;
                }
                at += segmentLength;
            }
            if (!sof || !sos || !eoi || !quantization || !codingTable || entropyBytes == 0)
            { error = "missing JPEG frame, tables, scan data or EOI"; return false; }
            return true;
        }

        private static bool IsSof(byte marker)
            => marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc;

        private static bool ValidDqt(byte[] bytes, int at, int length)
        {
            int end = at + length;
            while (at < end)
            {
                byte table = bytes[at++];
                int precision = table >> 4, id = table & 0x0f;
                if (precision > 1 || id > 3) return false;
                int values = 64 * (precision + 1);
                if (end - at < values) return false;
                at += values;
            }
            return at == end && length > 0;
        }

        private static bool ValidDht(byte[] bytes, int at, int length)
        {
            int end = at + length;
            while (at < end)
            {
                if (end - at < 17) return false;
                byte table = bytes[at++];
                if ((table >> 4) > 1 || (table & 0x0f) > 3) return false;
                int symbols = 0;
                for (int i = 0; i < 16; i++) symbols += bytes[at++];
                if (symbols == 0 || symbols > 256 || end - at < symbols) return false;
                at += symbols;
            }
            return at == end && length > 0;
        }

        private static bool ValidSos(byte[] bytes, int at, int length, HashSet<byte> frameComponentIds)
        {
            if (length < 6) return false;
            int components = bytes[at];
            if (components < 1 || components > 4 || length != 1 + 2 * components + 3) return false;
            var ids = new HashSet<byte>();
            for (int i = 0; i < components; i++)
            {
                byte id = bytes[at + 1 + 2 * i];
                byte tables = bytes[at + 2 + 2 * i];
                if (!ids.Add(id) || frameComponentIds == null || !frameComponentIds.Contains(id) ||
                    (tables >> 4) > 3 || (tables & 0x0f) > 3) return false;
            }
            int spectral = at + 1 + 2 * components;
            int start = bytes[spectral], end = bytes[spectral + 1];
            byte approx = bytes[spectral + 2];
            return start <= end && end <= 63 && (approx >> 4) <= 13 && (approx & 0x0f) <= 13;
        }

        private static uint ReadBe32(byte[] bytes, int offset)
            => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

        private static uint PngCrc(byte[] bytes, int offset, int count)
        {
            uint crc = 0xffffffff;
            for (int i = 0; i < count; i++)
                crc = PngCrcTable[(crc ^ bytes[offset + i]) & 0xff] ^ (crc >> 8);
            return crc ^ 0xffffffff;
        }

        private static readonly uint[] PngCrcTable = BuildPngCrcTable();
        private static uint[] BuildPngCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        /// <summary>
        /// The whole hop, from the envelope the plugin put on the pipe to the MCP result.
        /// One function, so a test can prove end to end that nothing is lost - which is
        /// the only claim worth making about a transport.
        /// </summary>
        public static JObject FromPluginReply(JObject reply, string revitSaidText)
        {
            bool ok = reply["success"] != null && reply["success"].Type == JTokenType.Boolean &&
                      (bool)reply["success"];

            if (ok)
            {
                JToken data = reply["data"];
                // A SUCCESSFUL reply can still carry the fallback verdict: the dry run
                // is a success that found a capability gap. Dropping the block here was
                // the defect that made the first, default call useless for deciding -
                // the caller got success=true, invalid=1 and no signal at all.
                return Structured(data,
                                  (data == null ? "null" : data.ToString(Formatting.Indented)) +
                                  (revitSaidText ?? ""),
                                  reply["fallback"] as JObject,
                                  reply["capability_gaps"] as JArray);
            }

            return Error("Error: " + (string)reply["error"] + (revitSaidText ?? ""),
                         reply["fallback"] as JObject,
                         reply["capability_gaps"] as JArray,
                         reply["detail"] as JObject);
        }
    }
}
