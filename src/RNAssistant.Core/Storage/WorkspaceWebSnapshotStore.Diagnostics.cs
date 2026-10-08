using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceWebSnapshotStore
    {
        public const int MaximumScreenshotBytes = 512 * 1024;
        public const int MaximumTraceBytes = 128 * 1024;
        public const int MaximumTraceEntries = 256;

        private IReadOnlyList<WebDiagnosticDescriptor> StoreDiagnostics(WebVerificationRecord record,
            IReadOnlyList<WebDiagnosticCapture> captures)
        {
            if (captures == null) return new WebDiagnosticDescriptor[0];
            if (captures.Count > 2 || captures.Any(item => item == null || !Enum.IsDefined(typeof(WebDiagnosticKind), item.Kind)) ||
                captures.Select(item => item.Kind).Distinct().Count() != captures.Count)
                throw Unavailable("Invalid browser diagnostic captures.");
            // Validate everything before creating any payloads or publishing the terminal head.
            foreach (var item in captures)
            {
                if (item.Bytes != null)
                {
                    if (record.SnapshotId == null || item.Error != null) throw Unavailable("Diagnostic capture has no exact snapshot or has contradictory status.");
                    ValidateDiagnosticBytes(record, item.Kind, item.Bytes);
                }
                else if (string.IsNullOrWhiteSpace(item.Error) || item.Error.Length > 700)
                    throw Unavailable("Unavailable diagnostic requires a bounded reason.");
            }
            return captures.Select(item => new WebDiagnosticDescriptor { Kind = item.Kind,
                State = item.Bytes == null ? WebDiagnosticState.Unavailable : WebDiagnosticState.Captured,
                View = DiagnosticView(item.Kind), Error = item.Error,
                Payload = item.Bytes == null ? null : PayloadRef.FromBlob(_payloads.StoreBytes(item.Bytes, DiagnosticContentType(item.Kind))) }).ToArray();
        }

        public byte[] ReadDiagnostic(WorkspaceDescriptor workspace, ResourceRef verification, WebDiagnosticKind kind)
        {
            if (!Enum.IsDefined(typeof(WebDiagnosticKind), kind)) throw new ArgumentException("Unknown browser diagnostic kind.");
            var record = ReadVerification(workspace, verification);
            var item = record.Diagnostics.SingleOrDefault(value => value.Kind == kind);
            if (item == null || item.State != WebDiagnosticState.Captured)
                throw Unavailable(item?.Error ?? "This verification has no recorded diagnostic of this kind.");
            var view = _authority.GetView(Scope(workspace), verification, item.View);
            if (view?.Coverage?.Kind != ResourceCoverageKinds.Whole || view.Parts.Count != 0 ||
                !SamePayload(view.Payload, item.Payload) || view.ContentSha256 != item.Payload.Sha256)
                throw Unavailable("Exact browser diagnostic view is unavailable or mismatched.");
            var bytes = _payloads.ReadBytes(item.Payload.ToBlobReference());
            if (bytes == null) throw Unavailable("Browser diagnostic payload is missing or corrupt.");
            ValidateDiagnosticBytes(record, kind, bytes);
            return bytes;
        }

        private static void ValidateDiagnosticDescriptors(WebVerificationRecord record, ResourceRef reference, ResourceRevisionView view)
        {
            if (record.Diagnostics == null || record.Diagnostics.Count > 2 ||
                record.Diagnostics.Select(item => item?.Kind).Distinct().Count() != record.Diagnostics.Count)
                throw Unavailable("Invalid browser diagnostic metadata.");
            foreach (var item in record.Diagnostics)
            {
                if (item == null || !Enum.IsDefined(typeof(WebDiagnosticKind), item.Kind) ||
                    !Enum.IsDefined(typeof(WebDiagnosticState), item.State) || item.View != DiagnosticView(item.Kind))
                    throw Unavailable("Invalid browser diagnostic descriptor.");
                if (item.State == WebDiagnosticState.Captured)
                {
                    if (record.State == WebVerificationState.Pending || record.SnapshotId == null || item.Error != null ||
                        item.Reference?.Uri != reference.Uri || item.Reference.Revision != reference.Revision ||
                        item.Payload == null || item.Payload.ContentType != DiagnosticContentType(item.Kind) ||
                        item.Payload.ByteLength < 1 || item.Payload.ByteLength > DiagnosticLimit(item.Kind) ||
                        !view.Parts.Any(part => SamePayload(part, item.Payload)))
                        throw Unavailable("Diagnostic is not rooted in this exact terminal verification.");
                }
                else if (item.Payload != null || item.Reference != null || string.IsNullOrWhiteSpace(item.Error) || item.Error.Length > 700)
                    throw Unavailable("Unavailable diagnostic has invalid evidence.");
            }
        }

        private static void ValidateDiagnosticBytes(WebVerificationRecord record, WebDiagnosticKind kind, byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > DiagnosticLimit(kind)) throw Unavailable("Browser diagnostic exceeds its byte bound.");
            if (kind == WebDiagnosticKind.Screenshot)
            {
                if (!IsBoundedScreenshot(bytes)) throw Unavailable("Screenshot is not a bounded PNG.");
                return;
            }
            WebBrowserTrace trace;
            try { trace = JsonConvert.DeserializeObject<WebBrowserTrace>(new UTF8Encoding(false, true).GetString(bytes), JsonSettings); }
            catch (Exception ex) when (ex is JsonException || ex is DecoderFallbackException)
            { throw Unavailable("Invalid browser trace encoding."); }
            if (trace == null || trace.SchemaVersion != 1 || trace.VerificationId != record.Id ||
                trace.SnapshotId != record.SnapshotId || trace.SnapshotSha256 != record.SnapshotSha256 ||
                trace.Outcome != record.State || trace.DurationMilliseconds < 0 || trace.DroppedEntries < 0 ||
                trace.Truncated != (trace.DroppedEntries > 0) || trace.Entries == null || trace.Entries.Count > MaximumTraceEntries)
                throw Unavailable("Browser trace does not describe this exact verification.");
            long previous = 0;
            foreach (var entry in trace.Entries)
            {
                if (entry == null || !Enum.IsDefined(typeof(WebBrowserTraceEventKind), entry.Kind) ||
                    entry.Milliseconds < previous || entry.Milliseconds > trace.DurationMilliseconds ||
                    string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 128 || entry.Detail?.Length > 700)
                    throw Unavailable("Invalid or unbounded browser trace entry.");
                previous = entry.Milliseconds;
            }
        }

        // Header/dimension/byte admission, not a general-purpose image decoder.
        public static bool IsBoundedScreenshot(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 33 && bytes.Length <= MaximumScreenshotBytes &&
                bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 13 &&
                Encoding.ASCII.GetString(bytes, 12, 4) == "IHDR" && ValidPngDimension(bytes, 16) && ValidPngDimension(bytes, 20);
        }

        private static bool ValidPngDimension(byte[] bytes, int offset)
        { return bytes[offset] == 0 && bytes[offset + 1] == 0 &&
            (bytes[offset + 2] * 256 + bytes[offset + 3]) > 0 && (bytes[offset + 2] * 256 + bytes[offset + 3]) <= 2048; }

        private static bool SamePayload(PayloadRef left, PayloadRef right)
        { return left != null && right != null && left.Sha256 == right.Sha256 && left.ByteLength == right.ByteLength &&
            left.ContentType == right.ContentType && left.Encryption == right.Encryption && left.ProtectionKeyId == right.ProtectionKeyId; }

        private static string DiagnosticView(WebDiagnosticKind kind)
        { return kind == WebDiagnosticKind.Screenshot ? "browser-screenshot" : "browser-trace"; }
        private static string DiagnosticContentType(WebDiagnosticKind kind)
        { return kind == WebDiagnosticKind.Screenshot ? "image/png" : "application/json"; }
        private static int DiagnosticLimit(WebDiagnosticKind kind)
        { return kind == WebDiagnosticKind.Screenshot ? MaximumScreenshotBytes : MaximumTraceBytes; }
    }
}
