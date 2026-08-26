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
                    "common.task_tracking",
                    "Task tracking",
                    "Create and maintain a temporary visible checklist for work with at least three meaningful stages.",
                    "# Task Tracking\n\nCreate a task list for work with at least three meaningful user-level stages. Do not turn individual reads or tool calls into artificial tasks.\n\n- Call common.task_list_create before execution with stable ordered step ids and at most one in_progress step.\n- The list tracks execution; it is not a strategic design document, execution authority, or proof of success.\n- After material progress, call common.task_list_update with the complete step list. Preserve ids and mark completed only after matching TOOL_RESULT ok=true.\n- If the active list is outside context, read the activeTaskList rna:// URI through common.resources_read. Never guess a URI.\n- Keep blocked work visible. Before a successful final answer, make all steps terminal and call common.task_list_close with completed. Use cancelled or superseded when appropriate.\n- Do not update after every trivial tool call and do not expose hidden reasoning in task text."),
                Skill(
                    "common.text_search_replace",
                    "Text search and replace",
                    "Find and safely replace literal or regex text in Office content.",
                    "# Text Search and Replace\n\n- Use the host search tool when you need discovery, coordinates, or a preview; it is not a mandatory precondition for replacement.\n- Prefer literal mode unless the user needs a pattern. Use regex capture groups only in regex mode.\n- Keep scope as narrow as practical and review returned coordinates/previews for broad or ambiguous edits.\n- Replacement tools read the current scope themselves under the mutation lock, enforce maxReplacements, and verify the result. Do not invent or pass match-count/hash guards.\n- Regex and bulk replacement require confirmation."),
                Skill(
                    "common.vba_code_editing",
                    "VBA code editing",
                    "Safely inspect, create, rename, refactor, patch, delete, or restore VBA macros and components in Excel, Word, or PowerPoint. Use whenever a request changes VBA source or component identity.",
                    "# VBA Code Editing\n\n" +
                    "## Choose the operation\n\n" +
                    "- Discover VBA through the shared resource tools: list provider `vba` with kind `vba-component` or `vba-backup`, use `common.resources_search` for bounded literal discovery, and read an exact component URI with representation `source`. Continue with `nextCursor` when a source chunk is truncated. Never guess a resource URI or use removed VBA read/search aliases.\n" +
                    "- For whole-source write, use `common.vba_write_module` only when you have the complete intended module source. Default `mode=upsert` updates or creates; `componentType` matters only on creation. Use `createOnly` or `updateOnly` only when existence itself must be guarded. Continue with the actual normalized name returned by the tool.\n" +
                    "- Use `common.vba_apply_patch` only for an existing component. It never creates a missing module: when the target is absent, switch to `common.vba_write_module` with the full source and component type.\n" +
                    "- For an identity-preserving rename, call `common.vba_write_module` with exactly `moduleName`, `newModuleName`, and `mode=rename`; omit `code` and `componentType`. Runtime guards both names, rejects collisions, journals the old and new identities, and preserves source/type. Rename supports StdModule, ClassModule, and blank code-only MSForm, but not document modules. It does not rewrite explicit references such as `OldModule.Run`; search and update those deliberately. Never imitate rename with write plus delete.\n\n" +
                    "## Patch without corrupting code\n\n" +
                    "- Pass `patch` as a native JSON array, never stringified JSON. VBA patching supports only ordered exact `replace` hunks with `find` and `text`; there are no line-number, fuzzy, first-match, regex, or implicit insertion modes. Every later hunk sees the result of earlier hunks.\n" +
                    "- An exact replacement whose `find` and `text` already match is an idempotent no-op and is skipped. If every hunk is a no-op, the tool succeeds without a document write or journal mutation.\n" +
                    "- Copy `find` from a fresh `common.resources_read` source chunk and include enough unchanged surrounding source for exactly one case-sensitive match. Missing or ambiguous source is a stale/unsafe patch and must be re-read, never retried unchanged.\n" +
                    "- For insertion, repeat the exact anchor block in `text` and add new code before or after it. Runtime preserves string contents and boundary newlines, normalizing only LF/CRLF to the current module style. For deletion, use empty `text`.\n" +
                    "- Never rebuild an existing module with `common.vba_write_module` from a truncated read or partial context; patch exact current hunks instead. Use whole-source write only when the complete intended module is known.\n" +
                    "- Keep every `End Sub`, `End Function`, `End Property`, `End Type`, and `End Enum` on its own logical line. Never embed BOM, hidden Unicode formatting/line separators, NUL, or other raw control characters; produce needed values at runtime with `ChrW$(n)`.\n\n" +
                    "## Write maintainable VBA\n\n" +
                    "- Keep the module's contiguous `Option ...` directives at the top and before declarations. Include `Option Explicit` in authored modules, and insert declarations only after the complete Option block. Preserve host ownership: keep workbook, worksheet, document, presentation, class, and UserForm event procedures in the component that owns those events.\n" +
                    "- Qualify Office objects deliberately and avoid `Select`, `Activate`, `Selection`, or implicit `ActiveWorkbook`/active-document state when a stable object reference is available. Use `Value2` and a Variant 2D array for bulk Excel range transfer instead of cell-by-cell loops when practical.\n" +
                    "- Scope error suppression narrowly. When changing application-wide flags such as `ScreenUpdating`, `EnableEvents`, `DisplayAlerts`, or calculation mode, capture their previous values and restore them on both success and failure.\n" +
                    "- For Office API declarations, verify the DLL entry point and signature against platform/vendor documentation or an existing trusted project declaration; do not guess parameter types. Use VBA7-compatible `PtrSafe` and `LongPtr` only for pointer or handle values, and conditional compilation only when cross-version compatibility is actually required. Do not introduce an unverified external reference when late binding or an existing reference is sufficient.\n" +
                    "- A successful mutation proves VBE-equivalent source read-back and returns the actual hash, type, and name; `vbeNormalized=true` means literal text was normalized. It does not prove VBA compilation or runtime behavior. Do not claim the macro works solely from write success and do not invoke hidden macro backends to test it. State when Windows Office validation is still required.\n\n" +
                    "## Mutation and recovery\n\n" +
                    "- A public pre-read is optional because runtime reads current state, binds a guard through confirmation, journals intended state, and verifies read-back. Source-changing mutations create a source backup when prior code exists; rename instead journals both names and its hidden backend restores the original name on a verified failure. `expectedCodeSha256` is never a model argument. Re-read after stale detection when intent may conflict; repeat an unchanged whole-source overwrite only when that overwrite is deliberate.\n" +
                    "- Mutations require confirmation unless `AutoConfirmToolActions` is enabled. Source backup cannot undo behavior already executed by a macro or event handler.\n" +
                    "- Whole-source creation supports `StdModule`, `ClassModule`, and blank code-only `MSForm`. Load `common.vba_userform_authoring` for runtime-generated controls and events. Designer/FRX state is outside this protocol. Delete supports only `StdModule` and `ClassModule`; document modules and UserForms are not deleted. Restore by exact `backupId` when possible; use `moduleName` only to intentionally choose that module's latest backup."),
                Skill(
                    "common.vba_userform_authoring",
                    "Code-only VBA UserForm authoring",
                    "Create or edit a blank VBA UserForm whose controls, layout, properties and events are generated entirely from source code.",
                    "# Code-only VBA UserForm Authoring\n\nUse a blank MSForm as a generated host shell and keep the complete semantic UI definition in code. Designer-time controls/properties and FRX assets are unsupported.\n\n## Structure\n\n- Create the form with common.vba_write_module and componentType=MSForm. Build controls from one UserForm_Initialize path with Me.Controls.Add, stable explicit names and deterministic layout. Set the form caption, size and every relevant runtime-settable property in code. Do not depend on manual Designer edits.\n- For a fixed control set, declare concrete Private WithEvents MSForms fields and assign each result of Controls.Add. For repeated controls, use a typed event-sink class per control and retain every sink in a form-level Collection; an unreferenced sink loses its events.\n- Put the public Show entry point in a StdModule. Use additional ClassModule components only when event sinks or reusable view logic are needed. Keep initialization idempotent for one instance and never append controls from repeatedly fired UserForm_Activate.\n\n## Editing and recovery\n\n- Read code through provider vba and common.resources_*; patch or replace it through the typed common.vba_* mutation tools. After an edit or restore, unload an already loaded form and instantiate it again; source changes do not rebuild a live instance.\n- Source backup/restore covers the code-only UI definition, not Designer/FRX state and not Office document changes already performed by event handlers. Do not describe chat undo/redo as VBA undo.\n- Reusable packages may include an MSForm component with code-behind stored as .form.vba, plus a StdModule launcher and optional ClassModule event sinks. Never pass exported .frm/.frx content. Install/remove is one journaled component transaction and fails closed on an existing unowned form, Designer controls, unverified Designer state or a type collision.\n- Keep embedded images, custom ActiveX controls, manual tab order and other Designer-dependent behavior out of this profile. Validate actual form creation and events on Windows x64 with Office x64."),
                Skill(
                    "common.tool_authoring",
                    "Tool authoring",
                    "Design reusable pipeline or VBA tools safely.",
                    "# Tool Authoring\n\nUse this skill when creating or editing executable RNAssistant tools.\n\n" +
                    "- Prefer an existing capability. Create a tool only when the requested work needs a reusable missing capability.\n" +
                    "- Tools are executable actions, not guidance documents. Pipeline tools call existing exact tool ids through ordered steps.\n" +
                    "- Call common.tools_upsert with one effective definition: runtime creates a missing id or merges supplied fields into an existing tool, then validates before confirmation/save. Use createOnly or updateOnly only when strict existence semantics matter. Use common.tools_validate only for a requested no-save preflight.\n" +
                    "- Call common.tools_definition_read with id only when existing implementation fields must be inspected, or without id for a compact custom-tool list. This authoring read does not load the callable schema; use common.capabilities_read with that exact tool id separately before executing it.\n" +
                    "- In Agent mode prefer parameterDefinitions: one native entry per argument with name, type, description and optional required/default/limits. Runtime compiles it to the canonical strict parameters object. Do not combine parameterDefinitions with parameters.\n" +
                    "- In Agent mode prefer pipelineSteps: each step has toolId and an optional native arguments array of {name,value}. Values may be scalars, null, primitive arrays, tables or placeholders such as {{args.name}}. Runtime compiles them to the canonical keyed pipeline object. Use advanced pipeline only when a nested object shape cannot be represented; do not combine the two forms.\n" +
                    "- Advanced parameters and pipeline are actual JSON objects, and components is an actual array of VBA sources; never double-encode them as JSON strings.\n" +
                    "- Use only the supported schema keywords: type, description, properties, required, additionalProperties, items, anyOf, enum, const, default, minimum, maximum, minLength, maxLength, minItems and maxItems. Unsupported assertions are rejected.\n" +
                    "- Every args/steps placeholder must resolve before its nested call. An unresolved placeholder fails the pipeline; never rely on passing placeholder text through literally.\n" +
                    "- The catalog refreshes after a confirmed upsert or on the next user run. Creating a tool does not complete the original Office task.\n" +
                    "- VBA tools are host-specific. Put the manifest and entry function in the first StdModule component; custom VBA execution remains confirmation-controlled.\n" +
                    "- Every argument needs an explicit type and useful description; declare real defaults, enums, limits and array items.\n" +
                    "- Do not store secrets in code or metadata, weaken safety flags, or shadow built-in ids."),
                Skill(
                    "common.vba_tool_authoring",
                    "VBA tool authoring",
                    "Create versioned RNAssistant VBA tool packages with a strict manifest and String-returning entry function.",
                    "# VBA Tool Authoring\n\nUse this skill only for reusable VBA tools. Prefer an existing built-in or pipeline tool first.\n\n" +
                    "## Package\n\n- The global AppData package is canonical. Put source in `src/*.bas`, `src/*.cls`, and `src/*.form.vba`.\n- The first component is a standard entry module. Supporting standard/class modules and blank code-only MSForms are allowed. An MSForm source contains only code-behind; exported .frm/.frx, document modules and other Designer state are unsupported. Load common.vba_userform_authoring for its runtime control/event profile.\n- Component names start with a Latin letter, contain only letters/numbers/underscore and are at most the VBE limit of 31 characters; entry functions use the project limit of 40. Use `RNA_<Tool>` for the entry module, `RNATool_<Tool>` for the function and `RNA_<Tool>_<Role>` for dependencies.\n\n" +
                    "## Manifest and signature\n\n- Put exactly one comment-delimited JSON object between `<RNAssistantTool>` and `</RNAssistantTool>` immediately before the entry function.\n- Required fields: protocolVersion=1, id, name, description, host, packageVersion, entryPoint, components, argumentOrder, formal parameters JSON Schema, mutatesDocument, agentCanRun and requiresConfirmation. Set agentCanRun=true only when Agent should select the package; VBA execution remains confirmation-controlled unless auto-confirm is enabled.\n- Use `additionalProperties:false`. Every parameter needs an explicit type and useful description. Only String, Long, Double and Boolean arguments are allowed, with at most 30 positional arguments. Every function argument is `ByVal`, follows argumentOrder and matches its schema type. Optional arguments require a schema default.\n- The entry declaration is `Public Function ... As String`. Return a concise useful String. Raise a normal VBA error for failure; RNAssistant creates the JSON result envelope. Do not add a VBA JSON parser or manually build a result envelope.\n\n" +
                    "## Code rules\n\n- Use `Option Explicit`, deterministic names and explicit Office object references. Avoid Select/Activate where a direct object reference works.\n- Do not embed secrets, network credentials or machine-specific paths. Validate sheet/range/document inputs before mutation and leave application-wide settings restored in an error handler.\n- Never auto-run newly generated code. Validate and save the package first; installation is a separate confirmed operation. Safety behavior after installation follows manifest metadata."),
                Skill(
                    "common.skill_authoring",
                    "Skill authoring",
                    "Create markdown skills that guide agent behavior without executing actions.",
                    "# Skill Authoring\n\nUse this skill when the user asks to create or edit RNAssistant guidance.\n\nA skill is a concise Markdown instruction file. It describes workflow, constraints, and preferred tools; it never executes actions itself. Agent context contains one compact capability catalog with exact ids and explicit tool/skill kinds. A listed skill entry is not loaded guidance. Load the complete Markdown body through common.capabilities_read with its exact id when the user names the skill or its catalog summary matches the task, and reload it when matching data.loaded=true evidence is no longer fully present in active context. Keep the core body under 500 lines and split broad workflows into narrower skills. Put detailed UTF-8 Markdown directly under references/, link each file from SKILL.md, and explain when to read it. Read only needed chunks with common.capabilities_read using referencePath/offset/maxChars. Use common.skills_upsert with referencePath and referenceMarkdown to create or replace one reference, and common.skills_delete with referencePath to delete one. Mutate the core and a reference in separate confirmed calls. If the requested skill needs a new executable capability, create and validate a tool separately instead of embedding executable logic in the skill. For the core, common.skills_upsert creates a missing id or preserves omitted fields on update. Read existing content only when the requested edit depends on it. Built-in skill ids are reserved. Never store secrets or weaken runtime safety.\n\nRecommended sections:\n\n- Purpose\n- Workflow\n- Constraints\n- Useful tools"),
                Skill(
                    "common.prompt_authoring",
                    "Prompt authoring",
                    "Review and improve RNAssistant editable prompts without weakening its protocol or safety.",
                    "# Prompt Authoring\n\nUse this skill only when the user asks to inspect or improve RNAssistant prompts.\n\n- Call common.prompts_read with includeDefaults=true before proposing a change.\n- Agent instructions are composed in stable order from SystemPrompt (general contract), AgentToolsPrompt, and AgentSkillsPrompt; Chat uses ChatSystemPrompt. Both are followed by one dynamic RUNTIME_CONTEXT and use the same structured response envelope. Keep cross-tool policy in AgentToolsPrompt and tool-specific inputs/errors in each tool description. The Agent capability catalog is metadata only; prompts must require common.capabilities_read with an exact catalog id and matching loaded evidence before tool- or skill-governed work.\n- Other editable Markdown prompts are ContextCompactionPrompt, ChatTitlePrompt, and AttachmentAnalysisPrompt. Compatibility probes are intentionally fixed so their diagnostics remain trustworthy.\n- Preserve the required response fields message and tool_calls. A non-empty tool_calls array continues the run; an empty array ends it. Do not add status, phase, or another response envelope. Each call needs a unique id, exact name, and object arguments. Independent calls may be returned together and execute sequentially.\n- Chat may use only the read-only common.resources_list/resolve/search/read catalog; never add mutation or skill use to ChatSystemPrompt. ContextCompactionPrompt owns durable summary criteria.\n- Do not weaken confirmation, secret handling, or the rule that tool success is established only by TOOL_RESULT ok=true.\n- common.prompts_save changes local settings and requires confirmation."),
                Skill(
                    "common.html_workspace_authoring",
                    "HTML workspace authoring",
                    "Build, inspect, search, patch, and maintain local HTML reports, dashboards, CSS, scripts, static JSON, and refreshable Office-bound data when visual presentation materially helps.",
                    "# HTML Workspace Authoring\n\n## Inspect and edit\n\n" +
                    "- For an existing workspace, use the activeHtml rna:// URI from RUNTIME_CONTEXT with common.resources_read and representation structure for its compact manifest. List provider chat with exact kind html-file or html-data to discover current members. Use common.resources_search for bounded discovery and common.resources_read with source/text plus cursor/maxChars for only the needed body chunks. Never guess a member URI or read every body.\n" +
                    "- Use common.html_workspace_upsert for new resources, small files, and intentional whole-source rewrites. Default mode=upsert creates or updates; use createOnly/updateOnly only when existence itself matters.\n" +
                    "- Use common.html_workspace_apply_patch for targeted edits to an existing HTML/CSS/JavaScript file. Pass patch as a native ordered array. A separate read is optional because runtime applies all operations atomically to current source. Prefer unique replace/insert anchors or replaceLines; use replaceAll only intentionally.\n" +
                    "- After material edits, call common.html_workspace_inspect as a static preflight. Fix errors; treat unresolved-reference warnings as review prompts because runtime-created DOM/data may be valid. This tool does not execute JavaScript or render WebView.\n" +
                    "- Use common.html_workspace_delete for removal. Workspace mutations are recoverable artifact revisions and do not require VBA-style backups.\n\n" +
                    "## Runtime model\n\n" +
                    "- The active HTML file is the entry page. RNAssistant injects every workspace CSS file into its head and every classic JavaScript file before its closing body in workspace order. Do not add local link/script references and do not use ES module import/export.\n" +
                    "- Every script runs on every active entry page. Use an IIFE or one stable namespace, avoid global collisions, and guard DOM lookups. Keep the main DOM in the entry HTML; split substantial styling and behavior into focused CSS and JavaScript files.\n" +
                    "- Default to a responsive accessible full-page layout with body margin 0; do not force a narrow centered card unless requested.\n\n" +
                    "## Data and safety\n\n" +
                    "- For Office-backed data that should stay current, use common.html_data_bind with an approved read-only source. Choose sourceTool first and pass only fields from that tool's exact schema in sourceArguments. For excel.read_range these are sheet, address and content; never pass kind. Prefer transform=table for grids/charts and raw when the source already suits the page. Read it through window.RNAssistantData and handle missing, empty, and binding-error states.\n" +
                    "- Refresh bound data instead of copying Office values into scripts. Prefer common.html_data_freeze before intentionally converting a binding to static JSON.\n" +
                    "- Network fetch is allowed only through the RNAssistant host after the user explicitly allows the HTTP(S) origin. Do not rely on CDN scripts, remote frames, credentials, mode:no-cors, or embedded secrets.")
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
