using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class PlanDocumentToolExecutor
    {
        public const string CreateToolId = "common.plan_doc_create";
        public const string UpdateToolId = "common.plan_doc_update";
        public const string DeleteToolId = "common.plan_doc_delete";
        public const int MaximumMarkdownCharacters = 32000;

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(CreateToolId, "Common",
                "Plan document: Create the single revisioned Markdown plan for this chat after discovery and alignment.",
                CreateSchema(), mutatesLocalState: true, name: "plan_doc_create", scope: "session");
            yield return ControllerToolDefinition.Create(UpdateToolId, "Common",
                "Plan document: Replace the active Markdown plan body using an exact current revision guard.",
                UpdateSchema(), mutatesLocalState: true, name: "plan_doc_update", scope: "session");
            yield return ControllerToolDefinition.Create(DeleteToolId, "Common",
                "Plan document: Delete every revision only when the user explicitly asks to remove the plan.",
                DeleteSchema(), mutatesLocalState: true, riskLevel: 1, name: "plan_doc_delete", scope: "session");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, ChatSession session, bool dryRun)
        {
            if (command == null) return ToolResult.Fail("Tool command is empty.");
            if (session == null) return ToolResult.Fail("Plan document requires an active chat.", null, "plan_session_required", false);
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            try
            {
                if (string.Equals(command.ToolId, CreateToolId, StringComparison.OrdinalIgnoreCase)) return Create(command, session, dryRun);
                if (string.Equals(command.ToolId, UpdateToolId, StringComparison.OrdinalIgnoreCase)) return Update(command, session, dryRun);
                if (string.Equals(command.ToolId, DeleteToolId, StringComparison.OrdinalIgnoreCase)) return Delete(command, session, dryRun);
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message, null, "invalid_plan_document", true);
            }
            return ToolResult.Fail("Unknown plan-document tool: " + command.ToolId);
        }

        private static ToolResult Create(ToolCommand command, ChatSession session, bool dryRun)
        {
            if (!string.IsNullOrWhiteSpace(session.ActivePlanDocumentArtifactId))
                return ToolResult.Fail("This chat already has an active plan document; update it instead.", null, "plan_already_exists", false);
            var id = "plan_doc_" + Guid.NewGuid().ToString("N");
            var title = RequiredText(command, "title", 200);
            var markdown = RequiredText(command, "markdown", MaximumMarkdownCharacters);
            var status = NormalizeStatus(ToolArgumentReader.String(command.Arguments, "status", "draft"));
            var artifact = CreateArtifact(id, title, markdown, status, null, 1);
            if (dryRun) return ToolResult.Ok("Dry run: would create a Markdown plan.", Payload(id, status, artifact).ToString(Formatting.None));
            session.Artifacts.Add(artifact);
            session.ActivePlanDocumentArtifactId = artifact.Id;
            return ToolResult.Ok("Plan document created: " + title, Payload(id, status, artifact).ToString(Formatting.None));
        }

        private static ToolResult Update(ToolCommand command, ChatSession session, bool dryRun)
        {
            var id = RequiredText(command, "id", 128);
            var expected = RequiredText(command, "expectedRevisionArtifactId", 160);
            var current = FindCurrent(session, id);
            if (current == null) return ToolResult.Fail("Plan document not found: " + id, null, "plan_not_found", false);
            if (!string.Equals(current.Id, expected, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("Plan document changed; read the active revision and retry intentionally.", null, "stale_plan_revision", false);
            var markdown = RequiredText(command, "markdown", MaximumMarkdownCharacters);
            var title = HasArgument(command, "title") ? RequiredText(command, "title", 200) : current.Title;
            var status = NormalizeStatus(ToolArgumentReader.String(command.Arguments, "status", "draft"));
            var artifact = CreateArtifact(id, title, markdown, status, current, Math.Max(1, current.Revision) + 1);
            if (dryRun) return ToolResult.Ok("Dry run: would update the Markdown plan.", Payload(id, status, artifact).ToString(Formatting.None));
            session.Artifacts.Add(artifact);
            session.ActivePlanDocumentArtifactId = artifact.Id;
            return ToolResult.Ok("Plan document updated: " + title, Payload(id, status, artifact).ToString(Formatting.None));
        }

        private static ToolResult Delete(ToolCommand command, ChatSession session, bool dryRun)
        {
            var id = RequiredText(command, "id", 128);
            var revisions = Revisions(session, id).ToList();
            if (revisions.Count == 0) return ToolResult.Fail("Plan document not found: " + id, null, "plan_not_found", false);
            if (dryRun) return ToolResult.Ok("Dry run: would delete the plan document.", JsonConvert.SerializeObject(new { id, deletedRevisions = revisions.Count }));
            var ids = new HashSet<string>(revisions.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            session.Artifacts.RemoveAll(item => item != null && ids.Contains(item.Id));
            foreach (var message in session.Messages ?? new List<ChatMessage>())
            {
                if (message == null || message.ResourceRefs == null) continue;
                message.ResourceRefs.RemoveAll(reference => References(reference, ids));
            }
            if (ids.Contains(session.ActivePlanDocumentArtifactId)) session.ActivePlanDocumentArtifactId = null;
            return ToolResult.Ok("Plan document deleted.", JsonConvert.SerializeObject(new { id, deletedRevisions = revisions.Count }));
        }

        private static ChatArtifact CreateArtifact(string id, string title, string markdown, string status, ChatArtifact parent, int revision)
        {
            return new ChatArtifact
            {
                Id = id + "_r" + revision + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = ChatArtifactKinds.PlanDocument,
                Title = title,
                MimeType = "text/markdown",
                Revision = revision,
                ParentArtifactId = parent == null ? null : parent.Id,
                InlineText = markdown,
                MetadataJson = JsonConvert.SerializeObject(new { planId = id, status })
            };
        }

        private static ChatArtifact FindCurrent(ChatSession session, string id)
        {
            var current = (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item => item != null &&
                string.Equals(item.Id, session.ActivePlanDocumentArtifactId, StringComparison.OrdinalIgnoreCase));
            return current != null && string.Equals(PlanId(current), id, StringComparison.OrdinalIgnoreCase) ? current : null;
        }

        private static IEnumerable<ChatArtifact> Revisions(ChatSession session, string id)
        {
            return (session.Artifacts ?? new List<ChatArtifact>()).Where(item => item != null &&
                string.Equals(item.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PlanId(item), id, StringComparison.OrdinalIgnoreCase));
        }

        private static string PlanId(ChatArtifact artifact)
        {
            try { return (string)JObject.Parse(artifact == null ? "{}" : artifact.MetadataJson ?? "{}")["planId"] ?? string.Empty; }
            catch (JsonException) { return string.Empty; }
        }

        private static bool References(ResourceRef reference, ISet<string> ids)
        {
            string sessionId; string artifactId; int revision;
            return ChatResourceUri.TryParseArtifactRevision(reference, out sessionId, out artifactId, out revision) && ids.Contains(artifactId);
        }

        private static string RequiredText(ToolCommand command, string name, int max)
        {
            var value = (ToolArgumentReader.String(command.Arguments, name, string.Empty) ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > max) throw new InvalidOperationException(name + " must contain 1-" + max + " characters.");
            return value;
        }

        private static string NormalizeStatus(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "draft" || value == "ready") return value;
            throw new InvalidOperationException("Plan status must be draft or ready.");
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private static JObject Payload(string id, string status, ChatArtifact artifact)
        {
            return new JObject { ["planId"] = id, ["status"] = status, ["artifactId"] = artifact.Id, ["revision"] = artifact.Revision };
        }

        private static string CreateSchema()
        {
            return Schema(false).ToString(Formatting.None);
        }

        private static string UpdateSchema()
        {
            return Schema(true).ToString(Formatting.None);
        }

        private static JObject Schema(bool update)
        {
            var properties = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable plan id from create or metadata." },
                ["expectedRevisionArtifactId"] = new JObject { ["type"] = "string", ["description"] = "Exact active revision artifact id used as an optimistic concurrency guard." },
                ["title"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200, ["description"] = "User-visible plan title." },
                ["markdown"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaximumMarkdownCharacters, ["description"] = "Complete free-form Markdown plan body." },
                ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("draft", "ready"), ["default"] = "draft", ["description"] = "Draft while being refined; ready only after requirements are decision-complete." }
            };
            if (!update) { properties.Remove("id"); properties.Remove("expectedRevisionArtifactId"); }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = update ? new JArray("id", "expectedRevisionArtifactId", "markdown") : new JArray("title", "markdown"),
                ["additionalProperties"] = false
            };
        }

        private static string DeleteSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Stable plan id; all revisions are removed.\"}},\"required\":[\"id\"],\"additionalProperties\":false}";
        }
    }
}
