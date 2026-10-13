using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office.Services
{
    internal sealed class ContextCompactionService
    {
        // Office captures host authority and domain projection; the checkpoint
        // algorithm is shared with the independent workspace runtime.
        private readonly ContextCompactionRuntime _runtime;
        private readonly ResourceAuthorityService _authority;
        private readonly Func<ChatSession, IReadOnlyList<ToolCatalogEntry>, SkillCatalogSnapshot, CallableToolPack> _captureTools;
        private readonly Func<SkillCatalogSnapshot> _captureSkills;

        public ContextCompactionService(LlmCompletionDelegate completeAsync,
            ResourceAuthorityService authority = null, ChatBlobStore payloads = null,
            Func<ChatSession, IReadOnlyList<ToolCatalogEntry>, SkillCatalogSnapshot, CallableToolPack> captureTools = null,
            Func<SkillCatalogSnapshot> captureSkills = null)
        {
            _authority = authority; _captureTools = captureTools; _captureSkills = captureSkills;
            _runtime = new ContextCompactionRuntime(completeAsync, payloads, ModelToolResultProjection.Instance,
                authority == null || payloads == null ? null : new DocumentArtifactStore(authority.Store, authority.Revisions, payloads),
                RuntimeLog.Info);
        }

        public Task<ContextCheckpoint> EnsureWithinBudgetAsync(ChatSession session, AppSettings settings,
            string incomingText, bool force, Action<string, string, ChatActivity> progress,
            CancellationToken cancellationToken, ModelAuthoritySnapshot authoritySnapshot = null,
            IReadOnlyList<ToolCatalogEntry> runnableCatalog = null, IReadOnlyList<ToolCatalogEntry> capturedTools = null,
            SkillCatalogSnapshot capturedSkills = null)
        {
            if (session == null || session.Messages == null || session.Messages.Count == 0 ||
                !force && settings != null && !settings.AutoCompressContext) return Task.FromResult<ContextCheckpoint>(null);
            settings = settings ?? new AppSettings();
            var evidence = BuildReplayTail(session).SelectMany(item => item.ResourceEvidence ?? new List<ResourceEvidence>())
                .Concat((ActiveCheckpoint(session)?.Claims ?? new List<StructuredContextClaim>()).SelectMany(item => item.Evidence));
            var scopes = evidence.Select(item => item.ScopeId).Concat(new[] {
                new ResourceAuthorityScopeId("conversation", session.Id), CatalogPublicationService.ScopeId }).Distinct().ToList();
            if (!string.IsNullOrEmpty(session.DocumentAuthorityId)) scopes.Add(ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId)));
            var skills = authoritySnapshot == null ? capturedSkills ?? (_captureSkills == null ? new SkillCatalogSnapshot(null) : _captureSkills()) : null;
            var pack = authoritySnapshot == null ? (_captureTools == null
                ? CallableToolPack.Create(session.Mode, session.Host, session.LastRun?.RunId, capturedTools ?? new ToolCatalogEntry[0])
                : _captureTools(session, capturedTools, skills)) : null;
            var resources = authoritySnapshot?.Resources ?? (_authority == null
                ? new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]) : _authority.CaptureMany(scopes));
            var frozen = authoritySnapshot ?? new ModelAuthoritySnapshot(resources, pack.Revision,
                skills, ResourceStateProvider.CaptureSchemas(resources), session.Revision);
            var instructionTokens = new[] { ChatModes.Agent, ChatModes.Plan }
                .Max(mode => ModelContextBudget.EstimateTextTokens(ConversationPromptComposer.BuildInstruction(mode, settings), settings));
            return _runtime.EnsureWithinBudgetAsync(session, settings, incomingText, force, progress, cancellationToken,
                frozen, runnableCatalog ?? pack?.Catalog ?? new ToolCatalogEntry[0], instructionTokens);
        }

        internal static List<ChatMessage> BuildActiveWindow(ChatSession session) { return ContextCompactionRuntime.BuildActiveWindow(session); }
        internal static List<ChatMessage> BuildReplayTail(ChatSession session) { return ContextCompactionRuntime.BuildReplayTail(session); }
        internal static ContextCheckpoint ActiveCheckpoint(ChatSession session) { return ContextCompactionRuntime.ActiveCheckpoint(session); }
        internal static bool IsReplayMessage(ChatMessage message) { return ContextCompactionRuntime.IsReplayMessage(message); }
    }
}
