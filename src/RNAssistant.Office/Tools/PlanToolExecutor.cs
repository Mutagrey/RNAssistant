using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class PlanToolExecutor
    {
        public const string CreateToolId = "common.plan_create";
        public const string ReadToolId = "common.plan_read";
        public const string UpdateToolId = "common.plan_update";
        public const string DeleteToolId = "common.plan_delete";

        private const int MaxSteps = 32;
        private const int MaxGoalChars = 500;
        private const int MaxStepChars = 500;

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(CreateToolId, "Common",
                "Plan: Create a new visible multi-step plan for the active chat and make it active. Use only when a visible plan materially helps the task.",
                CreateSchema(), mutatesLocalState: true, name: "plan_create");
            yield return ControllerToolDefinition.Create(ReadToolId, "Common",
                "Read-only: Read the latest revision of a plan by stable plan id or any artifact revision id. Omit id to read the active chat plan.",
                ReadSchema(), name: "plan_read");
            yield return ControllerToolDefinition.Create(UpdateToolId, "Common",
                "Plan: Update the goal and/or replace the complete steps of an existing plan. Omitted fields are preserved and a new artifact revision is created.",
                UpdateSchema(), mutatesLocalState: true, name: "plan_update");
            yield return ControllerToolDefinition.Create(DeleteToolId, "Common",
                "Plan: Delete every stored revision of one plan from the active chat.",
                DeleteSchema(), mutatesLocalState: true, riskLevel: 1, name: "plan_delete");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, ChatSession session, bool dryRun)
        {
            if (command == null) return ToolResult.Fail("Tool command is empty.");
            if (session == null) return ToolResult.Fail("Plan tools require an active chat session.", null, "plan_session_required", false);
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();

            try
            {
                if (string.Equals(command.ToolId, CreateToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return Create(command, session, dryRun);
                }
                if (string.Equals(command.ToolId, ReadToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return Read(command, session);
                }
                if (string.Equals(command.ToolId, UpdateToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return Update(command, session, dryRun);
                }
                if (string.Equals(command.ToolId, DeleteToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return Delete(command, session, dryRun);
                }
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Plan JSON is invalid: " + ex.Message, null, "invalid_plan", true);
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message, null, "invalid_plan", true);
            }

            return ToolResult.Fail("Unknown plan tool: " + command.ToolId);
        }

        private static ToolResult Create(ToolCommand command, ChatSession session, bool dryRun)
        {
            var plan = new ChatPlan
            {
                Id = "plan_" + Guid.NewGuid().ToString("N"),
                Goal = ToolArgumentReader.String(command.Arguments, "goal", string.Empty),
                Steps = ReadSteps(command, "steps")
            };
            ValidatePlan(plan);
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would create a visible plan.", Payload(plan, null).ToString(Formatting.None));
            }

            var artifact = CreateArtifact(plan, null, 1);
            session.Artifacts.Add(artifact);
            session.ActivePlanArtifactId = artifact.Id;
            return ToolResult.Ok("Plan created: " + plan.Goal, Payload(plan, artifact).ToString(Formatting.None));
        }

        private static ToolResult Read(ToolCommand command, ChatSession session)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            ChatPlan plan;
            var artifact = FindRevision(session, id, out plan);
            return artifact == null
                ? ToolResult.Fail(string.IsNullOrWhiteSpace(id) ? "The active chat has no plan." : "Plan not found: " + id, null, "plan_not_found", false)
                : ToolResult.Ok("Plan read: " + plan.Goal, Payload(plan, artifact).ToString(Formatting.None));
        }

        private static ToolResult Update(ToolCommand command, ChatSession session, bool dryRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            ChatPlan current;
            var previous = FindRevision(session, id, out current);
            if (previous == null)
            {
                return ToolResult.Fail("Plan not found: " + id, null, "plan_not_found", false);
            }
            var hasGoal = HasArgument(command, "goal");
            var hasSteps = HasArgument(command, "steps");
            if (!hasGoal && !hasSteps)
            {
                return ToolResult.Fail("Plan update requires goal and/or steps.", null, "plan_update_empty", true);
            }

            var updated = Clone(current);
            if (hasGoal) updated.Goal = ToolArgumentReader.String(command.Arguments, "goal", string.Empty);
            if (hasSteps) updated.Steps = ReadSteps(command, "steps");
            ValidatePlan(updated);
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would update plan " + updated.Id + ".", Payload(updated, previous).ToString(Formatting.None));
            }

            var artifact = CreateArtifact(updated, previous, Math.Max(1, previous.Revision) + 1);
            session.Artifacts.Add(artifact);
            session.ActivePlanArtifactId = artifact.Id;
            return ToolResult.Ok("Plan updated: " + updated.Goal, Payload(updated, artifact).ToString(Formatting.None));
        }

        private static ToolResult Delete(ToolCommand command, ChatSession session, bool dryRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            ChatPlan selected;
            var selectedArtifact = FindRevision(session, id, out selected);
            if (selectedArtifact == null)
            {
                return ToolResult.Fail("Plan not found: " + id, null, "plan_not_found", false);
            }
            var revisions = PlanRevisions(session)
                .Where(item => string.Equals(item.Plan.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would delete plan " + selected.Id + ".",
                    JsonConvert.SerializeObject(new { id = selected.Id, deletedRevisions = revisions.Count }));
            }

            var artifactIds = new HashSet<string>(revisions.Select(item => item.Artifact.Id), StringComparer.OrdinalIgnoreCase);
            session.Artifacts.RemoveAll(item => item != null && artifactIds.Contains(item.Id));
            foreach (var message in session.Messages ?? new List<ChatMessage>())
            {
                if (message == null || message.ArtifactIds == null) continue;
                message.ArtifactIds.RemoveAll(artifactId => artifactIds.Contains(artifactId));
            }
            foreach (var artifact in session.Artifacts.Where(item => item != null))
            {
                if (artifact.RelatedArtifactIds != null) artifact.RelatedArtifactIds.RemoveAll(artifactId => artifactIds.Contains(artifactId));
                if (artifactIds.Contains(artifact.ParentArtifactId)) artifact.ParentArtifactId = null;
            }
            if (artifactIds.Contains(session.ActivePlanArtifactId)) session.ActivePlanArtifactId = null;
            return ToolResult.Ok("Plan deleted: " + selected.Id,
                JsonConvert.SerializeObject(new { id = selected.Id, deletedRevisions = revisions.Count }));
        }

        private static ChatArtifact CreateArtifact(ChatPlan plan, ChatArtifact parent, int revision)
        {
            return new ChatArtifact
            {
                Id = plan.Id + "_r" + revision + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = ChatArtifactKinds.Plan,
                Title = plan.Goal,
                MimeType = "application/vnd.rnassistant.plan+json",
                Revision = revision,
                ParentArtifactId = parent == null ? null : parent.Id,
                InlineText = JsonConvert.SerializeObject(plan),
                ModelContextPolicy = "reference",
                MetadataJson = JsonConvert.SerializeObject(new { planId = plan.Id })
            };
        }

        private static ChatArtifact FindRevision(ChatSession session, string id, out ChatPlan plan)
        {
            plan = null;
            var revisions = PlanRevisions(session).ToList();
            PlanRevision selected = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                selected = revisions.FirstOrDefault(item => string.Equals(item.Artifact.Id, session.ActivePlanArtifactId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var exactArtifact = revisions.FirstOrDefault(item => string.Equals(item.Artifact.Id, id, StringComparison.OrdinalIgnoreCase));
                var planId = exactArtifact == null ? id : exactArtifact.Plan.Id;
                selected = revisions.Where(item => string.Equals(item.Plan.Id, planId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Artifact.Revision)
                    .ThenByDescending(item => item.Artifact.CreatedUtc)
                    .FirstOrDefault();
            }
            if (selected == null) return null;
            plan = selected.Plan;
            return selected.Artifact;
        }

        private static IEnumerable<PlanRevision> PlanRevisions(ChatSession session)
        {
            foreach (var artifact in (session == null ? null : session.Artifacts) ?? new List<ChatArtifact>())
            {
                if (artifact == null || !string.Equals(artifact.Kind, ChatArtifactKinds.Plan, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(artifact.InlineText)) continue;
                ChatPlan plan;
                try
                {
                    plan = JsonConvert.DeserializeObject<ChatPlan>(artifact.InlineText);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (plan == null || string.IsNullOrWhiteSpace(plan.Id)) continue;
                yield return new PlanRevision { Artifact = artifact, Plan = plan };
            }
        }

        private static List<ChatPlanStep> ReadSteps(ToolCommand command, string name)
        {
            var raw = ToolArgumentReader.String(command.Arguments, name, "[]");
            return JsonConvert.DeserializeObject<List<ChatPlanStep>>(raw) ?? new List<ChatPlanStep>();
        }

        private static void ValidatePlan(ChatPlan plan)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.Id)) throw new InvalidOperationException("Plan id is required.");
            plan.Goal = (plan.Goal ?? string.Empty).Trim();
            if (plan.Goal.Length == 0 || plan.Goal.Length > MaxGoalChars) throw new InvalidOperationException("Plan goal must contain 1-" + MaxGoalChars + " characters.");
            plan.Steps = plan.Steps ?? new List<ChatPlanStep>();
            if (plan.Steps.Count == 0 || plan.Steps.Count > MaxSteps) throw new InvalidOperationException("Plan must contain 1-" + MaxSteps + " steps.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in plan.Steps)
            {
                if (step == null) throw new InvalidOperationException("Plan steps must be objects.");
                step.Id = (step.Id ?? string.Empty).Trim();
                step.Text = (step.Text ?? string.Empty).Trim();
                step.Status = NormalizeStatus(step.Status);
                if (step.Id.Length == 0 || step.Id.Length > 80 || step.Id.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Each plan step needs a unique non-whitespace id of at most 80 characters.");
                if (!ids.Add(step.Id)) throw new InvalidOperationException("Duplicate plan step id: " + step.Id);
                if (step.Text.Length == 0 || step.Text.Length > MaxStepChars) throw new InvalidOperationException("Each plan step text must contain 1-" + MaxStepChars + " characters.");
            }
        }

        private static string NormalizeStatus(string value)
        {
            var status = string.IsNullOrWhiteSpace(value) ? "pending" : value.Trim().ToLowerInvariant();
            switch (status)
            {
                case "pending":
                case "in_progress":
                case "completed":
                case "blocked":
                case "cancelled":
                    return status;
                default:
                    throw new InvalidOperationException("Unknown plan step status: " + status);
            }
        }

        private static ChatPlan Clone(ChatPlan value)
        {
            return JsonConvert.DeserializeObject<ChatPlan>(JsonConvert.SerializeObject(value)) ?? new ChatPlan();
        }

        private static JObject Payload(ChatPlan plan, ChatArtifact artifact)
        {
            return new JObject
            {
                ["artifactId"] = artifact == null ? null : artifact.Id,
                ["revision"] = artifact == null ? 0 : artifact.Revision,
                ["plan"] = plan == null ? null : JObject.FromObject(plan)
            };
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private static string CreateSchema()
        {
            return PlanPayloadSchema(false);
        }

        private static string UpdateSchema()
        {
            return PlanPayloadSchema(true);
        }

        private static string PlanPayloadSchema(bool update)
        {
            var properties = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable plan id returned by plan_create/read, or any artifact revision id; the latest revision is updated." },
                ["goal"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible goal for the plan.", ["minLength"] = 1, ["maxLength"] = MaxGoalChars },
                ["steps"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = update ? "Complete replacement list of plan steps." : "Complete ordered plan steps.",
                    ["minItems"] = 1,
                    ["maxItems"] = MaxSteps,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable unique step id without whitespace." },
                            ["text"] = new JObject { ["type"] = "string", ["description"] = "Concise user-visible step description.", ["minLength"] = 1, ["maxLength"] = MaxStepChars },
                            ["status"] = new JObject { ["type"] = "string", ["description"] = "Explicit current step status.", ["enum"] = new JArray("pending", "in_progress", "completed", "blocked", "cancelled"), ["default"] = "pending" }
                        },
                        ["required"] = new JArray("id", "text"),
                        ["additionalProperties"] = false
                    }
                }
            };
            if (!update) properties.Remove("id");
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = update ? new JArray("id") : new JArray("goal", "steps"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string ReadSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Stable plan id or any artifact revision id; omit to read the active plan. The latest revision is returned.\"}},\"required\":[],\"additionalProperties\":false}";
        }

        private static string DeleteSchema()
        {
            return "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Stable plan id or any artifact revision id; all revisions of that plan are deleted.\",\"minLength\":1}},\"required\":[\"id\"],\"additionalProperties\":false}";
        }

        private sealed class PlanRevision
        {
            public ChatArtifact Artifact { get; set; }
            public ChatPlan Plan { get; set; }
        }
    }
}
