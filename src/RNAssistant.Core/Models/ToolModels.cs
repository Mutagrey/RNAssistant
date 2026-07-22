using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class ToolDefinition
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ArgumentSchemaJson { get; set; }
        public string Executor { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool MutatesDocument { get; set; }
        public bool MutatesLocalState { get; set; }
        public bool AgentCanRun { get; set; }
        public string PipelineJson { get; set; }
        public string Code { get; set; }
        public string Readme { get; set; }
        public string StoragePath { get; set; }
        public bool Enabled { get; set; }
        public bool BuiltIn { get; set; }
        public int RiskLevel { get; set; }
        public string UseWhen { get; set; }
        public string DoNotUseWhen { get; set; }
        public string ExamplesJson { get; set; }
        public string PreconditionsJson { get; set; }
        public string VerifyJson { get; set; }
        public string CapabilityStatus { get; set; }
        public string Limitations { get; set; }
        public string ReplacementToolId { get; set; }

        public ToolDefinition()
        {
            Enabled = true;
            Executor = "builtin";
            ArgumentSchemaJson = "{}";
            AgentCanRun = true;
            CapabilityStatus = "available";
        }
    }

    public sealed class ToolCommand
    {
        public string ToolId { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Arguments { get; set; }

        public ToolCommand()
        {
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class ToolVerification
    {
        public string ToolId { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
        public string ExpectedCodeSha256 { get; set; }

        public ToolVerification()
        {
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class ToolResult
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string ErrorCode { get; set; }
        public bool? Retryable { get; set; }
        public string PendingId { get; set; }
        public string Message { get; set; }
        public string DataJson { get; set; }
        public ToolVerification Verification { get; set; }

        public static ToolResult Ok(string message, string dataJson = null)
        {
            return new ToolResult { Success = true, Status = "completed", Message = message, DataJson = dataJson };
        }

        public static ToolResult Fail(string message, string dataJson = null, string errorCode = null, bool? retryable = null)
        {
            return new ToolResult
            {
                Success = false,
                Status = "failed",
                ErrorCode = errorCode,
                Retryable = retryable,
                Message = message,
                DataJson = dataJson
            };
        }

        public static ToolResult PartialFailure(string message, string dataJson = null, string errorCode = "partial_failure")
        {
            return new ToolResult
            {
                Success = false,
                Status = "partial_failure",
                ErrorCode = errorCode,
                Retryable = false,
                Message = message,
                DataJson = dataJson
            };
        }

        public static ToolResult WaitingConfirmation(string message)
        {
            return new ToolResult { Success = false, Status = "waiting_confirmation", Retryable = false, Message = message };
        }

        public static ToolResult SkippedAutoRun(string message)
        {
            return new ToolResult { Success = false, Status = "skipped_auto_run", Retryable = false, Message = message };
        }

        public static ToolResult Cancelled(string message)
        {
            return new ToolResult { Success = false, Status = "cancelled", Retryable = false, Message = message };
        }
    }
}
