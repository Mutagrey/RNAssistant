using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    public sealed class DiagnosticLogFileRequest
    {
        [JsonProperty("fileId", Required = Required.Always)] public string FileId { get; set; }
    }

    public sealed class DiagnosticLogReadRequest
    {
        [JsonProperty("fileId", Required = Required.Always)] public string FileId { get; set; }
        [JsonProperty("beforeOffset")] public long? BeforeOffset { get; set; }
        [JsonProperty("snapshotToken")] public string SnapshotToken { get; set; }
        [JsonProperty("maxBytes")] public int MaxBytes { get; set; } = 65536;
    }

    public sealed class DiagnosticLogOptionsDto
    {
        [JsonProperty("maxFileMiB", Required = Required.Always)] public int MaxFileMiB { get; set; }
        [JsonProperty("maxTotalMiB", Required = Required.Always)] public int MaxTotalMiB { get; set; }
        [JsonProperty("retentionDays", Required = Required.Always)] public int RetentionDays { get; set; }
        [JsonProperty("maxFiles", Required = Required.Always)] public int MaxFiles { get; set; }

        public DiagnosticLogOptions ToOptions()
        {
            return new DiagnosticLogOptions { MaxFileMiB = MaxFileMiB, MaxTotalMiB = MaxTotalMiB,
                RetentionDays = RetentionDays, MaxFiles = MaxFiles };
        }

        public static DiagnosticLogOptionsDto From(DiagnosticLogOptions value)
        {
            return new DiagnosticLogOptionsDto { MaxFileMiB = value.MaxFileMiB, MaxTotalMiB = value.MaxTotalMiB,
                RetentionDays = value.RetentionDays, MaxFiles = value.MaxFiles };
        }
    }

    public sealed class DiagnosticLogFileDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
        [JsonProperty("lastWriteUtc")] public DateTime LastWriteUtc { get; set; }
        [JsonProperty("isActive")] public bool IsActive { get; set; }

        public static DiagnosticLogFileDto From(DiagnosticLogFile value)
        {
            return new DiagnosticLogFileDto { Id = value.Id, Name = value.Name, Source = value.Source,
                ByteLength = value.ByteLength, LastWriteUtc = value.LastWriteUtc, IsActive = value.IsActive };
        }
    }

    public sealed class DiagnosticLogIssueDto
    {
        [JsonProperty("fileId")] public string FileId { get; set; }
        [JsonProperty("operation")] public string Operation { get; set; }
        [JsonProperty("message")] public string Message { get; set; }

        public static DiagnosticLogIssueDto From(DiagnosticLogIssue value)
        {
            return new DiagnosticLogIssueDto { FileId = value.FileId, Operation = value.Operation, Message = value.Message };
        }
    }

    public sealed class DiagnosticLogInventoryDto
    {
        [JsonProperty("files")] public IReadOnlyList<DiagnosticLogFileDto> Files { get; set; }
        [JsonProperty("options")] public DiagnosticLogOptionsDto Options { get; set; }
        [JsonProperty("totalBytes")] public long TotalBytes { get; set; }
        [JsonProperty("overLimit")] public bool OverLimit { get; set; }
        [JsonProperty("issues")] public IReadOnlyList<DiagnosticLogIssueDto> Issues { get; set; }

        public static DiagnosticLogInventoryDto From(DiagnosticLogInventory value)
        {
            return new DiagnosticLogInventoryDto { Files = value.Files.Select(DiagnosticLogFileDto.From).ToArray(),
                Options = DiagnosticLogOptionsDto.From(value.Options), TotalBytes = value.TotalBytes,
                OverLimit = value.OverLimit, Issues = value.Issues.Select(DiagnosticLogIssueDto.From).ToArray() };
        }
    }

    public sealed class DiagnosticLogPageDto
    {
        [JsonProperty("fileId")] public string FileId { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("snapshotToken")] public string SnapshotToken { get; set; }
        [JsonProperty("startOffset")] public long StartOffset { get; set; }
        [JsonProperty("endOffset")] public long EndOffset { get; set; }
        [JsonProperty("snapshotLength")] public long SnapshotLength { get; set; }
        [JsonProperty("hasEarlier")] public bool HasEarlier { get; set; }
        [JsonProperty("resetRequired")] public bool ResetRequired { get; set; }
        [JsonProperty("partialFirstLine")] public bool PartialFirstLine { get; set; }

        public static DiagnosticLogPageDto From(DiagnosticLogPage value)
        {
            return new DiagnosticLogPageDto { FileId = value.FileId, Text = value.Text,
                SnapshotToken = value.SnapshotToken, StartOffset = value.StartOffset, EndOffset = value.EndOffset,
                SnapshotLength = value.SnapshotLength, HasEarlier = value.HasEarlier,
                ResetRequired = value.ResetRequired, PartialFirstLine = value.PartialFirstLine };
        }
    }

    public sealed class DiagnosticLogMutationDto
    {
        [JsonProperty("affectedFiles")] public int AffectedFiles { get; set; }
        [JsonProperty("reclaimedBytes")] public long ReclaimedBytes { get; set; }
        [JsonProperty("issues")] public IReadOnlyList<DiagnosticLogIssueDto> Issues { get; set; }
        [JsonProperty("inventory")] public DiagnosticLogInventoryDto Inventory { get; set; }

        public static DiagnosticLogMutationDto From(DiagnosticLogMutationResult value)
        {
            return new DiagnosticLogMutationDto { AffectedFiles = value.AffectedFiles, ReclaimedBytes = value.ReclaimedBytes,
                Issues = value.Issues.Select(DiagnosticLogIssueDto.From).ToArray(), Inventory = DiagnosticLogInventoryDto.From(value.Inventory) };
        }
    }
}
