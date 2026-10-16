using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void TabularProjectsCreateReopenAndRelocate()
        {
            WithTempPaths(paths =>
            {
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.Open(Path.Combine(paths.Root, "workspace"));
                var projects = new TabularProjectStore(new WorkspaceFileService(paths));
                const string manifestPath = "reports/project.json";
                var created = projects.Create(workspace, manifestPath, "Отчёты");
                var original = File.ReadAllText(Path.Combine(workspace.RootPath, manifestPath));
                AssertEqual("Отчёты", created.Manifest.Name, "name survives UTF-8 read-back");
                AssertTrue(created.ManifestReference.IsExact, "create returns the file owner's published revision");
                AssertTrue(!original.Contains(workspace.WorkspaceId) && !original.Contains(workspace.RootPath),
                    "portable metadata contains neither workspace identity nor absolute root");
                var second = projects.Create(workspace, "another/project.json", "Another project");
                AssertTrue(second.Manifest.ProjectId != created.Manifest.ProjectId,
                    "one workspace can contain independent projects");

                var restarted = new TabularProjectStore(new WorkspaceFileService(paths));
                var readOnly = workspaces.Open(workspace.RootPath, false, false);
                var reopened = restarted.Open(readOnly, manifestPath);
                AssertEqual(created.Manifest.ProjectId, reopened.Manifest.ProjectId, "domain identity after restart");
                AssertEqual(created.ManifestReference.Revision, reopened.ManifestReference.Revision, "same exact revision");
                AssertTrue(reopened.ReadOnly && restarted.RequireUnchanged(readOnly, created).ReadOnly,
                    "current workspace open owns read-only permission");
                ExpectTabularFailure(() => restarted.Create(readOnly, "denied/project.json", "Denied"), "read_only_mount");
                AssertTrue(!Directory.Exists(Path.Combine(workspace.RootPath, "denied")), "refusal creates no folder");
                ExpectTabularFailure(() => restarted.Create(workspace, manifestPath, "Overwrite"), "target_exists");
                AssertEqual(original, File.ReadAllText(Path.Combine(workspace.RootPath, manifestPath)), "duplicate create preserves bytes");

                var movedRoot = Path.Combine(paths.Root, "moved");
                Directory.Move(workspace.RootPath, movedRoot);
                var moved = workspaces.Open(movedRoot, false);
                var afterMove = restarted.RequireUnchanged(moved, created);
                AssertEqual(created.ManifestReference.Uri, afterMove.ManifestReference.Uri, "workspace relocation preserves file identity");
                AssertEqual(created.ContentSha256, afterMove.ContentSha256, "relocation preserves manifest bytes");
            });
        }

        private static void TabularProjectsRejectInvalidManifests()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "workspace"));
                var projects = new TabularProjectStore(new WorkspaceFileService(paths));
                ExpectTabularFailure(() => projects.Create(workspace, "bad/project.json", "\n"), "project_invalid");
                AssertTrue(!Directory.Exists(Path.Combine(workspace.RootPath, "bad")), "invalid metadata has no file effect");
                ExpectTabularFailure(() => projects.Create(workspace, "../project.json", "Escape"), "path_outside_mount");
                AssertTrue(!File.Exists(Path.Combine(paths.Root, "project.json")), "file owner enforces workspace containment");
                ExpectTabularFailure(() => projects.Create(workspace, "Project.json", "Wrong filename"), "project_invalid");

                projects.Create(workspace, "project.json", "Valid");
                var path = Path.Combine(workspace.RootPath, "project.json");
                var valid = File.ReadAllText(path);
                var invalid = new[] {
                    "null", "[]", "{", valid + " {}",
                    valid.Replace("\"formatVersion\": 1", "\"formatVersion\": 2"),
                    valid.Replace("\"formatVersion\": 1", "\"formatVersion\": \"1\""),
                    valid.Replace("\"formatVersion\": 1", "\"formatVersion\": 1.0"),
                    valid.Replace("\"kind\":", "\"Kind\":"),
                    valid.Replace("data-templates", "other-project"),
                    valid.Replace("\"name\": \"Valid\"", "\"name\": null"),
                    valid.Replace("\"name\": \"Valid\"", "\"name\": \"Valid\", \"name\": \"Duplicate\""),
                    valid.Replace("\"name\": \"Valid\"", "\"name\": \"Valid\", \"workspaceId\": \"foreign\""),
                    valid.Replace("\"name\": \"Valid\"", "\"name\": {}"),
                    valid.Replace("Valid", " "), valid.Replace("Valid", new string('x', 201)),
                    valid.Replace("tp_", "ws_"), valid + new string(' ', TabularProjectStore.MaximumManifestBytes)
                };
                foreach (var malformed in invalid)
                {
                    File.WriteAllText(path, malformed);
                    ExpectTabularFailure(() => projects.Open(workspace, "project.json"), "project_invalid");
                    AssertEqual(malformed, File.ReadAllText(path), "invalid manifest is never repaired or reset implicitly");
                }
            });
        }

        private static void TabularProjectsDetectDriftAndForeignWorkspace()
        {
            WithTempPaths(paths =>
            {
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.Open(Path.Combine(paths.Root, "workspace"));
                var files = new WorkspaceFileService(paths);
                var projects = new TabularProjectStore(files);
                var opened = projects.Create(workspace, "project.json", "Before");
                var originalPath = Path.Combine(workspace.RootPath, "project.json");
                var original = File.ReadAllText(originalPath);
                var foreign = workspaces.Open(Path.Combine(paths.Root, "copy"));
                File.Copy(originalPath, Path.Combine(foreign.RootPath, "project.json"));
                var imported = projects.Open(foreign, "project.json");
                AssertEqual(opened.Manifest.ProjectId, imported.Manifest.ProjectId, "portable project ID can be copied");
                AssertTrue(imported.WorkspaceId != opened.WorkspaceId && imported.ManifestReference.Uri != opened.ManifestReference.Uri,
                    "a copy has its own workspace association and file authority");
                ExpectTabularFailure(() => projects.RequireUnchanged(foreign, opened), "project_workspace_mismatch");

                var changed = JObject.Parse(original);
                changed["name"] = "After";
                File.WriteAllText(originalPath, changed.ToString());
                ExpectTabularFailure(() => projects.RequireUnchanged(workspace, opened), "project_changed");
                AssertEqual("Before", opened.Manifest.Name, "existing snapshot stays immutable");
                var current = projects.Open(workspace, "project.json");
                AssertEqual("After", current.Manifest.Name, "explicit open observes new content");
                AssertTrue(current.ManifestReference.Revision != opened.ManifestReference.Revision, "external drift advances common revision");
                AssertEqual(original, files.ReadExactText(workspace, "project.json", opened.ManifestReference).Text,
                    "historical bytes remain in the existing CAS");
                File.WriteAllText(originalPath, "{}");
                ExpectTabularFailure(() => projects.RequireUnchanged(workspace, current), "project_changed");
                ExpectTabularFailure(() => projects.Open(workspace, "project.json"), "project_invalid");
            });
        }

        private static void TabularProjectsPreserveUnknownEffects()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "workspace"));
                var files = new WorkspaceFileService(paths);
                files.FaultPoint = stage => { if (stage == "after-dispatch") throw new IOException("interrupted manifest write"); };
                var projects = new TabularProjectStore(files);
                ExpectTabularFailure(() => projects.Create(workspace, "project.json", "Interrupted"), "effect_unknown");
                var path = Path.Combine(workspace.RootPath, "project.json");
                var dispatched = File.ReadAllText(path);
                var restartedFiles = new WorkspaceFileService(paths);
                var restarted = new TabularProjectStore(restartedFiles);
                ExpectTabularFailure(() => restarted.Open(workspace, "project.json"), "unresolved_previous_effect");
                ExpectTabularFailure(() => restarted.Create(workspace, "project.json", "Retry"), "target_exists");
                AssertEqual(dispatched, File.ReadAllText(path), "no automatic replay or overwrite");
                var recovery = restartedFiles.ReconcileUncertain(workspace, "project.json");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, recovery.Outcome, "existing owner preserves uncertain causality");
                var recovered = restarted.Open(workspace, "project.json");
                AssertEqual("Interrupted", recovered.Manifest.Name, "explicit inspection opens dispatched bytes");
                AssertEqual(recovery.Current.Reference.Revision, recovered.ManifestReference.Revision, "one resource head after recovery");
            });
        }

        private static void ExpectTabularFailure(Action action, string code)
        {
            try { action(); }
            catch (TabularProjectException ex) { AssertEqual(code, ex.Code, "domain refusal"); return; }
            catch (WorkspaceFileException ex) { AssertEqual(code, ex.Code, "shared file refusal"); return; }
            throw new InvalidOperationException("Expected refusal: " + code);
        }
    }
}
