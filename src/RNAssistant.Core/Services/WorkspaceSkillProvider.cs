using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class WorkspaceSkillProvider
    {
        public const string FilesId = "workspace.files";
        public const string WebId = "workspace.web_verify_repair";
        public const string PluginId = "workspace.plugin_authoring";

        public static IReadOnlyList<SkillDefinition> GetSkills()
        {
            return new[] {
                Skill(FilesId, "Workspace files",
                    "Create or change real workspace files, inspect exact source and finish with current read evidence.",
@"# Workspace files
Use for tasks that create, inspect or edit real workspace files.

1. Identify the required files and completion checks. Use common.resources_find for an unfamiliar directory; known relative paths can be read directly with common.resources_read (type=file).
2. Before editing, reuse current source already included from a read or verified after-state; read missing/stale source. Use files.create only for a new path. Success means it exists; do not create it again. After an already-existing error, read its current content.
3. Use files.patch for one unique exact anchor inside an observed excerpt of a UTF-8 file. The model context bounds each supplied fragment; the file owner streams large source and result through an exact-revision write. files.replace requires complete current source and an intended whole-file change. A failed call is not a change; reconcile an unknown effect before continuing and never retry it automatically.
4. Verify the changed behavior with the available task checks. A write read-back confirms bytes, not application behavior. A skill read supplies instructions, not file source or mutation permission.
5. Reuse current verified after-state for final source assessment. If RUNTIME_CONTEXT.readAcceptance is present, complete its remaining exact final-file reads and expected paths; later writes invalidate those reads, and browser checks or skill reads do not satisfy them. Report only verified outcomes and explicit remaining limitations.

Keep all paths relative to the selected workspace. Respect the available tool catalog and confirmation boundaries. Do not write runtime IDs, revisions or absolute internal paths into tool arguments."),
                Skill(WebId, "Local web verify and repair",
                    "Build or repair a local multi-file HTML/CSS/JS app, run browser checks, fix failures and meet explicit final-read acceptance when present.",
@"# Local web verify and repair
Use for a local HTML/CSS/JS application when web.verify and file mutation tools are available. Load workspace.files with common.skills_load for file prerequisites and final source reads.

1. Keep HTML, CSS and JS in real workspace files with local relative assets. When the task requires offline operation, use no CDN, external requests or dependencies.
2. For an existing broken app, run web.verify before editing to obtain the failing checks. For a new app, create its files first, then run web.verify with its entryPath.
3. Inspect the latest verification result: asset/runtime errors and failed functional checks identify behavior to repair. Read the relevant file, fix the cause, then verify again. Do not repeat an unchanged failing verification or weaken the accepted checks. A historical passing snapshot does not verify later edits.
4. Once the current snapshot passes the accepted checks, reuse verified current after-state. If RUNTIME_CONTEXT.readAcceptance is supplied, read its required final files; a later repair requires fresh verification and fresh reads of changed files.
5. Finish only when both behavioral checks and current source-read requirements are satisfied. Report an unavailable browser or an unmet requirement explicitly; do not claim a visual review or broader testing than web.verify actually performed."),
                Skill(PluginId, "Plugin authoring and SVG icon",
                    "Build a local plugin source package and draw its icon from a text brief without an image-generation model.",
@"# Plugin authoring and SVG icon
Use only in the source workspace of a plugin with plugin.json. Load workspace.files for exact source reads and mutations. Read README.md, plugin.json and the relevant current files before editing. Preserve existing behavior and declarations while changing the requested feature.

For an icon brief, draw the icon yourself as a complete static SVG document in icon.svg. Give it a viewBox fitting the artwork tightly with a small safety margin, without a large empty canvas; the host sizes it to 24 pixels while preserving the aspect ratio. Colors, fills, strokes, gradients, transforms, SVG styles and local defs/references are supported and preserved. Use currentColor only for a theme-colored design. Keep it compact and legible at 20 px. Do not include scripts, event handlers, animation, foreignObject, external resources or raster images. No image-generation model is needed. Set plugin.json icon to icon.svg and include icon.svg in files; update an existing icon through an exact source read and file patch/replace. The default puzzle icon remains until a validated SVG is published.

After source edits, call plugins.check when available. A successful check proves package structure and safe icon syntax, not application behavior. Open the checked preview and verify the requested UI or command behavior. Apply only after the intended publication is confirmed; then read the active state. If the icon validator rejects the file, repair the SVG source and check again. Never replace an existing plugin's source merely to change its icon.")
            };
        }

        private static SkillDefinition Skill(string id, string name, string description, string body)
        {
            var tools = new List<string> { "common.resources_find", "common.resources_read", "files.create", "files.patch", "files.replace" };
            if (id == WebId) tools.Add("web.verify");
            return new SkillDefinition { Id = id, Host = "Workspace", Name = name, Description = description,
                BodyMarkdown = body,
                BuiltIn = true, Enabled = true, Version = "1.0.0",
                Package = new SkillPackageManifest { PackageId = id,
                    RequiredTools = tools.ConvertAll(tool => new SkillToolDependency { Id = tool }) } };
        }
    }
}
