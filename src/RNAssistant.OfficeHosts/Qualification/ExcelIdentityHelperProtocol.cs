using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.OfficeHosts.Qualification
{
    public sealed class ExcelIdentityHelperRequest
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("nonce")] public string Nonce { get; set; }
        [JsonProperty("operation")] public string Operation { get; set; }
        [JsonProperty("hwnd")] public long Hwnd { get; set; }
        [JsonProperty("workbookIndex")] public int WorkbookIndex { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("scenario")] public string Scenario { get; set; }
        [JsonProperty("ownerAssemblyMvid")] public string OwnerAssemblyMvid { get; set; }
    }

    public sealed class ExcelIdentityWorkbookTarget
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("fullName")] public string FullName { get; set; }
    }

    public sealed class ExcelIdentityHelperResponse
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("nonce")] public string Nonce { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("scenario")] public string Scenario { get; set; }
        [JsonProperty("clientProcessId")] public int ClientProcessId { get; set; }
        [JsonProperty("ownerThreadId")] public int OwnerThreadId { get; set; }
        [JsonProperty("excelProcessId")] public int ExcelProcessId { get; set; }
        [JsonProperty("excelProcessStartUtc")] public string ExcelProcessStartUtc { get; set; }
        [JsonProperty("excelVersion")] public string ExcelVersion { get; set; }
        [JsonProperty("ownerAssemblyMvid")] public string OwnerAssemblyMvid { get; set; }
        [JsonProperty("candidate")] public string Candidate { get; set; }
        [JsonProperty("oxid")] public string Oxid { get; set; }
        [JsonProperty("oid")] public string Oid { get; set; }
        [JsonProperty("ipid")] public string Ipid { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("fullName")] public string FullName { get; set; }
        [JsonProperty("savedBeforeBind")] public bool? SavedBeforeBind { get; set; }
        [JsonProperty("savedBeforeRead")] public bool? SavedBeforeRead { get; set; }
        [JsonProperty("savedAfterRead")] public bool? SavedAfterRead { get; set; }
        [JsonProperty("windowCount")] public int? WindowCount { get; set; }
        [JsonProperty("workbooks")] public IReadOnlyList<ExcelIdentityWorkbookTarget> Workbooks { get; set; }
    }

    public static class ExcelIdentityHelperProtocol
    {
        public const int SchemaVersion = 1;
        public const int MaximumMessageChars = 32768;

        private static readonly HashSet<string> Operations = new HashSet<string>(
            new[] { "bind", "observe", "list", "release" }, StringComparer.Ordinal);

        public static string SerializeRequest(ExcelIdentityHelperRequest request)
        {
            ValidateRequest(request);
            return JsonConvert.SerializeObject(request, Formatting.None);
        }

        public static ExcelIdentityHelperRequest ParseRequest(string json)
        {
            var root = ParseObject(json);
            EnsureOnly(root, new[]
            {
                "schemaVersion", "nonce", "operation", "hwnd", "workbookIndex", "label", "scenario",
                "ownerAssemblyMvid"
            });
            var result = root.ToObject<ExcelIdentityHelperRequest>();
            ValidateRequest(result);
            return result;
        }

        public static string SerializeResponse(ExcelIdentityHelperResponse response)
        {
            ValidateResponse(response);
            return JsonConvert.SerializeObject(response, Formatting.None);
        }

        public static ExcelIdentityHelperResponse ParseResponse(string json, string nonce)
        {
            var root = ParseObject(json);
            EnsureOnly(root, new[]
            {
                "schemaVersion", "nonce", "type", "status", "code", "message", "label", "scenario",
                "clientProcessId", "ownerThreadId", "excelProcessId", "excelProcessStartUtc", "excelVersion",
                "ownerAssemblyMvid", "candidate", "oxid", "oid", "ipid", "name", "fullName",
                "savedBeforeBind", "savedBeforeRead", "savedAfterRead", "windowCount", "workbooks"
            });
            var result = root.ToObject<ExcelIdentityHelperResponse>();
            ValidateResponse(result);
            if (!string.Equals(result.Nonce, nonce, StringComparison.Ordinal))
                throw new InvalidDataException("Identity helper nonce mismatch.");
            return result;
        }

        public static string ReadBoundedLine(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            var value = new StringBuilder();
            while (true)
            {
                var next = reader.Read();
                if (next < 0)
                {
                    if (value.Length == 0) return null;
                    break;
                }
                if (next == '\n') break;
                if (next == '\r') continue;
                if (value.Length >= MaximumMessageChars)
                    throw new InvalidDataException("Identity helper message exceeds its bounded size.");
                value.Append((char)next);
            }
            return value.ToString();
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumMessageChars)
                throw new InvalidDataException("Identity helper message is empty or overlong.");
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json))
                {
                    DateParseHandling = DateParseHandling.None,
                    MaxDepth = 8
                })
                {
                    var result = JObject.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    if (reader.Read()) throw new JsonReaderException("More than one JSON value.");
                    return result;
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Identity helper message is invalid JSON.", ex);
            }
        }

        private static void EnsureOnly(JObject root, IEnumerable<string> names)
        {
            var allowed = new HashSet<string>(names, StringComparer.Ordinal);
            var unexpected = root.Properties().FirstOrDefault(item => !allowed.Contains(item.Name));
            if (unexpected != null)
                throw new InvalidDataException("Identity helper message contains an unknown field: " + unexpected.Name + ".");
        }

        private static void ValidateRequest(ExcelIdentityHelperRequest request)
        {
            if (request == null || request.SchemaVersion != SchemaVersion)
                throw new InvalidDataException("Identity helper request schema is unsupported.");
            Bounded(request.Nonce, 64, "nonce", true);
            Bounded(request.OwnerAssemblyMvid, 36, "ownerAssemblyMvid", true);
            Guid ownerMvid;
            if (request.Nonce.Length != 64 || request.Nonce.Any(value => !Uri.IsHexDigit(value)) ||
                !Guid.TryParseExact(request.OwnerAssemblyMvid, "D", out ownerMvid) || ownerMvid == Guid.Empty)
                throw new InvalidDataException("Identity helper request provenance is malformed.");
            Bounded(request.Label, 64, "label", false);
            Bounded(request.Scenario, 96, "scenario", false);
            if (!Operations.Contains(request.Operation))
                throw new InvalidDataException("Identity helper operation is unsupported.");
            if ((request.Operation == "bind" || request.Operation == "list") && request.Hwnd == 0)
                throw new InvalidDataException("Identity helper requires an explicit HWND.");
            if (request.Operation == "bind" && (request.WorkbookIndex < 1 || request.WorkbookIndex > 1024))
                throw new InvalidDataException("Identity helper workbook index is outside bounds.");
            if (request.Operation != "bind" && request.Operation != "list" && request.Hwnd != 0)
                throw new InvalidDataException("Only bind/list may carry an HWND.");
        }

        private static void ValidateResponse(ExcelIdentityHelperResponse response)
        {
            if (response == null || response.SchemaVersion != SchemaVersion)
                throw new InvalidDataException("Identity helper response schema is unsupported.");
            Bounded(response.Nonce, 64, "nonce", true);
            Bounded(response.Type, 32, "type", true);
            Bounded(response.Status, 32, "status", true);
            Bounded(response.Code, 96, "code", false);
            Bounded(response.Message, 1000, "message", false);
            Bounded(response.Label, 64, "label", false);
            Bounded(response.Scenario, 96, "scenario", false);
            Bounded(response.ExcelProcessStartUtc, 40, "excelProcessStartUtc", false);
            Bounded(response.ExcelVersion, 64, "excelVersion", false);
            Bounded(response.OwnerAssemblyMvid, 36, "ownerAssemblyMvid", true);
            Bounded(response.Candidate, 33, "candidate", false);
            Bounded(response.Oxid, 16, "oxid", false);
            Bounded(response.Oid, 16, "oid", false);
            Bounded(response.Ipid, 36, "ipid", false);
            Bounded(response.Name, 512, "name", false);
            Bounded(response.FullName, 2048, "fullName", false);
            Guid ownerMvid;
            if (response.Nonce.Length != 64 || response.Nonce.Any(value => !Uri.IsHexDigit(value)) ||
                !Guid.TryParseExact(response.OwnerAssemblyMvid, "D", out ownerMvid) || ownerMvid == Guid.Empty)
                throw new InvalidDataException("Identity helper response provenance is malformed.");
            var shapeValid = response.Type == "observation" &&
                    (response.Status == "observed" || response.Status == "closed") ||
                response.Type == "workbooks" && response.Status == "listed" ||
                response.Type == "released" && response.Status == "released" ||
                response.Type == "error" && response.Status == "failed";
            if (!shapeValid)
                throw new InvalidDataException("Identity helper response type/status is unsupported.");
            if (response.Type == "observation")
            {
                DateTime processStart;
                ulong oxid;
                ulong oid;
                Guid ipid;
                if (response.ExcelProcessId <= 0 ||
                    !DateTime.TryParse(response.ExcelProcessStartUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out processStart) ||
                    !ulong.TryParse(response.Oxid, System.Globalization.NumberStyles.HexNumber, null, out oxid) ||
                    !ulong.TryParse(response.Oid, System.Globalization.NumberStyles.HexNumber, null, out oid) ||
                    oxid == 0 || oid == 0 || !Guid.TryParseExact(response.Ipid, "D", out ipid) ||
                    ipid == Guid.Empty || response.Candidate != response.Oxid + ":" + response.Oid)
                    throw new InvalidDataException("Identity helper observation identity is malformed.");
            }
            if (response.Workbooks != null)
            {
                if (response.Workbooks.Count > 256)
                    throw new InvalidDataException("Identity helper workbook list exceeds bounds.");
                foreach (var workbook in response.Workbooks)
                {
                    if (workbook == null || workbook.Index < 1 || workbook.Index > 1024)
                        throw new InvalidDataException("Identity helper workbook target is invalid.");
                    Bounded(workbook.Name, 512, "workbook.name", true);
                    Bounded(workbook.FullName, 2048, "workbook.fullName", false);
                }
            }
        }

        private static void Bounded(string value, int maximum, string name, bool required)
        {
            if (required && string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Identity helper " + name + " is required.");
            if (value != null && value.Length > maximum)
                throw new InvalidDataException("Identity helper " + name + " exceeds bounds.");
        }
    }
}
