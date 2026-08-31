using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Office.Qualification;

namespace RNAssistant.Office.Contracts
{
    public sealed class QualificationCatalogPayload : ChatPayload
    {
        [JsonProperty("suite")] public string Suite { get; set; }
    }

    public sealed class QualificationStartPayload : ChatPayload
    {
        [JsonProperty("packId")] public string PackId { get; set; }
        [JsonProperty("previousRunId")] public string PreviousRunId { get; set; }
    }

    public sealed class QualificationRunPayload : ChatPayload
    {
        [JsonProperty("runId")] public string RunId { get; set; }
    }

    public sealed class QualificationAdvancePayload : ChatPayload
    {
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("acknowledged")] public bool Acknowledged { get; set; }
        [JsonProperty("cancel")] public bool Cancel { get; set; }
        [JsonProperty("note")] public string Note { get; set; }
    }

    public sealed class QualificationCatalogResponse
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("suite")] public string Suite { get; set; }
        [JsonProperty("packs")] public IReadOnlyList<QualificationPackDto> Packs { get; set; }
        [JsonProperty("missingCoverage")] public IReadOnlyList<string> MissingCoverage { get; set; }
    }

    public sealed class QualificationSessionResponse
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("chat")] public ChatStateResponse Chat { get; set; }
        [JsonProperty("run")] public QualificationRunDto Run { get; set; }
    }

    public sealed class QualificationPackDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("revision")] public string Revision { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("suite")] public string Suite { get; set; }
        [JsonProperty("workspacePolicy")] public string WorkspacePolicy { get; set; }
        [JsonProperty("requirements")] public IReadOnlyList<string> Requirements { get; set; }
        [JsonProperty("coverage")] public IReadOnlyList<string> Coverage { get; set; }
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("missingRequirements")] public IReadOnlyList<string> MissingRequirements { get; set; }
        [JsonProperty("steps")] public IReadOnlyList<QualificationStepDto> Steps { get; set; }

        public static QualificationPackDto From(QualificationPackAvailability availability)
        {
            if (availability == null || availability.Pack == null) return null;
            var pack = availability.Pack;
            return new QualificationPackDto
            {
                Id = pack.Id,
                Revision = pack.Revision,
                Sha256 = pack.ContentSha256,
                Title = pack.Title,
                Description = pack.Description,
                Suite = pack.Suite,
                WorkspacePolicy = pack.WorkspacePolicy,
                Requirements = pack.Requirements,
                Coverage = pack.Coverage,
                Available = availability.Available,
                MissingRequirements = availability.MissingRequirements,
                Steps = Array.AsReadOnly(pack.Steps.Select(QualificationStepDto.From).ToArray())
            };
        }
    }

    public sealed class QualificationStepDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("instructionKey")] public string InstructionKey { get; set; }
        [JsonProperty("required")] public bool Required { get; set; }

        public static QualificationStepDto From(QualificationStep step)
        {
            if (step == null) return null;
            return new QualificationStepDto
            {
                Id = step.Id,
                Kind = QualificationManifestParser.StepKindName(step.Kind),
                Title = step.Title,
                InstructionKey = step.InstructionKey,
                Required = step.Required
            };
        }
    }

    public sealed class QualificationRunDto
    {
        private const int MaximumReportEvidence = 262144;

        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("previousRunId")] public string PreviousRunId { get; set; }
        [JsonProperty("packId")] public string PackId { get; set; }
        [JsonProperty("packRevision")] public string PackRevision { get; set; }
        [JsonProperty("packSha256")] public string PackSha256 { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("productVersion")] public string ProductVersion { get; set; }
        [JsonProperty("buildCommit")] public string BuildCommit { get; set; }
        [JsonProperty("channel")] public string Channel { get; set; }
        [JsonProperty("suite")] public string Suite { get; set; }
        [JsonProperty("capabilities")] public IReadOnlyList<string> Capabilities { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("currentStepId")] public string CurrentStepId { get; set; }
        [JsonProperty("canResume")] public bool CanResume { get; set; }
        [JsonProperty("hasDurableTerminal")] public bool HasDurableTerminal { get; set; }
        [JsonProperty("startedUtc")] public DateTime StartedUtc { get; set; }
        [JsonProperty("completedUtc")] public DateTime? CompletedUtc { get; set; }
        [JsonProperty("startedEventId")] public string StartedEventId { get; set; }
        [JsonProperty("startedSequence")] public long? StartedSequence { get; set; }
        [JsonProperty("completedEventId")] public string CompletedEventId { get; set; }
        [JsonProperty("completedSequence")] public long? CompletedSequence { get; set; }
        [JsonProperty("reportTruncated")] public bool ReportTruncated { get; set; }
        [JsonProperty("steps")] public IReadOnlyList<QualificationStepResultDto> Steps { get; set; }

        public static QualificationRunDto From(QualificationRunState run)
        {
            if (run == null) return null;
            var remainingEvidence = MaximumReportEvidence;
            var steps = new List<QualificationStepResultDto>();
            foreach (var step in run.Steps)
            {
                steps.Add(QualificationStepResultDto.From(step, ref remainingEvidence));
            }
            return new QualificationRunDto
            {
                RunId = run.RunId,
                PreviousRunId = run.PreviousRunId,
                PackId = run.Pack.Id,
                PackRevision = run.Pack.Revision,
                PackSha256 = run.Pack.ContentSha256,
                Host = run.Context.Host,
                ProductVersion = run.Context.ProductVersion,
                BuildCommit = run.Context.BuildCommit,
                Channel = run.Context.Channel,
                Suite = run.Pack.Suite,
                Capabilities = run.Context.Capabilities,
                Status = QualificationManifestParser.RunStatusName(run.Status),
                CurrentStepId = run.CurrentStepIndex >= 0 && run.CurrentStepIndex < run.Pack.Steps.Count
                    ? run.Pack.Steps[run.CurrentStepIndex].Id : null,
                CanResume = run.CanResume,
                HasDurableTerminal = run.HasDurableTerminal,
                StartedUtc = run.StartedUtc,
                CompletedUtc = run.CompletedUtc,
                StartedEventId = run.StartedEventId,
                StartedSequence = run.StartedSequence,
                CompletedEventId = run.CompletedEventId,
                CompletedSequence = run.CompletedSequence,
                ReportTruncated = steps.Any(step => step.ExpectedTruncated || step.ActualTruncated),
                Steps = Array.AsReadOnly(steps.ToArray())
            };
        }
    }

    public sealed class QualificationStepResultDto
    {
        private const int MaximumInlineEvidence = 65536;

        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("outcome")] public string Outcome { get; set; }
        [JsonProperty("evidenceStrength")] public string EvidenceStrength { get; set; }
        [JsonProperty("attemptId")] public string AttemptId { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("expectedJson")] public string ExpectedJson { get; set; }
        [JsonProperty("expectedTruncated")] public bool ExpectedTruncated { get; set; }
        [JsonProperty("actualJson")] public string ActualJson { get; set; }
        [JsonProperty("actualTruncated")] public bool ActualTruncated { get; set; }
        [JsonProperty("domainEffect")] public string DomainEffect { get; set; }
        [JsonProperty("startedEventId")] public string StartedEventId { get; set; }
        [JsonProperty("startedSequence")] public long? StartedSequence { get; set; }
        [JsonProperty("completedEventId")] public string CompletedEventId { get; set; }
        [JsonProperty("completedSequence")] public long? CompletedSequence { get; set; }

        public static QualificationStepResultDto From(QualificationStepSnapshot step)
        {
            var remaining = MaximumInlineEvidence * 2;
            return From(step, ref remaining);
        }

        internal static QualificationStepResultDto From(QualificationStepSnapshot step, ref int remainingEvidence)
        {
            if (step == null) return null;
            var expected = Bound(step.ExpectedJson, ref remainingEvidence);
            var actual = Bound(step.ActualJson, ref remainingEvidence);
            return new QualificationStepResultDto
            {
                StepId = step.StepId,
                Kind = QualificationManifestParser.StepKindName(step.Kind),
                Outcome = QualificationManifestParser.OutcomeName(step.Outcome),
                EvidenceStrength = step.EvidenceStrength.ToString().ToLowerInvariant(),
                AttemptId = step.AttemptId,
                Code = step.Code,
                Message = step.Message,
                ExpectedJson = expected,
                ExpectedTruncated = IsTruncated(step.ExpectedJson, expected),
                ActualJson = actual,
                ActualTruncated = IsTruncated(step.ActualJson, actual),
                DomainEffect = step.DomainEffect,
                StartedEventId = step.StartedEventId,
                StartedSequence = step.StartedSequence,
                CompletedEventId = step.CompletedEventId,
                CompletedSequence = step.CompletedSequence
            };
        }

        private static bool IsTruncated(string value, string bounded)
        {
            return value != null && (bounded == null || value.Length > bounded.Length);
        }

        private static string Bound(string value, ref int remainingEvidence)
        {
            if (value == null) return null;
            if (value.Length == 0) return string.Empty;
            var length = Math.Min(value.Length, Math.Min(MaximumInlineEvidence,
                Math.Max(0, remainingEvidence)));
            if (length == 0) return null;
            if (length < value.Length && char.IsHighSurrogate(value[length - 1]) &&
                char.IsLowSurrogate(value[length])) length--;
            remainingEvidence -= length;
            return length == value.Length ? value : value.Substring(0, length);
        }
    }
}
