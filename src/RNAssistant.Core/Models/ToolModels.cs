using System;
using System.Collections.Generic;
using Newtonsoft.Json;

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
        public bool CanSourceHtmlData { get; set; }
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
        public string CapabilityStatus { get; set; }
        public string Limitations { get; set; }
        public string PackageVersion { get; set; }
        public string EntryPoint { get; set; }
        public List<string> ArgumentOrder { get; set; }
        public List<VbaToolComponent> Components { get; set; }
        public string Scope { get; set; }
        public string InstallationStatus { get; set; }

        public ToolDefinition()
        {
            Enabled = true;
            Executor = "builtin";
            ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
            AgentCanRun = true;
            CapabilityStatus = "available";
            PackageVersion = "1.0.0";
            ArgumentOrder = new List<string>();
            Components = new List<VbaToolComponent>();
            Scope = "global";
        }

        public ToolDefinition Clone()
        {
            var clone = (ToolDefinition)MemberwiseClone();
            clone.ArgumentOrder = new List<string>(ArgumentOrder ?? new List<string>());
            clone.Components = new List<VbaToolComponent>();
            foreach (var component in Components ?? new List<VbaToolComponent>())
            {
                clone.Components.Add(component == null ? null : component.Clone());
            }
            return clone;
        }
    }

    public sealed class VbaToolComponent
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string FileName { get; set; }
        public string Code { get; set; }
        public string CodeSha256 { get; set; }

        public VbaToolComponent Clone()
        {
            return (VbaToolComponent)MemberwiseClone();
        }
    }

    public sealed class ToolCommand
    {
        public string ToolId { get; set; }
        public string Description { get; set; }
        public string ToolCallId { get; set; }
        public Dictionary<string, object> Arguments { get; set; }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string RuntimeGuardJson { get; set; }

        [JsonIgnore]
        public string RuntimeStepId { get; set; }

        public ToolCommand()
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
        public string ConfirmationCatalogSha256 { get; set; }
        public string Message { get; set; }
        public string DataJson { get; set; }
        public int ToolStepsConsumed { get; set; }

        [JsonIgnore]
        public IReadOnlyList<ChatAttachment> ModelAttachments { get; set; }

        [JsonIgnore]
        public IReadOnlyList<ResourceRef> ModelResourceRefs { get; set; }

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

        public static ToolResult Cancelled(string message)
        {
            return new ToolResult { Success = false, Status = "cancelled", Retryable = false, Message = message };
        }
    }
}
