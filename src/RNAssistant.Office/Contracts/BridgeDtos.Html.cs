using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Contracts
{
    public sealed class HtmlFetchRequest
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("method")]
        public string Method { get; set; }

        [JsonProperty("headers")]
        public Dictionary<string, string> Headers { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }
    }

    public sealed class HtmlOriginPayload
    {
        [JsonProperty("origin")]
        public string Origin { get; set; }
    }

    public sealed class HtmlOriginPermissionResponse
    {
        [JsonProperty("origin")]
        public string Origin { get; set; }

        [JsonProperty("allowed")]
        public bool Allowed { get; set; }
    }

    public sealed class HtmlFetchResponse
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("statusText")]
        public string StatusText { get; set; }

        [JsonProperty("headers")]
        public Dictionary<string, string> Headers { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }
    }

    public sealed class HtmlWorkspaceMutationUploadRequest : ChatPayload
    {
        [JsonProperty("byteLength", Required = Required.Always)]
        public long ByteLength { get; set; }
    }

    public abstract class HtmlWorkspaceMutationPayload : ChatPayload
    {
        // Empty is an explicit guard for a workspace that has not been created yet.
        [JsonProperty("expectedActiveHtmlArtifactId", Required = Required.Always)]
        public string ExpectedActiveHtmlArtifactId { get; set; }

        [JsonProperty("uploadLeaseId", Required = Required.Always)]
        public string UploadLeaseId { get; set; }

        [JsonProperty("sha256", Required = Required.Always)]
        public string Sha256 { get; set; }
    }

    public sealed class HtmlWorkspaceFilePayload : HtmlWorkspaceMutationPayload
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("setActive")]
        public bool? SetActive { get; set; }
    }

    public sealed class HtmlWorkspaceDataPayload : HtmlWorkspaceMutationPayload
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public sealed class HtmlWorkspaceDeleteFilePayload : ChatPayload
    {
        [JsonProperty("path")]
        public string Path { get; set; }
    }

    public sealed class HtmlWorkspaceDeleteDataPayload : ChatPayload
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public sealed class HtmlWorkspaceActiveFilePayload : ChatPayload
    {
        [JsonProperty("path")]
        public string Path { get; set; }
    }

    public sealed class HtmlWorkspaceRestorePayload : ChatPayload
    {
        [JsonProperty("snapshotId")]
        public string SnapshotId { get; set; }
    }

    public sealed class HtmlWorkspaceImportPayload : ChatPayload
    {
        [JsonProperty("sourceResourceUri")]
        public string SourceResourceUri { get; set; }

        [JsonProperty("expectedActiveHtmlArtifactId")]
        public string ExpectedActiveHtmlArtifactId { get; set; }

        [JsonProperty("targetPath")]
        public string TargetPath { get; set; }
    }

    public sealed class HtmlWorkspaceExportPayload : ChatPayload
    {
        [JsonProperty("expectedActiveHtmlArtifactId")]
        public string ExpectedActiveHtmlArtifactId { get; set; }
    }

    public sealed class HtmlWorkspaceResponse
    {
        [JsonProperty("sessionRevision")]
        public long SessionRevision { get; set; }

        [JsonProperty("activeChatId")]
        public string ActiveChatId { get; set; }

        [JsonProperty("activeHtmlArtifactId")]
        public string ActiveHtmlArtifactId { get; set; }

        [JsonProperty("artifacts")]
        public IReadOnlyList<ChatArtifactDto> Artifacts { get; set; }

        [JsonProperty("artifactLibrary")]
        public ArtifactLibraryProjectionDto ArtifactLibrary { get; set; }

        [JsonProperty("workspace")]
        public HtmlWorkspaceDto Workspace { get; set; }

        [JsonProperty("staticPreflight")]
        public HtmlWorkspacePreflightDto StaticPreflight { get; set; }

        [JsonProperty("redoChoiceRequired")]
        public bool RedoChoiceRequired { get; set; }

        [JsonProperty("importedPath", NullValueHandling = NullValueHandling.Ignore)]
        public string ImportedPath { get; set; }

        [JsonProperty("importedFromResourceUri", NullValueHandling = NullValueHandling.Ignore)]
        public string ImportedFromResourceUri { get; set; }

        [JsonProperty("exportRevisionArtifactId", NullValueHandling = NullValueHandling.Ignore)]
        public string ExportRevisionArtifactId { get; set; }

        [JsonProperty("exportResourceUri", NullValueHandling = NullValueHandling.Ignore)]
        public string ExportResourceUri { get; set; }

        [JsonProperty("exportContentSha256", NullValueHandling = NullValueHandling.Ignore)]
        public string ExportContentSha256 { get; set; }

        [JsonProperty("resourceExport", NullValueHandling = NullValueHandling.Ignore)]
        public HtmlResourceExport ResourceExport { get; set; }
    }

    public sealed class HtmlResourceExport
    {
        [JsonProperty("bindings")] public IReadOnlyList<HtmlResourceExportBinding> Bindings { get; set; }
        [JsonProperty("generations")] public IReadOnlyDictionary<string, long> Generations { get; set; }
    }

    public sealed class HtmlResourceExportBinding
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("lease")] public ResourceDataOpenResponse Lease { get; set; }
    }

    public sealed class HtmlWorkspacePreflightDto
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("passed")] public bool Passed { get; set; }
        [JsonProperty("entryName")] public string EntryName { get; set; }
        [JsonProperty("errorCount")] public int ErrorCount { get; set; }
        [JsonProperty("warningCount")] public int WarningCount { get; set; }
        [JsonProperty("issueCount")] public int IssueCount { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("issues")] public IReadOnlyList<HtmlWorkspacePreflightIssueDto> Issues { get; set; }
    }

    public sealed class HtmlWorkspacePreflightIssueDto
    {
        [JsonProperty("severity")] public string Severity { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("line", NullValueHandling = NullValueHandling.Ignore)]
        public int? Line { get; set; }
        [JsonProperty("column", NullValueHandling = NullValueHandling.Ignore)]
        public int? Column { get; set; }
    }

    public sealed class HtmlWorkspaceDto
    {
        [JsonProperty("activeFileId")] public string ActiveFileId { get; set; }
        [JsonProperty("files")] public IReadOnlyList<HtmlWorkspaceFile> Files { get; set; }
        [JsonProperty("dataSources")] public IReadOnlyList<HtmlWorkspaceDataSource> DataSources { get; set; }
        [JsonProperty("history")] public IReadOnlyList<HtmlWorkspaceSnapshotDto> History { get; set; }
        [JsonProperty("redoHistory")] public IReadOnlyList<HtmlWorkspaceSnapshotDto> RedoHistory { get; set; }
        [JsonProperty("redoBranches")] public IReadOnlyList<HtmlWorkspaceRedoBranchDto> RedoBranches { get; set; }
        [JsonProperty("recovery")] public HtmlWorkspaceRecoveryDto Recovery { get; set; }
        [JsonProperty("updatedUtc")] public System.DateTime UpdatedUtc { get; set; }

        public static HtmlWorkspaceDto From(HtmlWorkspace workspace)
        {
            return From(workspace, null);
        }

        public static HtmlWorkspaceDto From(HtmlWorkspace workspace, HtmlWorkspaceRecoveryState recovery)
        {
            workspace = workspace ?? new HtmlWorkspace();
            var redoBranches = RedoBranchSummaries(workspace.RedoBranches);
            return new HtmlWorkspaceDto
            {
                ActiveFileId = workspace.ActiveFileId,
                Files = HtmlWorkspaceCopyService.CloneFiles(workspace.Files),
                DataSources = HtmlWorkspaceCopyService.CloneDataSources(workspace.DataSources),
                History = SnapshotSummaries(workspace.History),
                RedoHistory = redoBranches.Select(item => new HtmlWorkspaceSnapshotDto
                {
                    Id = item.Id,
                    Label = item.Label,
                    CreatedUtc = item.CreatedUtc
                }).ToList(),
                RedoBranches = redoBranches,
                Recovery = HtmlWorkspaceRecoveryDto.From(recovery),
                UpdatedUtc = workspace.UpdatedUtc
            };
        }

        private static IReadOnlyList<HtmlWorkspaceRedoBranchDto> RedoBranchSummaries(IEnumerable<HtmlWorkspaceRedoBranch> branches)
        {
            return (branches ?? new HtmlWorkspaceRedoBranch[0])
                .Where(branch => branch != null)
                .Select(branch => new HtmlWorkspaceRedoBranchDto
                {
                    Id = branch.Id,
                    ParentArtifactId = branch.ParentArtifactId,
                    Label = branch.Label,
                    Revision = branch.Revision,
                    FileCount = branch.FileCount,
                    DataSourceCount = branch.DataSourceCount,
                    CreatedUtc = branch.CreatedUtc
                }).ToList();
        }

        private static IReadOnlyList<HtmlWorkspaceSnapshotDto> SnapshotSummaries(IEnumerable<HtmlWorkspaceSnapshot> snapshots)
        {
            var result = new List<HtmlWorkspaceSnapshotDto>();
            foreach (var snapshot in snapshots ?? new HtmlWorkspaceSnapshot[0])
            {
                if (snapshot == null) continue;
                result.Add(new HtmlWorkspaceSnapshotDto
                {
                    Id = snapshot.Id,
                    Label = snapshot.Label,
                    CreatedUtc = snapshot.CreatedUtc
                });
            }
            return result;
        }
    }

    public sealed class HtmlWorkspaceRecoveryDto
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("issue")] public string Issue { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("activeArtifactId")] public string ActiveArtifactId { get; set; }
        [JsonProperty("problemArtifactId")] public string ProblemArtifactId { get; set; }
        [JsonProperty("canMutate")] public bool CanMutate { get; set; }
        [JsonProperty("candidates")] public IReadOnlyList<HtmlWorkspaceRecoveryCandidateDto> Candidates { get; set; }

        public static HtmlWorkspaceRecoveryDto From(HtmlWorkspaceRecoveryState recovery)
        {
            recovery = recovery ?? new HtmlWorkspaceRecoveryState();
            return new HtmlWorkspaceRecoveryDto
            {
                Status = recovery.Status,
                Issue = recovery.Issue,
                Message = recovery.Message,
                ActiveArtifactId = recovery.ActiveArtifactId,
                ProblemArtifactId = recovery.ProblemArtifactId,
                CanMutate = recovery.CanMutate,
                Candidates = (recovery.Candidates ?? new List<HtmlWorkspaceRecoveryCandidate>())
                    .Where(item => item != null)
                    .Select(item => new HtmlWorkspaceRecoveryCandidateDto
                    {
                        Id = item.Id,
                        ParentArtifactId = item.ParentArtifactId,
                        Label = item.Label,
                        Revision = item.Revision,
                        FileCount = item.FileCount,
                        DataSourceCount = item.DataSourceCount,
                        CreatedUtc = item.CreatedUtc
                    }).ToList()
            };
        }
    }

    public sealed class HtmlWorkspaceRecoveryCandidateDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("parentArtifactId")] public string ParentArtifactId { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("fileCount")] public int? FileCount { get; set; }
        [JsonProperty("dataSourceCount")] public int? DataSourceCount { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime CreatedUtc { get; set; }
    }

    public sealed class HtmlWorkspaceSnapshotDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime CreatedUtc { get; set; }
    }

    public sealed class HtmlWorkspaceRedoBranchDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("parentArtifactId")] public string ParentArtifactId { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("fileCount")] public int? FileCount { get; set; }
        [JsonProperty("dataSourceCount")] public int? DataSourceCount { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime CreatedUtc { get; set; }
    }
}
