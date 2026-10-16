using System;
using System.IO;
using System.Linq;
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
                    valid.Replace("\"formatVersion\": 2", "\"formatVersion\": 1"),
                    valid.Replace("\"formatVersion\": 2", "\"formatVersion\": 3"),
                    valid.Replace("\"formatVersion\": 2", "\"formatVersion\": \"2\""),
                    valid.Replace("\"formatVersion\": 2", "\"formatVersion\": 2.0"),
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

        private static void TabularProjectSourcesRoundTripAndGuards()
        {
            WithTempPaths(paths =>
            {
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.Open(Path.Combine(paths.Root, "workspace"));
                var files = new WorkspaceFileService(paths);
                var store = new TabularProjectStore(files);
                var created = store.Create(workspace, "reports/project.json", "Sources");
                var saved = store.SaveSources(workspace, created, new[] { "input/missing.xlsb", "input/data.json" });
                AssertEqual(0, created.Manifest.Sources.Count, "saving cannot mutate an accepted snapshot");
                AssertTrue(saved.ManifestReference.Revision != created.ManifestReference.Revision, "source selection advances common file authority");
                AssertEqual(0, JObject.Parse(files.ReadExactText(workspace, created.ManifestPath, created.ManifestReference).Text)["sources"].Count(), "historical manifest remains exact");
                AssertTrue(!Directory.Exists(Path.Combine(workspace.RootPath, "input")), "declarations do not read/create/copy source files");
                ExpectTabularFailure(() => store.SaveSources(workspace, created, new[] { "stale.csv" }), "project_changed");
                var restarted = new TabularProjectStore(new WorkspaceFileService(paths));
                var opened = restarted.Open(workspace, created.ManifestPath);
                AssertEqual(saved.Manifest.Sources[0].SourceId, opened.Manifest.Sources[0].SourceId, "stable declaration identity survives restart");
                var reordered = restarted.SaveSources(workspace, opened, new[] { "input/data.json", "input/missing.xlsb" });
                AssertEqual(saved.Manifest.Sources[0].SourceId, reordered.Manifest.Sources[1].SourceId, "reordering preserves identity");
                var readOnly = workspaces.Open(workspace.RootPath, false, false);
                ExpectTabularFailure(() => restarted.SaveSources(readOnly, reordered, Array.Empty<string>()), "read_only_mount");
                var path = Path.Combine(workspace.RootPath, created.ManifestPath);
                var before = File.ReadAllText(path);
                foreach (var invalid in new[] { new[] { "../escape.csv" }, new[] { "/absolute.csv" }, new[] { "C:/source.csv" },
                    new[] { "input\\source.csv" }, new[] { "a.csv", "A.csv" }, new[] { "a//b.csv" }, new[] { "./b.csv" },
                    new[] { "bad\n.csv" }, Enumerable.Range(0, 501).Select(i => i + ".csv").ToArray(),
                    Enumerable.Range(0, 500).Select(i => i + new string('x', 600) + ".csv").ToArray() })
                {
                    ExpectTabularFailure(() => restarted.SaveSources(workspace, reordered, invalid), "project_invalid");
                    AssertEqual(before, File.ReadAllText(path), "invalid selection has no manifest effect");
                }
                var emptied = restarted.SaveSources(workspace, reordered, Array.Empty<string>());
                AssertEqual(0, restarted.Open(workspace, emptied.ManifestPath).Manifest.Sources.Count, "explicit empty selection persists");
            });
        }

        private static void TabularProjectSourcesRejectMalformedAndUnknown()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "workspace"));
                var files = new WorkspaceFileService(paths);
                var store = new TabularProjectStore(files);
                var project = store.Create(workspace, "project.json", "Sources");
                var saved = store.SaveSources(workspace, project, new[] { "a.csv", "b.xlsb" });
                var path = Path.Combine(workspace.RootPath, "project.json");
                var valid = File.ReadAllText(path);
                foreach (var invalid in new[] {
                    valid.Replace("\"path\": \"a.csv\"", "\"path\": null"),
                    valid.Replace("\"path\": \"a.csv\"", "\"path\": \"a.csv\", \"prepared\": true"),
                    valid.Replace("\"path\": \"a.csv\"", "\"path\": \"a.csv\", \"path\": \"b.csv\""),
                    valid.Replace(saved.Manifest.Sources[1].SourceId, saved.Manifest.Sources[0].SourceId),
                    valid.Replace("src_", "bad_"), valid.Replace("b.xlsb", "A.csv") })
                {
                    File.WriteAllText(path, invalid);
                    ExpectTabularFailure(() => store.Open(workspace, "project.json"), "project_invalid");
                    AssertEqual(invalid, File.ReadAllText(path), "invalid declarations are not silently dropped");
                }
                File.WriteAllText(path, valid);
                saved = store.Open(workspace, "project.json");
                files.FaultPoint = stage => { if (stage == "after-dispatch") throw new IOException("lost save receipt"); };
                ExpectTabularFailure(() => store.SaveSources(workspace, saved, new[] { "new.json" }), "effect_unknown");
                var dispatched = File.ReadAllText(path);
                var restartedFiles = new WorkspaceFileService(paths);
                var restarted = new TabularProjectStore(restartedFiles);
                ExpectTabularFailure(() => restarted.Open(workspace, "project.json"), "unresolved_previous_effect");
                ExpectTabularFailure(() => restarted.SaveSources(workspace, saved, new[] { "retry.csv" }), "unresolved_previous_effect");
                AssertEqual(dispatched, File.ReadAllText(path), "unknown save is not replayed");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, restartedFiles.ReconcileUncertain(workspace, "project.json").Outcome, "inspection does not prove causality");
                AssertEqual("new.json", restarted.Open(workspace, "project.json").Manifest.Sources.Single().Path, "explicit reopen observes recovered declarations");
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
