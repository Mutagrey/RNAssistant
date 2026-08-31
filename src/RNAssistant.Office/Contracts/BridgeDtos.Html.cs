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

    public sealed class HtmlWorkspaceFilePayload : ChatPayload
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("setActive")]
        public bool? SetActive { get; set; }
    }

    public sealed class HtmlWorkspaceDataPayload : ChatPayload
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("json")]
        public string Json { get; set; }
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

    public class UploadedHtmlSourcePayload : ChatPayload
    {
        [JsonProperty("sourceResourceUri")]
        public string SourceResourceUri { get; set; }
    }

    public sealed class HtmlWorkspaceImportPayload : UploadedHtmlSourcePayload
    {
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

    public sealed class UploadedHtmlSourcePreviewDto
    {
        [JsonProperty("sourceResourceUri")] public string SourceResourceUri { get; set; }
        [JsonProperty("mimeType")] public string MimeType { get; set; }
        [JsonProperty("contentSha256")] public string ContentSha256 { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("returnedCharacters")] public int ReturnedCharacters { get; set; }
        [JsonProperty("totalCharacters")] public int TotalCharacters { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
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
