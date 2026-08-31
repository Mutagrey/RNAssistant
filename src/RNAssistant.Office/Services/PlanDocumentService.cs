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

        public PlanDocumentMutation Delete(ChatSession session, string planId, bool dryRun)
        {
            RequireSession(session);
            planId = RequiredTrimmed(planId, "id", 128);
            var revisions = Revisions(session, planId).ToList();
            if (revisions.Count == 0)
            {
                return PlanDocumentMutation.Fail(
                    "Plan document not found: " + planId,
                    "plan_not_found",
                    false);
            }
            if (!dryRun)
            {
                var ids = new HashSet<string>(revisions.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
                session.Artifacts.RemoveAll(item => item != null && ids.Contains(item.Id));
                foreach (var message in session.Messages ?? new List<ChatMessage>())
                {
                    if (message == null || message.ResourceRefs == null) continue;
                    message.ResourceRefs.RemoveAll(reference => References(reference, ids));
                }
                if (ids.Contains(session.ActivePlanDocumentArtifactId)) session.ActivePlanDocumentArtifactId = null;
            }

            return PlanDocumentMutation.OkDelete(
                dryRun ? "Dry run: would delete the plan document." : "Plan document deleted.",
                planId,
                revisions.Count);
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
            int revision)
        {
            return new ChatArtifact
            {
                Id = planId + "_r" + revision + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = ChatArtifactKinds.PlanDocument,
                Title = title,
                MimeType = "text/markdown",
                Revision = revision,
                ParentArtifactId = parent == null ? null : parent.Id,
                InlineText = markdown,
                MetadataJson = JsonConvert.SerializeObject(new { planId, status })
            };
        }

        private static ChatArtifact FindCurrent(ChatSession session, string planId)
        {
            var current = (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item => item != null &&
                string.Equals(item.Id, session.ActivePlanDocumentArtifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase));
            return current != null && string.Equals(PlanId(current), planId, StringComparison.OrdinalIgnoreCase)
                ? current
                : null;
        }

        private static IEnumerable<ChatArtifact> Revisions(ChatSession session, string planId)
        {
            return (session.Artifacts ?? new List<ChatArtifact>()).Where(item => item != null &&
                string.Equals(item.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PlanId(item), planId, StringComparison.OrdinalIgnoreCase));
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

        private static bool References(ResourceRef reference, ISet<string> ids)
        {
            string sessionId;
            string artifactId;
            int revision;
            return ChatResourceUri.TryParseArtifactRevision(reference, out sessionId, out artifactId, out revision) &&
                ids.Contains(artifactId);
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
        public int DeletedRevisions { get; private set; }

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

        public static PlanDocumentMutation OkDelete(string message, string planId, int deletedRevisions)
        {
            return new PlanDocumentMutation
            {
                Success = true,
                Message = message,
                PlanId = planId,
                DeletedRevisions = deletedRevisions
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
