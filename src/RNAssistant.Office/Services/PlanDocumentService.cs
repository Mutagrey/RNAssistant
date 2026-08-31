using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed class PlanDocumentService
    {
        public const int MaximumMarkdownCharacters = 32000;

        public PlanDocumentMutation Create(
            ChatSession session,
            string title,
            string markdown,
            string status,
            bool dryRun)
        {
            RequireSession(session);
            if (!string.IsNullOrWhiteSpace(session.ActivePlanDocumentArtifactId))
            {
                return PlanDocumentMutation.Fail(
                    "This chat already has an active plan document; update it instead.",
                    "plan_already_exists",
                    false);
            }

            var planId = "plan_doc_" + Guid.NewGuid().ToString("N");
            var normalizedTitle = RequiredTrimmed(title, "title", 200);
            var exactMarkdown = RequiredMarkdown(markdown);
            var normalizedStatus = NormalizeStatus(status);
            var artifact = CreateArtifact(planId, normalizedTitle, exactMarkdown, normalizedStatus, null, 1);
            if (!dryRun)
            {
                session.Artifacts.Add(artifact);
                session.ActivePlanDocumentArtifactId = artifact.Id;
            }

            return PlanDocumentMutation.Ok(
                dryRun ? "Dry run: would create a Markdown plan." : "Plan document created: " + normalizedTitle,
                planId,
                normalizedStatus,
                artifact);
        }

        public PlanDocumentMutation Update(
            ChatSession session,
            string planId,
            string expectedRevisionArtifactId,
            string title,
            bool hasTitle,
            string markdown,
            string status,
            bool dryRun)
        {
            RequireSession(session);
            planId = RequiredTrimmed(planId, "id", 128);
            expectedRevisionArtifactId = RequiredTrimmed(
                expectedRevisionArtifactId,
                "expectedRevisionArtifactId",
                160);

            var current = FindCurrent(session, planId);
            if (current == null)
            {
                return PlanDocumentMutation.Fail(
                    "Plan document not found: " + planId,
                    "plan_not_found",
                    false);
            }
            if (!string.Equals(current.Id, expectedRevisionArtifactId, StringComparison.OrdinalIgnoreCase))
            {
                return PlanDocumentMutation.Fail(
                    "Plan document changed; read the active revision and retry intentionally.",
                    "stale_plan_revision",
                    false);
            }

            var revisions = Revisions(session, planId)
                .OrderBy(item => item.Revision)
                .ThenBy(item => item.CreatedUtc)
                .ToList();
            if (!HasLinearCurrentHead(revisions, current))
            {
                return PlanDocumentMutation.Fail(
                    "Plan revision lineage is not linear at the active head; select or repair the exact current revision before updating.",
                    "plan_lineage_conflict",
                    false);
            }

            var exactMarkdown = RequiredMarkdown(markdown);
            var normalizedTitle = hasTitle ? RequiredTrimmed(title, "title", 200) : current.Title;
            var normalizedStatus = NormalizeStatus(status);
            var revision = revisions[revisions.Count - 1].Revision + 1;
            var artifact = CreateArtifact(
                planId,
                normalizedTitle,
                exactMarkdown,
                normalizedStatus,
                current,
                revision);
            if (!dryRun)
            {
                session.Artifacts.Add(artifact);
                session.ActivePlanDocumentArtifactId = artifact.Id;
            }

            return PlanDocumentMutation.Ok(
                dryRun ? "Dry run: would update the Markdown plan." : "Plan document updated: " + normalizedTitle,
                planId,
                normalizedStatus,
                artifact);
        }

        public PlanDocumentMutation Restore(
            ChatSession session,
            string planId,
            string expectedRevisionArtifactId,
            string sourceRevisionArtifactId,
            bool dryRun)
        {
            RequireSession(session);
            planId = RequiredTrimmed(planId, "id", 128);
            expectedRevisionArtifactId = RequiredTrimmed(
                expectedRevisionArtifactId,
                "expectedRevisionArtifactId",
                160);
            sourceRevisionArtifactId = RequiredTrimmed(
                sourceRevisionArtifactId,
                "sourceRevisionArtifactId",
                160);
            var current = FindCurrent(session, planId);
            if (current == null)
            {
                return PlanDocumentMutation.Fail(
                    "Plan document not found: " + planId,
                    "plan_not_found",
                    false);
            }
            if (!string.Equals(current.Id, expectedRevisionArtifactId, StringComparison.OrdinalIgnoreCase))
            {
                return PlanDocumentMutation.Fail(
                    "Plan document changed; read the active revision and retry intentionally.",
                    "stale_plan_revision",
                    false);
            }
            var revisions = OrderedRevisions(session, planId);
            if (!HasLinearCurrentHead(revisions, current)) return LineageConflict();
            var source = revisions.FirstOrDefault(item =>
                string.Equals(item.Id, sourceRevisionArtifactId, StringComparison.OrdinalIgnoreCase));
            if (source == null || IsTombstone(source))
            {
                return PlanDocumentMutation.Fail(
                    "Plan revision not found: " + sourceRevisionArtifactId,
                    "plan_revision_not_found",
                    false);
            }
            if (string.Equals(source.Id, current.Id, StringComparison.OrdinalIgnoreCase))
            {
                return PlanDocumentMutation.Fail(
                    "The selected Plan revision is already current.",
                    "plan_revision_already_current",
                    false);
            }
            if (string.IsNullOrWhiteSpace(source.InlineText))
            {
                return PlanDocumentMutation.Fail(
                    "The selected Plan revision body is unavailable.",
                    "plan_revision_unavailable",
                    false);
            }

            var sourceStatus = Status(source);
            if (sourceStatus != "draft" && sourceStatus != "ready") sourceStatus = "draft";
            var artifact = CreateArtifact(
                planId,
                source.Title,
                source.InlineText,
                sourceStatus,
                current,
                revisions[revisions.Count - 1].Revision + 1,
                source.Id);
            if (!dryRun)
            {
                session.Artifacts.Add(artifact);
                session.ActivePlanDocumentArtifactId = artifact.Id;
            }

            return PlanDocumentMutation.OkRestore(
                dryRun ? "Dry run: would restore the selected Plan revision as a new head." :
                    "Plan revision restored as a new head: " + source.Title,
                planId,
                sourceStatus,
                artifact,
                source.Id);
        }

        public PlanDocumentMutation Delete(
            ChatSession session,
            string planId,
            string expectedRevisionArtifactId,
            bool dryRun)
        {
            RequireSession(session);
            planId = RequiredTrimmed(planId, "id", 128);
            expectedRevisionArtifactId = RequiredTrimmed(
                expectedRevisionArtifactId,
                "expectedRevisionArtifactId",
                160);
            var current = FindCurrent(session, planId);
            if (current == null)
            {
                return PlanDocumentMutation.Fail(
                    "Plan document not found: " + planId,
                    "plan_not_found",
                    false);
            }
            if (!string.Equals(current.Id, expectedRevisionArtifactId, StringComparison.OrdinalIgnoreCase))
            {
                return PlanDocumentMutation.Fail(
                    "Plan document changed; read the active revision and retry intentionally.",
                    "stale_plan_revision",
                    false);
            }
            var revisions = OrderedRevisions(session, planId);
            if (!HasLinearCurrentHead(revisions, current)) return LineageConflict();
            var referencingMessageIds = ReferencingMessageIds(session, revisions);
            var tombstone = CreateTombstone(
                planId,
                current,
                revisions[revisions.Count - 1].Revision + 1);
            if (!dryRun)
            {
                session.Artifacts.Add(tombstone);
                session.ActivePlanDocumentArtifactId = null;
            }

            return PlanDocumentMutation.OkRemoval(
                dryRun ? "Dry run: would append a Plan removal tombstone." :
                    "Plan document removed; historical exact references remain as removal placeholders.",
                planId,
                tombstone,
                revisions.Count,
                referencingMessageIds);
        }

        internal static string PlanId(ChatArtifact artifact)
        {
            try
            {
                return (string)JObject.Parse(artifact == null ? "{}" : artifact.MetadataJson ?? "{}")["planId"] ?? string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        internal static string Status(ChatArtifact artifact)
        {
            return MetadataString(artifact, "status").ToLowerInvariant();
        }

        internal static bool IsTombstone(ChatArtifact artifact)
        {
            if (artifact == null || !string.Equals(
                artifact.Kind,
                ChatArtifactKinds.PlanDocument,
                StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var metadata = JObject.Parse(artifact.MetadataJson ?? "{}");
                return (bool?)metadata.GetValue("removed", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static bool IsRemoved(ChatSession session, ChatArtifact artifact)
        {
            if (session == null || artifact == null || !string.Equals(
                artifact.Kind,
                ChatArtifactKinds.PlanDocument,
                StringComparison.OrdinalIgnoreCase)) return false;
            var planId = PlanId(artifact);
            return !string.IsNullOrWhiteSpace(planId) &&
                UniqueArtifacts(session).Any(item =>
                    IsApplicableTombstone(session, item) &&
                    string.Equals(PlanId(item), planId, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsApplicableTombstone(ChatSession session, ChatArtifact artifact)
        {
            if (!IsTombstone(artifact)) return false;
            if (string.IsNullOrWhiteSpace(artifact.SourceMessageId)) return true;
            return (session == null ? new List<ChatMessage>() : session.Messages ?? new List<ChatMessage>()).Any(message =>
                message != null && string.Equals(
                    message.Id,
                    artifact.SourceMessageId,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsRemovedReference(ChatSession session, ResourceRef reference)
        {
            string artifactId;
            int revision;
            if (session == null || !ChatResourceUri.TryParseArtifactRevision(
                session.Id,
                reference,
                out artifactId,
                out revision)) return false;
            var artifact = UniqueArtifacts(session).FirstOrDefault(item =>
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase) &&
                Math.Max(1, item.Revision) == revision);
            return IsRemoved(session, artifact);
        }

        private static void RequireSession(ChatSession session)
        {
            if (session == null) throw new InvalidOperationException("Plan document requires an active chat.");
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
        }

        private static ChatArtifact CreateArtifact(
            string planId,
            string title,
            string markdown,
            string status,
            ChatArtifact parent,
            int revision,
            string restoredFromArtifactId = null)
        {
            var metadata = new JObject
            {
                ["planId"] = planId,
                ["status"] = status
            };
            if (!string.IsNullOrWhiteSpace(restoredFromArtifactId))
                metadata["restoredFromArtifactId"] = restoredFromArtifactId;
            return new ChatArtifact
            {
                Id = planId + "_r" + revision + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = ChatArtifactKinds.PlanDocument,
                Title = title,
                MimeType = "text/markdown",
                Revision = revision,
                ParentArtifactId = parent == null ? null : parent.Id,
                InlineText = markdown,
                MetadataJson = metadata.ToString(Formatting.None)
            };
        }

        private static ChatArtifact CreateTombstone(string planId, ChatArtifact current, int revision)
        {
            return new ChatArtifact
            {
                Id = planId + "_r" + revision + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = ChatArtifactKinds.PlanDocument,
                Title = current.Title,
                MimeType = "text/markdown",
                Revision = revision,
                ParentArtifactId = current.Id,
                MetadataJson = new JObject
                {
                    ["planId"] = planId,
                    ["status"] = "removed",
                    ["removed"] = true,
                    ["removedHeadArtifactId"] = current.Id
                }.ToString(Formatting.None)
            };
        }

        private static ChatArtifact FindCurrent(ChatSession session, string planId)
        {
            var current = UniqueArtifacts(session).FirstOrDefault(item =>
                string.Equals(item.Id, session.ActivePlanDocumentArtifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase));
            return current != null && string.Equals(PlanId(current), planId, StringComparison.OrdinalIgnoreCase)
                ? current
                : null;
        }

        private static IEnumerable<ChatArtifact> Revisions(ChatSession session, string planId)
        {
            return UniqueArtifacts(session).Where(item =>
                string.Equals(item.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PlanId(item), planId, StringComparison.OrdinalIgnoreCase));
        }

        private static List<ChatArtifact> UniqueArtifacts(ChatSession session)
        {
            return (session == null ? new List<ChatArtifact>() : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .ToList();
        }

        private static List<ChatArtifact> OrderedRevisions(ChatSession session, string planId)
        {
            return Revisions(session, planId)
                .OrderBy(item => item.Revision)
                .ThenBy(item => item.CreatedUtc)
                .ToList();
        }

        private static bool HasLinearCurrentHead(IList<ChatArtifact> revisions, ChatArtifact current)
        {
            if (revisions == null || revisions.Count == 0 || current == null) return false;
            for (var index = 0; index < revisions.Count; index++)
            {
                var revision = revisions[index];
                if (revision.Revision != index + 1) return false;
                if (index == 0)
                {
                    if (!string.IsNullOrWhiteSpace(revision.ParentArtifactId)) return false;
                    continue;
                }
                if (!string.Equals(
                    revision.ParentArtifactId,
                    revisions[index - 1].Id,
                    StringComparison.OrdinalIgnoreCase)) return false;
            }
            return string.Equals(
                revisions[revisions.Count - 1].Id,
                current.Id,
                StringComparison.OrdinalIgnoreCase);
        }

        private static PlanDocumentMutation LineageConflict()
        {
            return PlanDocumentMutation.Fail(
                "Plan revision lineage is not linear at the active head; select or repair the exact current revision before mutating it.",
                "plan_lineage_conflict",
                false);
        }

        private static List<string> ReferencingMessageIds(
            ChatSession session,
            IEnumerable<ChatArtifact> revisions)
        {
            var ids = new HashSet<string>((revisions ?? new ChatArtifact[0])
                .Where(item => item != null)
                .Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            return (session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null && (message.ResourceRefs ?? new List<ResourceRef>())
                    .Any(reference => References(reference, ids, session.Id)))
                .Select(message => message.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool References(ResourceRef reference, ISet<string> ids, string sessionId)
        {
            string artifactId;
            int revision;
            return ChatResourceUri.TryParseArtifactRevision(
                sessionId,
                reference,
                out artifactId,
                out revision) && ids.Contains(artifactId);
        }

        private static string MetadataString(ChatArtifact artifact, string name)
        {
            try
            {
                return ((string)JObject.Parse(artifact == null ? "{}" : artifact.MetadataJson ?? "{}").GetValue(
                    name,
                    StringComparison.OrdinalIgnoreCase) ?? string.Empty).Trim();
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static string RequiredTrimmed(string value, string name, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > max)
                throw new InvalidOperationException(name + " must contain 1-" + max + " characters.");
            return value;
        }

        private static string RequiredMarkdown(string value)
        {
            value = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumMarkdownCharacters)
            {
                throw new InvalidOperationException(
                    "markdown must contain 1-" + MaximumMarkdownCharacters + " characters.");
            }
            return value;
        }

        private static string NormalizeStatus(string value)
        {
            value = (value ?? "draft").Trim().ToLowerInvariant();
            if (value == "draft" || value == "ready") return value;
            throw new InvalidOperationException("Plan status must be draft or ready.");
        }
    }

    internal sealed class PlanDocumentMutation
    {
        public bool Success { get; private set; }
        public string Message { get; private set; }
        public string ErrorCode { get; private set; }
        public bool? Retryable { get; private set; }
        public string PlanId { get; private set; }
        public string Status { get; private set; }
        public ChatArtifact Artifact { get; private set; }
        public string RestoredFromArtifactId { get; private set; }
        public bool Removed { get; private set; }
        public int AffectedRevisions { get; private set; }
        public IReadOnlyList<string> ReferencingMessageIds { get; private set; }

        private PlanDocumentMutation()
        {
            ReferencingMessageIds = new string[0];
        }

        public static PlanDocumentMutation Ok(
            string message,
            string planId,
            string status,
            ChatArtifact artifact)
        {
            return new PlanDocumentMutation
            {
                Success = true,
                Message = message,
                PlanId = planId,
                Status = status,
                Artifact = artifact
            };
        }

        public static PlanDocumentMutation OkRestore(
            string message,
            string planId,
            string status,
            ChatArtifact artifact,
            string restoredFromArtifactId)
        {
            return new PlanDocumentMutation
            {
                Success = true,
                Message = message,
                PlanId = planId,
                Status = status,
                Artifact = artifact,
                RestoredFromArtifactId = restoredFromArtifactId
            };
        }

        public static PlanDocumentMutation OkRemoval(
            string message,
            string planId,
            ChatArtifact artifact,
            int affectedRevisions,
            IReadOnlyList<string> referencingMessageIds)
        {
            return new PlanDocumentMutation
            {
                Success = true,
                Message = message,
                PlanId = planId,
                Status = "removed",
                Artifact = artifact,
                Removed = true,
                AffectedRevisions = affectedRevisions,
                ReferencingMessageIds = referencingMessageIds ?? new string[0]
            };
        }

        public static PlanDocumentMutation Fail(string message, string errorCode, bool? retryable)
        {
            return new PlanDocumentMutation
            {
                Success = false,
                Message = message,
                ErrorCode = errorCode,
                Retryable = retryable
            };
        }
    }
}
