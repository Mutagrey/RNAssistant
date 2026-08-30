using System;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Vba
{
    internal sealed class VbaBackupSnapshot
    {
        public string BackupId { get; private set; }
        public string ModuleName { get; private set; }
        public string ComponentType { get; private set; }
        public string CodeSha256 { get; private set; }
        public long CodeByteLength { get; private set; }
        public string Code { get; private set; }
        public DateTime CreatedUtc { get; private set; }

        public VbaBackupSnapshot(
            string backupId,
            string moduleName,
            string componentType,
            string codeSha256,
            long codeByteLength,
            string code,
            DateTime createdUtc)
        {
            BackupId = backupId;
            ModuleName = moduleName;
            ComponentType = componentType;
            CodeSha256 = codeSha256;
            CodeByteLength = codeByteLength;
            Code = code;
            CreatedUtc = createdUtc;
        }
    }

    internal sealed class VbaRestoreGuardRequest
    {
        public string BackupId { get; set; }
        public string ModuleName { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaRestoreGuardPreparation
    {
        public VbaRestoreGuard Guard { get; set; }
        public string BackupId { get; set; }
        public string ModuleName { get; set; }
        public VbaMutationOutcome Error { get; set; }
        public bool Success { get { return Error == null && Guard != null; } }
    }

    internal sealed class VbaRestoreRequest
    {
        public string BackupId { get; set; }
        public string ModuleName { get; set; }
        public bool DryRun { get; set; }
        public VbaRestoreGuard Guard { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaRestoreBackendRequest
    {
        public string ModuleName { get; set; }
        public string Code { get; set; }
        public string ComponentType { get; set; }
        public bool ModuleExists { get; set; }
        public string ExpectedCodeSha256 { get; set; }
    }

    internal sealed class VbaRestoreGuard
    {
        public int Version { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string RuntimeDocumentKey { get; set; }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string BackupId { get; set; }
        public string ModuleName { get; set; }
        public string BackupComponentType { get; set; }
        public string BackupLiveCodeSha256 { get; set; }
        public bool ModuleExists { get; set; }
        public string CurrentCodeSha256 { get; set; }
    }

    internal sealed class VbaBackupReadResult
    {
        private readonly JObject _data;

        public bool Success { get; private set; }
        public bool IsNotFound { get; private set; }
        public VbaBackupSnapshot Backup { get; private set; }
        public string Message { get; private set; }
        public string ErrorCode { get; private set; }
        public bool? Retryable { get; private set; }
        public JObject Data { get { return _data == null ? null : (JObject)_data.DeepClone(); } }

        private VbaBackupReadResult(
            bool success,
            bool isNotFound,
            VbaBackupSnapshot backup,
            string message,
            string errorCode,
            bool? retryable,
            JObject data)
        {
            Success = success;
            IsNotFound = isNotFound;
            Backup = backup;
            Message = message ?? string.Empty;
            ErrorCode = errorCode;
            Retryable = retryable;
            _data = data == null ? null : (JObject)data.DeepClone();
        }

        public static VbaBackupReadResult Found(VbaBackupSnapshot backup)
        {
            if (backup == null) throw new ArgumentNullException(nameof(backup));
            return new VbaBackupReadResult(true, false, backup, string.Empty, null, null, null);
        }

        public static VbaBackupReadResult NotFound()
        {
            return new VbaBackupReadResult(
                false,
                true,
                null,
                "VBA backup not found.",
                "vba_backup_not_found",
                false,
                null);
        }

        public static VbaBackupReadResult Failure(
            string message,
            string errorCode,
            bool? retryable,
            JObject data = null)
        {
            return new VbaBackupReadResult(
                false,
                false,
                null,
                message,
                errorCode,
                retryable,
                data);
        }
    }
}
