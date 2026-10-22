using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;
using RNAssistant.Runtime;
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
                var protectedOpen = workspaces.Open(root, false, false);
                AssertTrue(protectedOpen.ReadOnly && protectedOpen.Mounts.Single().ReadOnly,
                    "an existing manifest cannot override an explicit read-only open");
                AssertEqual(opened.WorkspaceId, protectedOpen.WorkspaceId, "read-only open preserves workspace identity");
                var files = new WorkspaceFileService(paths);
                File.WriteAllText(Path.Combine(root, "protected.txt"), "original");
                var observed = files.ReadText(protectedOpen, "protected.txt");
                var writeRejected = false;
                try { files.ReplaceText(protectedOpen, "protected.txt", observed.Reference, "unwanted"); }
                catch (WorkspaceFileException error) { writeRejected = error.Code == "read_only_mount"; }
                AssertTrue(writeRejected, "read-only file owner rejects writes even with exact read evidence");
                AssertEqual("original", File.ReadAllText(Path.Combine(root, "protected.txt")), "read-only source is intact");
                AssertTrue(!workspaces.Open(root, false, true).ReadOnly, "read-only permission is per open, not persisted");
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
                var overlapping = files.CreateText(workspace, "overlap.txt", "aaaa");
                var dispatched = false;
                try
                {
                    files.PatchExact(workspace, "overlap.txt", overlapping.Reference, "aaa", "b", () => dispatched = true);
                    throw new Exception("Overlapping anchor was accepted.");
                }
                catch (WorkspaceFileException ex)
                { AssertEqual("patch_ambiguous", ex.Code, "distinct overlapping starts are ambiguous"); }
                AssertTrue(!dispatched, "ambiguous patch never reaches the effect boundary");
                AssertEqual("aaaa", File.ReadAllText(Path.Combine(workspace.RootPath, "overlap.txt")), "ambiguous patch preserves bytes");
                var mixed = files.CreateText(workspace, "mixed.txt", "first\r\nTARGET\nlast\r尾😀");
                var excerpt = files.ReadTextLines(workspace, "mixed.txt", 2, 1);
                AssertEqual("TARGET\n", excerpt.Text, "line slicing preserves exact terminators");
                AssertTrue(!excerpt.Evidence.Complete && excerpt.Evidence.Coverage.Kind == ResourceCoverageKinds.CharacterRange,
                    "line selection cannot authorize whole replacement");
                var mixedPatch = files.PatchExact(workspace, "mixed.txt", excerpt.Reference, "TARGET", "UPDATED");
                AssertEqual("first\r\nUPDATED\nlast\r尾😀", mixedPatch.Text, "patch preserves untouched mixed newlines and Unicode");
                AssertTrue(mixedPatch.Evidence != null && mixedPatch.Evidence.Resource.Revision == mixedPatch.Reference.Revision,
                    "verified mutation supplies exact after-source evidence");
                AssertEqual("first\r\nTARGET\nlast\r尾😀", files.ReadExactText(workspace, "mixed.txt", mixed.Reference).Text,
                    "old snapshots are not replaced by latest");
                files.FaultPoint = stage => { if (stage == "before-replace-check") File.WriteAllText(Path.Combine(workspace.RootPath, "mixed.txt"), "foreign"); };
                dispatched = false;
                try
                {
                    files.ReplaceText(workspace, "mixed.txt", mixedPatch.Reference, "candidate", () => dispatched = true);
                    throw new Exception("Late precondition conflict accepted.");
                }
                catch (WorkspaceFileException ex)
                { AssertEqual("target_conflict", ex.Code, "late proven pre-dispatch conflict is not unknown"); }
                finally { files.FaultPoint = null; }
                AssertTrue(!dispatched, "precondition failure precedes possible target effect");
                AssertEqual("foreign", files.ReadText(workspace, "mixed.txt").Text, "no unresolved attempt or blind retry after conflict");
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
                AssertEqual("const value = 1;\r\n", files.ReadExactText(workspace, "dashboard/app.js", first.Reference).Text,
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
                AssertTrue(!page.Complete && page.ScannedEntries.Value <= 5000,
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

        private static void WorkspaceFilesReadCharacterWindows()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var body = "A\uD83D\uDE00" + new string('x', 20000) + "END";
                files.CreateText(workspace, "long.txt", body);
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                AssertEqual("read_too_large", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.Read("long.txt", 1, 1, maximumCharacters: 16000)).Code,
                    "one long line cannot fit a selected low context budget");
                var start = 16003;
                var selected = provider.ReadCharacters("long.txt", start, 16000);
                AssertTrue(selected.Result.Resource.ByteLength <= WorkspaceFileService.MaximumTextBytes,
                    "a bounded read still reports the source length");
                AssertEqual(new string('x', 4000) + "END", selected.Result.Text,
                    "character window reads the exact tail of a long line");
                var projection = ResourceReadProjection.FromCharacters(selected.Result,
                    "long.txt", "file", "workspace", start, 16000);
                AssertTrue(projection.SelectionComplete == true && !projection.Complete &&
                    projection.Coverage.Start == start && projection.Coverage.End == body.Length &&
                    !selected.Evidence.Single().Complete,
                    "EOF completes the requested window but not whole-source evidence");
                AssertEqual("resource_chars_invalid", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.ReadCharacters("long.txt", 1, 1)).Code,
                    "character window cannot end inside a surrogate pair");
                AssertEqual("resource_chars_invalid", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.ReadCharacters("long.txt", 2, 1)).Code,
                    "character window cannot start inside a surrogate pair");
                var observed = new Dictionary<string, ResourceEvidence>();
                var handler = new WorkspaceResourceHandler(provider, null, () => null, observed, false, null, 16000);
                var execution = new ToolRuntimeFixture().Context(toolId: "common.resources_read");
                var whole = handler.ExecuteAsync(new ToolHandlerContext(execution,
                    new Dictionary<string, object> { ["target"] = "long.txt" }), CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual("read_too_large", (string)JObject.Parse(whole.Result.DataJson)["code"],
                    "model tool refuses whole delivery before a bounded character recovery");
                var result = handler.ExecuteAsync(new ToolHandlerContext(execution,
                    new Dictionary<string, object> { ["target"] = "long.txt",
                        ["startChar"] = start, ["charCount"] = 16000 }), CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Ok, result.Result.Status,
                    "model resource handler accepts a bounded character window");
                var visible = ModelResultProjection.ProjectResourceData("common.resources_read",
                    JObject.Parse(result.Result.DataJson));
                AssertTrue((bool)visible["selectionComplete"] && !(bool)visible["complete"] &&
                    (int)visible["startChar"] == start && (int)visible["requestedCharacters"] == 16000 &&
                    (int)visible["coverage"]["end"] == body.Length &&
                    observed["long.txt"].Coverage.Kind == ResourceCoverageKinds.CharacterRange,
                    "model sees requested/actual coverage while runtime retains partial evidence");
                var mixed = handler.ExecuteAsync(new ToolHandlerContext(execution,
                    new Dictionary<string, object> { ["target"] = "long.txt", ["startChar"] = 0,
                        ["charCount"] = 10, ["startLine"] = 1, ["lineCount"] = 1 }), CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Error, mixed.Result.Status,
                    "line and character selectors cannot be combined");
                AssertEqual("resource_chars_invalid", (string)JObject.Parse(mixed.Result.DataJson)["code"],
                    "mixed selectors fail with a closed error code");

                files.CreateText(workspace, "wide.txt", new string('z', 40000));
                var expanded = new WorkspaceResourceHandler(provider, null, () => null,
                    new Dictionary<string, ResourceEvidence>(), false, null, 50000);
                var expandedRead = expanded.ExecuteAsync(new ToolHandlerContext(execution,
                    new Dictionary<string, object> { ["target"] = "wide.txt",
                        ["startChar"] = 0, ["charCount"] = 40000 }), CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Ok, expandedRead.Result.Status,
                    "the model file read can exceed the former 16000-character ceiling");
                var expandedData = JObject.Parse(expandedRead.Result.DataJson);
                AssertTrue((bool)expandedData["selectionComplete"] && !(bool)expandedData["complete"] &&
                    (int)expandedData["returnedCharacters"] == 40000 &&
                    (int)expandedData["coverage"]["end"] == 40000,
                    "a larger model-budget selection retains exact partial coverage");
                var expandedWhole = expanded.ExecuteAsync(new ToolHandlerContext(execution,
                    new Dictionary<string, object> { ["target"] = "wide.txt" }), CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Ok, expandedWhole.Result.Status,
                    "the whole-file read also follows the configured model budget");
                AssertTrue((bool)JObject.Parse(expandedWhole.Result.DataJson)["complete"],
                    "a fitting whole file keeps complete exact evidence");

                files.CreateText(workspace, "breaks.txt", "\uFEFFa\rb\nc\r\nd\r");
                var lines = new[] { "\uFEFFa\r", "b\n", "c\r\n", "d\r", "" };
                for (var i = 0; i < lines.Length; i++)
                    AssertEqual(lines[i], provider.Read("breaks.txt", i + 1, 1).Result.Text,
                        "streamed line window preserves mixed breaks and terminal empty line " + i);
                AssertEqual("resource_lines_invalid", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.Read("breaks.txt", lines.Length + 1, 1)).Code,
                    "streamed line read rejects a start beyond the final empty line");

                var largePath = Path.Combine(workspace.RootPath, "large.txt");
                const int longLineLength = 16 * 1024 * 1024;
                const string prefix = "\uFEFFone\r\n";
                const string suffix = "\n\uD83D\uDE00DONE";
                using (var writer = new StreamWriter(largePath, false, new UTF8Encoding(false, true)))
                {
                    writer.Write(prefix);
                    var block = new string('x', 8192);
                    for (var i = 0; i < longLineLength / block.Length; i++) writer.Write(block);
                    writer.Write(suffix);
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var allocations = GC.GetAllocatedBytesForCurrentThread();
                var tailOffset = prefix.Length + longLineLength + 1;
                var large = provider.ReadCharacters("large.txt", tailOffset, 16000);
                AssertTrue(GC.GetAllocatedBytesForCurrentThread() - allocations < 8L * 1024 * 1024,
                    "large model text window does not allocate the 16-MiB source body");
                AssertEqual("\uD83D\uDE00DONE", large.Result.Text, "character window reaches beyond the whole-read bound");
                AssertEqual(tailOffset + large.Result.Text.Length, large.Result.TotalCharacters,
                    "large read counts the full UTF-16 source");
                AssertEqual(new FileInfo(largePath).Length, large.Result.Resource.ByteLength.Value,
                    "descriptor reports source bytes rather than excerpt bytes");
                AssertTrue(!large.Result.Complete && !large.Evidence.Single().Complete &&
                    large.Evidence.Single().Coverage.Start == tailOffset &&
                    large.Evidence.Single().Coverage.End == large.Result.TotalCharacters,
                    "large read retains only exact partial text evidence");
                AssertTrue(large.Result.Resource.ByteLength > WorkspaceFileService.MaximumTextBytes,
                    "the descriptor reports the large source size, not the excerpt size");
                var modelLarge = handler.ExecuteAsync(new ToolHandlerContext(execution,
                    new Dictionary<string, object> { ["target"] = "large.txt",
                        ["startChar"] = tailOffset, ["charCount"] = 16000 }), CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Ok, modelLarge.Result.Status,
                    "the model may still read a bounded large-source excerpt");
                AssertTrue(!modelLarge.Result.Message.Contains("files.patch is unavailable"),
                    "the model read permits an observed large-source patch");
                var selectedLine = provider.Read("large.txt", 3, 1);
                AssertEqual(large.Result.Text, selectedLine.Result.Text, "line window reaches beyond one MiB");
                AssertEqual(prefix.Length + longLineLength + 1, selectedLine.Result.Offset,
                    "line range accounts for BOM and CRLF");
                AssertEqual(prefix, provider.Read("large.txt", 1, 1).Result.Text,
                    "streaming line read preserves BOM and CRLF");
                AssertEqual("read_too_large", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.Read("large.txt", 2, 1, maximumCharacters: 16000)).Code,
                    "selected line over the configured context budget is refused without a whole-body allocation");
                AssertEqual("resource_chars_invalid", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.ReadCharacters("large.txt", tailOffset, 1)).Code,
                    "large character window cannot end inside a surrogate pair");
                AssertEqual("resource_chars_invalid", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.ReadCharacters("large.txt", tailOffset + 1, 1)).Code,
                    "large character window cannot start inside a surrogate pair");
                var current = files.CaptureFile(workspace, "large.txt", WorkspaceFileService.MaximumWindowTextSourceBytes);
                AssertEqual(EvidenceState.Current, new EvidenceStateReducer().Reduce(large.Evidence.Single(),
                    files.CaptureAuthority(large.Evidence)).State,
                    "revalidating the raw source preserves exact partial evidence");
                AssertEqual(large.Result.Resource.Reference.Revision, current.Reference.Revision,
                    "revalidation reuses the same exact revision");
                AssertEqual("file_too_large", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.Read("large.txt")).Code, "whole text remains bounded to one MiB");
                AssertEqual(EvidenceState.Current, new EvidenceStateReducer().Reduce(large.Evidence.Single(),
                    files.CaptureAuthority(large.Evidence)).State,
                    "whole-read refusal does not erase an exact large-source excerpt");
                using (var edited = new FileStream(largePath, FileMode.Open, FileAccess.Write))
                { edited.Position = Encoding.UTF8.GetByteCount(prefix); edited.WriteByte((byte)'y'); }
                files.CaptureFile(workspace, "large.txt", WorkspaceFileService.MaximumWindowTextSourceBytes);
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(large.Evidence.Single(),
                    files.CaptureAuthority(large.Evidence)).State,
                    "large source drift supersedes the old excerpt");
                var fresh = provider.ReadCharacters("large.txt", tailOffset, 16000);
                AssertEqual("file_too_large", RuntimeThrows<WorkspaceFileException>(() =>
                    files.CaptureFile(workspace, "large.txt", WorkspaceFileService.MaximumTextBytes,
                        markUnknownOnSizeRefusal: true)).Code,
                    "an oversized context refresh refuses the source");
                AssertEqual(EvidenceState.Unknown, new EvidenceStateReducer().Reduce(fresh.Evidence.Single(),
                    files.CaptureAuthority(fresh.Evidence)).State,
                    "an oversized context refresh invalidates unverified prior evidence");
                using (var invalid = new FileStream(largePath, FileMode.Open, FileAccess.Write))
                { invalid.Position = invalid.Length - 1; invalid.WriteByte(255); }
                AssertEqual("encoding_ambiguous", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.ReadCharacters("large.txt", 0, 1)).Code,
                    "invalid UTF-8 after the selected region cannot produce text evidence");
            });
        }

        private static void WorkspaceFilesStreamLargePatch()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                var prefix = new string('x', 81919);
                var body = prefix + "TAG_OLD" + new string('y', 2 * 1024 * 1024) + "\r\n尾\uD83D\uDE00\n";
                var path = Path.Combine(workspace.RootPath, "large.txt");
                File.WriteAllText(path, body, new UTF8Encoding(false, true));
                var observed = provider.ReadCharacters("large.txt", prefix.Length - 4, 128);
                var refusedDispatch = false;
                AssertEqual("patch_not_found", RuntimeThrows<WorkspaceFileException>(() =>
                    files.PatchExact(workspace, "large.txt", observed.Result.Resource.Reference,
                        "NOT_PRESENT", "x", () => refusedDispatch = true)).Code,
                    "an absent large-source anchor is refused before dispatch");
                AssertEqual("invalid_patch", RuntimeThrows<WorkspaceFileException>(() =>
                    files.PatchExact(workspace, "large.txt", observed.Result.Resource.Reference,
                        "TAG_OLD", new string('z', WorkspaceFileService.MaximumTextBytes + 1),
                        () => refusedDispatch = true)).Code,
                    "replacement bytes remain independently bounded");
                AssertTrue(!refusedDispatch, "invalid large patch arguments never dispatch");
                var dispatched = false;
                var changed = files.PatchExact(workspace, "large.txt", observed.Result.Resource.Reference,
                    "TAG_OLD", "TAG_新\uD83D\uDE00", () => dispatched = true);
                AssertTrue(dispatched && changed.AuthorityCommit != null && changed.Text == null &&
                    changed.Reference.Revision != observed.Result.Resource.Reference.Revision,
                    "streamed patch publishes a new exact revision without a whole text body");
                AssertEqual(body.Replace("TAG_OLD", "TAG_新\uD83D\uDE00"), File.ReadAllText(path),
                    "unique anchor crossing the stream buffer is changed without touching other bytes");
                AssertEqual((long)Encoding.UTF8.GetByteCount(body.Replace("TAG_OLD", "TAG_新\uD83D\uDE00")),
                    changed.SourceByteLength.Value, "read-back reports the exact UTF-8 result length");
                var after = files.SelectPatchResultRange(workspace, changed, prefix.Length - 4, 128);
                AssertTrue(after.Text.Contains("TAG_新\uD83D\uDE00") && !after.Evidence.Complete &&
                    after.AuthorityCommit.CommitId == changed.AuthorityCommit.CommitId &&
                    after.Evidence.Coverage.Kind == ResourceCoverageKinds.CharacterRange,
                    "verified large patch delivers only an exact after-source excerpt");
                AssertEqual(EvidenceState.Current, new EvidenceStateReducer().Reduce(after.Evidence,
                    files.CaptureAuthority(new[] { after.Evidence })).State,
                    "the partial after-source belongs to the published current revision");
                var staleDispatched = false;
                AssertEqual("target_conflict", RuntimeThrows<WorkspaceFileException>(() =>
                    files.PatchExact(workspace, "large.txt", observed.Result.Resource.Reference,
                        "TAG_OLD", "STALE", () => staleDispatched = true)).Code,
                    "a prior exact revision cannot patch the new source");
                AssertTrue(!staleDispatched, "stale patch fails before dispatch");
                var current = provider.ReadCharacters("large.txt", prefix.Length - 4, 128);
                var noopDispatched = false;
                var same = files.PatchExact(workspace, "large.txt", current.Result.Resource.Reference,
                    "TAG_新\uD83D\uDE00", "TAG_新\uD83D\uDE00", () => noopDispatched = true);
                AssertTrue(!noopDispatched && same.Reference.Revision == current.Result.Resource.Reference.Revision,
                    "a large no-op patch leaves the revision and file intact");

                var duplicatePath = Path.Combine(workspace.RootPath, "duplicate.txt");
                File.WriteAllText(duplicatePath, new string('q', 2 * 1024 * 1024) + "aaaaa");
                var duplicate = provider.ReadCharacters("duplicate.txt", 2 * 1024 * 1024, 128);
                var duplicateDispatched = false;
                AssertEqual("patch_ambiguous", RuntimeThrows<WorkspaceFileException>(() =>
                    files.PatchExact(workspace, "duplicate.txt", duplicate.Result.Resource.Reference,
                        "aaa", "b", () => duplicateDispatched = true)).Code,
                    "overlapping anchor starts are ambiguous across the complete large source");
                AssertTrue(!duplicateDispatched, "ambiguous large patch never dispatches");
                using (var drift = new FileStream(duplicatePath, FileMode.Open, FileAccess.Write))
                    drift.WriteByte((byte)'Z');
                AssertEqual("target_conflict", RuntimeThrows<WorkspaceFileException>(() =>
                    files.PatchExact(workspace, "duplicate.txt", duplicate.Result.Resource.Reference,
                        "aaaaa", "b", () => duplicateDispatched = true)).Code,
                    "external drift is rejected against the accepted exact revision");
                AssertTrue(!duplicateDispatched, "drifted large patch never dispatches");

                var unknownPath = Path.Combine(workspace.RootPath, "unknown.txt");
                File.WriteAllText(unknownPath, new string('m', 2 * 1024 * 1024) + "REPLACE_ME");
                var unknownRead = provider.ReadCharacters("unknown.txt", 2 * 1024 * 1024, 128);
                files.FaultPoint = stage => { if (stage == "after-dispatch") throw new IOException("interrupted patch"); };
                AssertEqual("effect_unknown", RuntimeThrows<WorkspaceFileException>(() =>
                    files.PatchExact(workspace, "unknown.txt", unknownRead.Result.Resource.Reference,
                        "REPLACE_ME", "REPLACED")).Code,
                    "interrupted large patch reports an unknown effect");
                var restarted = new WorkspaceFileService(paths);
                AssertEqual("unresolved_previous_effect", RuntimeThrows<WorkspaceFileException>(() =>
                    restarted.ReadTextCharacters(workspace, "unknown.txt", 2 * 1024 * 1024, 128)).Code,
                    "restart blocks reads and repeats until explicit reconciliation");
                var recovered = restarted.ReconcileUncertain(workspace, "unknown.txt");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, recovered.Outcome,
                    "recovery records current bytes without claiming causality or replaying the patch");
                AssertEqual(null, recovered.Current.Text,
                    "large recovery does not materialize a whole text body");
                AssertEqual("REPLACED", new WorkspaceFileResourceProvider(restarted, workspace)
                    .ReadCharacters("unknown.txt", 2 * 1024 * 1024, 128).Result.Text,
                    "the reconciled large source is readable in a bounded window");
                AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count,
                    "explicit recovery resolves the large patch journal attempt");

                var mismatchPath = Path.Combine(workspace.RootPath, "mismatch.txt");
                File.WriteAllText(mismatchPath, new string('m', 2 * 1024 * 1024) + "SOURCE");
                var mismatchRead = new WorkspaceFileResourceProvider(restarted, workspace)
                    .ReadCharacters("mismatch.txt", 2 * 1024 * 1024, 128);
                restarted.FaultPoint = stage =>
                {
                    if (stage != "after-dispatch") return;
                    using (var output = new FileStream(mismatchPath, FileMode.Open, FileAccess.Write))
                        output.WriteByte((byte)'Z');
                };
                AssertEqual("effect_unknown", RuntimeThrows<WorkspaceFileException>(() =>
                    restarted.PatchExact(workspace, "mismatch.txt", mismatchRead.Result.Resource.Reference,
                        "SOURCE", "TARGET")).Code,
                    "a mismatched streamed read-back never reports verified success");
                var mismatchRecovery = new WorkspaceFileService(paths).ReconcileUncertain(workspace, "mismatch.txt");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, mismatchRecovery.Outcome,
                    "read-back mismatch is reconciled without restoring or replaying bytes");
                AssertTrue(File.ReadAllText(mismatchPath).StartsWith("Z", StringComparison.Ordinal),
                    "recovery preserves the actual post-interruption file for inspection");
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
                try { files.ReadExactText(workspace, "app.js", original.Reference); }
                catch (WorkspaceFileException ex) { wrongRef = ex.Code == "invalid_resource_ref"; }
                AssertTrue(wrongRef, "historical reference cannot cross file identity");
                AssertEqual("safe", files.ReadExactText(workspace, "app.js", other.Reference).Text, "exact read");
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

        private static void WorkspaceFilesRetainExactProviderSnapshots()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                const string original = "\uFEFFconst value = 1;\r\n";
                var created = files.CreateText(workspace, "app.js", original);
                var livePath = Path.Combine(workspace.RootPath, "app.js");
                File.WriteAllText(livePath, "changed externally");
                var current = provider.Read("app.js");
                var selectedLines = provider.Read("app.js", 1, 2);
                var lineProjection = ResourceReadProjection.FromLines(selectedLines.Result,
                    "app.js", "file", "workspace", 1, 2);
                AssertEqual(1, lineProjection.StartLine.Value, "workspace selection retains requested start");
                AssertEqual(2, lineProjection.RequestedLines.Value, "workspace selection retains requested count");
                AssertTrue(lineProjection.SelectionComplete == true && !lineProjection.Complete &&
                    lineProjection.Coverage.Kind == ResourceCoverageKinds.CharacterRange &&
                    lineProjection.Coverage.End == selectedLines.Result.Text.Length,
                    "workspace line selection has exact coverage without whole-source authority");
                var exact = provider.ReadExact("app.js", created.Reference);
                exact.RequireCompleteExactText();
                AssertEqual(original, exact.Result.Text, "retained text preserves BOM and CRLF after external edit");
                AssertTrue(!exact.Result.Resource.Mutable, "retained descriptor does not advertise mutation");
                AssertEqual(created.ContentSha256, exact.Result.CompleteViewPayload.Sha256,
                    "exact payload identifies original bytes");
                var frozen = provider.CaptureAuthority(exact.Evidence);
                var reducer = new EvidenceStateReducer();
                AssertEqual(EvidenceState.Superseded, reducer.Reduce(exact.Evidence.Single(), frozen).State,
                    "retained read remains stale against refreshed authority");
                AssertEqual(EvidenceState.Current, reducer.Reduce(current.Evidence.Single(), frozen).State,
                    "retained read does not republish the historical head");

                var authority = new ResourceAuthorityStore(paths);
                var unpublished = new ResourceRef(created.Reference.Uri, "r_" + Guid.NewGuid().ToString("N"));
                var scope = exact.Evidence.Single().ScopeId;
                authority.RegisterRevision(scope, new ResourceRevisionMetadata(unpublished,
                    created.ContentSha256, exact.Result.CompleteViewPayload));
                authority.RegisterView(scope, new ResourceRevisionView(unpublished, "text",
                    created.ContentSha256, exact.Result.CompleteViewPayload, ResourceCoverage.Whole()));
                var rejected = false;
                try { provider.ReadExact("app.js", unpublished); }
                catch (WorkspaceFileException ex) { rejected = ex.Code == "snapshot_unavailable"; }
                AssertTrue(rejected, "registered but unpublished revision is not readable evidence");

                var blob = new ChatBlobStore(paths).PathFor(created.ContentSha256);
                File.WriteAllBytes(blob, new byte[] { 1, 2, 3 });
                foreach (var missing in new[] { false, true })
                {
                    if (missing) File.Delete(blob);
                    rejected = false;
                    try { provider.ReadExact("app.js", created.Reference); }
                    catch (WorkspaceFileException ex) { rejected = ex.Code == "snapshot_unavailable"; }
                    AssertTrue(rejected, "missing/corrupt exact payload never falls back to the live file");
                }
                AssertEqual("changed externally", File.ReadAllText(livePath), "retained reads leave live bytes untouched");

                var empty = files.CreateText(workspace, "empty.css", string.Empty);
                File.Delete(Path.Combine(workspace.RootPath, "empty.css"));
                try { provider.Read("empty.css"); }
                catch (FileNotFoundException) { }
                var deleted = provider.ReadExact("empty.css", empty.Reference);
                deleted.RequireCompleteExactText();
                AssertEqual(string.Empty, deleted.Result.Text, "empty retained view remains readable after deletion");
                AssertEqual(EvidenceState.Unavailable,
                    reducer.Reduce(deleted.Evidence.Single(), provider.CaptureAuthority(deleted.Evidence)).State,
                    "retained bytes do not make the deleted source current");
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

        private static void WorkspaceFilesRejectCorruptPreimages()
        {
            foreach (var operation in new[] { "move", "delete", "restore" })
            WithTempPaths(paths =>
            {
                const string body = "Retained file source";
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                files.CreateText(workspace, "source.txt", body);
                var source = files.ReadText(workspace, "source.txt");
                if (operation == "restore") files.DeleteText(workspace, "source.txt", source.Reference);
                var payload = source.Evidence.Payload;
                var blobPath = Path.Combine(paths.ChatBlobDirectory, payload.Sha256.Substring(0, 2), payload.Sha256 + ".blob");
                var bytes = File.ReadAllBytes(blobPath);
                bytes[bytes.Length - 1] ^= 1;
                File.WriteAllBytes(blobPath, bytes);
                var dispatched = false;
                Action mutate = () =>
                {
                    if (operation == "move") files.MoveText(workspace, "source.txt", source.Reference, "target.txt", () => dispatched = true);
                    else if (operation == "delete") files.DeleteText(workspace, "source.txt", source.Reference, () => dispatched = true);
                    else files.RestoreDeletedText(workspace, "source.txt", () => dispatched = true);
                };
                var rejected = false;
                try { mutate(); }
                catch (WorkspaceFileException ex) { rejected = ex.Code == "snapshot_unavailable"; }
                AssertTrue(rejected && !dispatched, operation + " rejects corrupt retained bytes before dispatch");
                AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count,
                    "rejected mutation leaves no prepared attempt");
                var sourcePath = Path.Combine(workspace.RootPath, "source.txt");
                AssertEqual(operation != "restore", File.Exists(sourcePath), "refusal preserves source existence");
                if (operation != "restore") AssertEqual(body, File.ReadAllText(sourcePath), "refusal preserves source bytes");
                AssertTrue(!File.Exists(Path.Combine(workspace.RootPath, "target.txt")), "refusal creates no move target");

                new ChatBlobStore(paths).StoreText(body, "text/plain; charset=utf-8");
                mutate();
                AssertTrue(dispatched, "explicit retry succeeds after source repair");
                var currentPath = operation == "move" ? Path.Combine(workspace.RootPath, "target.txt") : sourcePath;
                if (operation == "delete") AssertTrue(!File.Exists(currentPath), "verified delete moves source to trash");
                else AssertEqual(body, File.ReadAllText(currentPath), "verified operation preserves exact bytes");
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
                AssertEqual("const ready = true;\n", files.ReadExactText(workspace, "src/app.js", source.Reference).Text,
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
                AssertEqual("const app = 1;\n", files.ReadExactText(workspace, "dst/app.js", created.Reference).Text,
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

        private static void WorkspaceFilesPinIdentityAcrossMove(bool recreateSource)
        {
            foreach (var operation in new[] { "describe", "text", "raw" })
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var mover = new WorkspaceFileService(paths);
                var reader = new WorkspaceFileService(paths);
                var source = mover.CreateText(workspace, "source.txt", "moved body");
                var scope = new ResourceAuthorityScopeId("file", ResourceUri.Parse(source.Reference.Uri).Segments.Single());
                var authority = new ResourceAuthorityStore(paths);
                WorkspaceFileObservation moved = null;
                long movedGeneration = 0;
                // Deterministically interleave a completed move after path resolution,
                // before the reader can acquire the resource lease. No timing dependency.
                reader.FaultPoint = point =>
                {
                    if (point != "before-file-lease") return;
                    moved = mover.MoveText(workspace, "source.txt", source.Reference, "target.txt");
                    if (recreateSource)
                        File.WriteAllText(Path.Combine(workspace.RootPath, "source.txt"), "unrelated body");
                    movedGeneration = authority.Capture(scope).Generation;
                };
                IOException failure = null;
                try
                {
                    if (operation == "describe") reader.Describe(workspace, "source.txt");
                    else if (operation == "text") reader.ReadText(workspace, "source.txt");
                    else reader.ReadBytes(workspace, "source.txt");
                }
                catch (IOException ex) { failure = ex; }
                AssertTrue(moved != null, operation + ": move completed inside the identity/lease gap");
                var snapshot = authority.Capture(scope);
                AssertEqual(moved.Reference.Revision, snapshot.GetHead(new ResourceIdentity(source.Reference.Uri))?.Revision?.Revision,
                    operation + ": old path must not invalidate or replace the moved file head");
                AssertEqual(movedGeneration, snapshot.Generation, operation + ": stale path publishes no authority changes");
                AssertEqual("target_conflict", (failure as WorkspaceFileException)?.Code,
                    operation + ": changed path binding fails before observation");
                AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count,
                    operation + ": stale read creates no mutation attempts");

                // A fresh owner proves both durable identity and release of the rejected lease.
                var restarted = new WorkspaceFileService(paths);
                var current = restarted.ReadText(workspace, "target.txt");
                AssertEqual(moved.Reference.Revision, current.Reference.Revision, operation + ": restart retains the move revision");
                AssertEqual("moved body", current.Text, operation + ": target bytes survive");
                AssertEqual("moved body", restarted.ReadExactText(workspace, "target.txt", source.Reference).Text,
                    operation + ": historical CAS view survives");
                if (recreateSource)
                {
                    var replacement = restarted.ReadText(workspace, "source.txt");
                    AssertTrue(replacement.Reference.Uri != current.Reference.Uri, operation + ": reused path gets its own identity");
                    AssertEqual("unrelated body", replacement.Text, operation + ": unrelated bytes survive");
                }
            });
        }

        private static void WorkspaceFilesRecoveryPinsPathIdentity()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var mover = new WorkspaceFileService(paths);
                var recovery = new WorkspaceFileService(paths);
                var source = mover.CreateText(workspace, "source.txt", "original");
                var scope = new ResourceAuthorityScopeId("file", ResourceUri.Parse(source.Reference.Uri).Segments.Single());
                WorkspaceFileObservation moved = null;
                recovery.FaultPoint = point =>
                {
                    if (point != "before-file-lease") return;
                    moved = mover.MoveText(workspace, "source.txt", source.Reference, "target.txt");
                    mover.FaultPoint = stage => { if (stage == "after-dispatch") throw new IOException("interrupted publication"); };
                    var unknown = false;
                    try { mover.ReplaceText(workspace, "target.txt", moved.Reference, "written before interruption"); }
                    catch (WorkspaceFileException ex) { unknown = ex.Code == "effect_unknown"; }
                    AssertTrue(unknown, "replacement has a durable unresolved dispatch");
                };
                WorkspaceFileException failure = null;
                try { recovery.ReconcileUncertain(workspace, "source.txt"); }
                catch (WorkspaceFileException ex) { failure = ex; }
                AssertEqual(1, new ResourceMutationJournal(paths).Unresolved().Count,
                    "stale source path cannot resolve the target's interrupted write");
                AssertEqual(moved.Reference.Revision,
                    new ResourceAuthorityStore(paths).Capture(scope).GetHead(new ResourceIdentity(source.Reference.Uri))?.Revision?.Revision,
                    "stale recovery cannot publish a missing head for the moved file");
                AssertEqual("target_conflict", failure?.Code, "recovery checks path binding before inspecting attempts");
                var restarted = new WorkspaceFileService(paths);
                var reconciled = restarted.ReconcileUncertain(workspace, "target.txt");
                AssertEqual(WorkspaceRecoveryOutcome.UnknownAfterDispatch, reconciled.Outcome,
                    "explicit recovery keeps unknown causality without replay");
                AssertEqual("written before interruption", reconciled.Current.Text, "recovery reads the actual target");
                AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count, "correctly addressed recovery resolves once");
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
                session.Messages = history;
                var recent = ChatCloneService.CloneRecentMessagesForBridge(session, out startIndex);
                AssertEqual(101, startIndex, "bridge opens at bounded recent history");
                AssertEqual(80, recent.Count, "bridge recent page size");
                AssertEqual("message-101", recent[0].Id, "bridge recent page boundary");
                var previous = ChatCloneService.ClonePreviousMessagesForBridge(session, startIndex);
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

        private static void RejectedChatRelocationPreservesSource(string obstacle)
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var source = store.Create("Word", "source", "Source.docx", "Source chat");
                source.Messages.Add(new ChatMessage { Role = "user", Content = "Keep source history" });
                store.Save(source);
                var target = store.CreateTransient("Word", "target", "Target.docx", "Target chat");
                target.Id = source.Id;
                var sourcePath = SessionEventFile(paths, source);
                var targetPath = SessionEventFile(paths, target);
                byte[] targetBytes = null;
                if (obstacle == "file")
                {
                    target.Messages.Add(new ChatMessage { Role = "user", Content = "Keep destination history" });
                    store.Save(target);
                    targetBytes = File.ReadAllBytes(targetPath);
                }
                else if (obstacle == "directory") Directory.CreateDirectory(targetPath);
                else if (obstacle == "parent") File.WriteAllText(Path.GetDirectoryName(targetPath), "Occupied parent");
                else source.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource());
                var original = File.ReadAllBytes(sourcePath);
                var revision = source.Revision;
                var updatedUtc = source.UpdatedUtc;
                var keys = source.PreviousDocumentKeys.ToArray();
                Exception failure = null;
                try { store.Move(source, target.Host, target.DocumentKey, target.DocumentTitle); }
                catch (Exception ex) when (ex is IOException || ex is InvalidOperationException) { failure = ex; }
                AssertTrue(failure != null, "invalid relocation is refused");
                AssertTrue(original.SequenceEqual(File.ReadAllBytes(sourcePath)), "refused relocation appends no source event");
                AssertEqual(revision, source.Revision, "refusal preserves revision");
                AssertEqual(updatedUtc, source.UpdatedUtc, "refusal preserves update time");
                AssertEqual("source", source.DocumentKey, "refusal preserves caller identity");
                AssertEqual("Source.docx", source.DocumentTitle, "refusal preserves document title");
                AssertEqual("source", source.Context.DocumentKey, "refusal preserves context identity");
                AssertTrue(keys.SequenceEqual(source.PreviousDocumentKeys), "refusal adds no document alias");
                var cold = new ChatStore(paths).Load("Word", "source", source.Id);
                AssertEqual("source", cold.DocumentKey, "restart retains canonical source identity");
                AssertEqual("Keep source history", cold.Messages.Single().Content, "restart retains source history");
                if (targetBytes != null)
                    AssertTrue(targetBytes.SequenceEqual(File.ReadAllBytes(targetPath)), "destination history is untouched");
                else AssertTrue(!File.Exists(targetPath), "refusal creates no destination log");

                if (obstacle == "projection") source.HtmlWorkspace.DataSources.Clear();
                store.Move(source, "Word", "retry", "Retry.docx");
                AssertTrue(!File.Exists(sourcePath), "retry moves the source log");
                var moved = new ChatStore(paths).Load("Word", "retry", source.Id);
                AssertEqual("retry", moved.DocumentKey, "retry persists the accepted destination");
                AssertEqual("Keep source history", moved.Messages.Single().Content, "retry preserves history");
                AssertTrue(moved.PreviousDocumentKeys.Contains("source") && !moved.PreviousDocumentKeys.Contains("target"),
                    "retry retains only the accepted document lineage");
            });
        }

        private static void ChatRelocationDoesNotRecreateDeletedSource()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var source = store.Create("Word", "delete-race", "Source.docx", "Deleted chat");
                source.Messages.Add(new ChatMessage { Role = "user", Content = "Deleted history" });
                store.Save(source);
                var originalPath = SessionEventFile(paths, source);
                var target = store.CreateTransient("Word", "delete-race-target", "Target.docx", "Target");
                target.Id = source.Id;
                var targetPath = SessionEventFile(paths, target);
                var lockPath = Path.Combine(paths.Root, "locks", "chat_" + AppDataPaths.SafeFileName(
                    Path.GetFullPath(SessionDirectory(paths, source) + ".document")) + ".lck");
                Exception failure = null;
                var worker = new Thread(() =>
                {
                    try { store.Move(source, target.Host, target.DocumentKey, target.DocumentTitle); }
                    catch (Exception ex) { failure = ex; }
                }) { IsBackground = true };
                bool waiting;
                using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    worker.Start();
                    // A dedicated thread sleeps only while waiting for the held document lock.
                    waiting = SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 5000);
                    if (waiting) File.Delete(originalPath);
                }
                AssertTrue(worker.Join(12000), "relocation worker releases its locks");
                AssertTrue(waiting, "relocation reached the document lock before deletion");
                AssertTrue(failure is ChatConcurrencyException, "deleted persisted source is refused after acquiring the lock");
                AssertTrue(!File.Exists(originalPath) && !File.Exists(targetPath), "relocation cannot resurrect deleted history");
                AssertEqual("delete-race", source.DocumentKey, "deleted source does not change caller identity");
                AssertTrue(new ChatStore(paths).Load(source.Id) == null, "restart keeps deleted history absent");
            });
        }

        private static void ChatRelocationKeepsTransientDraft()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var draft = store.CreateTransient("Word", "draft-source", "Draft.docx", "Draft");
                store.Move(draft, "Word", "draft-target", "Saved.docx");
                AssertEqual("draft-target", draft.DocumentKey, "transient binding follows its document");
                AssertEqual("draft-target", draft.Context.DocumentKey, "transient context follows its document");
                AssertTrue(draft.PreviousDocumentKeys.Contains("draft-source"), "transient binding retains its prior key");
                AssertTrue(!store.IsPersisted(draft) && store.ListHeaders().Count == 0, "relocation does not persist a draft");
                store.Save(draft);
                AssertEqual("draft-target", new ChatStore(paths).Load(draft.Id).DocumentKey, "first explicit save uses the destination");
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
                var active = service.CreatePersistentChat("Active");
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

                var selected = service.LoadSession(archived.Id);
                AssertEqual(archivedAuthorityId, selected.DocumentAuthorityId,
                    "selecting a closed chat preserves its document authority");
                AssertEqual(activeAuthorityId, registry.Resolve(adapter.HostName,
                    adapter.RuntimeDocumentKey, adapter.DocumentPathValue).Id,
                    "selection does not move the live document authority to the archived chat");
                AssertEqual(archivedAuthorityId, registry.Resolve(archived.Host,
                    null, archived.DocumentPath).Id,
                    "selection preserves the archived document locator");
                AssertEqual(archived.Id, service.GetActiveSessionForOfficeState().Id,
                    "catalog polling keeps the selected closed chat");
                AssertTrue(!service.GetChatSummaries(selected.Id).Single(item => item.Id == selected.Id).IsCurrentDocument,
                    "selected closed document remains unavailable to Office tools");
                AssertEqual(active.Id, service.TryLoadCurrentDocumentChat(active.Id).Id,
                    "the original document chat remains selectable");
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
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost(host);
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
                var otherAdapter = FakeOfficeAdapter.ForHost(host == "Excel" ? "PowerPoint" : "Excel");
                var other = new ChatSessionService(otherAdapter, ConversationStore(store));
                var otherChat = other.CreatePersistentChat("Other host");
                AssertTrue(other.TryLoadCurrentDocumentChat(created.Id) == null,
                    "same-document navigation cannot bind another host's chat: " + host);
                AssertEqual(otherChat.Id, other.GetActiveSession().Id, "refused selection preserves the other host chat");
                AssertEqual(created.Id, service.GetActiveSession().Id, "original document chat stays selected");
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
                var summary = new RunSummary("old-run", "old-run", RunLifecycle.Running,
                    new ToolCounts(), 1, 0, "", null, null);
                store.CommitRun(session, new ConversationRunCommit(
                    new AgentRunEvent(AgentRunEventKind.ToolStarted, summary,
                        toolContext: PersistenceContext("old-run", "call-without-result")),
                    new ChatMessage[0], session.Messages.Single(message =>
                        message.Activity?.ToolCallId == "call-without-result")));
                AssertTrue(store.HasOpenToolExecution(session, "old-run"),
                    "startup fixture has a durable execution boundary, independent of UI labels");

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

        private static void InterruptedRunPreservesCompletedExchanges()
        {
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            foreach (var batch in new[] { false, true })
            foreach (var inFlight in new[] { false, true })
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var session = store.Create(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle, "Mixed recovery");
                const string runId = "mixed-run";
                var summary = new RunSummary(runId, runId, RunLifecycle.Running,
                    new ToolCounts(readOk: 1), 2, 1, "", null, null);
                session.LastRun = new ChatRunRecord { RunId = runId, RuntimeId = "old-runtime", Status = "running",
                    StartedUtc = DateTime.UtcNow, KernelState = new AgentRunState(summary, new AgentRunLimits(10, 10),
                        inFlight ? PersistenceContext(runId, "unfinished") : null) };
                Func<string, ChatMessage> call = id => AgentJsonProtocol.CreateToolCallMessage(
                    new AgentToolCall { Id = id, Name = "excel.inspect" }, "Read", null, role, FixtureCallOrigin("step-" + id));
                var completed = call("completed");
                var unfinished = call("unfinished");
                var result = AgentJsonProtocol.CreateToolResultMessage(
                    new ToolInvocation { ToolCallId = "completed", ToolId = "excel.inspect" },
                    RuntimeToolResult.Ok("READ_BODY_SENTINEL", "{\"text\":\"READ_BODY_SENTINEL\"}"), role);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Read and continue" });
                session.Messages.AddRange(batch ? new[] { completed, unfinished, result } : new[] { completed, result, unfinished });
                foreach (var message in session.Messages) message.RunId = runId;
                store.Save(session);

                var service = new ChatSessionService(adapter, ConversationStore(new ChatStore(paths)));
                service.ReconcileInterruptedRuns("new-runtime");
                var recovered = new ChatStore(paths).Load(session.Id);
                var replay = ContextCompactionRuntime.BuildActiveWindow(recovered);
                AssertEqual(2, replay.Count(item => item.ToolCallId == "completed"), "completed pair survives mixed recovery: " + role);
                AssertTrue(replay.Any(item => item.Content == result.Content), "completed result bytes are retained");
                AssertTrue(!replay.Any(item => item.ToolCallId == "unfinished"), "unfinished call never reaches the model");
                AssertEqual(3, recovered.Messages.Count(item => item.ProtocolMessage), "recovery deletes no accepted facts");
                AssertEqual(1, recovered.LastRun.KernelState.Summary.ToolCounts.ReadOk, "completed outcome is not erased");
                var count = recovered.Messages.Count;
                service.ReconcileInterruptedRuns("another-runtime");
                AssertEqual(count, new ChatStore(paths).Load(session.Id).Messages.Count, "recovery is idempotent");
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
