using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Agent;
using Newtonsoft.Json;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AuthoredHtmlViewerReadsRetainedRevisions()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var store = new ChatStore(FixturePaths.Value);
                var tools = executor.GetControllerTools().ToList();
                var source = "<!doctype html><main>Пример🙂 before</main><script>window.run()</script>";
                var created = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Write,
                    "target", "file: index.html", "content", source), tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(created.Success, "HTML creation succeeds: " + created.Message);
                var original = session.Artifacts.Single(item => item.Title == "index.html");
                AssertTrue(ChatArtifactResourceProvider.FindExactAttachment(session, original) == null,
                    "created HTML has retained file bytes, not an uploaded attachment");
                Action<ChatSession> assertPromptTargets = owner =>
                {
                    var rows = ChatResourcePromptIndex.Build(owner, 5000).Split('\n')
                        .Where(line => line.StartsWith("- target=", StringComparison.Ordinal)).ToArray();
                    AssertTrue(rows.Length > 0, "authored files are advertised in context");
                    foreach (var row in rows)
                    {
                        var promptTarget = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(
                            row.Substring(9, row.IndexOf(" | type=", StringComparison.Ordinal) - 9));
                        var resolved = executor.ResourceGateway.ResolveIntentTarget(owner, promptTarget);
                        if (!resolved.Descriptor.Metadata.ContainsKey("authoredFile")) continue;
                        AssertTrue(promptTarget.StartsWith("file: ", StringComparison.Ordinal), "prompt uses the authored-file target");
                        AssertContains(row, "read=text", "retained authored text advertises a supported read");
                    }
                };
                assertPromptTargets(session);
                var viewer = new ArtifactViewerService(executor.ResourceGateway);
                Action<ChatSession, ChatArtifact, string> assertSource = (owner, artifact, expected) =>
                {
                    using (var plane = new ResourceDataPlaneService(executor.ResourceGateway))
                    {
                        var text = new StringBuilder();
                        var uri = ChatResourceUri.CreateArtifactRevisionUri(owner, artifact);
                        string cursor = null;
                        do
                        {
                            var page = viewer.ReadPage(owner, uri, cursor, plane);
                            AssertEqual(ArtifactViewerKinds.Text, page.ViewerKind, "HTML is always inert source");
                            AssertEqual(uri, page.ResourceUri, "viewer preserves the selected revision");
                            AssertEqual(TextPatternEngine.Sha256(expected), page.ContentSha256, "viewer pins decoded text");
                            AssertEqual(text.Length, page.Offset, "HTML pages are contiguous");
                            AssertEqual(expected.Length, page.TotalCharacters, "HTML total is exact");
                            text.Append(ReadViewerBatch(plane, page));
                            plane.Close(owner.Id, "viewer", page.Data.LeaseId);
                            cursor = page.NextCursor;
                            if (cursor == null) AssertTrue(page.Complete && page.SourceComplete, "HTML source is complete");
                            else AssertTrue(text.Length < expected.Length, "HTML continuation makes bounded progress");
                        } while (cursor != null);
                        AssertEqual(expected, text.ToString(), "viewer returns exact saved HTML");
                    }
                };
                assertSource(session, original, source);
                var patched = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Patch,
                    "targets", PatchTargets(PatchSection((string)JObject.Parse(created.DataJson)["target"], EditSet(Edit(source, source.Replace("before", "after")))))),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(patched.Success, "HTML patch succeeds: " + patched.Message);
                var latest = session.Artifacts.Single(item => item.Title == "index.html" && item.Revision == 2);
                var changed = source.Replace("before", "after");
                assertSource(session, latest, changed);
                assertSource(session, original, source);

                var projection = ArtifactLibraryProjectionService.Project(session);
                var head = projection.Heads.Single(item => item.Title == "index.html");
                AssertEqual(latest.Id, head.ArtifactId, "one authored-file head selects the latest version");
                AssertEqual(ArtifactLibraryResourceClasses.VersionedDocument, head.ResourceClass, "authored HTML is not an upload");
                AssertEqual(ArtifactLibraryGroups.AuthoredDocuments, head.Group, "authored HTML belongs with documents");
                AssertEqual("v2", head.VersionLabel, "authored file has a version label");
                var history = ArtifactLibraryProjectionService.History(session, new ArtifactLibraryHistoryRequest
                { ChatId = session.Id, ExpectedSessionRevision = session.Revision, HeadArtifactId = head.ArtifactId });
                AssertEqual(2, history.Items.Count, "both exact HTML revisions remain accessible");
                store.Save(session);
                var restarted = store.Load(session.Id);
                assertSource(restarted, restarted.Artifacts.Single(item => item.Id == original.Id), source);
                assertSource(restarted, restarted.Artifacts.Single(item => item.Id == latest.Id), changed);

                var uploadedText = source + new string('x', 33000);
                var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(uploadedText)).ToArray();
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(FixturePaths.Value), store.DocumentArtifacts);
                var draft = ingestion.Stage(session, "copy.htm", "text/html", bytes);
                var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                session.Messages.Add(message);
                ingestion.CommitAndLink(session, message, session.Messages.Count - 1);
                var upload = session.Artifacts.Single(item => item.Title == "copy.htm");
                var target = executor.ResourceGateway.Find(session, "copy.htm", "document").Items.Single().Target;
                var copied = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Copy,
                    "target", target, "destination", "file: copy.htm"), tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(copied.Success, "HTML upload copies as an authored file: " + copied.Message);
                var copy = session.Artifacts.Single(item => item.Title == "copy.htm" && item.Kind == ChatArtifactKinds.File);
                AssertEqual("text/html", copy.MimeType, "copied HTML retains its MIME type");
                assertSource(session, copy, uploadedText);
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, ChatResourceUri.CreateArtifactRevision(session, copy))
                    .SequenceEqual(bytes), "source viewing preserves UTF-16 file bytes");
                AssertEqual(ArtifactLibraryResourceClasses.ImmutableOriginal,
                    ArtifactLibraryProjectionService.Project(session).Heads.Single(item => item.ArtifactId == upload.Id).ResourceClass,
                    "uploaded originals retain their separate viewer and import policy");
                var rawHash = copy.ContentSha256;
                store.Save(session);
                restarted = new ChatStore(FixturePaths.Value).Load(session.Id);
                var retained = restarted.Artifacts.Single(item => item.Id == copy.Id);
                AssertTrue(retained.InlineText == null, "opening a chat leaves authored file bodies lazy");
                AssertTrue(store.LoadArtifactBody(restarted, copy.Id), "explicit authored-file read uses its owner");
                AssertEqual(uploadedText, retained.InlineText, "UTF-16 text survives restart");
                store.Save(restarted);
                restarted = new ChatStore(FixturePaths.Value).Load(session.Id);
                retained = restarted.Artifacts.Single(item => item.Id == copy.Id);
                AssertEqual(rawHash, retained.ContentSha256, "saving decoded text cannot replace original bytes");
                AssertTrue(store.DocumentArtifacts.ExportBytes(restarted, ChatResourceUri.CreateArtifactRevision(restarted, retained))
                    .SequenceEqual(bytes), "UTF-16 BOM and bytes survive hydration and another save");
                assertSource(restarted, retained, uploadedText);
                assertPromptTargets(restarted);
                var utf16Target = executor.ResourceGateway.Find(session, "copy.htm", "document").Items.Single(item => item.Target.StartsWith("file: ", StringComparison.Ordinal)).Target;
                var utf16Patch = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Patch, "targets",
                    PatchTargets(PatchSection(utf16Target, EditSet(Edit(uploadedText, uploadedText.Replace("before", "after")))))),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(utf16Patch.Success, "UTF-16 authored patch: " + utf16Patch.Message);
                var utf16After = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "copy.htm" && item.Revision == 2);
                var utf16Text = uploadedText.Replace("before", "after");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, ChatResourceUri.CreateArtifactRevision(session, utf16After))
                    .SequenceEqual(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(utf16Text))), "patch preserves UTF-16 BOM, untouched bytes and absent final newline");
                var authorityStore = new ResourceAuthorityStore(FixturePaths.Value);
                var authority = new ResourceAuthorityService(authorityStore, authorityStore, new ResourceMutationJournal(FixturePaths.Value), new ChatBlobStore(FixturePaths.Value));
                AssertEqual(utf16Text, PatchChangesProjection.ReadSource(authority, ChatResourceUri.CreateArtifactRevision(session, utf16After), 128000), "retained decoded after-source exists without a follow-up model read");
                var rawPath = System.IO.Path.Combine(FixturePaths.Value.ChatBlobDirectory,
                    rawHash.Substring(0, 2), rawHash + ".blob");
                System.IO.File.Move(rawPath, rawPath + ".held");
                try
                {
                    restarted = new ChatStore(FixturePaths.Value).Load(session.Id);
                    AssertTrue(restarted != null, "a missing authored body does not hide the conversation");
                    AssertTrue(!store.LoadArtifactBody(restarted, copy.Id), "missing exact body cannot silently fall back");
                }
                finally { System.IO.File.Move(rawPath + ".held", rawPath); }
            });
        }

        private static void AuthoredFilePatchEncodingsAndGuards()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var store = new ChatStore(FixturePaths.Value);
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(FixturePaths.Value), store.DocumentArtifacts);
                var tools = executor.GetControllerTools().ToList();
                var codecs = new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
                    new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
                    new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) };
                const string source = "prefix 😀\r\nold = 1\nunchanged\r\ntail";
                Func<ToolInvocation, IEnumerable<ResourceEvidence>, VbaPendingExecution> prepare = (command, evidence) =>
                {
                    command.ToolCallId = "file_" + Guid.NewGuid().ToString("N");
                    var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                        Id = command.ToolCallId, Name = command.ToolId, Arguments = command.Arguments },
                        "Edit.", "Change the observed lines.", null, ToolResultRoles.User, FixtureCallOrigin("run-vba-native:1"));
                    accepted.RunId = "run-vba-native";
                    accepted.ResourceEvidence = evidence.ToList(); session.Messages.Add(accepted);
                    return PrepareVbaNative(executor, session, command);
                };
                for (var i = 0; i < codecs.Length; i++)
                {
                    var codec = codecs[i];
                    var name = "original" + i + ".txt";
                    var draft = ingestion.Stage(session, name, "text/plain", codec.GetPreamble().Concat(codec.GetBytes(source)).ToArray());
                    var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                    session.Messages.Add(message); ingestion.CommitAndLink(session, message, session.Messages.Count - 1);
                    var originalTarget = executor.ResourceGateway.Find(session, name, "document").Items.Single().Target;
                    var copy = executor.ExecuteManual(Command("resources.copy", "target", originalTarget, "destination", "file: code" + i + ".txt"),
                        tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                    AssertTrue(copy.Success, "copy encoding " + i + ": " + copy.Message);
                    var target = (string)JObject.Parse(copy.DataJson)["target"];
                    var exact = executor.ResourceGateway.ResolveIntentTarget(session, target).Reference;
                    var excerpt = executor.ResourceGateway.ReadCharacters(session, exact, "text", source.IndexOf("old", StringComparison.Ordinal), 8).Result;
                    var evidence = executor.ResourceGateway.Evidence(session, excerpt).ToArray();
                    var body = PatchTargets(PatchSection(target, EditSet(Edit("old = 1", "new = 22"))));
                    var unseen = prepare(Command("core.patch", "targets", PatchTargets(PatchSection(target, EditSet(Edit("old = 1", "new = 22"), Edit("tail", "changed"))))), evidence).Record;
                    AssertTrue(unseen.Outcome == ToolExecutionOutcome.Error && !unseen.MayHaveDispatched,
                        "unobserved second hunk rejects the whole file: " + unseen.Result.DataJson);
                    AssertEqual(ToolRetryPolicy.RefreshRequired, unseen.Recovery.RetryPolicy, "partial file refusal requires a fresh relevant read");
                    AssertTrue(store.DocumentArtifacts.ExportBytes(session, exact).SequenceEqual(codec.GetPreamble().Concat(codec.GetBytes(source))),
                        "preparation never changes the original snapshot");
                    var pending = prepare(Command("core.patch", "targets", body), evidence);
                    AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Record.Outcome, "observed range prepares: " + pending.Record.Result?.DataJson);
                    var result = ConfirmVbaNative(pending);
                    AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "prepared streamed patch executes: " + result.Result.DataJson);
                    var after = result.ResourceEffect.Targets.Single().After;
                    var expected = source.Replace("old = 1", "new = 22");
                    AssertTrue(store.DocumentArtifacts.ExportBytes(session, executor.ResourceGateway.ResolveIntentTarget(session, JsonConvert.DeserializeObject<PatchBatchResult>(result.Result.DataJson).Targets.Single().CurrentTarget).Reference).SequenceEqual(codec.GetPreamble().Concat(codec.GetBytes(expected))),
                        "UTF-8/16/32, BOM, mixed EOLs, astral characters and absent final newline stay exact: " + i);
                    var authorityStore = new ResourceAuthorityStore(FixturePaths.Value);
                    var authority = new ResourceAuthorityService(authorityStore, authorityStore, new ResourceMutationJournal(FixturePaths.Value), new ChatBlobStore(FixturePaths.Value));
                    AssertEqual(source, PatchChangesProjection.ReadSource(authority, exact, 1000), "partial observation does not hide the retained before-source from the diff");
                    AssertEqual(expected, PatchChangesProjection.ReadSource(authority, after, 1000), "diff reads the verified after-source in every encoding");
                    var materialized = new ToolResultMaterialization(result.Result, resourceEffect: result.ResourceEffect);
                    var invocation = new ToolInvocation { ToolId = "core.patch", ToolCallId = result.Context.Call.Id };
                    var terminal = AgentJsonProtocol.CreateToolResultMessage(invocation, materialized, int.MaxValue, ToolResultRoles.User);
                    ConversationModelSession.AddVerifiedSourceEvidence(session, executor.ResourceAuthority, executor.Payloads, terminal, invocation, materialized);
                    AssertTrue(terminal.ResourceEvidence.Count > 0, "verified authored source becomes eligible evidence");
                    AssertEqual(expected, executor.Payloads.ReadText(terminal.ResourceEvidence.Last().Payload.ToBlobReference()), "evidence contains actual decoded source, never the logical-head link");
                    var scope = executor.ResourceAuthority.ScopeFor(session, after, false);
                    var frozen = new ModelAuthoritySnapshot(executor.ResourceAuthority.CaptureMany(new[] { scope }), "tools", new SkillCatalogSnapshot(null), null, 1);
                    var compiled = new ModelContextCompiler(executor.Payloads, projection: ModelToolResultProjection.Instance).Compile(
                        frozen, new ChatMessage[0], new[] { session.Messages.Last(m => m.AcceptedCallOrigin != null && m.ToolCallId == result.Context.Call.Id), terminal }, null, new ToolCatalogEntry[0], new AppSettings(), 12000);
                    AssertTrue(compiled.Messages.Any(m => m.SyntheticResourceObservation && m.Content.Contains("new = 22")), "actual next request includes verified after-source");
                    var fragments = PatchChangesProjection.ReadFragments(authority, exact, after, EditSet(Edit("old = 1", "new = 22")));
                    AssertContains(string.Concat(fragments.Select(edit => edit.After)), "new = 22", "compact diff fragments come from the actual retained result");
                    var currentTarget = JsonConvert.DeserializeObject<PatchBatchResult>(result.Result.DataJson).Targets.Single().CurrentTarget;
                    AssertTrue(!string.IsNullOrWhiteSpace(currentTarget), "confirmed patch returns the next semantic target");
                    var noop = PrepareVbaNative(executor, session, Command("core.patch", "targets", PatchTargets(PatchSection(currentTarget, EditSet(Edit("new = 22", "new = 22"))))));
                    var unchanged = ConfirmVbaNative(noop);
                    AssertEqual(ResourceTargetStatus.Unchanged, unchanged.ResourceEffect.Targets.Single().Status, "no-op retains its explicit status");
                    if (i == 0)
                    {
                        var stale = PrepareVbaNative(executor, session, Command("core.patch", "targets", PatchTargets(PatchSection(currentTarget, EditSet(Edit("new = 22", "new = 3"))))));
                        var external = executor.ExecuteManual(Command("core.write", "target", currentTarget, "content", expected + " external"),
                            tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                        AssertTrue(external.Success, "another writer changes the current head");
                        var refused = ConfirmVbaNative(stale);
                        AssertTrue(refused.Outcome == ToolExecutionOutcome.Error && !refused.MayHaveDispatched,
                            "a changed head invalidates confirmation before any patch write");
                    }
                }
            });
        }

        private static void AuthoredFileLargeCopyPatch()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var store = new ChatStore(FixturePaths.Value);
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(FixturePaths.Value), store.DocumentArtifacts);
                var source = "const a = 1;\n" + new string('x', 10 * 1024 * 1024) + "\nconst unique = slow(slow(value));\n";
                var originalBytes = Encoding.UTF8.GetBytes(source);
                var draft = ingestion.Stage(session, "large.js", "text/plain", originalBytes);
                var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                session.Messages.Add(message);
                ingestion.CommitAndLink(session, message, 0);
                var original = session.Artifacts.Single(item => item.Title == "large.js");
                var originalRef = ChatResourceUri.CreateArtifactRevision(session, original);
                var sourceTarget = executor.ResourceGateway.Find(session, "large.js", "document").Items.Single().Target;
                var tools = executor.GetControllerTools().ToList();
                var copied = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Copy, "target", sourceTarget, "destination", "file: large.js"),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(copied.Success, "large file copy: " + copied.Message);
                var copyTarget = (string)JObject.Parse(copied.DataJson)["target"];
                var copy = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "large.js");
                var copiedRef = ChatResourceUri.CreateArtifactRevision(session, copy);
                AssertEqual(originalBytes.Length, (int)copy.ContentByteLength.Value, "copy retains exact byte length");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, copiedRef).SequenceEqual(originalBytes), "copy preserves exact original bytes");
                var patches = EditSet(Edit("const unique = slow(slow(value));", "const unique = fast(value);"));
                var allocationStart = GC.GetAllocatedBytesForCurrentThread();
                var preparation = store.DocumentArtifacts.PrepareFilePatch(session, copiedRef, patches);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                AssertTrue(allocated < 8L * 1024 * 1024, "10 MiB patch preparation uses bounded buffers; allocated " + allocated);
                AssertEqual((long)source.Length, preparation.SourceCharacters, "streamed coverage counts decoded characters");
                var patchCommand = Command(ResourceMutationToolCatalog.Patch, "targets", PatchTargets(PatchSection(copyTarget, patches)));
                var callAllocations = GC.GetTotalAllocatedBytes(true);
                var patched = executor.ExecuteManual(patchCommand, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                var callBytes = GC.GetTotalAllocatedBytes(true) - callAllocations;
                AssertTrue(patched.Success, "large file patch: " + patched.Message);
                AssertTrue(callBytes < 20L * 1024 * 1024, "public patch/confirmation/read-back avoid whole source allocation; allocated " + callBytes);
                AssertTrue(!patched.DataJson.Contains("rna://") && !patched.DataJson.Contains("\"offset\"") &&
                    !patched.DataJson.Contains("\"version\""),
                    "model result keeps exact references and coordinates private");
                var latest = session.Artifacts.Where(item => item.Kind == ChatArtifactKinds.File && item.Title == "large.js")
                    .OrderByDescending(item => item.Revision).First();
                var latestRef = ChatResourceUri.CreateArtifactRevision(session, latest);
                var expected = source.Replace("slow(slow(value))", "fast(value)");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, latestRef).SequenceEqual(Encoding.UTF8.GetBytes(expected)),
                    "read-back contains only the intended edit");
                var logicalId = latest.Id.Substring(0, latest.Id.IndexOf("_r", StringComparison.Ordinal));
                var logical = AuthoredFileService.Identity(session, logicalId);
                var scope = executor.ResourceAuthority.ScopeFor(session, latestRef, false);
                var head = executor.ResourceAuthority.Store.Capture(scope).GetHead(logical).Revision;
                var effect = new ResourceEffect("bounded-file-source", "core.patch", ResourceEffectOutcome.VerifiedChanged,
                    targets: new[] { new ResourceTargetEffect { Target = "file: large.js", Status = ResourceTargetStatus.Changed, After = head } });
                var invocation = new ToolInvocation { ToolId = "core.patch", ToolCallId = "bounded-file-source" };
                var materialized = new ToolResultMaterialization(RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Verified"), resourceEffect: effect);
                var terminal = AgentJsonProtocol.CreateToolResultMessage(invocation, materialized, int.MaxValue, ToolResultRoles.User);
                ConversationModelSession.AddVerifiedSourceEvidence(session, executor.ResourceAuthority, executor.Payloads, terminal, invocation, materialized);
                var delivered = terminal.ResourceEvidence.Single();
                AssertTrue(!delivered.Complete && delivered.Coverage.End <= 32000, "large after-source is explicitly bounded");
                AssertEqual(latest.ContentSha256, delivered.ContentSha256, "bounded evidence retains the complete file hash");
                AssertTrue(expected.StartsWith(executor.Payloads.ReadText(delivered.Payload.ToBlobReference()), StringComparison.Ordinal),
                    "bounded evidence contains actual verified source bytes");
                var catalog = new ArtifactCatalogService(new DocumentAuthorityRegistry(FixturePaths.Value),
                    store.DocumentArtifacts, new ArtifactWorkingSetService(store.DocumentArtifacts,
                        new ResourceMutationJournal(FixturePaths.Value)), new ResourceAuthorityStore(FixturePaths.Value),
                    executor.Payloads);
                var downloaded = catalog.Export(session, new ArtifactTransferRequest { ChatId = session.Id,
                    DocumentId = session.DocumentAuthorityId, ResourceUri = latestRef.Uri });
                AssertEqual("large.js", downloaded.FileName, "Library export preserves the requested code extension");
                AssertTrue(downloaded.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(expected)),
                    "Library export returns the exact verified edited bytes");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, originalRef).SequenceEqual(originalBytes),
                    "uploaded original remains byte-for-byte unchanged");
                var renamed = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Copy,
                    "target", sourceTarget, "destination", "file: code-export.md"),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(renamed.Success, "10 MiB code original can be saved under a requested .md extension");
                var renamedArtifact = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "code-export.md");
                var renamedDownload = catalog.Export(session, new ArtifactTransferRequest { ChatId = session.Id,
                    DocumentId = session.DocumentAuthorityId,
                    ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, renamedArtifact) });
                AssertEqual("code-export.md", renamedDownload.FileName, "renamed large file downloads as Markdown");
                AssertTrue(renamedDownload.Bytes.SequenceEqual(originalBytes),
                    "changing the output extension does not alter 10 MiB of source code");
                var diff = new RunChangesService(a => a.InlineText, null, null).Read(session, latest.RunId);
                AssertTrue(diff.Items.Any(item => item.Availability == "hunks" &&
                    item.Hunks.Any(h => h.Before.Contains("slow(slow(value))") && h.After.Contains("fast(value)"))),
                    "run changes presents verified bounded file hunks");
                AssertTrue(!executor.ExecuteManual(patchCommand, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session).Success,
                    "stale file target cannot overwrite a newer version");
                var part = store.DocumentArtifacts.ReadIndexedTextPart(session, latestRef, 1);
                AssertTrue(part.PartCount > 300 && part.Text.Length <= 32000, "large authored file is navigable by bounded parts");
                var viewer = new ArtifactViewerService(executor.ResourceGateway);
                var dataPlane = new ResourceDataPlaneService(executor.ResourceGateway);
                var viewerPage = viewer.ReadPage(session, latestRef.Uri, null, dataPlane);
                for (var pageNumber = 0; pageNumber < 17; pageNumber++)
                    viewerPage = viewer.ReadPage(session, latestRef.Uri, viewerPage.NextCursor, dataPlane);
                AssertTrue(viewerPage.Offset > ArtifactViewerService.MaximumDocumentCharacters &&
                    viewerPage.NextCursor != null && !viewerPage.FullReadAllowed,
                    "large file viewer pages past the small-document ceiling without enabling full copy");
                var found = store.DocumentArtifacts.SearchTextOccurrences(session, latestRef, "fast(value)", 10, false);
                AssertTrue(!found.ScanTruncated && found.Matches.Count == 1, "exact large-file search reaches the changed tail");
                var nextTarget = (string)JObject.Parse(patched.DataJson)["targets"][0]["currentTarget"];
                var modelSearch = executor.ResourceGateway.SearchWithin(session, nextTarget, "fast(value)", 10,
                    false, System.Threading.CancellationToken.None);
                AssertTrue(modelSearch.ScanComplete && modelSearch.Matches.Single().PartNumber > 300,
                    "semantic search locates the late changed part");
                AssertTrue(executor.ResourceGateway.Outline(session, nextTarget, 1, 10).PartCount > 300,
                    "semantic outline navigates the complete authored file");
                var ambiguous = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Patch,
                    "targets", PatchTargets(PatchSection(nextTarget, EditSet(Edit("xx", "x"))))),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(!ambiguous.Success, "ambiguous patch is refused without another revision");
                store.Save(session);
                var restarted = store.Load(session.Id);
                AssertTrue(store.DocumentArtifacts.ExportBytes(restarted, latestRef).SequenceEqual(Encoding.UTF8.GetBytes(expected)),
                    "verified large-file version survives chat restart");

                var markdown = "# Large\n" + new string('m', 300000);
                var mdDraft = ingestion.Stage(session, "large.md", "text/markdown", Encoding.UTF8.GetBytes(markdown));
                var mdMessage = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { mdDraft.Id }).ToList() };
                session.Messages.Add(mdMessage); ingestion.CommitAndLink(session, mdMessage, session.Messages.Count - 1);
                var mdTarget = executor.ResourceGateway.Find(session, "large.md", "document").Items.Single().Target;
                var mdCopy = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Copy, "target", mdTarget, "destination", "file: large.md"),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(mdCopy.Success, "Markdown beyond the inline save limit copies as a real .md file");
                var mdArtifact = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "large.md");
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, ChatResourceUri.CreateArtifactRevision(session, mdArtifact))
                    .SequenceEqual(Encoding.UTF8.GetBytes(markdown)), "large .md export preserves all bytes");
                var mdDownload = catalog.Export(session, new ArtifactTransferRequest { ChatId = session.Id,
                    DocumentId = session.DocumentAuthorityId,
                    ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, mdArtifact) });
                AssertEqual("large.md", mdDownload.FileName, "Library downloads large Markdown with its .md extension");
                AssertTrue(mdDownload.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(markdown)),
                    "Library downloads complete Markdown beyond the inline save limit");

                var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("first\r\nslow(value)\r\n")).ToArray();
                var codeDraft = ingestion.Stage(session, "unicode.cs", "text/plain", utf16);
                var codeMessage = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { codeDraft.Id }).ToList() };
                session.Messages.Add(codeMessage); ingestion.CommitAndLink(session, codeMessage, session.Messages.Count - 1);
                var codeTarget = executor.ResourceGateway.Find(session, "unicode.cs", "document").Items.Single().Target;
                var codeCopy = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Copy, "target", codeTarget, "destination", "file: unicode.cs"),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(codeCopy.Success, "UTF-16 source copies without conversion");
                var codePatch = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Patch,
                    "targets", PatchTargets(PatchSection((string)JObject.Parse(codeCopy.DataJson)["target"], EditSet(Edit("slow(value)", "fast(value)"))))),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(codePatch.Success, "UTF-16 source patches losslessly");
                var codeLatest = session.Artifacts.Where(item => item.Kind == ChatArtifactKinds.File && item.Title == "unicode.cs")
                    .OrderByDescending(item => item.Revision).First();
                var expectedUtf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("first\r\nfast(value)\r\n")).ToArray();
                AssertTrue(store.DocumentArtifacts.ExportBytes(session, ChatResourceUri.CreateArtifactRevision(session, codeLatest))
                    .SequenceEqual(expectedUtf16), "UTF-16 BOM and CRLF survive exact patch and export");
                var codeRef = ChatResourceUri.CreateArtifactRevision(session, codeLatest);
                var priorText = executor.ResourceAuthority.Revisions.GetView(executor.ResourceAuthority.Scope(session, true), codeRef, "text");
                AssertTrue(priorText == null, "new file revision has no preexisting text read view");
                AssertEqual("first\r\nfast(value)\r\n", executor.ResourceGateway.Read(session,
                    new ResourceReadRequest { Reference = codeRef, Representation = "text", MaxChars = 32000 }).Result.Text,
                    "UTF-16 authored file is readable as text through the gateway");
                AssertEqual("first\r\nfast(value)\r\n", store.DocumentArtifacts.ReadIndexedTextPart(session, codeRef, 1).Text,
                    "UTF-16 authored file is indexed without losing source bytes");
                var created = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Write,
                    "target", "file: new.py", "content", "print('ready')\n"),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(created.Success, "small code file can be authored in its requested format");
                var newFile = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "new.py");
                AssertEqual("print('ready')\n", Encoding.UTF8.GetString(store.DocumentArtifacts.ExportBytes(session,
                    ChatResourceUri.CreateArtifactRevision(session, newFile))), "created source is exactly exportable");
                var unicodeCreated = executor.ExecuteManual(Command(ResourceMutationToolCatalog.Write,
                    "target", "file: emoji.txt", "content", "A😀B"),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(unicodeCreated.Success, "Unicode text file creation succeeds");
                var emojiFile = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.File && item.Title == "emoji.txt");
                var emojiRef = ChatResourceUri.CreateArtifactRevision(session, emojiFile);
                var emojiFirst = executor.ResourceGateway.Read(session,
                    new ResourceReadRequest { Reference = emojiRef, Representation = "text", MaxChars = 2 });
                AssertEqual("A", emojiFirst.Result.Text, "bounded page does not split a surrogate pair");
                var emojiSecond = executor.ResourceGateway.Read(session,
                    new ResourceReadRequest { Reference = emojiRef, Representation = "text", MaxChars = 2,
                        Cursor = emojiFirst.Result.NextCursor });
                AssertEqual("😀", emojiSecond.Result.Text, "continuation resumes at the full character");
            });
        }
    }
}
