using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Application.Contracts
{
    public sealed class PluginCloseRequest { [JsonProperty("instanceId")] public string InstanceId { get; set; } }
    public sealed class PluginVendorRequest
    { [JsonProperty("id")] public string Id { get; set; } [JsonProperty("version")] public string Version { get; set; } }
    public sealed class PluginVendorCatalogDto { [JsonProperty("items")] public IReadOnlyList<VendorManifestV1> Items { get; set; } }
    public sealed class HtmlVendorSourceDto
    { [JsonProperty("js")] public string Js { get; set; } [JsonProperty("css")] public string Css { get; set; } }
    public sealed class PluginIdRequest { [JsonProperty("pluginId")] public string PluginId { get; set; } }
    public sealed class PluginConnectRequest { [JsonProperty("rootPath")] public string RootPath { get; set; } }
    public sealed class PluginCreateRequest
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("rootPath")] public string RootPath { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("iconDescription")] public string IconDescription { get; set; }
        [JsonProperty("iconId")] public string IconId { get; set; }
        [JsonProperty("iconColor")] public string IconColor { get; set; }
    }
    public sealed class PluginIconSaveRequest
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("svg")] public string Svg { get; set; }
    }
    public sealed class PluginAppearanceRequest
    {
        [JsonProperty("pluginId", Required = Required.Always)] public string PluginId { get; set; }
        [JsonProperty("name", Required = Required.Always)] public string Name { get; set; }
        [JsonProperty("iconId")] public string IconId { get; set; }
        [JsonProperty("iconColor")] public string IconColor { get; set; }
    }
    public sealed class PluginIconDrawRequest
    {
        [JsonProperty("iconColor")] public string IconColor { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
    }
    public sealed class PluginIconDeleteRequest { [JsonProperty("id")] public string Id { get; set; } }
    public sealed class PluginIconCatalogDto { [JsonProperty("items")] public IReadOnlyList<PluginIconRecord> Items { get; set; } }
    public sealed class PluginVisibilityDto
    {
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("hidden")] public bool Hidden { get; set; }
    }
    public sealed class PluginApplyRequest
    {
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("candidateId")] public string CandidateId { get; set; }
    }
    public sealed class PluginOpenRequest
    {
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("candidateId")] public string CandidateId { get; set; }
        [JsonProperty("releaseId")] public string ReleaseId { get; set; }
    }
    public sealed class PluginReleaseHistoryRequest
    {
        [JsonProperty("pluginId", Required = Required.Always)] public string PluginId { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
    }
    public sealed class PluginPublishedVersionDto
    {
        [JsonProperty("releaseId")] public string ReleaseId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("capturedUtc")] public DateTime CapturedUtc { get; set; }
        [JsonProperty("active")] public bool Active { get; set; }
    }
    public sealed class PluginReleaseHistoryDto
    {
        [JsonProperty("items")] public IReadOnlyList<PluginPublishedVersionDto> Items { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
    }
    public sealed class PluginRestoreVersionRequest
    {
        [JsonProperty("pluginId", Required = Required.Always)] public string PluginId { get; set; }
        [JsonProperty("releaseId", Required = Required.Always)] public string ReleaseId { get; set; }
        [JsonProperty("expectedActiveReleaseId")] public string ExpectedActiveReleaseId { get; set; }
    }
    public sealed class PluginCatalogDto
    {
        [JsonProperty("error")] public string Error { get; set; }
        [JsonProperty("items")] public IReadOnlyList<PluginItemDto> Items { get; set; }
        [JsonProperty("defaultSourceParent", NullValueHandling = NullValueHandling.Ignore)] public string DefaultSourceParent { get; set; }
    }
    public sealed class PluginItemDto
    {
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("icon")] public string Icon { get; set; }
        [JsonProperty("iconSvg", NullValueHandling = NullValueHandling.Ignore)] public string IconSvg { get; set; }
        [JsonProperty("iconColor", NullValueHandling = NullValueHandling.Ignore)] public string IconColor { get; set; }
        [JsonProperty("iconError", NullValueHandling = NullValueHandling.Ignore)] public string IconError { get; set; }
        [JsonProperty("sourceWorkspaceId")] public string SourceWorkspaceId { get; set; }
        [JsonProperty("sourceRoot")] public string SourceRoot { get; set; }
        [JsonProperty("releaseId")] public string ReleaseId { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("hidden")] public bool Hidden { get; set; }
    }
    public sealed class PluginCandidateDto
    {
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("candidateId")] public string CandidateId { get; set; }
        [JsonProperty("hasChanges")] public bool HasChanges { get; set; }
        [JsonProperty("files")] public int Files { get; set; }
        [JsonProperty("bytes")] public long Bytes { get; set; }
        [JsonProperty("checks")] public string[] Checks { get; set; }
    }
    public sealed class PluginCheckSummary
    {
        [JsonProperty("files")] public int Files { get; set; }
        [JsonProperty("bytes")] public long Bytes { get; set; }
        [JsonProperty("checks")] public string[] Checks { get; set; }
    }
    public sealed class PluginPublicationSummary
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; }
    }
    public sealed class PluginViewDto
    {
        [JsonProperty("instanceId")] public string InstanceId { get; set; }
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("releaseId")] public string ReleaseId { get; set; }
        [JsonProperty("entryUrl")] public string EntryUrl { get; set; }
        [JsonProperty("preview")] public bool Preview { get; set; }
    }
    public sealed class PluginChatMessageDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("reasoningContent", NullValueHandling = NullValueHandling.Ignore)] public string ReasoningContent { get; set; }
        [JsonProperty("reasoningTokens", NullValueHandling = NullValueHandling.Ignore)] public int? ReasoningTokens { get; set; }
        [JsonProperty("reasoningTruncated")] public bool ReasoningTruncated { get; set; }
        [JsonProperty("activity", NullValueHandling = NullValueHandling.Ignore)] public ChatActivity Activity { get; set; }
        [JsonProperty("role")] public string Role { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("actions")] public IReadOnlyList<string> Actions { get; set; }
        [JsonProperty("reflectionDecision", NullValueHandling = NullValueHandling.Ignore)]
        public string ReflectionDecision { get; set; }
        [JsonProperty("results", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<ModelResultViewState> Results { get; set; }
    }
    public sealed class PluginSourceStateDto
    {
        [JsonProperty("pluginId")] public string PluginId { get; set; }
        [JsonProperty("stamp")] public string Stamp { get; set; }
    }
    public sealed class ProjectChatRequest
    {
        [JsonProperty("workspaceId", Required = Required.Always)] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("input")] public WorkspaceSubmitInputRequest Input { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
        [JsonProperty("pendingId")] public string PendingId { get; set; }
        [JsonProperty("approve")] public bool Approve { get; set; }
    }
    public sealed class ProjectChatUploadOpenRequest
    {
        [JsonProperty("workspaceId", Required = Required.Always)] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId", Required = Required.Always)] public string SessionId { get; set; }
        [JsonProperty("fileName", Required = Required.Always)] public string FileName { get; set; }
        [JsonProperty("byteLength", Required = Required.Always)] public long ByteLength { get; set; }
    }
    public sealed class ProjectChatUploadChunkRequest
    {
        [JsonProperty("workspaceId", Required = Required.Always)] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId", Required = Required.Always)] public string SessionId { get; set; }
        [JsonProperty("leaseId", Required = Required.Always)] public string LeaseId { get; set; }
        [JsonProperty("offset", Required = Required.Always)] public long Offset { get; set; }
        [JsonProperty("base64", Required = Required.Always)] public string Base64 { get; set; }
    }
    public sealed class ProjectChatUploadLeaseRequest
    {
        [JsonProperty("workspaceId", Required = Required.Always)] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId", Required = Required.Always)] public string SessionId { get; set; }
        [JsonProperty("leaseId", Required = Required.Always)] public string LeaseId { get; set; }
    }
    public sealed class ProjectChatUploadOpenDto
    {
        [JsonProperty("leaseId")] public string LeaseId { get; set; }
        [JsonProperty("maxChunkBytes")] public int MaxChunkBytes { get; set; }
        [JsonProperty("relativePath")] public string RelativePath { get; set; }
    }
    public sealed class ProjectChatUploadProgressDto
    {
        [JsonProperty("nextOffset")] public long NextOffset { get; set; }
    }
    public sealed class ProjectChatImportedFileDto
    {
        [JsonProperty("relativePath")] public string RelativePath { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
        [JsonProperty("modelReadable")] public bool ModelReadable { get; set; }
    }
    public sealed class ProjectChatProjectDto
    {
        [JsonProperty("pluginId", NullValueHandling = NullValueHandling.Ignore)] public string PluginId { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("rootPath")] public string RootPath { get; set; }
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("chats")] public IReadOnlyList<ChatSessionHeader> Chats { get; set; }
    }
    public sealed class ProjectChatCatalogDto
    {
        [JsonProperty("projects")] public IReadOnlyList<ProjectChatProjectDto> Projects { get; set; }
        [JsonProperty("selectedWorkspaceId", NullValueHandling = NullValueHandling.Ignore)] public string SelectedWorkspaceId { get; set; }
    }
    public sealed class ProjectConnectionRequest
    {
        [JsonProperty("rootPath", Required = Required.Always)] public string RootPath { get; set; }
    }
    public sealed class ProjectChatStateDto
    {
        [JsonProperty("project")] public ProjectChatProjectDto Project { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("state")] public WorkspaceSessionSnapshot State { get; set; }
        [JsonProperty("pendingId")] public string PendingId { get; set; }
        [JsonProperty("pendingTool")] public string PendingTool { get; set; }
        [JsonProperty("pendingArguments")] public string PendingArguments { get; set; }
        [JsonProperty("cancelStatus", NullValueHandling = NullValueHandling.Ignore)] public WorkspaceCancellationStatus? CancelStatus { get; set; }
        [JsonProperty("messages")] public IReadOnlyList<PluginChatMessageDto> Messages { get; set; }
    }
    public sealed class PluginDataSelection
    {
        public string RootPath { get; set; }
        public bool Writable { get; set; }
    }
    public interface IPluginProjectPicker
    { Task<PluginDataSelection> PickAsync(CancellationToken token); }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum PluginMethod { Ready, PickProject, BeginResult, CommitResult, OpenResult, DropResult, List, Stat, OpenFile, CloseFile, BeginWrite, CommitWrite, AbortWrite, MakeDirectory, ReconcileFile, Invoke, Cancel, ReportError }
    public sealed class PluginFrameRequest
    {
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("requestId")] public string RequestId { get; set; }
        [JsonProperty("method")] public PluginMethod Method { get; set; }
        [JsonProperty("projectId")] public string ProjectId { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("handle")] public string Handle { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("command")] public string Command { get; set; }
        [JsonProperty("argumentsJson")] public string ArgumentsJson { get; set; }
        [JsonProperty("invocationId")] public string InvocationId { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }
    public sealed class PluginFrameResponse
    {
        [JsonProperty("version")] public int Version { get; set; } = 1;
        [JsonProperty("requestId")] public string RequestId { get; set; }
        [JsonProperty("progress")] public string Progress { get; set; }
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("result")] public PluginFrameResult Result { get; set; }
    }
    public sealed class PluginFrameResult
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("handle")] public string Handle { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("byteLength")] public long? ByteLength { get; set; }
        [JsonProperty("items")] public IReadOnlyList<WorkspaceFileEntryDto> Items { get; set; }
        [JsonProperty("nextOffset")] public int? NextOffset { get; set; }
        [JsonProperty("json")] public string Json { get; set; }
        [JsonProperty("writable")] public bool Writable { get; set; }
    }
    public sealed class PluginCommandResult
    {
        public string Json { get; set; }
        public bool HadVerifiedWrites { get; set; }
        public bool EffectUnknown { get; set; }
    }
    public sealed class PluginWorkerEnvelope
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("request")] public PluginFrameRequest Request { get; set; }
        [JsonProperty("json")] public string Json { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
    }
    public sealed class PluginWorkerStart
    {
        [JsonProperty("type")] public string Type { get; } = "start";
        [JsonProperty("entry")] public string Entry { get; set; }
        [JsonProperty("command")] public string Command { get; set; }
        [JsonProperty("argumentsJson")] public string ArgumentsJson { get; set; }
    }
    public interface IPluginCommandExecutor
    {
        Task<string> ExecuteAsync(PluginInstance operation, PluginCommandDefinition command,
            string argumentsJson, CancellationToken token, Action<string> progress);
    }
}
