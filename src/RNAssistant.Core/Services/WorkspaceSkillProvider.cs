using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    public static class WorkspaceSkillProvider
    {
        public const string FilesId = "skills.workspace.files";
        public const string WebId = "skills.workspace.web.verify.repair";
        public const string PluginId = "skills.workspace.plugin.authoring";

        public static IReadOnlyList<SkillDefinition> GetSkills()
        {
            return new[] {
                ChatVisualizationSkillProvider.Create(),
                Skill(FilesId, "Workspace files",
                    "Inspect, create or edit workspace files using current source and verified after-state; honor explicit final-read requirements.",
@"# Workspace files
Use for tasks that create, inspect or edit real workspace files.

1. Identify the required files and completion checks. Use core.find for an unfamiliar directory (recursive/glob selectors are available), and core.search for literal or regex content across scope=workspace; choose the scope and depth yourself. Search snippets are not source-read authorization; known relative paths can be read directly with core.read (type=file).
2. Before editing, reuse current source already included from a read or verified after-state; read missing/stale source. core.write creates a missing path or replaces a completely observed existing file; runtime guards existence and revision. Use core.patch for focused edits.
3. Follow the current core.patch schema for structured targets/edits, old/new and ordered full-line anchors_before/anchors_after and per-target outcomes. Copy fragments and context from observed source, disambiguate with more context and reconcile unknown effects before another write. Use core.search then core.replace for an explicitly counted literal/regex replacement. The file owner streams large source and result; core.write requires complete current source and a deliberate whole-file change.
4. Verify the changed behavior with the available task checks. A write read-back confirms bytes, not application behavior. A skill read supplies instructions, not file source or mutation permission.
5. Reuse current verified after-state for final source assessment. If RUNTIME_CONTEXT.readAcceptance is present, complete its remaining exact final-file reads and expected paths; later writes invalidate those reads, and browser checks or skill reads do not satisfy them. Report only verified outcomes and explicit remaining limitations.

Keep all paths relative to the selected workspace. Respect the available tool catalog and confirmation boundaries. Do not write runtime IDs, revisions or absolute internal paths into tool arguments."),
                Skill(WebId, "Local web verify and repair",
                    "Build or repair a local multi-file HTML/CSS/JS app, run browser checks, fix failures and meet explicit final-read acceptance when present.",
@"# Local web verify and repair
Use for a local HTML/CSS/JS application when web.verify and file mutation tools are available. Load skills.workspace.files with core.skills.load for source inspection and guarded file edits.

1. Keep HTML, CSS and JS in real workspace files with local relative assets. When the task requires offline operation, use no CDN, external requests or dependencies.
2. For an existing broken app, run web.verify before editing when reproducing the failure will guide the fix; reuse relevant current failure evidence when available. A clear source defect can be corrected directly. For a new app, create its files first. Verify the resulting app with web.verify and its entryPath.
3. Inspect the latest verification result: asset/runtime errors and failed functional checks identify behavior to repair. Read the relevant file, fix the cause, then verify again. Do not repeat an unchanged failing verification or weaken the accepted checks. A historical passing snapshot does not verify later edits.
4. Once the current snapshot passes the accepted checks, reuse verified current after-state. If RUNTIME_CONTEXT.readAcceptance is supplied, read its required final files; a later repair requires fresh verification and fresh reads of changed files.
5. Finish only when both behavioral checks and current source-read requirements are satisfied. Report an unavailable browser or an unmet requirement explicitly; do not claim a visual review or broader testing than web.verify actually performed."),
                Skill(PluginId, "Plugin authoring and SVG icon",
                    "Build or edit a local plugin source package, including changes requested through visual annotations, and draw its SVG icon.",
@"# Plugin authoring and SVG icon
Use only in the source workspace of a plugin with plugin.json. Load skills.workspace.files for exact source reads and mutations. Read README.md, plugin.json and the relevant current files before editing. Preserve existing behavior and declarations while changing the requested feature.

For an icon brief, draw the icon yourself as a complete static SVG document in icon.svg. Give it a viewBox fitting the artwork tightly with a small safety margin, without a large empty canvas; the host sizes it to 24 pixels while preserving the aspect ratio. Colors, fills, strokes, gradients, transforms, SVG styles and local defs/references are supported and preserved. Use currentColor only for a theme-colored design. Keep it compact and legible at 20 px. Do not include scripts, event handlers, animation, foreignObject, external resources or raster images. No image-generation model is needed. Set plugin.json icon to icon.svg and include icon.svg in files; update an existing icon through an exact source read and file patch/replace. The default puzzle icon remains until a validated SVG is published.

After source edits, call plugins.check when available. A successful check proves package structure and safe icon syntax, not application behavior. Open the checked preview and verify the requested UI or command behavior. Apply only after the intended publication is confirmed; then read the active state. If the icon validator rejects the file, repair the SVG source and check again. Never replace an existing plugin's source merely to change its icon." + VisualAnnotationSourceWorkflow.Common + @"

For a plugin annotation, inspect plugin.json and only its declared HTML/CSS/JS source files. The accepted annotation is pinned to a retained release, while core.patch targets the current source workspace: re-read the current file and refuse an unexplained source/preview mismatch. Patch a unique current source anchor with core.patch; use plugins.check to retain a candidate and inspect that exact candidate in the right preview. A check alone validates the package, not the requested appearance or interaction. Only call plugins.apply when the user's publication intent and the normal confirmation boundary permit it.")
            };
        }

        private static SkillDefinition Skill(string id, string name, string description, string body)
        {
            var tools = new List<string> { "core.find", "core.read", "core.search", "core.write", "core.patch", "core.replace" };
            if (id == WebId) tools.Add("web.verify");
            var optional = new List<SkillToolDependency>();
            if (id == PluginId)
            {
                optional.Add(new SkillToolDependency { Id = "plugins.check",
                    Fallback = "Inspect the plugin source and report that package validation and a checked preview are unavailable; do not claim a validated candidate." });
                optional.Add(new SkillToolDependency { Id = "plugins.apply",
                    Fallback = "Keep the source changes and report that activation is unavailable; do not claim publication." });
            }
            return new SkillDefinition { Id = id, Host = "Workspace", Name = name, Description = description,
                BodyMarkdown = body,
                BuiltIn = true, Enabled = true, Version = "1.0.0",
                Package = new SkillPackageManifest { PackageId = id,
                    RequiredTools = tools.ConvertAll(tool => new SkillToolDependency {
                        Id = tool, ContractVersion = tool == ResourceMutationToolCatalog.Patch ? "3" : "1"
                    }),
                    OptionalTools = optional } };
        }
    }
}
