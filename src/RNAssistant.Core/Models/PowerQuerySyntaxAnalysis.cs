using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Models
{
    [JsonConverter(typeof(PowerQuerySyntaxStatusConverter))]
    public enum PowerQuerySyntaxStatus
    {
        [EnumMember(Value = "passed")] Passed,
        [EnumMember(Value = "failed")] Failed,
        [EnumMember(Value = "not_checked")] NotChecked
    }

    internal sealed class PowerQuerySyntaxStatusConverter : StringEnumConverter
    {
        public PowerQuerySyntaxStatusConverter() { AllowIntegerValues = false; }
    }

    // A source snapshot, never executable M and never model-supplied transport identity.
    public sealed class PowerQuerySyntaxRequest
    {
        public const int MaximumSourceCharacters = 262144;
        [JsonProperty("requestId")] public string RequestId { get; private set; }
        [JsonProperty("source")] public string Source { get; private set; }
        [JsonProperty("sourceSha256")] public string SourceSha256 { get; private set; }

        [JsonConstructor]
        public PowerQuerySyntaxRequest(string requestId, string source, string sourceSha256)
        {
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64 || source == null ||
                source.Length > MaximumSourceCharacters || sourceSha256 != TextPatternEngine.Sha256(source))
                throw new ArgumentException("A bounded exact Power Query source snapshot is required.");
            RequestId = requestId; Source = source; SourceSha256 = sourceSha256;
        }
    }

    public sealed class PowerQuerySyntaxDiagnostic
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("startOffset")] public int? StartOffset { get; set; }
        [JsonProperty("endOffset")] public int? EndOffset { get; set; }
    }

    public sealed class PowerQuerySyntaxAnalysis
    {
        public const string SupportedParserVersion = "5.0.0";
        public const int MaximumResultCharacters = 32768;
        public const int MaximumDiagnostics = 8;
        [JsonProperty("requestId", Required = Required.Always)] public string RequestId { get; set; }
        [JsonProperty("sourceSha256", Required = Required.Always)] public string SourceSha256 { get; set; }
        [JsonProperty("syntax", Required = Required.Always)] public PowerQuerySyntaxStatus Syntax { get; set; } = PowerQuerySyntaxStatus.NotChecked;
        [JsonProperty("parserVersion")] public string ParserVersion { get; set; }
        [JsonProperty("diagnostics", Required = Required.Always)] public List<PowerQuerySyntaxDiagnostic> Diagnostics { get; set; } = new List<PowerQuerySyntaxDiagnostic>();
        [JsonProperty("reason")] public string Reason { get; set; }

        public void RequireMatches(PowerQuerySyntaxRequest request)
        {
            if (request == null || RequestId != request.RequestId || SourceSha256 != request.SourceSha256 ||
                !Enum.IsDefined(typeof(PowerQuerySyntaxStatus), Syntax) || Diagnostics == null ||
                Diagnostics.Count > MaximumDiagnostics || (Reason != null && Reason.Length > 2048) ||
                (ParserVersion != null && ParserVersion.Length > 64))
                throw new InvalidOperationException("Power Query analysis does not match the captured source request.");
            if (Syntax != PowerQuerySyntaxStatus.NotChecked && ParserVersion != SupportedParserVersion ||
                Syntax == PowerQuerySyntaxStatus.Passed && (Diagnostics.Count != 0 || !string.IsNullOrEmpty(Reason)) ||
                Syntax == PowerQuerySyntaxStatus.Failed && Diagnostics.Count == 0 ||
                Syntax == PowerQuerySyntaxStatus.NotChecked && (Diagnostics.Count != 0 || string.IsNullOrWhiteSpace(Reason)))
                throw new InvalidOperationException("Power Query analysis has an inconsistent result or parser version.");
            foreach (var diagnostic in Diagnostics)
            {
                if (diagnostic == null || string.IsNullOrWhiteSpace(diagnostic.Code) || diagnostic.Code.Length > 128 ||
                    string.IsNullOrWhiteSpace(diagnostic.Message) || diagnostic.Message.Length > 2048 ||
                    diagnostic.StartOffset.HasValue != diagnostic.EndOffset.HasValue ||
                    diagnostic.StartOffset < 0 || diagnostic.EndOffset < diagnostic.StartOffset || diagnostic.EndOffset > request.Source.Length)
                    throw new InvalidOperationException("Power Query diagnostic is not bounded to the captured source.");
            }
        }

        public static PowerQuerySyntaxAnalysis Unavailable(PowerQuerySyntaxRequest request, string reason)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new PowerQuerySyntaxAnalysis { RequestId = request.RequestId, SourceSha256 = request.SourceSha256,
                Syntax = PowerQuerySyntaxStatus.NotChecked, Reason = reason };
        }
    }
}
