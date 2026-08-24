using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class BuiltInSkillProvider
    {
        public static IReadOnlyList<SkillDefinition> GetSkills(IOfficeApplicationAdapter adapter)
        {
            var result = new List<SkillDefinition>
            {
                Skill(
                    "common.task_planning",
                    "Task planning",
                    "Create and maintain a visible step plan for complex Agent tasks.",
                    "# Task Planning\n\nUse a visible plan only when a task has several meaningful stages or the user asks for one. Do not create a plan for a direct answer or a single obvious action.\n\n- Call common.plan_create once with a concise goal and stable ordered step ids. Use only pending, in_progress, completed, blocked, or cancelled statuses.\n- A plan is presentation data, not execution authority and not proof of success. Continue to use exact tools normally.\n- After material progress, call common.plan_update with the stable plan id and the complete updated step list. Preserve step ids and mark completed only after the relevant TOOL_RESULT has ok=true.\n- If the current plan id or contents are not present in the conversation, call common.plan_read without id to read the active plan before updating it.\n- Mark a step blocked when work genuinely cannot continue; explain the blocker in the final answer.\n- Create a new plan for a different goal. Delete a plan only when the user asks or it was created by mistake.\n- Do not update the plan after every trivial tool call and do not expose hidden reasoning in plan text."),
                Skill(
                    "common.text_search_replace",
                    "Text search and replace",
                    "Find and safely replace literal or regex text in Office content.",
                    "# Text Search and Replace\n\n- Use the host search tool before replacement and preserve its exact scope, options, matchCount, and scopeSha256.\n- Prefer literal mode unless the user needs a pattern. Use regex capture groups only in regex mode.\n- Keep scope as narrow as practical and review returned coordinates/previews.\n- Pass expectedMatches and expectedScopeSha256 to replacement; never guess them.\n- Regex and bulk replacement require confirmation. If the scope changed, search again instead of bypassing the stale-scope error.\n- Verify the returned content hash after mutation."),
                Skill(
                    "common.vba_code_editing",
                    "VBA code editing",
                    "Inspect, search, write/upsert, patch, delete, and restore VBA components safely.",
                    "# VBA Code Editing\n\n- Use the compact common.vba_* facade in Excel, Word, and PowerPoint. Call common.vba_list_modules, common.vba_read_module, or common.vba_search_code only when you need to discover or inspect code. common.vba_read_module reads the whole bounded source by default; add startLine/lineCount for an exact range.\n- A separate read is not required before a mutation. Runtime reads and binds current state itself, checks it again after confirmation, creates a backup where prior source exists, and verifies read-back. expectedCodeSha256 is never a model argument. If a patch/delete becomes stale, retry it and inspect only if intent may no longer match. For a stale whole-source write derived from an earlier read, re-read and reconcile; retry unchanged only when a complete overwrite is intentional.\n- Use common.vba_write_module when you have the complete intended source. Its default upsert mode updates an existing component or creates a missing one. componentType matters only for creation. Invalid new names are normalized to a stable VBA identifier and the result returns the actual name. Use createOnly or updateOnly only when strict existence semantics matter.\n- Use common.vba_apply_patch for targeted edits and pass patch as a native JSON array, never stringified JSON. Each operation exposes only its relevant fields; use text for replacement content. Combine known edits for one module into one ordered array. replace requires exactly one match; replaceAll is explicit; insertBefore/insertAfter add line-safe blocks around one unique anchor; replaceLines is one-based and sees prior operations; regexReplace supports capture groups and a replacement limit. The old replace content field is compatibility input, not part of the model-facing schema.\n- After a range read, prefer replaceLines over copying a large multi-line fragment. Set replaceAll only after considering every occurrence. Never embed raw control characters; generate them at VBA runtime with ChrW$(n). A failed safe mutation is not permission to call hidden whole-module or macro backends.\n- VBA mutations require confirmation unless the user enabled AutoConfirmToolActions. This remains an execution boundary because snapshots recover source but cannot undo behavior of executable or auto-run VBA.\n- Whole-source write supports StdModule, ClassModule, and a blank MSForm/UserForm with code-behind. Visual UserForm controls, layout, properties, and FRX assets are outside these tools. Delete supports only StdModule and ClassModule; document modules and UserForms are not deleted. For restore, prefer an exact backupId from common.vba_list_backups; use moduleName only when restoring that module's latest backup is intentional."),
                Skill(
                    "common.tool_authoring",
                    "Tool authoring",
                    "Design reusable pipeline or VBA tools safely.",
                    "# Tool Authoring\n\nUse this skill when creating or editing executable RNAssistant tools.\n\n- Prefer an existing capability. Create a tool only when the requested work needs a reusable missing capability.\n- Tools are executable actions, not guidance documents. Pipeline tools call existing exact tool ids through ordered pipeline.steps with object arguments.\n- For create: build one complete definition, call common.tools_validate, then common.tools_create. For update: call common.tools_read, validate the complete intended definition, then call common.tools_update with only changed fields.\n- parameters is the actual strict object JSON Schema, pipeline is an actual JSON object, and components is an actual array of VBA sources; never double-encode them as JSON strings.\n- The catalog refreshes after a confirmed create/update or on the next user run. Creating a tool does not complete the original Office task.\n- VBA tools are host-specific. Put the manifest and entry function in the first StdModule component; custom VBA execution remains confirmation-controlled.\n- parameters must contain properties, required, and additionalProperties:false. Every argument needs an explicit type and useful description; declare real defaults, enums, limits, and array items.\n- Do not store secrets in code or metadata, weaken safety flags, or shadow built-in ids."),
                Skill(
                    "common.vba_tool_authoring",
                    "VBA tool authoring",
                    "Create versioned RNAssistant VBA tool packages with a strict manifest and String-returning entry function.",
                    "# VBA Tool Authoring\n\nUse this skill only for reusable VBA tools. Prefer an existing built-in or pipeline tool first.\n\n" +
                    "## Package\n\n- The global AppData package is canonical. Put source in `src/*.bas` and `src/*.cls`.\n- The first component is a standard entry module. Supporting standard/class modules are allowed. UserForms, document modules and binary FRX assets are not supported in v1.\n- Component names start with a Latin letter, contain only letters/numbers/underscore and are at most 40 characters. Use `RNA_<Tool>` for the entry module, `RNATool_<Tool>` for the function and `RNA_<Tool>_<Role>` for dependencies.\n\n" +
                    "## Manifest and signature\n\n- Put exactly one comment-delimited JSON object between `<RNAssistantTool>` and `</RNAssistantTool>` immediately before the entry function.\n- Required fields: protocolVersion=1, id, name, description, host, packageVersion, entryPoint, components, argumentOrder, formal parameters JSON Schema, mutatesDocument, agentCanRun and requiresConfirmation. Set agentCanRun=true only when Agent should select the package; VBA execution remains confirmation-controlled unless auto-confirm is enabled.\n- Use `additionalProperties:false`. Every parameter needs an explicit type and useful description. Only String, Long, Double and Boolean arguments are allowed, with at most 30 positional arguments. Every function argument is `ByVal`, follows argumentOrder and matches its schema type. Optional arguments require a schema default.\n- The entry declaration is `Public Function ... As String`. Return a concise useful String. Raise a normal VBA error for failure; RNAssistant creates the JSON result envelope. Do not add a VBA JSON parser or manually build a result envelope.\n\n" +
                    "## Code rules\n\n- Use `Option Explicit`, deterministic names and explicit Office object references. Avoid Select/Activate where a direct object reference works.\n- Do not embed secrets, network credentials or machine-specific paths. Validate sheet/range/document inputs before mutation and leave application-wide settings restored in an error handler.\n- Never auto-run newly generated code. Validate and save the package first; installation is a separate confirmed operation. Safety behavior after installation follows manifest metadata."),
                Skill(
                    "common.skill_authoring",
                    "Skill authoring",
                    "Create markdown skills that guide agent behavior without executing actions.",
                    "# Skill Authoring\n\nUse this skill when the user asks to create or edit RNAssistant guidance.\n\nA skill is a Markdown instruction file. It describes workflow, constraints, and preferred tools; it never executes actions itself. Agent context contains only the enabled skill catalog with id, name, and description. The agent loads the complete versioned Markdown body through common.skills_read when the catalog description matches the task. If the requested skill needs a new executable capability, create and validate a tool separately instead of embedding executable logic in the skill. Use common.skills_create for a new id. Before common.skills_update, read the existing skill and send only changed fields; omitted fields are preserved. Built-in skill ids are reserved. Create/update/delete require confirmation. Never store secrets or weaken runtime safety.\n\nRecommended sections:\n\n- Purpose\n- Workflow\n- Constraints\n- Useful tools"),
                Skill(
                    "common.prompt_authoring",
                    "Prompt authoring",
                    "Review and improve RNAssistant editable prompts without weakening its protocol or safety.",
                    "# Prompt Authoring\n\nUse this skill only when the user asks to inspect or improve RNAssistant prompts.\n\n- Call common.prompts_read_defaults before proposing a change.\n- All editable prompts use Markdown. Organize longer instructions with stable headings and short rule lists; keep service prompts compact.\n- Agent mode receives one RUNTIME_CONTEXT JSON object with strict function-style tools, a compact skill catalog, document identity, user context, and artifacts. Relevant complete Markdown skill instructions arrive only through common.skills_read results.\n- Preserve the minimal response fields: message and tool_calls. Each tool call contains a unique id, exact name, and object arguments. Independent calls may be returned together and execute sequentially.\n- Chat mode has no tools. ContextCompactionPrompt owns durable summary criteria.\n- Do not weaken confirmation, secret handling, or the rule that tool success is established only by TOOL_RESULT ok=true.\n- common.prompts_save changes local settings and requires confirmation."),
                Skill(
                    "common.html_workspace_authoring",
                    "HTML workspace authoring",
                    "Build and edit local HTML workspace pages, reports, dashboards, CSS, scripts, and data sources.",
                    "# HTML Workspace Authoring\n\n- Call common.html_workspace_read without arguments to list files/data sources, then read only the exact path or dataName needed before changing or deleting existing content.\n- Use common.html_workspace_upsert_file for html, css, or script files and common.html_workspace_upsert_data for editable data. Use the matching delete tools for removal.\n- Keep pages local and editable. Split substantial CSS and JavaScript into separate files and return at most one content-bearing upsert step per model turn.\n- Default to a responsive full-page layout with body margin 0; do not force a narrow centered card unless requested.\n- Network fetch is allowed only through the RNAssistant host after the user explicitly allows the HTTP(S) origin. Never use mode:no-cors or embed API keys and credentials.")
            };

            var hostProvider = adapter as IOfficeBuiltInSkillProvider;
            if (hostProvider != null)
            {
                result.AddRange((hostProvider.GetBuiltInSkills() ?? new SkillDefinition[0]).Where(skill => skill != null));
            }

            return result;
        }

        private static SkillDefinition Skill(string id, string name, string description, string body)
        {
            return new SkillDefinition
            {
                Id = id,
                Host = "Common",
                Name = name,
                Description = description,
                BodyMarkdown = body,
                Enabled = true,
                BuiltIn = true,
                Version = "1.0.0"
            };
        }
    }
}
