using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class WorkspaceSkillProvider
    {
        public const string FilesId = "workspace.files";
        public const string WebId = "workspace.web_verify_repair";

        public static IReadOnlyList<SkillDefinition> GetSkills()
        {
            return new[] {
                Skill(FilesId, "Workspace files",
                    "Create or change real workspace files, inspect exact source and finish with current read evidence.",
@"# Workspace files
Use for tasks that create, inspect or edit real workspace files.

1. Identify the required files and completion checks. Use common.resources_find for an unfamiliar directory; known relative paths can be read directly with common.resources_read (type=file).
2. Read existing source before editing. Use files.create only for a new path. A successful create means the file exists: continue to the next file or read it; do not create it again. An already-existing error also requires a read before any replacement.
3. Use files.patch for one unique exact anchor, files.replace for an intentional whole-file change. Read again after a stale-source or missing-source response. Review the latest accepted tool result before choosing the next step. A failed call is not a change; an unknown effect requires reconciliation and must not be retried automatically.
4. Verify the changed behavior with the available task checks. A write read-back confirms bytes, not application behavior. A skill read supplies instructions, not file source or mutation permission.
5. After the last edit, read the final requested files with common.resources_read. Later writes invalidate earlier source reads. If RUNTIME_CONTEXT.readAcceptance is present, finish its remaining reads and expected paths; browser checks and skill reads do not satisfy it. Report only verified outcomes and explicit remaining limitations.

Keep all paths relative to the selected workspace. Respect the available tool catalog and confirmation boundaries. Do not write runtime IDs, revisions or absolute internal paths into tool arguments."),
                Skill(WebId, "Local web verify and repair",
                    "Build or repair a local multi-file HTML/CSS/JS app, run browser checks, fix failures and read the final files.",
@"# Local web verify and repair
Use for a local HTML/CSS/JS application when web.verify and file mutation tools are available. Read workspace.files as well for file prerequisites and final source reads.

1. Keep HTML, CSS and JS in real workspace files with local relative assets. When the task requires offline operation, use no CDN, external requests or dependencies.
2. For an existing broken app, run web.verify before editing to obtain the failing checks. For a new app, create its files first, then run web.verify with its entryPath.
3. Inspect the latest verification result: asset/runtime errors and failed functional checks identify behavior to repair. Read the relevant file, fix the cause, then verify again. Do not repeat an unchanged failing verification or weaken the accepted checks. A historical passing snapshot does not verify later edits.
4. Once the current snapshot passes the accepted checks, read every final file requested by the task. A later repair requires a fresh verification and fresh reads of changed files. Check RUNTIME_CONTEXT.readAcceptance when supplied.
5. Finish only when both behavioral checks and current source-read requirements are satisfied. Report an unavailable browser or an unmet requirement explicitly; do not claim a visual review or broader testing than web.verify actually performed.")
            };
        }

        private static SkillDefinition Skill(string id, string name, string description, string body)
        { return new SkillDefinition { Id = id, Host = "Workspace", Name = name, Description = description,
            BodyMarkdown = body, BuiltIn = true, Enabled = true, Version = "1.0.0" }; }
    }
}
