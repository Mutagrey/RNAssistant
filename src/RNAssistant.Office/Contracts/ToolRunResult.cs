using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    // Versioned local/manual/UI projection of one typed runtime record. Model
    // serialization consumes Core.Tools.Contracts.ToolResult directly.
    public sealed class ToolRunResult
    {
        public const int CurrentContractVersion = 1;
        public const string ContractType = "rnassistant.toolRunResult";

        [JsonProperty("type")]
        public string Type { get; set; } = ContractType;

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; } = CurrentContractVersion;

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("code")]
        public string ErrorCode { get; set; }

        [JsonProperty("retryable")]
        public bool? Retryable { get; set; }

        [JsonProperty("pendingId")]
        public string PendingId { get; set; }

        [JsonProperty("catalogRevision")]
        public string CatalogRevision { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("dataJson")]
        public string DataJson { get; set; }

        [JsonProperty("toolStepsConsumed")]
        public int ToolStepsConsumed { get; set; }

        [JsonIgnore]
        public IReadOnlyList<ChatAttachment> ModelAttachments { get; set; }

        [JsonIgnore]
        public IReadOnlyList<ResourceRef> ModelResourceRefs { get; set; }

        [JsonIgnore]
        public ResourceRef ModelResultResourceRef { get; set; }

        [JsonIgnore]
        public string ModelResultResourceKind { get; set; }

        public static ToolRunResult Ok(string message, string dataJson = null)
        {
            return Create(true, "ok", message, dataJson, null, null);
        }

        public static ToolRunResult Error(string message, string dataJson = null,
            string code = null, bool? retryable = null)
        {
            return Create(false, "error", message, dataJson, code, retryable);
        }

        public static ToolRunResult Unknown(string message, string dataJson = null,
            string code = "tool_effect_uncertain")
        {
            return Create(false, "unknown", message, dataJson, code, false);
        }

        public static ToolRunResult AwaitingConfirmation(string message,
            string dataJson, string catalogRevision)
        {
            var result = Create(false, "awaiting_confirmation", message,
                dataJson, null, false);
            result.CatalogRevision = catalogRevision;
            return result;
        }

        public static ToolRunResult AwaitingUser(string message, string dataJson)
        {
            return Create(true, "awaiting_user", message, dataJson, null, false);
        }

        public static ToolRunResult Cancelled(string message)
        {
            return Create(false, "cancelled", message, null,
                "tool_cancelled", false);
        }

        internal static ToolRunResult Running()
        {
            return Create(false, "running", string.Empty, null, null, false);
        }

        private static ToolRunResult Create(bool success, string status,
            string message, string dataJson, string code, bool? retryable)
        {
            return new ToolRunResult
            {
                Success = success,
                Status = status,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = code,
                Retryable = retryable
            };
        }
    }
}
