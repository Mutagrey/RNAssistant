using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Models
{
    public static class VbaJournalEventTypes
    {
        public const string BackupCreated = "backup.created";
        public const string MutationPrepared = "mutation.prepared";
        public const string MutationTerminal = "mutation.terminal";
        public const string PackageMutationPrepared = "package.mutation.prepared";
        public const string PackageMutationTerminal = "package.mutation.terminal";
    }

    public static class VbaMutationStatuses
    {
        public const string Open = "open";
        public const string Committed = "committed";
        public const string NotApplied = "not_applied";
        public const string RolledBack = "rolled_back";
        public const string Failed = "failed";
        public const string Unknown = "unknown";

        public static bool IsTerminal(string value)
        {
            return string.Equals(value, Committed, StringComparison.Ordinal) ||
                string.Equals(value, NotApplied, StringComparison.Ordinal) ||
                string.Equals(value, RolledBack, StringComparison.Ordinal) ||
                string.Equals(value, Failed, StringComparison.Ordinal) ||
                string.Equals(value, Unknown, StringComparison.Ordinal);
        }
    }

    public static class VbaMutationKinds
    {
        public const string Module = "module";
        public const string Package = "package";

        public static bool IsValid(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, Module, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, Package, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class VbaJournalEvent
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public long Sequence { get; set; }
        public string EventId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Type { get; set; }
        public string MutationId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string PreviousHash { get; set; }
        public string HashAlgorithm { get; set; }
        public string ProtectionKeyId { get; set; }
        public string Hash { get; set; }
        public JToken Data { get; set; }
        public string EncryptedData { get; set; }

        public VbaJournalEvent()
        {
            SchemaVersion = CurrentSchemaVersion;
            EventId = Guid.NewGuid().ToString("N");
            CreatedUtc = DateTime.UtcNow;
            HashAlgorithm = HistoryIntegrityModes.Sha256;
        }

        public bool ShouldSerializeData()
        {
            return string.IsNullOrWhiteSpace(EncryptedData);
        }

        public bool ShouldSerializeEncryptedData()
        {
            return !string.IsNullOrWhiteSpace(EncryptedData);
        }
    }

    public sealed class VbaMutationPreparation
    {
        public string MutationId { get; set; }
        public string Operation { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string RuntimeDocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string ModuleName { get; set; }
        public string ComponentType { get; set; }
        public bool BeforeExists { get; set; }
        public string BeforeCodeSha256 { get; set; }
        public string BeforeComparableCodeSha256 { get; set; }
        public ChatBlobReference BeforeCodeReference { get; set; }
        public bool IntendedAfterExists { get; set; }
        public string IntendedAfterCodeSha256 { get; set; }
        public string IntendedAfterComparableCodeSha256 { get; set; }
        public ChatBlobReference IntendedAfterCodeReference { get; set; }
        public string BackupId { get; set; }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public sealed class VbaMutationTerminal
    {
        public string MutationId { get; set; }
        public string Status { get; set; }
        public bool? ActualExists { get; set; }
        public string ActualCodeSha256 { get; set; }
        public string ActualComparableCodeSha256 { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public sealed class VbaMutationRecord
    {
        public VbaMutationPreparation Prepared { get; set; }
        public VbaMutationTerminal Terminal { get; set; }
    }

    public sealed class VbaPackageMutationComponent
    {
        public string ModuleName { get; set; }
        public bool BeforeExists { get; set; }
        public string BeforeComponentType { get; set; }
        public string BeforeCodeSha256 { get; set; }
        public ChatBlobReference BeforeCodeReference { get; set; }
        public string BackupId { get; set; }
        public bool IntendedAfterExists { get; set; }
        public string IntendedAfterComponentType { get; set; }
        public string IntendedAfterCodeSha256 { get; set; }
        public ChatBlobReference IntendedAfterCodeReference { get; set; }

        [JsonIgnore]
        public string BeforeCode { get; set; }

        [JsonIgnore]
        public string IntendedAfterCode { get; set; }
    }

    public sealed class VbaPackageMutationPreparation
    {
        public string MutationId { get; set; }
        public string Operation { get; set; }
        public string PackageId { get; set; }
        public string PackageVersion { get; set; }
        public bool SessionOnly { get; set; }
        public bool RetainBackups { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string RuntimeDocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public List<VbaPackageMutationComponent> Components { get; set; }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public DateTime CreatedUtc { get; set; }

        public VbaPackageMutationPreparation()
        {
            Components = new List<VbaPackageMutationComponent>();
        }
    }

    public sealed class VbaPackageMutationComponentAssessment
    {
        public string ModuleName { get; set; }
        public bool? ActualExists { get; set; }
        public string ActualComponentType { get; set; }
        public string ActualCodeSha256 { get; set; }
        public bool MatchesBefore { get; set; }
        public bool MatchesIntendedAfter { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
    }

    public sealed class VbaPackageMutationTerminal
    {
        public string MutationId { get; set; }
        public string Status { get; set; }
        public List<VbaPackageMutationComponentAssessment> Components { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public DateTime CreatedUtc { get; set; }

        public VbaPackageMutationTerminal()
        {
            Components = new List<VbaPackageMutationComponentAssessment>();
        }
    }

    public sealed class VbaPackageMutationRecord
    {
        public VbaPackageMutationPreparation Prepared { get; set; }
        public VbaPackageMutationTerminal Terminal { get; set; }
    }

    public sealed class VbaModuleBackup
    {
        public string BackupId { get; set; }
        public string MutationId { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string ModuleName { get; set; }
        public string ComponentType { get; set; }
        public string CodeSha256 { get; set; }
        public long CodeByteLength { get; set; }
        public ChatBlobReference CodeReference { get; set; }

        [JsonIgnore]
        public string Code { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public sealed class VbaMutationQueryRequest
    {
        public string Cursor { get; set; }
        public int PageSize { get; set; }
        public string Search { get; set; }
        public string Kind { get; set; }
        public string Status { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }

        public VbaMutationQueryRequest()
        {
            PageSize = 100;
        }
    }

    public sealed class VbaMutationQueryRow
    {
        public string MutationId { get; set; }
        public string Kind { get; set; }
        public string Operation { get; set; }
        public string Status { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public long FirstSequence { get; set; }
        public long LastSequence { get; set; }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string ModuleName { get; set; }
        public string ComponentType { get; set; }
        public string BackupId { get; set; }
        public string PackageId { get; set; }
        public string PackageVersion { get; set; }
        public int ComponentCount { get; set; }
        public List<string> ComponentNames { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public List<long> SourceEventSeqs { get; set; }
        public List<string> SourceEventIds { get; set; }

        public VbaMutationQueryRow()
        {
            ComponentNames = new List<string>();
            SourceEventSeqs = new List<long>();
            SourceEventIds = new List<string>();
        }
    }

    public sealed class VbaMutationQueryPage
    {
        public int TotalEvents { get; set; }
        public int TotalRows { get; set; }
        public int TotalMatches { get; set; }
        public string Cursor { get; set; }
        public string NextCursor { get; set; }
        public bool HasMore { get; set; }
        public List<VbaMutationQueryRow> Rows { get; set; }

        public VbaMutationQueryPage()
        {
            Rows = new List<VbaMutationQueryRow>();
        }
    }

    public sealed class VbaMutationComponentDetail
    {
        public string ModuleName { get; set; }
        public bool BeforeExists { get; set; }
        public string BeforeComponentType { get; set; }
        public string BeforeCodeSha256 { get; set; }
        public string BeforeCode { get; set; }
        public bool IntendedAfterExists { get; set; }
        public string IntendedAfterComponentType { get; set; }
        public string IntendedAfterCodeSha256 { get; set; }
        public string IntendedAfterCode { get; set; }
        public string BackupId { get; set; }
        public bool CanRestore { get; set; }
        public bool? ActualExists { get; set; }
        public string ActualComponentType { get; set; }
        public string ActualCodeSha256 { get; set; }
        public bool? MatchesBefore { get; set; }
        public bool? MatchesIntendedAfter { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
    }

    public sealed class VbaMutationDetail
    {
        public string MutationId { get; set; }
        public string Kind { get; set; }
        public string Operation { get; set; }
        public string Status { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string PackageId { get; set; }
        public string PackageVersion { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public List<long> SourceEventSeqs { get; set; }
        public List<string> SourceEventIds { get; set; }
        public List<VbaMutationComponentDetail> Components { get; set; }

        public VbaMutationDetail()
        {
            SourceEventSeqs = new List<long>();
            SourceEventIds = new List<string>();
            Components = new List<VbaMutationComponentDetail>();
        }
    }
}
