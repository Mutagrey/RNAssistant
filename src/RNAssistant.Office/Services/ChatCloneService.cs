using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    public static class ChatCloneService
    {
        public static ChatSession CloneSessionSnapshot(ChatSession session)
        {
            if (session == null)
            {
                return null;
            }

            var messages = CloneMessages(session.Messages);
            return new ChatSession
            {
                FormatVersion = session.FormatVersion,
                Revision = session.Revision,
                Id = session.Id,
                ParentSessionId = session.ParentSessionId,
                ParentSessionRevision = session.ParentSessionRevision,
                ForkedThroughMessageId = session.ForkedThroughMessageId,
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                PreviousDocumentKeys = (session.PreviousDocumentKeys ?? new List<string>()).ToList(),
                DocumentTitle = session.DocumentTitle,
                DocumentPath = session.DocumentPath,
                Title = session.Title,
                Model = session.Model,
                Mode = session.Mode,
                ReasoningEnabled = session.ReasoningEnabled,
                CreatedUtc = session.CreatedUtc,
                UpdatedUtc = session.UpdatedUtc,
                Context = CloneContext(session.Context),
                LastRun = CloneRun(session.LastRun),
                HtmlWorkspace = CloneWorkspace(session.HtmlWorkspace),
                HtmlWorkspaceRecovery = CloneWorkspaceRecovery(session.HtmlWorkspaceRecovery),
                Messages = messages,
                ContextCheckpoints = CloneContextCheckpoints(session.ContextCheckpoints, messages),
                ActiveContextCheckpointId = session.ActiveContextCheckpointId,
                Artifacts = (session.Artifacts ?? new List<ChatArtifact>())
                    .Where(artifact => artifact != null)
                    .Select(CloneArtifact)
                    .ToList(),
                ActiveHtmlArtifactId = session.ActiveHtmlArtifactId,
                ActivePlanArtifactId = session.ActivePlanArtifactId
            };
        }

        public static DocumentContext CloneContext(DocumentContext context)
        {
            if (context == null)
            {
                return null;
            }

            return new DocumentContext
            {
                Host = context.Host,
                DocumentKey = context.DocumentKey,
                Title = context.Title,
                UpdatedUtc = context.UpdatedUtc,
                Notes = context.Notes == null ? null : context.Notes.Select(CloneContextNote).ToList()
            };
        }

        public static List<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages)
        {
            return messages == null
                ? new List<ChatMessage>()
                : messages.Select(CloneMessage).ToList();
        }

        public static List<ContextCheckpoint> CloneContextCheckpoints(IEnumerable<ContextCheckpoint> checkpoints, IEnumerable<ChatMessage> messages)
        {
            var messageIds = new HashSet<string>((messages ?? new ChatMessage[0]).Where(message => message != null).Select(message => message.Id), System.StringComparer.OrdinalIgnoreCase);
            return (checkpoints ?? new ContextCheckpoint[0])
                .Where(checkpoint => checkpoint != null && messageIds.Contains(checkpoint.ThroughMessageId))
                .Select(checkpoint => new ContextCheckpoint
                {
                    Id = checkpoint.Id,
                    ThroughMessageId = checkpoint.ThroughMessageId,
                    SummaryJson = checkpoint.SummaryJson,
                    SummaryMarkdown = checkpoint.SummaryMarkdown,
                    Model = checkpoint.Model,
                    PromptVersion = checkpoint.PromptVersion,
                    SourceMessageCount = checkpoint.SourceMessageCount,
                    SourceTokens = checkpoint.SourceTokens,
                    SummaryTokens = checkpoint.SummaryTokens,
                    CreatedUtc = checkpoint.CreatedUtc
                }).ToList();
        }

        public static List<ChatArtifact> CloneArtifactsForMessages(IEnumerable<ChatArtifact> artifacts, IEnumerable<ChatMessage> messages)
        {
            return ChatResourceReferenceService.ReachableForMessages(artifacts, messages)
                .Select(CloneArtifact)
                .ToList();
        }

        public static HtmlWorkspace CloneWorkspaceForFork(HtmlWorkspace workspace)
        {
            return HtmlWorkspaceCopyService.CloneCurrent(workspace);
        }

        private static ChatRunRecord CloneRun(ChatRunRecord run)
        {
            return run == null ? null : new ChatRunRecord
            {
                RunId = run.RunId,
                TurnId = run.TurnId,
                RuntimeId = run.RuntimeId,
                Status = run.Status,
                Phase = run.Phase,
                CurrentAction = run.CurrentAction,
                DocumentRuntimeKey = run.DocumentRuntimeKey,
                IterationsUsed = run.IterationsUsed,
                ToolStepsUsed = run.ToolStepsUsed,
                StartedUtc = run.StartedUtc
            };
        }

        private static HtmlWorkspace CloneWorkspace(HtmlWorkspace workspace)
        {
            workspace = workspace ?? new HtmlWorkspace();
            return new HtmlWorkspace
            {
                ActiveFileId = workspace.ActiveFileId,
                Files = HtmlWorkspaceCopyService.CloneFiles(workspace.Files),
                DataSources = HtmlWorkspaceCopyService.CloneDataSources(workspace.DataSources),
                History = CloneSnapshots(workspace.History),
                RedoBranches = CloneRedoBranches(workspace.RedoBranches),
                UpdatedUtc = workspace.UpdatedUtc
            };
        }

        private static HtmlWorkspaceRecoveryState CloneWorkspaceRecovery(HtmlWorkspaceRecoveryState recovery)
        {
            recovery = recovery ?? new HtmlWorkspaceRecoveryState();
            return new HtmlWorkspaceRecoveryState
            {
                Status = recovery.Status,
                Issue = recovery.Issue,
                Message = recovery.Message,
                ActiveArtifactId = recovery.ActiveArtifactId,
                ProblemArtifactId = recovery.ProblemArtifactId,
                CanMutate = recovery.CanMutate,
                Candidates = (recovery.Candidates ?? new List<HtmlWorkspaceRecoveryCandidate>())
                    .Where(item => item != null)
                    .Select(item => new HtmlWorkspaceRecoveryCandidate
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

        private static List<HtmlWorkspaceRedoBranch> CloneRedoBranches(IEnumerable<HtmlWorkspaceRedoBranch> branches)
        {
            return (branches ?? new HtmlWorkspaceRedoBranch[0])
                .Where(branch => branch != null)
                .Select(branch => new HtmlWorkspaceRedoBranch
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

        private static List<HtmlWorkspaceSnapshot> CloneSnapshots(IEnumerable<HtmlWorkspaceSnapshot> snapshots)
        {
            return (snapshots ?? new HtmlWorkspaceSnapshot[0])
                .Where(snapshot => snapshot != null)
                .Select(snapshot => new HtmlWorkspaceSnapshot
                {
                    Id = snapshot.Id,
                    Label = snapshot.Label,
                    ActiveFileId = snapshot.ActiveFileId,
                    Files = HtmlWorkspaceCopyService.CloneFiles(snapshot.Files),
                    DataSources = HtmlWorkspaceCopyService.CloneDataSources(snapshot.DataSources),
                    CreatedUtc = snapshot.CreatedUtc
                }).ToList();
        }

        private static ChatMessage CloneMessage(ChatMessage message)
        {
            if (message == null)
            {
                return null;
            }

            return new ChatMessage
            {
                Id = message.Id,
                Role = message.Role,
                Content = message.Content,
                ExcludeFromModelContext = message.ExcludeFromModelContext,
                ProtocolMessage = message.ProtocolMessage,
                ToolCallId = message.ToolCallId,
                ToolName = message.ToolName,
                ToolResultRole = message.ToolResultRole,
                ToolCalls = message.ToolCalls == null
                    ? new List<LlmToolCall>()
                    : message.ToolCalls.Where(call => call != null).Select(call => new LlmToolCall
                    {
                        Id = call.Id,
                        Type = call.Type,
                        Name = call.Name,
                        ArgumentsJson = call.ArgumentsJson
                    }).ToList(),
                Attachments = message.Attachments == null
                    ? new List<ChatAttachment>()
                    : message.Attachments.Select(CloneAttachment).ToList(),
                AttachmentAnalysis = CloneAttachmentAnalysis(message.AttachmentAnalysis),
                ResourceRefs = CloneResourceRefs(message.ResourceRefs),
                HtmlWorkspaceCheckpoint = CloneResourceRef(message.HtmlWorkspaceCheckpoint),
                Activity = CloneActivity(message.Activity),
                PromptTokens = message.PromptTokens,
                CompletionTokens = message.CompletionTokens,
                TotalTokens = message.TotalTokens,
                UsageJson = message.UsageJson,
                ReasoningContent = message.ReasoningContent,
                ReasoningTokens = message.ReasoningTokens,
                ReasoningTruncated = message.ReasoningTruncated,
                RunId = message.RunId,
                Sequence = message.Sequence,
                CreatedUtc = message.CreatedUtc
            };
        }

        private static AttachmentAnalysisContext CloneAttachmentAnalysis(AttachmentAnalysisContext analysis)
        {
            if (analysis == null) return null;
            return new AttachmentAnalysisContext
            {
                PromptVersion = analysis.PromptVersion,
                SourceFingerprint = analysis.SourceFingerprint,
                Content = analysis.Content,
                Models = analysis.Models == null ? new List<string>() : new List<string>(analysis.Models),
                AttachmentIds = analysis.AttachmentIds == null
                    ? new List<string>()
                    : new List<string>(analysis.AttachmentIds),
                CreatedUtc = analysis.CreatedUtc
            };
        }

        private static List<ResourceRef> CloneResourceRefs(IEnumerable<ResourceRef> references)
        {
            return (references ?? new ResourceRef[0])
                .Where(reference => reference != null)
                .Select(CloneResourceRef)
                .ToList();
        }

        private static ResourceRef CloneResourceRef(ResourceRef reference)
        {
            return reference == null ? null : new ResourceRef(reference.Uri, reference.Revision);
        }

        private static ChatArtifact CloneArtifact(ChatArtifact artifact)
        {
            return new ChatArtifact
            {
                Id = artifact.Id,
                Kind = artifact.Kind,
                Title = artifact.Title,
                MimeType = artifact.MimeType,
                SourceMessageId = artifact.SourceMessageId,
                RunId = artifact.RunId,
                Revision = artifact.Revision,
                ParentArtifactId = artifact.ParentArtifactId,
                RelativePath = artifact.RelativePath,
                InlineText = artifact.InlineText,
                ContentSha256 = artifact.ContentSha256,
                ContentByteLength = artifact.ContentByteLength,
                MetadataJson = artifact.MetadataJson,
                RelatedArtifactIds = artifact.RelatedArtifactIds == null ? new List<string>() : new List<string>(artifact.RelatedArtifactIds),
                CreatedUtc = artifact.CreatedUtc
            };
        }

        private static ChatAttachment CloneAttachment(ChatAttachment attachment)
        {
            if (attachment == null)
            {
                return null;
            }
            return new ChatAttachment
            {
                Id = attachment.Id,
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                Size = attachment.Size,
                Kind = attachment.Kind,
                RelativePath = attachment.RelativePath,
                ContentSha256 = attachment.ContentSha256,
                ContentByteLength = attachment.ContentByteLength,
                ExtractedText = attachment.ExtractedText,
                ExtractedTextPath = attachment.ExtractedTextPath,
                ExtractedTextSha256 = attachment.ExtractedTextSha256,
                ExtractedTextByteLength = attachment.ExtractedTextByteLength,
                ExtractedCharCount = attachment.ExtractedCharCount,
                TextTruncated = attachment.TextTruncated,
                PageCount = attachment.PageCount,
                PageTextLengths = attachment.PageTextLengths == null ? new List<int>() : new List<int>(attachment.PageTextLengths),
                ExtractionWarning = attachment.ExtractionWarning,
                Status = attachment.Status,
                Error = attachment.Error,
                CreatedUtc = attachment.CreatedUtc
            };
        }

        private static ChatActivity CloneActivity(ChatActivity activity)
        {
            if (activity == null)
            {
                return null;
            }

            return new ChatActivity
            {
                RunId = activity.RunId,
                Sequence = activity.Sequence,
                StepId = activity.StepId,
                StepMessage = activity.StepMessage,
                Kind = activity.Kind,
                Title = activity.Title,
                Subtitle = activity.Subtitle,
                Status = activity.Status,
                ExecutionStatus = activity.ExecutionStatus,
                ErrorCode = activity.ErrorCode,
                Retryable = activity.Retryable,
                PendingId = activity.PendingId,
                ConfirmationCatalogSha256 = activity.ConfirmationCatalogSha256,
                ToolId = activity.ToolId,
                ToolCallId = activity.ToolCallId,
                ArgumentsJson = activity.ArgumentsJson,
                RuntimeGuardJson = activity.RuntimeGuardJson,
                ResultMessage = activity.ResultMessage,
                DataJson = activity.DataJson,
                Children = activity.Children == null ? null : activity.Children.Select(CloneActivity).ToList()
            };
        }

        private static ContextNote CloneContextNote(ContextNote note)
        {
            if (note == null)
            {
                return null;
            }

            return new ContextNote
            {
                Id = note.Id,
                Host = note.Host,
                Kind = note.Kind,
                Title = note.Title,
                Reference = note.Reference,
                Source = note.Source,
                Text = note.Text,
                Preview = note.Preview,
                DetailsJson = note.DetailsJson,
                CreatedUtc = note.CreatedUtc
            };
        }
    }
}
