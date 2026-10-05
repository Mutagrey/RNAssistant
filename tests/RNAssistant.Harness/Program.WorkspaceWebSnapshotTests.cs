using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceWebSnapshotsSurviveRestartAndMoves()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                var html = files.CreateText(workspace, "index.html", "\uFEFF<script src='app.js'></script>\r\n");
                var script = files.CreateText(workspace, "app.js", "const oldSnapshot = true;\r\n");
                var reads = new[] { "index.html", "app.js" }.Select(path => provider.Read(path)).ToArray();
                var store = new WorkspaceWebSnapshotStore(paths, files);
                var snapshot = store.PublishSnapshot(workspace, "index.html", reads.Select(read =>
                    new WebSnapshotFile(read.Result.Resource.Metadata["relativePath"], read.Result.Resource.Reference,
                        read.Result.CompleteViewPayload)));
                var id = store.BeginVerification(workspace, "index.html", false,
                    new WebVerificationOrigin { SessionId = "session", RunId = "run", ToolCallId = "call" });
                store.AttachSnapshot(workspace, id, snapshot);

                // No terminal result was published before the simulated restart.
                store = new WorkspaceWebSnapshotStore(paths, new WorkspaceFileService(paths));
                var pending = store.ReadVerification(workspace, id);
                var page = store.ListVerifications(workspace);
                AssertEqual(id, page.Items.Single().Id, "pending verification is discoverable after restart");
                AssertTrue(page.Total == 1 && !page.NextOffset.HasValue, "bounded list reports completion");
                AssertEqual(WebVerificationState.Pending, pending.State, "restart cannot turn pending into success");
                AssertTrue(!pending.CompletedUtc.HasValue, "no terminal time is invented");
                AssertEqual(snapshot.Id, pending.SnapshotId, "pending record pins the captured manifest");
                var pendingRef = pending.Reference;

                Directory.CreateDirectory(Path.Combine(workspace.RootPath, "archive"));
                files.MoveText(workspace, "app.js", script.Reference, "archive/app.js");
                files.CreateText(workspace, "app.js", "const currentFile = true;");
                files.DeleteText(workspace, "index.html", html.Reference);
                provider = new WorkspaceFileResourceProvider(new WorkspaceFileService(paths), workspace);
                var reopened = store.ReadSnapshot(workspace, snapshot.Id);
                var oldScript = provider.ReadSnapshot(reopened, "app.js");
                oldScript.RequireCompleteExactText();
                AssertEqual(script.Text, oldScript.Result.Text, "historical route follows its exact identity after move/replacement");
                AssertEqual(html.Text, provider.ReadSnapshot(reopened, "index.html").Result.Text,
                    "deleted entry is available from the retained snapshot");
                AssertEqual("const currentFile = true;", File.ReadAllText(Path.Combine(workspace.RootPath, "app.js")),
                    "historical read never restores current files");

                var foreign = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "foreign"));
                var rejected = false;
                try { store.ReadSnapshot(foreign, snapshot.Id); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "snapshot id alone grants no access from another workspace");
                rejected = false;
                try { new WorkspaceFileResourceProvider(files, foreign).ReadSnapshot(reopened, "app.js"); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "loaded snapshot handle is also workspace-bound");

                store.CompleteVerification(workspace, id, WebVerificationState.Failed, "test-runner",
                    new[] { "Synthetic test failure" }, new string[0]);
                var terminal = new WorkspaceWebSnapshotStore(paths, files).ReadVerification(workspace, id);
                AssertEqual(WebVerificationState.Failed, terminal.State, "terminal record is durable");
                AssertEqual("call", terminal.Origin.ToolCallId, "tool origin is retained");
                var authority = new ResourceAuthorityStore(paths);
                var scope = new ResourceAuthorityScopeId("workspace-web", workspace.WorkspaceId);
                AssertEqual(pendingRef.Revision, authority.GetRevision(scope, terminal.Reference).Parent.Revision,
                    "terminal publication appends a child of pending");
                AssertTrue(authority.GetView(scope, pendingRef, "web-verification") != null,
                    "pending record remains historical evidence");
                rejected = false;
                try { store.CompleteVerification(workspace, id, WebVerificationState.Passed, "test-runner",
                    new string[0], new string[0]); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "a terminal verification cannot be silently overwritten");

                var blobs = new ChatBlobStore(paths);
                var orphan = blobs.StoreText("unpublished-gc-fixture", "text/plain");
                var collector = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None);
                var collected = collector.Collect();
                AssertTrue(collected.Completed, "canonical authority roots permit a complete GC audit");
                AssertTrue(!File.Exists(blobs.PathFor(orphan.Sha256)), "unreferenced fixture is collected");
                AssertEqual(script.Text, provider.ReadSnapshot(store.ReadSnapshot(workspace, snapshot.Id), "app.js").Result.Text,
                    "GC retains snapshot source bytes and manifest");
                AssertEqual(WebVerificationState.Failed, store.ReadVerification(workspace, id).State,
                    "GC retains verification records");

                File.Delete(blobs.PathFor(script.ContentSha256));
                rejected = false;
                try { provider.ReadSnapshot(reopened, "app.js"); }
                catch (WorkspaceFileException ex) { rejected = ex.Code == "snapshot_unavailable"; }
                AssertTrue(rejected, "missing historical payload cannot fall back to the replacement file");
                File.WriteAllBytes(blobs.PathFor(snapshot.Payload.Sha256), new byte[] { 0 });
                rejected = false;
                try { store.ReadSnapshot(workspace, snapshot.Id); }
                catch (WorkspaceFileException ex) { rejected = ex.Code == "snapshot_unavailable"; }
                AssertTrue(rejected, "corrupt manifest is rejected after reopening");
            });
        }

        private static void WorkspaceWebSnapshotsRequirePublication()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                files.CreateText(workspace, "index.html", "<h1>safe</h1>");
                var read = files.ReadText(workspace, "index.html");
                var file = new WebSnapshotFile("index.html", read.Reference, read.Evidence.Payload);
                var store = new WorkspaceWebSnapshotStore(paths, files);
                var rejected = false;
                try { store.PublishSnapshot(workspace, "index.html", new[] { file, file }); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "duplicate routes do not form a manifest");
                rejected = false;
                try { store.PublishSnapshot(workspace, "index.html", Enumerable.Range(0, 33).Select(index =>
                    new WebSnapshotFile(index == 0 ? "index.html" : "file" + index + ".js", read.Reference, read.Evidence.Payload))); }
                catch (WorkspaceFileException ex) { rejected = ex.Code == "snapshot_unavailable"; }
                AssertTrue(rejected, "manifest capture enforces the file bound before source reads");
                rejected = false;
                try { store.PublishSnapshot(workspace, "../index.html", new[] { file }); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "manifest entry stays inside the bounded route set");

                var id = Guid.NewGuid().ToString("N");
                var reference = new ResourceRef(ResourceUri.Create("workspace-web", workspace.WorkspaceId, "snapshot", id), "1");
                var scope = new ResourceAuthorityScopeId("workspace-web", workspace.WorkspaceId);
                var payload = PayloadRef.FromBlob(new ChatBlobStore(paths).StoreText(JsonConvert.SerializeObject(new
                { SchemaVersion = 1, Id = id, WorkspaceId = workspace.WorkspaceId, EntryPath = "index.html",
                    SnapshotSha256 = WorkspaceWebSnapshotStore.Digest(new[] { file }), Files = new[] { file } }), "application/json"));
                var authority = new ResourceAuthorityStore(paths);
                authority.RegisterRevision(scope, new ResourceRevisionMetadata(reference, payload.Sha256, payload));
                authority.RegisterView(scope, new ResourceRevisionView(reference, "web-project-manifest", payload.Sha256,
                    payload, ResourceCoverage.Whole(), new[] { file.Payload }));
                rejected = false;
                try { store.ReadSnapshot(workspace, id); }
                catch (WorkspaceFileException ex) { rejected = ex.Code == "snapshot_unavailable"; }
                AssertTrue(rejected, "crash before head publication cannot expose a snapshot");

                var pending = store.BeginVerification(workspace, "index.html", false);
                rejected = false;
                try { store.CompleteVerification(workspace, pending, WebVerificationState.Passed,
                    "test-runner", new string[0], new string[0]); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "success requires a published snapshot");
                AssertEqual(WebVerificationState.Pending, store.ReadVerification(workspace, pending).State,
                    "rejected completion preserves pending state");

                var steps = new[] { new WebCheckStep("heading", WebCheckOperation.TextEquals, "h1", "safe") };
                var checks = new WebFunctionalChecks("index.html", steps);
                steps[0] = new WebCheckStep("changed", WebCheckOperation.TextEquals, "p", "other");
                AssertEqual("heading", checks.Steps[0].Id, "checks freeze caller-owned step collection");
                var functional = store.BeginVerification(workspace, "index.html", false, checks: checks);
                var snapshot = store.PublishSnapshot(workspace, "index.html", new[] { file });
                store.AttachSnapshot(workspace, functional, snapshot);
                rejected = false;
                try { store.CompleteVerification(workspace, functional, WebVerificationState.Passed,
                    "test-runner", new string[0], new string[0]); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "a successful load cannot replace required functional checks");
                rejected = false;
                try { store.CompleteVerification(workspace, functional, WebVerificationState.Passed,
                    "test-runner", new string[0], new string[0], new[] { new WebCheckResult("changed", WebCheckStatus.Passed) }); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "a different assertion cannot replace an accepted check");
                rejected = false;
                try { store.CompleteVerification(workspace, functional, WebVerificationState.Passed,
                    "test-runner", new string[0], new string[0], new[] { new WebCheckResult("heading", WebCheckStatus.Passed, "wrong") }); }
                catch (WorkspaceFileException) { rejected = true; }
                AssertTrue(rejected, "passed text assertion requires the expected observed value");
                store.CompleteVerification(workspace, functional, WebVerificationState.Passed,
                    "test-runner", new string[0], new string[0], new[] { new WebCheckResult("heading", WebCheckStatus.Passed, "safe") });
                var saved = store.ReadVerification(workspace, functional);
                var exact = new WorkspaceWebSnapshotStore(paths, files).ReadVerification(workspace, saved.Reference);
                AssertEqual(checks.Sha256, exact.Checks.Sha256, "exact result binds the accepted checks after restart");
                AssertEqual("safe", exact.CheckResults.Single().Actual, "observed assertion value is durable");
                rejected = false;
                try { new WebFunctionalChecks("index.html", Enumerable.Repeat(checks.Steps[0], 33)); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "functional step input is bounded");
                rejected = false;
                try { new WebFunctionalChecks("index.html", new[] { new WebCheckStep("only-click", WebCheckOperation.Click, "button") }); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "interaction alone is not an assertion");
            });
        }
    }
}
