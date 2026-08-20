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
                    "Break Office requests into safe, executable tool steps.",
                    "# Task Planning\n\nUse this skill when the user asks RNAssistant to act on Office content.\n\n- Decide whether the task needs document inspection, mutation, or only prose.\n- Use existing tools exactly by id; never invent tool ids.\n- Prefer small steps with clear arguments.\n- If data is missing, inspect the document or ask a concise question.\n- Stop after the local tool result is sufficient and answer normally."),
                Skill(
                    "common.text_search_replace",
                    "Text search and replace",
                    "Find and safely replace literal or regexp text in Office content.",
                    "# Text Search and Replace\n\n- Use the host search tool before replacement and preserve its exact scope, options, matchCount, and scopeSha256.\n- Prefer literal mode unless the user needs a pattern. Use regexp capture groups only in regexp mode.\n- Keep scope as narrow as practical and review returned coordinates/previews.\n- Pass expectedMatches and expectedScopeSha256 to replacement; never guess them.\n- Regexp and bulk replacement require confirmation. If the scope changed, search again instead of bypassing the stale-scope error.\n- Verify the returned content hash after mutation."),
                Skill(
                    "common.vba_code_editing",
                    "VBA code editing",
                    "Inspect, search, patch, create, and delete VBA components safely.",
                    "# VBA Code Editing\n\n- Start with vba_list_modules, then read only the exact modules needed with vba_read_module. Use vba_search_code only when a project-wide search is required.\n- Prefer structured vba_apply_patch over replacing the whole module. regexReplace supports bounded regexp changes and capture groups.\n- Every mutation creates a backup and requires confirmation; verify the code hash afterward.\n- Create/delete is limited to StdModule and ClassModule. Document modules and UserForms are read/search/patch only.\n- For delete, pass the current codeSha256 from read/search and never bypass a stale hash."),
                Skill(
                    "common.tool_authoring",
                    "Tool authoring",
                    "Design reusable pipeline or VBA tools safely.",
                    "# Tool Authoring\n\nUse this skill when creating or editing executable RNAssistant tools.\n\n- Prefer an existing capability. Create a tool only when the requested work needs a reusable missing capability.\n- Tools are executable actions, not guidance documents. Pipeline tools call existing exact tool ids through ordered JSON steps.\n- Read an existing definition before changing it. For a new or changed tool call common.tools_validate, then common.tools_save, then use the saved exact id in a later turn.\n- Saving a tool does not complete the original Office task.\n- VBA tools must be host-specific and require confirmation for mutations. Keep formal object schemas small and explicit.\n- Do not store secrets in code or metadata, weaken safety flags, or shadow built-in ids."),
                Skill(
                    "common.vba_tool_authoring",
                    "VBA tool authoring",
                    "Create versioned RNAssistant VBA tool packages with a strict manifest and String-returning entry function.",
                    "# VBA Tool Authoring\n\nUse this skill only for reusable VBA tools. Prefer an existing built-in or pipeline tool first.\n\n" +
                    "## Package\n\n- The global AppData package is canonical. Put source in `src/*.bas` and `src/*.cls`.\n- The first component is a standard entry module. Supporting standard/class modules are allowed. UserForms, document modules and binary FRX assets are not supported in v1.\n- Component names start with a Latin letter, contain only letters/numbers/underscore and are at most 40 characters. Use `RNA_<Tool>` for the entry module, `RNATool_<Tool>` for the function and `RNA_<Tool>_<Role>` for dependencies.\n\n" +
                    "## Manifest and signature\n\n- Put exactly one comment-delimited JSON object between `<RNAssistantTool>` and `</RNAssistantTool>` immediately before the entry function.\n- Required fields: protocolVersion=1, id, name, description, host, packageVersion, entryPoint, components, argumentOrder, formal parameters JSON Schema, mutatesDocument, agentCanRun and requiresConfirmation.\n- Use `additionalProperties:false`. Only String, Long, Double and Boolean arguments are allowed, with at most 30 positional arguments. Every function argument is `ByVal`, follows argumentOrder and matches its schema type. Optional arguments require a schema default.\n- The entry declaration is `Public Function ... As String`. Return a concise useful String. Raise a normal VBA error for failure; RNAssistant creates the JSON result envelope. Do not add a VBA JSON parser or manually build a result envelope.\n\n" +
                    "## Code rules\n\n- Use `Option Explicit`, deterministic names and explicit Office object references. Avoid Select/Activate where a direct object reference works.\n- Do not embed secrets, network credentials or machine-specific paths. Validate sheet/range/document inputs before mutation and leave application-wide settings restored in an error handler.\n- Never auto-run newly generated code. Validate and save the package first; installation is a separate confirmed operation. Safety behavior after installation follows manifest metadata."),
                Skill(
                    "common.skill_authoring",
                    "Skill authoring",
                    "Create markdown skills that guide agent behavior without executing actions.",
                    "# Skill Authoring\n\nUse this skill when the user asks to create or edit RNAssistant guidance.\n\nA skill is a markdown instruction file. It describes workflow, constraints, and preferred tools; it never executes actions itself. All enabled skills are included in Agent context. If the requested skill needs a new executable capability, create and validate a tool separately instead of embedding executable logic in the skill. Use common.skills_list/read before editing and common.skills_save only for a focused custom skill. Built-in skill ids are reserved. Never store secrets or weaken runtime safety.\n\nRecommended sections:\n\n- Purpose\n- Workflow\n- Constraints\n- Useful tools"),
                Skill(
                    "common.prompt_authoring",
                    "Prompt authoring",
                    "Review and improve RNAssistant editable prompts without weakening its protocol or safety.",
                    "# Prompt Authoring\n\nUse this skill only when the user asks to inspect or improve RNAssistant prompts.\n\n- Call common.prompts_read_defaults before proposing a change.\n- Agent mode receives one RUNTIME_CONTEXT JSON object with tools, complete Markdown skills, document identity, user context, and artifacts.\n- Preserve the minimal response fields: message and tool_calls. Each tool call contains a unique id, exact name, and object arguments. Independent calls may be returned together and execute sequentially.\n- Chat mode has no tools. ContextCompactionPrompt owns durable summary criteria.\n- Do not weaken confirmation, secret handling, or the rule that tool success is established only by TOOL_RESULT ok=true.\n- common.prompts_save changes local settings and requires confirmation."),
                Skill(
                    "common.html_workspace_authoring",
                    "HTML workspace authoring",
                    "Build and edit local HTML workspace pages, reports, dashboards, CSS, scripts, and data sources.",
                    "# HTML Workspace Authoring\n\n- Use common.html_workspace_read before changing or deleting existing workspace content.\n- Use common.html_workspace_upsert_file for html, css, or script files and common.html_workspace_upsert_data for editable data. Use the matching delete tools for removal.\n- Keep pages local and editable. Split substantial CSS and JavaScript into separate files and return at most one content-bearing upsert step per model turn.\n- Default to a responsive full-page layout with body margin 0; do not force a narrow centered card unless requested.\n- Network fetch is allowed only through the RNAssistant host after the user explicitly allows the HTTP(S) origin. Never use mode:no-cors or embed API keys and credentials.")
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
