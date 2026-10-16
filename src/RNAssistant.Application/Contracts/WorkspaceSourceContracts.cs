using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum WorkspaceSourceState { Pending, Running, Prepared, Failed, Cancelled }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum WorkspaceSourceQueueState { Running, Pausing, Paused, Finished }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum WorkspaceSourceCommand { Pause, Resume, CancelCurrent, CancelPending, CancelAll, RetrySelected }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum WorkspaceSourceSort { Added, Path, LargestFirst }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum WorkspaceSourcePhase { WaitingForReader, CheckingProject, Capturing }

    public sealed class PrepareWorkspaceSourcesRequest
    {
        [JsonProperty("bindingId", Required = Required.Always)] public string BindingId { get; set; }
        [JsonProperty("projectRevision", Required = Required.Always)] public long ProjectRevision { get; set; }
        [JsonProperty("paths", Required = Required.Always)] public List<string> Paths { get; set; }
        [JsonProperty("maximumFileBytes", Required = Required.Always)] public long MaximumFileBytes { get; set; }
        [JsonProperty("maximumBatchBytes", Required = Required.Always)] public long MaximumBatchBytes { get; set; }
    }

    public sealed class WorkspaceSourcePageRequest
    {
        [JsonProperty("queueId", Required = Required.Always)] public string QueueId { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; } = 50;
        [JsonProperty("query")] public string Query { get; set; }
        [JsonProperty("state")] public WorkspaceSourceState? State { get; set; }
        [JsonProperty("sort")] public WorkspaceSourceSort Sort { get; set; }
    }

    public sealed class WorkspaceSourceControlRequest
    {
        [JsonProperty("queueId", Required = Required.Always)] public string QueueId { get; set; }
        [JsonProperty("command", Required = Required.Always)] public WorkspaceSourceCommand Command { get; set; }
        [JsonProperty("itemIds")] public List<string> ItemIds { get; set; }
    }

    public sealed class WorkspaceSourceProgressDto
    {
        [JsonProperty("stage"), JsonConverter(typeof(StringEnumConverter))] public FileTransferStage Stage { get; internal set; }
        [JsonProperty("completedBytes")] public long CompletedBytes { get; internal set; }
        [JsonProperty("totalBytes")] public long TotalBytes { get; internal set; }
    }

    public sealed class WorkspaceSourceItemDto
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("path")] public string Path { get; internal set; }
        [JsonProperty("state")] public WorkspaceSourceState State { get; internal set; }
        [JsonProperty("phase")] public WorkspaceSourcePhase? Phase { get; internal set; }
        [JsonProperty("byteLength")] public long? ByteLength { get; internal set; }
        [JsonProperty("progress")] public WorkspaceSourceProgressDto Progress { get; internal set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; internal set; }
        [JsonProperty("errorMessage")] public string ErrorMessage { get; internal set; }
    }

    public sealed class WorkspaceSourceQueuePage
    {
        [JsonProperty("type")] public string Type { get { return "rnassistant.workspaceSources"; } }
        [JsonProperty("contractVersion")] public int ContractVersion { get { return 1; } }
        [JsonProperty("queueId")] public string QueueId { get; internal set; }
        [JsonProperty("revision")] public long Revision { get; internal set; }
        [JsonProperty("bindingId")] public string BindingId { get; internal set; }
        [JsonProperty("projectRevision")] public long ProjectRevision { get; internal set; }
        [JsonProperty("manifestPath")] public string ManifestPath { get; internal set; }
        [JsonProperty("state")] public WorkspaceSourceQueueState State { get; internal set; }
        [JsonProperty("total")] public int Total { get; internal set; }
        [JsonProperty("pending")] public int Pending { get; internal set; }
        [JsonProperty("running")] public int Running { get; internal set; }
        [JsonProperty("prepared")] public int Prepared { get; internal set; }
        [JsonProperty("failed")] public int Failed { get; internal set; }
        [JsonProperty("cancelled")] public int Cancelled { get; internal set; }
        [JsonProperty("capturedBytes")] public long CapturedBytes { get; internal set; }
        [JsonProperty("maximumFileBytes")] public long MaximumFileBytes { get; internal set; }
        [JsonProperty("maximumBatchBytes")] public long MaximumBatchBytes { get; internal set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; internal set; }
        [JsonProperty("errorMessage")] public string ErrorMessage { get; internal set; }
        [JsonProperty("matched")] public int Matched { get; internal set; }
        [JsonProperty("offset")] public int Offset { get; internal set; }
        [JsonProperty("items")] public IReadOnlyList<WorkspaceSourceItemDto> Items { get; internal set; }
        [JsonProperty("currentItem")] public WorkspaceSourceItemDto CurrentItem { get; internal set; }
    }
}
