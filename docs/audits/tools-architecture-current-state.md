# Tools architecture audit — 2026-10-25

Baseline: `fb576221`. Owner: Core resource contracts / Application workspace / Office Gateway.
Static audit; Windows/Office/WebView2 and real-model comparative evaluation are not qualified.
The user accepted workspace + Office scope and explicitly retained v8 finish admission unchanged.
Implementation and checks: [plan](../proposals/tools-architecture-plan.md).

## Execution and ownership

Office ConversationRunService and Application WorkspaceConversationService use AgentKernel,
ModelProtocol v8, exact ToolPackSnapshot and ToolHandlerRegistry/ToolRuntime. Tool availability,
selected skills, schema admission and execution authority are distinct. Runtime controls
prepared/dispatch/read-back; source owners retain journal and authority. Accepted facts go to
append-only events, bodies to CAS, model projections to ModelContextCompiler. LlmClient captures
serialized request bytes before transport. ConversationFinishEvidenceAdmission resolves terminal
call IDs in the same session/run/turn; it does not prove semantic correctness of an assessment.

## Findings

- Confirmed defect: ToolDisplayCatalog used five retired VBA IDs. The active catalog uses
  vba_apply_patch/write_module/rename_module/delete_module/restore_backup.
- Confirmed limitation: workspace discovery was one directory / 200 results / 5000 scanned entries,
  without recursive glob filtering or workspace content search.
- Office already had project VBA discovery/search, literal target search and indexed text outlines.
  New search must preserve indexed long-line and part navigation, not claim those features absent.
- Workspace patch already checks exact revision and uniqueness (including streamed large files).
  VBA supports exact/context/position guards and deliberately restricted VBE format normalization.
  HTML has explicit atomic per-file patch operations. No generic patch rewrite is warranted.
- Office SearchWithin materializes live text locally; bounded model output is not bounded COM
  capture. This implementation does not claim constant-memory Office source acquisition.
- ModelResultProjection has closed resource/file envelopes. Remaining generic Office projections
  are tracked in MIGRATION_MAP; old-looking strings alone do not establish dead code.
- Saved request snapshots, context receipts, EvidenceStateReducer and compaction already exist.
  Do not introduce another evidence store, context builder, trace store or Inspector.
- No reproduced data loss, duplicate user messages, lost tools or payload mismatch was established
  by this audit. Historical Resource Context findings are not automatically current defects.

## Built-in inventory

Each row points to the source-owned schema/descriptor and policy. Executable handlers are in the
same thematic owner (Core/Tools, Office/Tools or Application/Workspace). ToolRuntime is the common
permission/error/effect boundary; ModelResultProjection and Office ModelToolResultProjection own
model envelopes; retained activity/result presentation owns UI. No row grants authority through
its name. Outputs remain domain-specific typed results, not a newly invented universal envelope.
Dynamic installed VBA/JS packages have user-owned IDs and cannot be exhaustively listed from Git.
Manual chat/HTML commands, file-owner journal operations and skill IDs are not public tool aliases.

| Public ID | Schema/catalog source | Decision |
|---|---|---|
| `common.capabilities_read` | [CapabilityToolCatalog.cs](../../src/RNAssistant.Core/Tools/CapabilityToolCatalog.cs) | keep |
| `common.capabilities_search` | [CapabilityToolCatalog.cs](../../src/RNAssistant.Core/Tools/CapabilityToolCatalog.cs) | keep |
| `common.file_copy` | [AuthoredFileToolCatalog.cs](../../src/RNAssistant.Office/Tools/AuthoredFileToolCatalog.cs) | keep until existing workspace-first cutover |
| `common.file_create` | [AuthoredFileToolCatalog.cs](../../src/RNAssistant.Office/Tools/AuthoredFileToolCatalog.cs) | keep until existing workspace-first cutover |
| `common.file_patch` | [AuthoredFileToolCatalog.cs](../../src/RNAssistant.Office/Tools/AuthoredFileToolCatalog.cs) | keep until existing workspace-first cutover |
| `common.html_assets_import` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep |
| `common.html_assets_list` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep |
| `common.html_assets_publish` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep |
| `common.html_data_bind` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep |
| `common.html_data_freeze` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep |
| `common.html_data_refresh` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep |
| `common.html_data_write` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep |
| `common.html_workspace_apply_patch` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep until existing workspace-first cutover |
| `common.html_workspace_delete` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep until existing workspace-first cutover |
| `common.html_workspace_write_file` | [HtmlWorkspaceToolCatalog.cs](../../src/RNAssistant.Office/Tools/HtmlWorkspaceToolCatalog.cs) | keep until existing workspace-first cutover |
| `common.js_run` | [JsToolHandler.cs](../../src/RNAssistant.Office/Tools/JsToolHandler.cs) | keep |
| `common.markdown_restore` | [MarkdownDocumentToolCatalog.cs](../../src/RNAssistant.Office/Tools/MarkdownDocumentToolCatalog.cs) | keep |
| `common.markdown_save` | [MarkdownDocumentToolCatalog.cs](../../src/RNAssistant.Office/Tools/MarkdownDocumentToolCatalog.cs) | keep |
| `common.office_run_macro` | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) | keep |
| `common.plan_doc_delete` | [PlanDocumentToolCatalog.cs](../../src/RNAssistant.Core/Tools/PlanDocumentToolCatalog.cs) | keep |
| `common.plan_doc_restore` | [PlanDocumentToolCatalog.cs](../../src/RNAssistant.Core/Tools/PlanDocumentToolCatalog.cs) | keep |
| `common.plan_doc_save` | [PlanDocumentToolCatalog.cs](../../src/RNAssistant.Core/Tools/PlanDocumentToolCatalog.cs) | keep |
| `common.prompts_save` | [PromptToolCatalog.cs](../../src/RNAssistant.Office/Tools/PromptToolCatalog.cs) | keep |
| `common.questions_ask` | [UserQuestionToolCatalog.cs](../../src/RNAssistant.Core/Tools/UserQuestionToolCatalog.cs) | keep |
| `common.resources_derive` | [ResourceDefinitionToolHandler.cs](../../src/RNAssistant.Office/Tools/ResourceDefinitionToolHandler.cs) | keep |
| `common.resources_find` | [ResourceToolCatalog.cs](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) | extend workspace traversal |
| `common.resources_mapping_publish` | [ResourceDefinitionToolHandler.cs](../../src/RNAssistant.Office/Tools/ResourceDefinitionToolHandler.cs) | keep |
| `common.resources_outline` | [ResourceToolCatalog.cs](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) | keep |
| `common.resources_read` | [ResourceToolCatalog.cs](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) | keep |
| `common.resources_schema_draft` | [ResourceDefinitionToolHandler.cs](../../src/RNAssistant.Office/Tools/ResourceDefinitionToolHandler.cs) | keep |
| `common.resources_schema_publish` | [ResourceDefinitionToolHandler.cs](../../src/RNAssistant.Office/Tools/ResourceDefinitionToolHandler.cs) | keep |
| `common.resources_search` | [ResourceToolCatalog.cs](../../src/RNAssistant.Office/Tools/ResourceToolCatalog.cs) | extend shared text search |
| `common.skills_delete` | [SkillAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/SkillAuthoringCatalog.cs) | keep |
| `common.skills_load` | [SkillLoadToolHandler.cs](../../src/RNAssistant.Core/Tools/SkillLoadToolHandler.cs) | keep |
| `common.skills_reference_delete` | [SkillAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/SkillAuthoringCatalog.cs) | keep |
| `common.skills_reference_upsert` | [SkillAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/SkillAuthoringCatalog.cs) | keep |
| `common.skills_upsert` | [SkillAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/SkillAuthoringCatalog.cs) | keep |
| `common.task_list_set` | [TaskListToolCatalog.cs](../../src/RNAssistant.Core/Tools/TaskListToolCatalog.cs) | keep |
| `common.tools_delete` | [ToolAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/ToolAuthoringCatalog.cs) | keep |
| `common.tools_upsert` | [ToolAuthoringCatalog.cs](../../src/RNAssistant.Office/Tools/ToolAuthoringCatalog.cs) | keep |
| `common.vba_apply_patch` | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) | keep; correct display IDs |
| `common.vba_delete_module` | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) | keep; correct display IDs |
| `common.vba_rename_module` | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) | keep; correct display IDs |
| `common.vba_restore_backup` | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) | keep; correct display IDs |
| `common.vba_write_module` | [VbaToolCatalog.cs](../../src/RNAssistant.Office/Tools/VbaToolCatalog.cs) | keep; correct display IDs |
| `excel.add_sheet` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.add_table` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.clear_range` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.create_chat_chart` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.delete_chart` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.delete_rows_columns` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.filter_range` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.find_cells` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.format_range` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.inspect` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.rename_sheet` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.replace_cells` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.sort_range` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.summarize_columns` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.upsert_chart` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `excel.write_range` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `files.copy` | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | keep |
| `files.create` | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | keep |
| `files.delete` | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | keep |
| `files.move` | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | keep |
| `files.patch` | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | keep |
| `files.replace` | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | keep |
| `files.restore` | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | keep |
| `outlook.create_draft` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `outlook.index_archive` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `outlook.search_mail` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `outlook.update_mail` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `plugins.apply` | [PluginTools.cs](../../src/RNAssistant.Application/PluginTools.cs) | keep |
| `plugins.check` | [PluginTools.cs](../../src/RNAssistant.Application/PluginTools.cs) | keep |
| `powerpoint.add_object` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `powerpoint.add_slide` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `powerpoint.duplicate_slide` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `powerpoint.list_objects` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `powerpoint.move_slide` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `powerpoint.replace_text` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `powerpoint.search_text` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `powerpoint.set_text` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `web.verify` | [WorkspaceConversationService.cs](../../src/RNAssistant.Application/Workspace/WorkspaceConversationService.cs) | keep |
| `word.add_comment` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `word.add_table` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `word.find_text` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `word.format_text` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `word.insert_page_break` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `word.inspect` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `word.replace_text` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |
| `word.write_text` | [OfficeToolCatalog.cs](../../src/RNAssistant.Office/Tools/OfficeToolCatalog.cs) | keep |

## Comparative evidence

[Codex prompting guide](https://developers.openai.com/cookbook/examples/gpt-5/codex_prompting_guide)
recommends shell and apply_patch; [public registry](https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/registry.rs)
has typed execution/lifecycle metadata. [Claude Code architecture](https://code.claude.com/docs/en/how-claude-code-works)
assigns exploration choices to the model and distinguishes basic search from optional code intelligence.
These are public mechanisms, not measured performance superiority or proof of a complete call graph.
Transfer language-independent exploration, not shell authority or filesystem assumptions, to VBE/Office.

## Rejected changes

No model-visible runtime URI/revision/cursor; no ev_* store or v8 amendment; no mandatory reflection,
call graph, LSP/index, shell executor, giant universal tool, mutation dual-run or automatic rollback.
No arbitrary deletion of legacy-looking branches without producer/consumer evidence.
