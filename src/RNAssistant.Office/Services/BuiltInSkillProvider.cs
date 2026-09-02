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
                    "# Task Tracking\n\nCreate a task list for work with at least three meaningful user-level stages. Do not turn individual reads or tool calls into artificial tasks. Loading this skill does not load the tool schema: before first use, load the exact common.task_list_set id through common.capabilities_read and wait for its successful schema result.\n\n- Before execution, call common.task_list_set with action=save, the concise goal, and the complete ordered steps. Supply only step text/status; runtime owns list and stable step ids. Keep at most one step in_progress.\n- The list tracks execution; it is not a strategic design document, execution authority, or proof of success.\n- After material progress, call the same save branch with the complete current list. Mark completed only after matching TOOL_RESULT status=ok and evidence that the requested work is complete. Tool success does not by itself prove an applied effect; a verified no-op may also be ok.\n- If the active list is outside context, find it with common.resources_find in the conversation scope and read the exact returned semantic target. Never pass a resource URI, revision, cursor, list id, or step id.\n- Keep blocked work visible. Before a successful final answer, make all steps terminal, save that state, then call common.task_list_set with action=close and outcome=completed. Use cancelled or superseded when appropriate.\n- Do not update after every trivial tool call and do not expose hidden reasoning in task text."),
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
                    "- For a project-wide VBA question, read `RUNTIME_CONTEXT.document.vba_project_target` directly with `common.resources_read` and representation `structure`; this is the complete component inventory for the exact bound document. If that target is unavailable, browse `common.resources_find` with scope `vba` and no query, then read the first `VBA project` target. Use a query only to find a specific module: it is filtered search, not project inventory. Read a component target with representation `source`; one successful result contains the complete representation. Use scope `backups` only for recovery evidence. After `resource_revision_changed`, repeat the read. URI, revision, cursor, provider, and provider-specific kind are runtime-owned.\n" +
                    "- For whole-source write, use `common.vba_write_module` only when you have the complete intended module source. Default `mode=upsert` updates or creates; `componentType` matters only on creation. Use `createOnly` or `updateOnly` only when existence itself must be guarded. Continue with the actual normalized name returned by the tool.\n" +
                    "- Use `common.vba_apply_patch` only for an existing component. It never creates a missing module: when the target is absent, switch to `common.vba_write_module` with the full source and component type.\n" +
                    "- For an identity-preserving rename, call `common.vba_write_module` with exactly `moduleName`, `newModuleName`, and `mode=rename`; omit `code` and `componentType`. Runtime guards both names, rejects collisions, journals the old and new identities, and preserves source/type. Rename supports StdModule, ClassModule, and blank code-only MSForm, but not document modules. It does not rewrite explicit references such as `OldModule.Run`; search and update those deliberately. Never imitate rename with write plus delete.\n\n" +
                    "## Patch without corrupting code\n\n" +
                    "- Pass `patch` as a native JSON array, never stringified JSON. VBA patching supports only ordered exact `replace` hunks with `find` and `text`; there are no line-number, fuzzy, first-match, regex, or implicit insertion modes. Every later hunk sees the result of earlier hunks.\n" +
                    "- An exact replacement whose `find` and `text` already match is an idempotent no-op and is skipped. If every hunk is a no-op, the tool succeeds without a document write or journal mutation.\n" +
                    "- Copy `find` from a fresh complete `common.resources_read` source result and include enough unchanged surrounding source for exactly one case-sensitive match. Missing or ambiguous source is a stale/unsafe patch and must be re-read, never retried unchanged.\n" +
                    "- For insertion, repeat the exact anchor block in `text` and add new code before or after it. Runtime preserves string contents and boundary newlines, normalizing only LF/CRLF to the current module style. For deletion, use empty `text`.\n" +
                    "- Never rebuild an existing module with `common.vba_write_module` unless the complete intended source is present in active context; patch exact current hunks instead. Use whole-source write only when the complete intended module is known.\n" +
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
                    "# Code-only VBA UserForm Authoring\n\nUse a blank MSForm as a generated host shell and keep the complete semantic UI definition in code. Designer-time controls/properties and FRX assets are unsupported.\n\n## Structure\n\n- Create the form with common.vba_write_module and componentType=MSForm. Build controls from one UserForm_Initialize path with Me.Controls.Add, stable explicit names and deterministic layout. Set the form caption, size and every relevant runtime-settable property in code. Do not depend on manual Designer edits.\n- For a fixed control set, declare concrete Private WithEvents MSForms fields and assign each result of Controls.Add. For repeated controls, use a typed event-sink class per control and retain every sink in a form-level Collection; an unreferenced sink loses its events.\n- Put the public Show entry point in a StdModule. Use additional ClassModule components only when event sinks or reusable view logic are needed. Keep initialization idempotent for one instance and never append controls from repeatedly fired UserForm_Activate.\n\n## Editing and recovery\n\n- Find code through common.resources_find with scope=vba and read the exact semantic target; patch or replace it through the typed common.vba_* mutation tools. After an edit or restore, unload an already loaded form and instantiate it again; source changes do not rebuild a live instance.\n- Source backup/restore covers the code-only UI definition, not Designer/FRX state and not Office document changes already performed by event handlers. Do not describe chat undo/redo as VBA undo.\n- Reusable packages may include an MSForm component with code-behind stored as .form.vba, plus a StdModule launcher and optional ClassModule event sinks. Never pass exported .frm/.frx content. Install/remove is one journaled component transaction and fails closed on an existing unowned form, Designer controls, unverified Designer state or a type collision.\n- Keep embedded images, custom ActiveX controls, manual tab order and other Designer-dependent behavior out of this profile. Validate actual form creation and events on Windows x64 with Office x64."),
                Skill(
                    "common.tool_authoring",
                    "Tool authoring",
                    "Design reusable VBA tools safely.",
                    "# Tool Authoring\n\nUse this skill when creating or editing executable RNAssistant tools. Prefer an existing capability; tools are executable actions, not guidance. Only manifest-based VBA packages are supported.\n\n" +
                    "## Authoring flow\n\n- Find custom ids through common.capabilities_search. Read common.tools_definition_read with one exact id only when an edit depends on the existing implementation. It does not load the callable schema; common.capabilities_read does that separately.\n- Call common.tools_upsert with id plus complete native components when creating or changing implementation. Optional readme/useWhen/doNotUseWhen/limitations are selection documentation; omitted update fields are preserved. Use createOnly/updateOnly only when existence itself matters. Upsert performs the same complete validation before any confirmed save, so there is no separate model-facing validate call.\n- Do not pass host, executor, parameters, enabled, agentCanRun, mutation flags, confirmation flags, risk, revision, URI, hash or storage path. Runtime derives manifest metadata and assigns conservative execution authority. Creating a tool does not execute it or complete the original Office task.\n\n" +
                    "## Package and manifest\n\n- Pass components as a native ordered array, never stringified JSON. The first component is a StdModule with exactly one comment-delimited JSON manifest between `<RNAssistantTool>` and `</RNAssistantTool>` immediately before the entry function. Supporting StdModule/ClassModule and blank code-only MSForm sources are allowed; exported .frm/.frx and document modules are not.\n- The manifest requires protocolVersion=1, id, name, description, host, packageVersion, entryPoint, components, argumentOrder, formal parameters, mutatesDocument, agentCanRun and requiresConfirmation. Runtime treats arbitrary VBA conservatively regardless of weaker manifest claims.\n- Formal parameters use a strict object schema with `additionalProperties:false`. Every argument has an explicit type and useful description. String, Long, Double and Boolean are supported, at most 30 positional arguments; each function argument is `ByVal`, follows argumentOrder and matches the schema. Optional arguments require a default.\n- A plumbing-shaped argument such as customerId is accepted only when its description contains `Domain identity rationale:` followed by why the caller must choose that domain value. Runtime URI/revision/cursor/guard/hash inputs are not legitimate custom parameters.\n- Component names use letters/numbers/underscore, start with a Latin letter and fit the 31-character VBE limit. The entry declaration is `Public Function ... As String`; return useful text and raise a normal VBA error on failure. Do not create a second JSON result envelope.\n\n" +
                    "## Safety\n\n- Use Option Explicit, deterministic names and explicit Office objects. Avoid Select/Activate, secrets, credentials and machine-specific paths. Validate domain inputs and restore application-wide state on both success and failure. Never auto-run generated code; save and later execution remain separately confirmation-controlled. Load common.vba_userform_authoring for code-only form rules."),
                Skill(
                    "common.skill_authoring",
                    "Skill authoring",
                    "Create markdown skills that guide agent behavior without executing actions.",
                    "# Skill Authoring\n\nUse this skill when the user asks to create or edit RNAssistant guidance.\n\nA skill is concise Markdown guidance; it never executes actions. Load an existing exact skill through common.capabilities_read when the edit depends on it. Keep the core under 500 lines and move detailed UTF-8 Markdown into direct references/*.md files linked from the core. Read a needed reference with exact id/referencePath; use action=next only when hasMore=true. Offset, page size, revision and package guards are runtime-owned.\n\n- Use common.skills_upsert only for core create/update; omitted core fields are preserved.\n- Use common.skills_reference_upsert for one complete reference and common.skills_reference_delete for one exact reference. Use common.skills_delete only for the complete skill. Core and reference changes are separate confirmed calls.\n- If executable behavior is needed, author a tool separately. Built-in ids are reserved. Never store secrets or weaken runtime safety.\n\nRecommended sections:\n\n- Purpose\n- Workflow\n- Constraints\n- Useful tools"),
                Skill(
                    "common.prompt_authoring",
                    "Prompt authoring",
                    "Review and improve RNAssistant editable prompts without weakening its protocol or safety.",
                    "# Prompt Authoring\n\nUse this skill only when the user asks to inspect or improve RNAssistant prompts.\n\n- Call common.prompts_read with includeDefaults=true before proposing a change. Save exactly one setting per confirmed common.prompts_save call using promptKey and its complete value; never send several optional prompt fields. systemPromptRole accepts only developer, system, or user.\n- Agent instructions compose SystemPrompt, AgentToolsPrompt, and AgentSkillsPrompt; Chat uses ChatSystemPrompt and Plan uses PlanSystemPrompt. ContextCompactionPrompt, ChatTitlePrompt and AttachmentAnalysisPrompt are also editable. Compatibility probes remain fixed.\n- Preserve the current structured response and Tool Result v1 contracts from defaults. Each model call contains only an exact name and object arguments; never include id, status, URI, revision, cursor, guard or hash fields. Runtime assigns call IDs after validation, before accepted history is persisted, and owns execution outcomes. Chat remains limited to common.resources_find/read.\n- Do not weaken confirmation, secret handling, batch restrictions or effect-evidence rules. TOOL_RESULT status=ok does not by itself prove an applied effect."),
                Skill(
                    "common.html_workspace_authoring",
                    "HTML workspace authoring",
                    "Build, search, patch, and maintain local HTML reports, dashboards, CSS, scripts, static JSON, and refreshable Office-bound data when visual presentation materially helps.",
                    "# HTML Workspace Authoring\n\n## Inspect and edit\n\n" +
                    "- For an existing workspace, call common.resources_find with scope=html and choose the exact returned workspace, HTML file, or HTML data target. Read its complete structure/source/text as needed. Provider, member URI, revision, cursor, and page size are runtime-owned.\n" +
                    "- Use common.html_workspace_write_file with path/content for a new file or an intentional whole-source rewrite. Use common.html_data_write with name/json for static JSON; file/data kind and preview selection are not model arguments.\n" +
                    "- Use common.html_workspace_apply_patch with path and an ordered patch array for targeted edits. Runtime applies only exact replace/replaceAll/insertBefore/insertAfter operations atomically to current source; use replaceAll only intentionally.\n" +
                    "- `content`, `json`, `find`, and `text` are exact decoded strings. In the outer conversation JSON use `\\n` for an actual line break and `\\\\` for one literal source backslash; for example source `\\n` or regex `\\d` must appear as `\\\\n` or `\\\\d`. Runtime stores decoded text unchanged.\n" +
                    "- Static preflight runs automatically after writes, patches, data changes, and preview projection. Fix returned errors; unresolved-reference warnings may describe runtime-created DOM/data. Do not call a separate inspection or active-file tool.\n" +
                    "- Use common.html_workspace_delete with the exact readable target path or data name. Runtime determines its kind and rejects ambiguity. Workspace mutations are recoverable artifact revisions.\n\n" +
                    "## Runtime model\n\n" +
                    "- The active HTML file is the entry page. RNAssistant injects every workspace CSS file into its head and every classic JavaScript file before its closing body in workspace order. Do not add local link/script references and do not use ES module import/export.\n" +
                    "- Every script runs on every active entry page. Use an IIFE or one stable namespace, avoid global collisions, and guard DOM lookups. Keep the main DOM in the entry HTML; split substantial styling and behavior into focused CSS and JavaScript files.\n" +
                    "- For charts use bundled ECharts: `var chart = echarts.init(node); chart.setOption(option);`. Do not add Chart.js or CDN loaders.\n" +
                    "- Default to a responsive accessible full-page layout with body margin 0; do not force a narrow centered card unless requested.\n\n" +
                    "## Data and safety\n\n" +
                    "- For live Office data, first run the intended approved read-only Office tool, then call common.html_data_bind with only name and optional transform/headers. Runtime reuses the most recent successful accepted read and its exact arguments from the current Agent run; never copy a nested tool name, arguments, URI, cursor, revision, or candidate id into bind. Prefer table for row arrays and raw otherwise.\n" +
                    "- Call common.html_data_refresh with an optional name, or omit it to refresh all bindings under runtime policy. Use common.html_data_freeze before intentionally keeping current JSON as static data.\n" +
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
