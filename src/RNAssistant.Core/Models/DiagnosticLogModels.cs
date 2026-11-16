using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class DiagnosticLogOptions
    {
        public int MaxFileMiB { get; set; } = 5;
        public int MaxTotalMiB { get; set; } = 50;
        public int RetentionDays { get; set; } = 7;
        public int MaxFiles { get; set; } = 20;

        public DiagnosticLogOptions Clone() { return (DiagnosticLogOptions)MemberwiseClone(); }

        public void Validate()
        {
            if (MaxFileMiB < 1 || MaxFileMiB > 100 || MaxTotalMiB < 2 || MaxTotalMiB > 1000 ||
                MaxTotalMiB < MaxFileMiB * 2 || RetentionDays < 1 || RetentionDays > 365 || MaxFiles < 2 || MaxFiles > 200)
                throw new ArgumentException("Лимиты логов: файл 1–100 МиБ; всего 2–1000 МиБ и не менее двух файлов; срок 1–365 дней; файлов 2–200.");
        }

        public static DiagnosticLogOptions Normalize(DiagnosticLogOptions value)
        {
            var result = value == null ? new DiagnosticLogOptions() : value.Clone();
            result.MaxFileMiB = Math.Max(1, Math.Min(100, result.MaxFileMiB));
            result.MaxTotalMiB = Math.Max(2 * result.MaxFileMiB, Math.Min(1000, result.MaxTotalMiB));
            result.RetentionDays = Math.Max(1, Math.Min(365, result.RetentionDays));
            result.MaxFiles = Math.Max(2, Math.Min(200, result.MaxFiles));
            return result;
        }
    }

    public sealed class DiagnosticLogFile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public long ByteLength { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public bool IsActive { get; set; }
    }

    public sealed class DiagnosticLogIssue
    {
        public string FileId { get; set; }
        public string Operation { get; set; }
        public string Message { get; set; }
    }

    public sealed class DiagnosticLogInventory
    {
        public List<DiagnosticLogFile> Files { get; set; } = new List<DiagnosticLogFile>();
        public DiagnosticLogOptions Options { get; set; }
        public long TotalBytes { get; set; }
        public bool OverLimit { get; set; }
        public List<DiagnosticLogIssue> Issues { get; set; } = new List<DiagnosticLogIssue>();
    }

    public sealed class DiagnosticLogPage
    {
        public string FileId { get; set; }
        public string Text { get; set; } = string.Empty;
        public string SnapshotToken { get; set; }
        public long StartOffset { get; set; }
        public long EndOffset { get; set; }
        public long SnapshotLength { get; set; }
        public bool HasEarlier { get; set; }
        public bool ResetRequired { get; set; }
        public bool PartialFirstLine { get; set; }
    }

    public sealed class DiagnosticLogMutationResult
    {
        public int AffectedFiles { get; set; }
        public long ReclaimedBytes { get; set; }
        public List<DiagnosticLogIssue> Issues { get; set; } = new List<DiagnosticLogIssue>();
        public DiagnosticLogInventory Inventory { get; set; }
    }
}
