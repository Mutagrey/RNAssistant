using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;
using RuntimeToolResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceCreatesAndRelocates()
        {
            WithTempPaths(paths =>
            {
                var root = Path.Combine(paths.Root, "project");
                var workspaces = new WorkspaceStore(paths);
                var opened = workspaces.Open(root);
                AssertTrue(opened.WorkspaceId.StartsWith("ws_", StringComparison.Ordinal), "workspace id");
                AssertTrue(!opened.ReadOnly, "workspace is writable after manifest creation");
                AssertTrue(File.Exists(Path.Combine(root, ".rnassistant", "workspace.json")), "portable manifest");
                var chats = new ChatStore(paths);
                var session = workspaces.CreateSession(chats, opened, "First workspace chat");
                AssertEqual(opened.WorkspaceId, session.WorkspaceId, "workspace session association");
                AssertEqual(null, session.DocumentAuthorityId, "no fake document authority");
                AssertEqual(session.Id, workspaces.ListSessions(chats, opened).Single().Id, "listed chat");
                AssertEqual(session.Id, workspaces.LoadSession(chats, opened, session.Id).Id, "replayed chat");

                var fileOwner = new WorkspaceFileService(paths);
                var fileBeforeMove = fileOwner.CreateText(opened, "index.html", "moved file");

                var moved = Path.Combine(paths.Root, "moved-project");
                Directory.Move(root, moved);
                var reopened = workspaces.Open(moved, false);
                AssertEqual(opened.WorkspaceId, reopened.WorkspaceId, "relocation preserves workspace id");
                AssertEqual(session.Id, workspaces.LoadSession(chats, reopened, session.Id).Id, "relocated chat");
                var fileAfterMove = fileOwner.ReadText(reopened, "index.html");
                AssertEqual(fileBeforeMove.Reference.Uri, fileAfterMove.Reference.Uri, "relocation preserves file identity");
                AssertEqual(fileBeforeMove.Reference.Revision, fileAfterMove.Reference.Revision, "relocation preserves current revision");
            });
        }

        private static void WorkspaceRejectsCopyConflict()
        {
            WithTempPaths(paths =>
            {
                var workspaces = new WorkspaceStore(paths);
                var root = Path.Combine(paths.Root, "project");
                var opened = workspaces.Open(root);
                var copy = Path.Combine(paths.Root, "copy");
                Directory.CreateDirectory(Path.Combine(copy, ".rnassistant"));
                File.Copy(Path.Combine(root, ".rnassistant", "workspace.json"),
                    Path.Combine(copy, ".rnassistant", "workspace.json"));
                var rejected = false;
                try { workspaces.Open(copy, false); }
                catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected, "a copy does not silently reuse a live workspace identity");

                var readOnlyRoot = Path.Combine(paths.Root, "external");
                Directory.CreateDirectory(readOnlyRoot);
                var readOnly = workspaces.Open(readOnlyRoot, false, false);
                AssertTrue(readOnly.ReadOnly, "manifest-free association is read-only");
                AssertTrue(!Directory.Exists(Path.Combine(readOnlyRoot, ".rnassistant")), "no control write in root");
                AssertEqual(readOnly.WorkspaceId, workspaces.Open(readOnlyRoot, false, false).WorkspaceId,
                    "read-only association reopens");
                AssertTrue(opened.WorkspaceId != readOnly.WorkspaceId, "independent roots");
            });
        }

        private static void WorkspaceFilesGuardWrites()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var first = files.CreateText(workspace, "dashboard/app.js", "const value = 1;\r\n");
                AssertEqual("const value = 1;\r\n", File.ReadAllText(Path.Combine(workspace.RootPath, "dashboard", "app.js")),
                    "created a real file");
                var exists = false;
                try { files.CreateText(workspace, "dashboard/app.js", "overwrite"); }
                catch (WorkspaceFileException ex) { exists = ex.Code == "target_exists"; }
                AssertTrue(exists, "create never overwrites");
                var patched = files.PatchExact(workspace, "dashboard/app.js", first.Reference, "value = 1", "value = 2");
                AssertEqual("const value = 2;\r\n", patched.Text, "exact patch and CRLF");
                AssertTrue(patched.Reference.Revision != first.Reference.Revision, "content revision is new");
                AssertEqual("files.patch", patched.AuthorityCommit.Effect.Operation,
                    "patch publication retains the exact operation id");
                var unchanged = files.ReplaceText(workspace, "dashboard/app.js", patched.Reference, patched.Text);
                AssertEqual(patched.Reference.Revision, unchanged.Reference.Revision, "same bytes are a no-op");
                var readEvidence = files.ReadText(workspace, "dashboard/app.js").Evidence;
                AssertTrue(readEvidence != null && readEvidence.Complete &&
                    readEvidence.Resource.Revision == patched.Reference.Revision,
                    "read retains complete exact resource evidence");
                AssertEqual("const value = 1;\r\n", files.ReadHistoricalText(workspace, "dashboard/app.js", first.Reference),
                    "historical view reads retained CAS bytes");
                var sourceForCopy = files.ReadText(workspace, "dashboard/app.js");
                var copied = files.CopyText(workspace, "dashboard/app.js", sourceForCopy.Reference,
                    "dashboard/app-copy.js");
                AssertEqual(sourceForCopy.Text, copied.Text, "copy writes exact observed source bytes");
                AssertTrue(copied.Reference.Uri != sourceForCopy.Reference.Uri,
                    "copy creates a distinct file identity");
                var copyScope = new ResourceAuthorityScopeId("file",
                    ResourceUri.Parse(copied.Reference.Uri).Segments.Single());
                var copyRevision = new ResourceAuthorityStore(paths).GetRevision(copyScope, copied.Reference);
                AssertEqual(sourceForCopy.Reference.Revision,
                    copyRevision.Dependencies.Single().Resource.Revision,
                    "copy retains exact source provenance");
                var restored = files.RestoreHistoricalText(workspace, "dashboard/app.js",
                    patched.Reference, first.Reference);
                AssertEqual("const value = 1;\r\n", restored.Text, "historical version restored to real file");
                AssertEqual(ResourceEffectOutcome.Restored, restored.AuthorityCommit.Effect.Outcome,
                    "restore is a published effect with provenance");
                var staleCopy = false;
                try { files.CopyText(workspace, "dashboard/app.js", sourceForCopy.Reference,
                    "dashboard/stale-copy.js"); }
                catch (WorkspaceFileException ex) { staleCopy = ex.Code == "target_conflict"; }
                AssertTrue(staleCopy && !File.Exists(Path.Combine(workspace.RootPath, "dashboard", "stale-copy.js")),
                    "stale source cannot create a copy");
                File.WriteAllText(Path.Combine(workspace.RootPath, "dashboard", "app.js"), "external edit\n");
                var conflict = false;
                try { files.ReplaceText(workspace, "dashboard/app.js", patched.Reference, "agent edit"); }
                catch (WorkspaceFileException ex) { conflict = ex.Code == "target_conflict"; }
                AssertTrue(conflict, "external edit blocks stale replace");
                AssertEqual("external edit\n", File.ReadAllText(Path.Combine(workspace.RootPath, "dashboard", "app.js")),
                    "external edit survives");
                var refreshed = files.ReadText(workspace, "dashboard/app.js");
                AssertTrue(refreshed.Reference.Revision != patched.Reference.Revision, "drift observed as a new revision");
                var currentAuthority = new ResourceAuthorityStore(paths).CaptureMany(new[] { readEvidence.ScopeId });
                AssertEqual(EvidenceState.Superseded,
                    new EvidenceStateReducer().Reduce(readEvidence, currentAuthority).State,
                    "old read evidence is superseded after external drift");

                var bomPath = Path.Combine(workspace.RootPath, "bom.txt");
                File.WriteAllBytes(bomPath, new byte[] { 0xef, 0xbb, 0xbf, (byte)'a', (byte)'\r', (byte)'\n' });
                var bom = files.ReadText(workspace, "bom.txt");
                files.ReplaceText(workspace, "bom.txt", bom.Reference, "b\n");
                AssertTrue(File.ReadAllBytes(bomPath).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }),
                    "whole replacement preserves UTF-8 BOM");
                AssertEqual("b\r\n", File.ReadAllText(bomPath), "whole replacement preserves CRLF");
            });
        }

        private static void WorkspaceFilesListBounded()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var gateway = new ResourceProviderRouter<WorkspaceFileResourceProvider>(new[]
                    { new WorkspaceFileResourceProvider(files, workspace) });
                for (var index = 0; index < 230; index++)
                    File.WriteAllText(Path.Combine(workspace.RootPath,
                        "item-" + index.ToString("D3") + ".txt"), "fixture");
                File.WriteAllText(Path.Combine(workspace.RootPath, ".env-local"), "secret");
                Directory.CreateDirectory(Path.Combine(workspace.RootPath, ".git"));
                var page = gateway.Select("file").Find("", null, 20);
                AssertEqual(20, page.Items.Count, "gateway file discovery respects requested bound");
                AssertTrue(page.RefineQuery && page.ScannedEntries.Value <= 5000,
                    "directory listing reports an incomplete scan");
                AssertTrue(!page.Complete && !page.Empty, "bounded listing does not establish absence");
                AssertTrue(!page.Items.Any(item => item.Target == ".rnassistant" || item.Target == ".git" ||
                    item.Target == ".env-local"), "protected entries stay out of discovery");
                var narrowed = gateway.Select("file").Find("", "ITEM-229");
                AssertEqual(1, narrowed.Items.Count, "query narrows directory discovery");
                AssertEqual("item-229.txt", narrowed.Items[0].Target, "case-insensitive filename query");
                AssertTrue(narrowed.Complete, "narrow query scanned the full directory");
                AssertTrue(narrowed.Items[0].Descriptor.Reference.Revision == null,
                    "metadata-only discovery does not claim current file bytes");
                var observed = gateway.Select("file").Read(narrowed.Items[0].Target);
                observed.RequireCompleteExactText();
                var exactEvidence = observed.Evidence.Single();
                var frozen = gateway.Select("file").CaptureAuthority(observed.Evidence);
                var reducer = new EvidenceStateReducer();
                AssertTrue(reducer.IsCurrentExactText(exactEvidence, observed.Result.Resource.Reference, frozen),
                    "exact whole-file observation is current in the captured authority");
                var excerpt = new ResourceEvidence("excerpt", exactEvidence.ScopeId, exactEvidence.Resource,
                    "text", new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: 2),
                    true, exactEvidence.AuthorityGeneration, exactEvidence.Payload);
                AssertTrue(!reducer.IsCurrentExactText(excerpt, exactEvidence.Resource, frozen),
                    "complete excerpt cannot satisfy whole-file observation");
                AssertEqual("fixture", observed.Result.Text, "gateway file read returns real source bytes");
                AssertTrue(observed.Evidence.Single().Complete &&
                    gateway.ForUri(observed.Result.Resource.Reference.Uri).Id == "file",
                    "gateway routes canonical file identity with complete exact evidence");
                var rejected = false;
                try { files.ListPage(workspace, query: "bad\nquery"); }
                catch (WorkspaceFileException ex) { rejected = ex.Code == "invalid_find_query"; }
                AssertTrue(rejected, "control characters are rejected in filename query");
            });
        }

        private static void WorkspaceFilesRejectUnsafePaths()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                foreach (var path in new[] { "../escape.txt", ".rnassistant/workspace.json", ".git/config",
                    ".env", ".env.production", ".codex/config", "cert.pem", "private.key",
                    "AGENTS.md", "SKILL.md", "/tmp/absolute.txt", "C:/device.txt" })
                {
                    var rejected = false;
                    try { files.CreateText(workspace, path, "data"); }
                    catch (WorkspaceFileException ex) { rejected = ex.Code == "path_outside_mount"; }
                    AssertTrue(rejected, "unsafe path rejected: " + path);
                }
                var original = files.CreateText(workspace, "index.html", "<h1>safe</h1>");
                var other = files.CreateText(workspace, "app.js", "safe");
                var wrongRef = false;
                try { files.ReadHistoricalText(workspace, "app.js", original.Reference); }
                catch (WorkspaceFileException ex) { wrongRef = ex.Code == "invalid_resource_ref"; }
                AssertTrue(wrongRef, "historical reference cannot cross file identity");
                AssertEqual("safe", files.ReadHistoricalText(workspace, "app.js", other.Reference), "exact read");
                AssertTrue(files.ListPage(workspace).Names.Contains("index.html"), "bounded real file listing");
                var badText = Path.Combine(workspace.RootPath, "bad.txt");
                File.WriteAllBytes(badText, new byte[] { 0xff, 0xfe });
                var badEncoding = false;
                try { files.ReadText(workspace, "bad.txt"); }
                catch (WorkspaceFileException ex) { badEncoding = ex.Code == "encoding_ambiguous"; }
                AssertTrue(badEncoding, "invalid UTF-8 is not decoded with replacement characters");
                files.CreateText(workspace, "ambiguous.txt", "valid text");
                var oldTextEvidence = files.ReadText(workspace, "ambiguous.txt").Evidence;
                File.WriteAllBytes(Path.Combine(workspace.RootPath, "ambiguous.txt"), new byte[] { 0xff });
                var externalBinary = false;
                try { files.ReadText(workspace, "ambiguous.txt"); }
                catch (WorkspaceFileException ex) { externalBinary = ex.Code == "encoding_ambiguous"; }
                AssertTrue(externalBinary, "external binary edit is reported explicitly");
                var frozen = new ResourceAuthorityStore(paths).CaptureMany(new[] { oldTextEvidence.ScopeId });
                AssertEqual(EvidenceState.Unknown,
                    new EvidenceStateReducer().Reduce(oldTextEvidence, frozen).State,
                    "old text evidence becomes unknown after unrepresentable external edit");
                var caseCollision = false;
                try { files.CreateText(workspace, "INDEX.HTML", "collision"); }
                catch (WorkspaceFileException ex) { caseCollision = ex.Code == "case_collision"; }
                AssertTrue(caseCollision, "portable case collision is rejected");
            });
        }

        private static void WorkspaceFilesDoNotReplayUncertainWrite()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                files.FaultPoint = stage => { if (stage == "after-dispatch") throw new IOException("injected interruption"); };
                var unknown = false;
                try { files.CreateText(workspace, "index.html", "<h1>created</h1>"); }
                catch (WorkspaceFileException ex) { unknown = ex.Code == "effect_unknown"; }
                AssertTrue(unknown, "possible effect is unknown after interruption");
                AssertEqual("<h1>created</h1>", File.ReadAllText(Path.Combine(workspace.RootPath, "index.html")),
                    "dispatched bytes remain visible");
                var restarted = new WorkspaceFileService(paths);
                var readBlocked = false;
                try { restarted.ReadText(workspace, "index.html"); }
                catch (WorkspaceFileException ex) { readBlocked = ex.Code == "unresolved_previous_effect"; }
                AssertTrue(readBlocked, "ordinary read cannot publish an unresolved effect as a known head");
                var blocked = false;
                try { restarted.ReplaceText(workspace, "index.html", null, "overwrite"); }
                catch (WorkspaceFileException ex) { blocked = ex.Code == "unresolved_previous_effect"; }
                AssertTrue(blocked, "restart does not automatically retry or conceal unresolved effect");
                AssertEqual("<h1>created</h1>", File.ReadAllText(Path.Combine(workspace.RootPath, "index.html")),
                    "blocked repeat leaves file intact");
                var reconciled = restarted.ReconcileUncertain(workspace, "index.html");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, reconciled.Outcome,
                    "recovery retains unknown causality");
                AssertEqual("<h1>created</h1>", reconciled.Current.Text, "recovery observes current bytes without replay");
                AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count,
                    "explicit reconciliation resolves pending attempt");
                var next = restarted.ReplaceText(workspace, "index.html", reconciled.Current.Reference, "<h1>next</h1>");
                AssertEqual("<h1>next</h1>", next.Text, "new guarded write allowed after reconciliation");
                var scope = new ResourceAuthorityScopeId("file",
                    ResourceUri.Parse(next.Reference.Uri).Segments.Single());
                new ResourceMutationJournal(paths).Prepare(scope, "files.replace",
                    next.Reference.Identity, next.Reference.Revision);
                var preparedOnly = restarted.ReconcileUncertain(workspace, "index.html");
                AssertEqual(WorkspaceRecoveryOutcome.AbandonedBeforeDispatch, preparedOnly.Outcome,
                    "prepared-only recovery proves no dispatch");
                AssertEqual("<h1>next</h1>", File.ReadAllText(Path.Combine(workspace.RootPath, "index.html")),
                    "prepared-only recovery does not touch the file");
            });
        }

        private static void WorkspaceFilesRecoverableDelete()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var source = files.CreateText(workspace, "src/app.js", "const ready = true;\n");
                var accepted = files.ReadText(workspace, "src/app.js");
                var deleted = files.DeleteText(workspace, "src/app.js", accepted.Reference);
                var sourcePath = Path.Combine(workspace.RootPath, "src", "app.js");
                AssertTrue(!File.Exists(sourcePath), "delete removes only the workspace source path");
                AssertEqual("files.delete", deleted.AuthorityCommit.Effect.Operation, "delete publishes verified effect");
                AssertEqual("const ready = true;\n", files.ReadHistoricalText(workspace, "src/app.js", source.Reference),
                    "deleted bytes remain available as an exact historical view");
                var oldState = new ResourceAuthorityStore(paths).CaptureMany(new[] { accepted.Evidence.ScopeId });
                AssertEqual(EvidenceState.Unavailable,
                    new EvidenceStateReducer().Reduce(accepted.Evidence, oldState).State,
                    "delete invalidates the former current read");
                var restored = files.RestoreDeletedText(workspace, "src/app.js");
                AssertEqual("const ready = true;\n", File.ReadAllText(sourcePath), "restore recreates the original file");
                AssertTrue(restored.Reference.Revision != source.Reference.Revision,
                    "restore publishes a new logical revision");
                AssertEqual(source.Reference.Revision,
                    new ResourceAuthorityStore(paths).GetRevision(accepted.Evidence.ScopeId, restored.Reference)
                        .RestoredFrom.Revision, "restored revision retains exact provenance");
                var repeated = false;
                try { files.RestoreDeletedText(workspace, "src/app.js"); }
                catch (WorkspaceFileException ex) { repeated = ex.Code == "target_conflict"; }
                AssertTrue(repeated, "restore never overwrites a present target");

                var current = files.ReadText(workspace, "src/app.js");
                File.WriteAllText(sourcePath, "external edit\n");
                var staleDelete = false;
                try { files.DeleteText(workspace, "src/app.js", current.Reference); }
                catch (WorkspaceFileException ex) { staleDelete = ex.Code == "target_conflict"; }
                AssertTrue(staleDelete && File.ReadAllText(sourcePath) == "external edit\n",
                    "stale delete leaves an external edit intact");
                current = files.ReadText(workspace, "src/app.js");
                files.DeleteText(workspace, "src/app.js", current.Reference);
                File.WriteAllText(sourcePath, "external");
                var conflict = false;
                try { files.RestoreDeletedText(workspace, "src/app.js"); }
                catch (WorkspaceFileException ex) { conflict = ex.Code == "target_conflict"; }
                AssertTrue(conflict && File.ReadAllText(sourcePath) == "external",
                    "external file survives a conflicting restore");

                var uncertain = files.CreateText(workspace, "pending.js", "pending\n");
                files.FaultPoint = stage => { if (stage == "after-dispatch") throw new IOException("injected interruption"); };
                var unknown = false;
                try { files.DeleteText(workspace, "pending.js", uncertain.Reference); }
                catch (WorkspaceFileException ex) { unknown = ex.Code == "effect_unknown"; }
                AssertTrue(unknown && !File.Exists(Path.Combine(workspace.RootPath, "pending.js")),
                    "interrupted delete has possible effect and does not replay");
                var restarted = new WorkspaceFileService(paths);
                var blocked = false;
                try { restarted.RestoreDeletedText(workspace, "pending.js"); }
                catch (WorkspaceFileException ex) { blocked = ex.Code == "unresolved_previous_effect"; }
                AssertTrue(blocked, "unresolved delete blocks restoration before reconciliation");
                var recovery = restarted.ReconcileUncertain(workspace, "pending.js");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, recovery.Outcome,
                    "recovery records unknown delete causality");
                var explicitRestore = restarted.RestoreDeletedText(workspace, "pending.js");
                AssertEqual("pending\n", explicitRestore.Text, "explicit restore after reconciliation recovers managed trash");
            });
        }

        private static void WorkspaceFilesMoveAndRecover()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var created = files.CreateText(workspace, "src/app.js", "const app = 1;\n");
                var accepted = files.ReadText(workspace, "src/app.js");
                Directory.CreateDirectory(Path.Combine(workspace.RootPath, "dst"));
                var moved = files.MoveText(workspace, "src/app.js", accepted.Reference, "dst/app.js");
                AssertTrue(!File.Exists(Path.Combine(workspace.RootPath, "src", "app.js")), "move removes source path");
                AssertEqual("const app = 1;\n", File.ReadAllText(Path.Combine(workspace.RootPath, "dst", "app.js")),
                    "move writes real target bytes");
                AssertEqual(created.Reference.Uri, moved.Reference.Uri, "move preserves logical file identity");
                AssertTrue(created.Reference.Revision != moved.Reference.Revision, "move advances revision");
                AssertEqual(ResourceEffectOutcome.VerifiedChanged, moved.AuthorityCommit.Effect.Outcome,
                    "move publishes verified effect");
                AssertEqual(created.Reference.Revision,
                    new ResourceAuthorityStore(paths).GetRevision(accepted.Evidence.ScopeId, moved.Reference).Parent.Revision,
                    "moved revision retains parent");
                AssertEqual("const app = 1;\n", files.ReadHistoricalText(workspace, "dst/app.js", created.Reference),
                    "historical view follows moved identity");
                AssertEqual(moved.Reference.Revision, files.ReadText(workspace, "dst/app.js").Reference.Revision,
                    "target read resolves published head");
                var currentAuthority = new ResourceAuthorityStore(paths).CaptureMany(new[] { accepted.Evidence.ScopeId });
                AssertEqual(EvidenceState.Superseded,
                    new EvidenceStateReducer().Reduce(accepted.Evidence, currentAuthority).State,
                    "old path observation is superseded by move");
                var sourceMissing = false;
                try { files.ReadText(workspace, "src/app.js"); }
                catch (FileNotFoundException) { sourceMissing = true; }
                AssertTrue(sourceMissing, "old path no longer resolves moved identity");

                files.CreateText(workspace, "dst/occupied.js", "other\n");
                var collision = false;
                try { files.MoveText(workspace, "dst/app.js", moved.Reference, "dst/occupied.js"); }
                catch (WorkspaceFileException ex) { collision = ex.Code == "target_exists"; }
                AssertTrue(collision && File.Exists(Path.Combine(workspace.RootPath, "dst", "app.js")),
                    "move does not overwrite occupied target");
                File.WriteAllText(Path.Combine(workspace.RootPath, "dst", "app.js"), "external\n");
                var external = false;
                try { files.MoveText(workspace, "dst/app.js", moved.Reference, "dst/stale.js"); }
                catch (WorkspaceFileException ex) { external = ex.Code == "target_conflict"; }
                AssertTrue(external && !File.Exists(Path.Combine(workspace.RootPath, "dst", "stale.js")),
                    "external edit blocks stale move");

                var aborted = files.CreateText(workspace, "abort.js", "abort\n");
                var refusedBeforeDispatch = false;
                try { files.MoveText(workspace, "abort.js", aborted.Reference, "not-moved.js",
                    () => throw new IOException("dispatch withheld")); }
                catch (IOException) { refusedBeforeDispatch = true; }
                AssertTrue(refusedBeforeDispatch && File.Exists(Path.Combine(workspace.RootPath, "abort.js")) &&
                    !File.Exists(Path.Combine(workspace.RootPath, "not-moved.js")),
                    "pre-dispatch refusal does not move or leave an unresolved effect");
                AssertEqual(aborted.Reference.Revision, files.ReadText(workspace, "abort.js").Reference.Revision,
                    "source remains readable after pre-dispatch refusal");

                var pending = files.CreateText(workspace, "pending.js", "pending\n");
                files.FaultPoint = point => { if (point == "after-dispatch") throw new IOException("simulated crash"); };
                var unknown = false;
                try { files.MoveText(workspace, "pending.js", pending.Reference, "moved.js"); }
                catch (WorkspaceFileException ex) { unknown = ex.Code == "effect_unknown"; }
                AssertTrue(unknown, "interrupted move has unknown effect");
                var restarted = new WorkspaceFileService(paths);
                var sourceBlocked = false;
                var targetBlocked = false;
                try { restarted.ReadText(workspace, "pending.js"); }
                catch (WorkspaceFileException ex) { sourceBlocked = ex.Code == "unresolved_previous_effect"; }
                try { restarted.ReadText(workspace, "moved.js"); }
                catch (WorkspaceFileException ex) { targetBlocked = ex.Code == "unresolved_previous_effect"; }
                AssertTrue(sourceBlocked && targetBlocked, "both paths block while move effect is unresolved");
                var recovery = restarted.ReconcileUncertain(workspace, "pending.js");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, recovery.Outcome,
                    "move reconciliation records unknown causality");
                AssertEqual(pending.Reference.Uri, restarted.ReadText(workspace, "moved.js").Reference.Uri,
                    "recovered move keeps logical identity without replay");

                var another = restarted.CreateText(workspace, "second.js", "second\n");
                restarted.FaultPoint = point => { if (point == "after-locator") throw new IOException("simulated crash"); };
                unknown = false;
                try { restarted.MoveText(workspace, "second.js", another.Reference, "relocated.js"); }
                catch (WorkspaceFileException ex) { unknown = ex.Code == "effect_unknown"; }
                AssertTrue(unknown, "interrupted locator publication has unknown effect");
                var relocatedRoot = Path.Combine(paths.Root, "relocated-project");
                Directory.Move(workspace.RootPath, relocatedRoot);
                workspace = new WorkspaceStore(paths).Open(relocatedRoot, false);
                var finalOwner = new WorkspaceFileService(paths);
                var afterLocator = finalOwner.ReconcileUncertain(workspace, "second.js");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, afterLocator.Outcome,
                    "source path resolves pending move after locator and workspace root switched");
                AssertEqual(another.Reference.Uri, finalOwner.ReadText(workspace, "relocated.js").Reference.Uri,
                    "target retains identity after locator recovery");
            });
        }

        private static void JsonFileStoreWritesAtomicUtf8()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var path = Path.Combine(paths.Root, "streamed.json");
                var store = new JsonFileStore();
                store.Save(path, new Dictionary<string, string>
                {
                    { "value", "Привет " + new string('x', 100000) }
                });
                store.Save(path, new Dictionary<string, string> { { "value", "Готово" } });

                var loaded = store.Load<Dictionary<string, string>>(path, null);
                AssertEqual("Готово", loaded["value"], "streamed json overwrite");
                AssertEqual(0, Directory.GetFiles(paths.Root, "streamed.json.*.tmp").Length, "atomic temp files cleaned");
                var bytes = File.ReadAllBytes(path);
                AssertTrue(bytes.Length > 3 && !(bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf),
                    "streamed json uses utf8 without bom");

                foreach (var invalid in new[] { "{broken", "null" })
                {
                    File.WriteAllText(paths.SettingsFile, invalid);
                    var rejected = false;
                    try { new SettingsService(paths).Save(new AppSettings()); }
                    catch (InvalidDataException) { rejected = true; }
                    AssertTrue(rejected, "corrupt existing settings reject a save");
                    AssertEqual(invalid, File.ReadAllText(paths.SettingsFile),
                        "rejected save preserves corrupt settings for recovery");
                }
            });
        }

        private static void CreatesAndListsChatsInTempRoot()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "doc-key", "Doc", "First");
                session.Messages.Add(new ChatMessage
                {
                    Id = null,
                    Role = "user",
                    Content = "hello",
                    ResourceRefs = new List<ResourceRef>
                    {
                        new ResourceRef(ResourceUri.Create("document", "selection")),
                        new ResourceRef(ResourceUri.Create("document", "selection")),
                        new ResourceRef("not-a-resource")
                    },
                    Activity = new ChatActivity
                    {
                        Kind = "notice",
                        Title = "Stored activity",
                        Status = "completed"
                    }
                });
                session.ContextCheckpoints.Add(new ContextCheckpoint { Id = null, ThroughMessageId = session.Messages[0].Id });
                session.Artifacts.Add(new ChatArtifact { Id = null, Kind = ChatArtifactKinds.Markdown, RelatedArtifactIds = null });
                store.Save(session);

                var loaded = store.Load("Word", "doc-key", session.Id);
                AssertTrue(loaded != null, "loaded session");
                AssertEqual("First", loaded.Title, "title");
                AssertEqual(1, loaded.Messages.Count, "message count");
                AssertEqual("hello", loaded.Messages[0].Content, "message content");
                AssertEqual("Stored activity", loaded.Messages[0].Activity.Title, "message activity title");
                AssertTrue(!string.IsNullOrWhiteSpace(loaded.Messages[0].Id), "missing message id normalized");
                AssertEqual(1, loaded.Messages[0].ResourceRefs.Count, "resource refs validated and deduplicated");
                AssertTrue(!string.IsNullOrWhiteSpace(loaded.Artifacts[0].Id), "missing artifact id normalized");
                AssertTrue(loaded.Artifacts[0].RelatedArtifactIds != null, "artifact relations normalized");
                var sessions = store.List("Word", "doc-key", "Doc");
                AssertEqual(1, sessions.Count, "document session count");
                AssertEqual(session.Id, sessions[0].Id, "session id");
                AssertEqual(session.Id, store.LoadActiveSessionId("Word", "doc-key"), "active id");

                var history = Enumerable.Range(0, 181).Select(index => new ChatMessage
                {
                    Id = "message-" + index, Role = "user", Content = "text-" + index
                }).ToList();
                int startIndex;
                var recent = ChatCloneService.CloneRecentMessagesForBridge(history, out startIndex);
                AssertEqual(101, startIndex, "bridge opens at bounded recent history");
                AssertEqual(80, recent.Count, "bridge recent page size");
                AssertEqual("message-101", recent[0].Id, "bridge recent page boundary");
                var previous = ChatCloneService.ClonePreviousMessagesForBridge(
                    session.Id, session.Revision, history, startIndex);
                AssertEqual(21, previous.StartIndex, "older page start");
                AssertEqual(181, previous.TotalCount, "older page retains total count");
                AssertEqual("message-100", previous.Messages.Last().Id, "older page does not overlap");
            });
        }

        private static void ChatActivityIgnoresNavigationAndMetadata()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var session = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Older");
                var created = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
                session.CreatedUtc = created;
                store.Save(session);
                AssertEqual(created, store.ListHeaders().Single().LastActivityUtc, "empty chat uses creation");
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Hello", CreatedUtc = created.AddHours(1) });
                store.Save(session);
                AssertEqual(created.AddHours(1), store.ListHeaders().Single().LastActivityUtc, "message advances warm header");
                session = service.LoadSession(session.Id);
                service.SetActiveSession(session);
                session.Title = "Renamed";
                session.Model = "changed-model";
                store.Save(session);
                service.NotifySaved(session);
                AssertEqual(created.AddHours(1), service.GetChatSummaries(session.Id).Single().LastActivityUtc,
                    "navigation and metadata save preserve activity summary");
                AssertEqual(created.AddHours(1), new ChatStore(paths).ListHeaders().Single().LastActivityUtc,
                    "cold replay preserves activity independently of save time");
                session.LastRun = new ChatRunRecord { RunId = "activity-run", Status = "running", StartedUtc = created.AddHours(2) };
                store.Save(session);
                AssertEqual(created.AddHours(2), store.ListHeaders().Single().LastActivityUtc, "new request advances activity");
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Done", CreatedUtc = created.AddHours(3) });
                session.LastRun.Status = "completed";
                store.Save(session);
                var header = new ChatStore(paths).ListHeaders().Single();
                AssertEqual(created.AddHours(3), header.LastActivityUtc, "response advances activity after request");
                AssertEqual(header.LastActivityUtc, ChatSessionHeaderFactory.Create(session).LastActivityUtc,
                    "live and cold header activity agree");

                store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Second");
                var adapterReads = 0;
                var countedAdapter = new ThreadRecordingOfficeAdapter(adapter, () => adapterReads++);
                var countedService = new ChatSessionService(countedAdapter, ConversationStore(store));
                var summaries = countedService.GetChatSummaries(session.Id);
                AssertEqual(2, summaries.Count, "both chat headers are projected");
                AssertEqual(2, adapterReads, "current Office identity is read once for the whole chat catalog");
                AssertTrue(summaries.All(item => item.IsCurrentDocument),
                    "captured document identity still marks each matching chat");
            });
        }

        private static void StaleChatRevisionIsRejected()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var created = store.Create("Excel", "revision-doc", "Revision.xlsx", "Initial");
                var first = store.Load(created.Id);
                var stale = store.Load(created.Id);

                first.Title = "First writer";
                store.Save(first);
                AssertTrue(first.Revision > created.Revision, "successful save advances revision");

                stale.Title = "Stale writer";
                try
                {
                    store.Save(stale);
                    throw new InvalidOperationException("stale save unexpectedly succeeded");
                }
                catch (ChatConcurrencyException)
                {
                }

                var loaded = store.Load(created.Id);
                AssertEqual("First writer", loaded.Title, "stale writer does not overwrite newer state");
                AssertEqual(loaded.Revision, store.ListHeaders()[0].Revision, "summary revision matches chat revision");

                var staleMover = store.Load(created.Id);
                loaded.Title = "Changed before move";
                store.Save(loaded);
                try
                {
                    store.Move(staleMover, "Excel", "revision-doc-moved", "Moved.xlsx");
                    throw new InvalidOperationException("stale move unexpectedly succeeded");
                }
                catch (ChatConcurrencyException)
                {
                }
                AssertEqual("Changed before move", store.Load(created.Id).Title,
                    "stale move does not delete a newer source revision");
                AssertEqual(0, store.List("Excel", "revision-doc-moved", "Moved.xlsx").Count,
                    "stale move does not create a destination copy");
            });
        }

        private static void DeletesDocumentChats()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var deletedWithArtifact = store.Create("Excel", "book-1", "Book1.xlsx", "First");
                store.Create("Excel", "book-1", "Book1.xlsx", "Second");
                var keptWithArtifact = store.Create("Excel", "book-2", "Book2.xlsx", "Keep");
                HtmlWorkspaceToolService.UpsertFile(deletedWithArtifact, "index.html", "html", "delete", true);
                HtmlWorkspaceArtifactService.CaptureCurrent(deletedWithArtifact, "Delete");
                store.Save(deletedWithArtifact);
                HtmlWorkspaceToolService.UpsertFile(keptWithArtifact, "index.html", "html", "keep", true);
                HtmlWorkspaceArtifactService.CaptureCurrent(keptWithArtifact, "Keep");
                store.Save(keptWithArtifact);

                AssertTrue(store.DeleteDocument("Excel", "book-1"), "document directory deleted");
                AssertEqual(0, store.List("Excel", "book-1", "Book1.xlsx").Count, "document chats deleted");
                AssertEqual(1, store.List("Excel", "book-2", "Book2.xlsx").Count, "other document preserved");
                AssertTrue(Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length > 0,
                    "shared content-addressed blobs remain valid for other sessions");
            });
        }

        private static void ChatSessionServiceMigratesDocumentKey()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var journal = new VbaJournalStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store), journal);
                var session = service.LoadSession(null);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "before save" });
                store.Save(session);
                var backup = journal.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Option Explicit");
                var interrupted = journal.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = "write",
                    Host = "Excel",
                    DocumentKey = "doc",
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey,
                    DocumentTitle = "Harness.xlsx",
                    ModuleName = "PendingModule",
                    ComponentType = "StdModule",
                    BeforeExists = false,
                    IntendedAfterExists = true
                }, string.Empty, "Sub Pending()\nEnd Sub");
                var otherSession = store.Create("Excel", "doc", "Harness.xlsx", "Other running chat");
                otherSession.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                store.Save(otherSession);

                var runOwned = true;
                service.RunOwnershipProvider = id => runOwned && string.Equals(id, otherSession.Id, StringComparison.OrdinalIgnoreCase);
                adapter.DocumentKeyValue = "saved-doc";
                adapter.DocumentPathValue = "C:\\Demo\\SavedWorkbook.xlsx";
                var stillRunning = service.LoadSession(null);
                AssertEqual("doc", stillRunning.DocumentKey, "active run postpones document migration");
                AssertEqual(2, store.List("Excel", "doc", "Harness.xlsx").Count, "all source chats remain while another chat owns a run");

                runOwned = false;
                var migrated = service.LoadSession(null);

                AssertEqual(session.Id, migrated.Id, "migrated session id");
                AssertEqual("saved-doc", migrated.DocumentKey, "migrated document key");
                AssertTrue(migrated.PreviousDocumentKeys.Contains("doc", StringComparer.OrdinalIgnoreCase),
                    "migrated session retains the previous live-resource document key");
                AssertEqual("saved-doc", migrated.Context.DocumentKey, "migrated chat context identity");
                AssertEqual(1, migrated.Messages.Count, "migrated message count");
                AssertEqual(0, store.List("Excel", "doc", "Harness.xlsx").Count, "old document sessions");
                var migratedSessions = store.List("Excel", "saved-doc", "Harness.xlsx");
                AssertEqual(2, migratedSessions.Count, "new document sessions");
                AssertTrue(migratedSessions.All(item => item.DocumentPath == "C:\\Demo\\SavedWorkbook.xlsx"),
                    "all migrated chats use the current full path");
                AssertEqual(0, journal.List("Excel", "doc").Count, "old VBA journal moved");
                var migratedBackups = journal.List("Excel", "saved-doc");
                AssertEqual(1, migratedBackups.Count, "VBA journal follows document identity");
                AssertEqual(backup.BackupId, migratedBackups[0].BackupId, "VBA backup identity preserved");
                AssertTrue(journal.ReadEvents("Excel", "saved-doc").Any(item =>
                    item.Type == VbaJournalEventTypes.DocumentIdentityChanged), "VBA identity migration is append-only");

                var executor = new OfficeToolExecutor(
                    adapter,
                    journal,
                    new SkillStore(paths),
                    null,
                    null,
                    null,
                    paths);
                var reconciled = ListVbaComponents(executor, migrated);
                AssertTrue(reconciled.Items.Count > 0,
                    "VBA resource access reconciles interrupted mutation after identity migration");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    journal.ListMutations("Excel", "saved-doc").Single(item =>
                        item.Prepared.MutationId == interrupted.MutationId).Terminal.Status,
                    "migrated open mutation closes in the new journal");
            });
        }

        private static void ChatSessionServiceFallsBackForStaleRequestedId()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var oldSession = service.LoadSession(null);
                var oldId = oldSession.Id;
                oldSession.Messages.Add(new ChatMessage { Role = "user", Content = "old doc" });
                store.Save(oldSession);

                adapter.DocumentKeyValue = "other-doc";
                adapter.RuntimeDocumentKeyValue = "other-runtime-doc";
                adapter.DocumentPathValue = "C:\\Demo\\Other.xlsx";

                var current = service.LoadSession(oldId, true);

                AssertTrue(!string.Equals(oldId, current.Id, StringComparison.OrdinalIgnoreCase), "fallback created current session");
                AssertEqual("other-doc", current.DocumentKey, "fallback document key");
                AssertEqual(0, current.Messages.Count, "fallback message count");
                AssertEqual(1, store.List("Excel", "doc", "Harness.xlsx").Count, "old document preserved");
                AssertEqual(0, store.List("Excel", "other-doc", "Harness.xlsx").Count, "empty fallback remains transient");
            });
        }

        private static void AddressedSessionLoadsExplicitChatAcrossDocuments()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var registry = new DocumentAuthorityRegistry(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store), null, registry);
                var active = service.LoadSession(null);
                var archived = store.Create("Word", "archived-doc", "Archive.docx", "Archive chat");
                archived.DocumentPath = "C:\\Archive.docx";
                archived.DocumentAuthorityId = registry.Resolve(
                    archived.Host, null, archived.DocumentPath).Id;
                store.Save(archived);
                var activeAuthorityId = active.DocumentAuthorityId;
                var archivedAuthorityId = archived.DocumentAuthorityId;

                var loaded = service.LoadAddressedSession(archived.Id);

                AssertTrue(service.TryLoadCurrentDocumentChat(archived.Id) == null,
                    "current-document selection does not attach a foreign chat");

                AssertEqual(archived.Id, loaded.Id, "addressed session id");
                AssertEqual("Word", loaded.Host, "addressed host");
                AssertEqual("archived-doc", loaded.DocumentKey, "addressed document key");
                AssertEqual(archivedAuthorityId, loaded.DocumentAuthorityId,
                    "addressed load preserves the foreign document authority");
                AssertEqual(activeAuthorityId, registry.Resolve(adapter.HostName,
                    adapter.RuntimeDocumentKey, adapter.DocumentPathValue).Id,
                    "addressed load does not bind the current runtime to the foreign authority");
                AssertEqual(archivedAuthorityId, registry.Resolve(archived.Host,
                    null, archived.DocumentPath).Id,
                    "addressed load does not move the foreign authority locator");
                AssertEqual(active.Id, service.GetActiveSession().Id,
                    "addressed load preserves the selected chat");
            });
        }

        private static void AddressedTransientSessionSurvivesDocumentSwitch()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var draft = service.LoadSession(null);

                adapter.DocumentKeyValue = "other-doc";
                adapter.RuntimeDocumentKeyValue = "other-runtime-doc";
                adapter.DocumentPathValue = "C:\\Demo\\Other.xlsx";
                var loaded = service.LoadAddressedSession(draft.Id);

                AssertEqual(draft.Id, loaded.Id, "addressed transient id");
                AssertEqual("doc", loaded.DocumentKey, "transient keeps original document");
                AssertTrue(!service.IsCurrentDocument(loaded), "transient is not rebound to another document");
            });
        }

        private static void AddressedSessionDoesNotFallbackToDifferentChat()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var first = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "First");
                var second = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Second");
                var removedId = first.Id;
                var activeId = second.Id;
                service.SetActiveSession(second);

                AssertTrue(store.Delete(adapter.HostName, adapter.DocumentKey, removedId), "deleted addressed chat");
                var threw = false;
                try
                {
                    service.LoadAddressedSession(removedId);
                }
                catch (InvalidOperationException ex)
                {
                    threw = ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
                }

                AssertTrue(threw, "missing addressed chat rejected");
                AssertEqual(activeId, service.GetActiveSession().Id, "active chat preserved");
            });
        }

        private static void EmptyChatDraftsAreNotPersisted()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));

                var first = service.LoadSession(null);
                var second = service.CreateChat("Another draft");

                AssertTrue(!string.Equals(first.Id, second.Id, StringComparison.OrdinalIgnoreCase), "new draft id");
                AssertEqual(ChatModes.Agent, first.Mode, "initial draft defaults to agent mode");
                AssertEqual(ChatModes.Agent, second.Mode, "new draft defaults to agent mode");
                AssertEqual(0, store.List(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle).Count, "empty drafts not persisted");
                AssertTrue(!store.IsPersisted(second), "active draft remains in memory");
                AssertEqual(second.Id, service.GetActiveSession().Id, "active draft survives list refresh");

                var draftSummaries = service.GetChatSummaries(second.Id);
                AssertEqual(1, draftSummaries.Count, "active transient draft is visible in chat tree");
                AssertEqual(ChatModes.Agent, draftSummaries[0].Mode, "visible transient draft keeps agent mode");

                var archived = service.CreateChatForDocument(
                    "Archived draft",
                    "Word",
                    "archived-doc",
                    "Archive.docx",
                    "C:\\Docs\\Archive.docx");
                AssertEqual(ChatModes.Agent, archived.Mode, "document group draft defaults to agent mode");
                AssertEqual("archived-doc", archived.DocumentKey, "document group draft uses target document");
                AssertEqual("C:\\Docs\\Archive.docx", archived.DocumentPath, "document group draft keeps document path");
            });
        }

        private static void OfficeHostRowPersistsEmptyChat()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var created = service.CreatePersistentChat("Новый чат");
                AssertEqual(created.Id, service.TryLoadCurrentDocumentChat(created.Id).Id,
                    "current-document selection resolves an exact chat without catalog lookup");
                var reloaded = new ChatStore(paths).Load(
                    adapter.HostName, adapter.DocumentKey, created.Id);
                AssertTrue(reloaded != null, "empty host row chat is durable");
                AssertEqual(adapter.HostName, reloaded.Host, "host identity is retained");
                AssertEqual(adapter.DocumentKey, reloaded.DocumentKey, "document identity is retained");
                AssertEqual(created.Id, new ChatStore(paths).LoadOrCreateActive(
                    adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle).Id,
                    "new host chat remains active after reload");
            });
        }

        private static void DeleteSelectsRemainingProjection()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var first = store.Create(
                    adapter.HostName,
                    adapter.DocumentKey,
                    adapter.DocumentTitle,
                    "First");
                var second = store.Create(
                    adapter.HostName,
                    adapter.DocumentKey,
                    adapter.DocumentTitle,
                    "Second");
                service.SetActiveSession(second);

                var remaining = service.DeleteAndSelectNext(second.Id);

                AssertEqual(first.Id, remaining.Id, "remaining durable chat selected");
                AssertTrue(store.Load(second.Id) == null, "deleted chat is absent");
                AssertEqual(first.Id, store.LoadActiveSessionId(adapter.HostName, adapter.DocumentKey),
                    "active pointer follows remaining chat");

                var draft = service.DeleteAndSelectNext(first.Id);

                AssertTrue(!store.IsPersisted(draft), "last deletion creates only a transient draft");
                AssertEqual(0, store.ListHeaders(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle).Count,
                    "no durable projection remains");
                AssertEqual(draft.Id, service.GetActiveSession().Id, "transient replacement is active");
            });
        }

        private static void BackgroundSaveKeepsActiveChat()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var first = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "First");
                var second = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Second");
                service.SetActiveSession(second);

                first.Messages.Add(new ChatMessage { Role = "assistant", Content = "background result" });
                store.Save(first);
                service.NotifySaved(first);

                AssertEqual(second.Id, service.GetActiveSession().Id, "background save does not select chat");
                AssertEqual(second.Id, store.LoadActiveSessionId(adapter.HostName, adapter.DocumentKey), "stored active chat remains selected");
            });
        }

        private static void LoadingActiveChatRefreshesPersistedState()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var session = service.LoadSession(null);
                store.Save(session);
                service.NotifySaved(session);

                var changed = store.Load(session.Id);
                changed.Title = "Changed in another window";
                store.Save(changed);

                AssertEqual("Changed in another window", service.LoadSession(session.Id).Title,
                    "addressed active chat reloads its current persisted revision");
            });
        }

        private static void ChatSessionServiceFollowsOfficeDocumentSwitches()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var current = store.Create("Excel", "doc", "MockWorkbook.xlsx", "Current chat");
                current.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                current.Messages.Add(new ChatMessage { Role = "user", Content = "current history A" });
                store.Save(current);
                var currentSecond = store.Create("Excel", "doc", "MockWorkbook.xlsx", "Current chat 2");
                currentSecond.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                currentSecond.Messages.Add(new ChatMessage { Role = "user", Content = "current history B" });
                store.Save(currentSecond);
                var forecast = store.Create("Excel", "forecast-doc", "Forecast.xlsx", "Forecast chat");
                forecast.DocumentPath = "C:\\Demo\\Forecast.xlsx";
                forecast.Messages.Add(new ChatMessage { Role = "user", Content = "forecast history A" });
                store.Save(forecast);
                var forecastSecond = store.Create("Excel", "forecast-doc", "Forecast.xlsx", "Forecast chat 2");
                forecastSecond.DocumentPath = "C:\\Demo\\Forecast.xlsx";
                forecastSecond.Messages.Add(new ChatMessage { Role = "user", Content = "forecast history B" });
                store.Save(forecastSecond);
                store.SaveActiveSessionId("Excel", "doc", current.Id);
                store.SaveActiveSessionId("Excel", "forecast-doc", forecast.Id);

                var service = new ChatSessionService(adapter, ConversationStore(store));
                AssertEqual(current.Id, service.LoadSession(null).Id, "current document active chat");
                AssertEqual("current history A", service.GetActiveSession().Messages[0].Content,
                    "current document history is isolated");

                AssertEqual(currentSecond.Id, service.LoadSession(currentSecond.Id).Id,
                    "second chat in current document selected");
                AssertEqual("current history B", service.GetActiveSession().Messages[0].Content,
                    "second current-document history is isolated");

                AssertEqual(forecast.Id, service.LoadSession(forecast.Id).Id, "archived chat selected");
                AssertEqual(forecast.Id, service.GetActiveSessionForOfficeState().Id,
                    "poll preserves intentional archive selection");

                adapter.ActivateDocument("forecast-doc");
                AssertEqual(forecast.Id, service.GetActiveSessionForOfficeState().Id,
                    "external Office switch restores target chat");
                AssertEqual(forecastSecond.Id, service.LoadSession(forecastSecond.Id).Id,
                    "second chat in forecast document selected");
                AssertEqual("forecast history B", service.GetActiveSession().Messages[0].Content,
                    "second forecast history is isolated");

                adapter.ActivateDocument("doc");
                var restored = service.GetActiveSessionForOfficeState();
                AssertEqual(currentSecond.Id, restored.Id, "returning to document restores its last active chat");
                AssertEqual("current history B", restored.Messages[0].Content,
                    "returning to document restores only its selected history");
                AssertTrue(service.IsCurrentDocument(restored), "restored chat belongs to current document");

                adapter.ActivateDocument("forecast-doc");
                var forecastRestored = service.GetActiveSessionForOfficeState();
                AssertEqual(forecastSecond.Id, forecastRestored.Id,
                    "returning to forecast restores its last active chat");
                AssertEqual("forecast history B", forecastRestored.Messages[0].Content,
                    "returning to forecast restores only its selected history");
            });
        }

        private static void LegacyChatRebindsByFullPath()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var current = store.Create("Excel", "doc", "MockWorkbook.xlsx", "Current chat");
                current.DocumentPath = "C:\\Demo\\MockWorkbook.xlsx";
                store.Save(current);
                var legacy = store.Create("Excel", "Excel:DocumentId:lost-id", "Forecast.xlsx", "Legacy forecast chat");
                legacy.DocumentPath = "C:\\Demo\\Forecast.xlsx";
                store.Save(legacy);
                var unrelated = store.Create("Excel", "Excel:DocumentId:lost-id", "Other.xlsx", "Unrelated chat in a legacy group");
                unrelated.DocumentPath = "C:\\Demo\\Other.xlsx";
                store.Save(unrelated);
                var duplicate = store.Create("Excel", "Excel:DocumentId:another-lost-id", "Forecast.xlsx", "Another legacy chat");
                duplicate.DocumentPath = "C:\\Demo\\Forecast.xlsx";
                store.Save(duplicate);
                var pathOnly = store.Create("Excel", "Excel:Path:C:\\Demo\\Forecast.xlsx", "Forecast.xlsx", "Path-only legacy chat");

                var service = new ChatSessionService(adapter, ConversationStore(store));
                service.LoadSession(null);
                service.LoadSession(legacy.Id);
                var catalog = (IOfficeDocumentCatalog)adapter;
                var target = catalog.ListOpenDocuments().First(document =>
                    DocumentOpenService.SamePath(document.Path, legacy.DocumentPath));
                AssertTrue(catalog.ActivateDocument(target.DocumentKey), "matching open document activated by path");

                service.RunOwnershipProvider = id => string.Equals(id, legacy.Id, StringComparison.OrdinalIgnoreCase);
                var deferred = service.LoadSession(legacy.Id);
                AssertEqual("Excel:DocumentId:lost-id", deferred.DocumentKey,
                    "running legacy chat postpones identity reconciliation");
                service.RunOwnershipProvider = null;
                var rebound = service.GetActiveSessionForOfficeState();
                AssertEqual("forecast-doc", adapter.DocumentKey, "matching document is active");
                AssertEqual(legacy.Id, rebound.Id, "requested legacy chat remains active");
                var legacyRemainder = store.List("Excel", "Excel:DocumentId:lost-id", "Other.xlsx");
                AssertEqual(1, legacyRemainder.Count, "different-path chat remains in mixed legacy group");
                AssertEqual(unrelated.Id, legacyRemainder[0].Id, "unrelated chat identity is preserved");
                AssertEqual(0, store.List("Excel", "Excel:DocumentId:another-lost-id", "Forecast.xlsx").Count,
                    "duplicate legacy document identity removed after migration");
                AssertEqual(legacy.Id, store.Load("Excel", "forecast-doc", legacy.Id).Id,
                    "legacy chat moved to live document identity");
                AssertEqual(duplicate.Id, store.Load("Excel", "forecast-doc", duplicate.Id).Id,
                    "all same-path chats merged into live document identity");
                AssertEqual(pathOnly.Id, store.Load("Excel", "forecast-doc", pathOnly.Id).Id,
                    "document-key path fallback is migrated without stored metadata");
            });
        }

        private static void UnsavedDocumentChatsStayIsolated()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.DocumentKeyValue = "Excel:Runtime:unsaved-a";
                adapter.RuntimeDocumentKeyValue = "Excel:Runtime:unsaved-a";
                adapter.DocumentPathValue = string.Empty;
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store));
                var first = service.LoadSession(null);
                first.Messages.Add(new ChatMessage { Role = "user", Content = "first unsaved workbook" });
                store.Save(first);
                service.NotifySaved(first);

                adapter.DocumentKeyValue = "Excel:Runtime:unsaved-b";
                adapter.RuntimeDocumentKeyValue = "Excel:Runtime:unsaved-b";
                var second = service.GetActiveSessionForOfficeState();
                AssertTrue(first.Id != second.Id, "new unsaved workbook gets its own transient chat");
                AssertEqual("Excel:Runtime:unsaved-b", second.DocumentKey, "second unsaved workbook identity");

                adapter.DocumentKeyValue = "Excel:Runtime:unsaved-a";
                adapter.RuntimeDocumentKeyValue = "Excel:Runtime:unsaved-a";
                AssertEqual(first.Id, service.GetActiveSessionForOfficeState().Id,
                    "returning to the first unsaved workbook restores its chat");
            });
        }

        private static void InterruptedRunIsRecoveredAsUnknown()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var session = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Interrupted");
                session.LastRun = new ChatRunRecord
                {
                    RunId = "old-run",
                    RuntimeId = "old-runtime",
                    Status = "running",
                    Phase = "executing",
                    StartedUtc = DateTime.UtcNow
                };
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    RunId = "earlier-run",
                    Activity = new ChatActivity
                    {
                        RunId = "earlier-run",
                        Status = "running",
                        ExecutionStatus = "executing",
                        PendingId = "old-pending"
                    }
                });
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    RunId = "old-run",
                    Activity = new ChatActivity
                    {
                        RunId = "old-run",
                        ToolCallId = "call-without-result",
                        ToolId = "excel.inspect",
                        Status = "running",
                        ExecutionStatus = "executing",
                        PendingId = "pending"
                    }
                });
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Running tool.",
                    ProtocolMessage = true,
                    ToolCallId = "call-without-result",
                    CreatedUtc = session.LastRun.StartedUtc.AddMilliseconds(1),
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall { Id = "call-without-result", Name = "excel.read_range" }
                    }
                });
                store.Save(session);

                var registry = new ChatRunRegistry(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store))
                {
                    RunOwnershipProvider = registry.IsExternallyRunning,
                    RunRecoveryLeaseProvider = value => registry.Start(value.Id, "recovery", value)
                };
                service.ReconcileInterruptedRuns("new-runtime");

                var recovered = store.Load(session.Id);
                AssertTrue(!registry.IsRunning(session.Id), "recovery lease released");
                AssertEqual("interrupted", recovered.LastRun.Status, "interrupted run status");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.ExecutionStatus == "interrupted_unknown"),
                    "uncertain running activity stored");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.PendingId == "old-pending" &&
                    message.Activity.ExecutionStatus == "executing"),
                    "activity from another run is not rewritten");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.Kind == "diagnostic" &&
                    message.Activity.ExecutionStatus == "interrupted_unknown"),
                    "restart diagnostic stored");
                AssertTrue(recovered.Messages.Any(message =>
                    message.ToolCallId == "call-without-result" && message.ExcludeFromModelContext),
                    "dangling native tool call excluded from replay");
            });
        }

        private static void InterruptedRunAtSavedBoundaryPreservesProtocol()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var session = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Interrupted safely");
                session.LastRun = new ChatRunRecord
                {
                    RunId = "safe-run",
                    RuntimeId = "old-runtime",
                    Status = "running",
                    Phase = "tool_result",
                    StartedUtc = DateTime.UtcNow
                };
                var call = new AgentToolCall
                {
                    Id = "safe-call", Name = "excel.inspect"
                };
                var callMessage = AgentJsonProtocol.CreateToolCallMessage(call, string.Empty, null,
                    ToolResultRoles.User, FixtureCallOrigin("safe-step"));
                callMessage.RunId = "safe-run";
                session.Messages.Add(callMessage);
                var resultMessage = AgentJsonProtocol.CreateToolResultMessage(
                    new ToolInvocation { ToolCallId = call.Id, ToolId = call.Name },
                    RuntimeToolResult.Ok("Read"), ToolResultRoles.User);
                resultMessage.RunId = "safe-run";
                session.Messages.Add(resultMessage);
                store.Save(session);

                var registry = new ChatRunRegistry(paths);
                var service = new ChatSessionService(adapter, ConversationStore(store))
                {
                    RunOwnershipProvider = registry.IsExternallyRunning,
                    RunRecoveryLeaseProvider = value => registry.Start(value.Id, "recovery", value)
                };
                service.ReconcileInterruptedRuns("new-runtime");

                var recovered = store.Load(session.Id);
                AssertTrue(recovered.Messages.Where(message => message.ProtocolMessage)
                    .All(message => !message.ExcludeFromModelContext),
                    "completed tool exchange remains replayable");
                var savedResult = recovered.Messages.Single(message => message.ToolCallId == call.Id &&
                    message.Role == ToolResultRoles.User);
                AssertEqual(1, savedResult.ToolResultProtocolVersion, "saved boundary retains the Tool Result v1 marker");
                AssertEqual(resultMessage.Content, savedResult.Content, "recovery preserves the completed result envelope");
                AssertTrue(recovered.Messages.Any(message =>
                    message.Activity != null && message.Activity.Kind == "diagnostic" &&
                    message.Activity.ExecutionStatus == "interrupted"),
                    "safe persisted boundary is not reported as unknown effect");
            });
        }
    }
}
