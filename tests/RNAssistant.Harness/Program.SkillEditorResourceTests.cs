using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static SkillSourceReadRequest SkillReferenceRequest(ChatSession session, string id, string revision)
        {
            return new SkillSourceReadRequest { Type = SkillSourceReadRequest.ContractType, ContractVersion = 1,
                ChatId = session.Id, SkillId = id, Path = "references/rules.md", ExpectedPackageRevision = revision };
        }

        private static string ReadSkillSourceDownload(ResourceDataPlaneService data, SkillSourceReadResponse response)
        {
            using (var output = new MemoryStream())
            {
                for (var offset = 0; offset < response.Data.Payload.ByteLength;)
                {
                    string mime;
                    var count = (int)Math.Min(response.Data.MaxChunkBytes, response.Data.Payload.ByteLength - offset);
                    var bytes = data.ReadDownload(response.Data.LeaseId, offset, count, CancellationToken.None, out mime);
                    AssertEqual("text/markdown; charset=utf-8", mime, "inert reference transfer");
                    output.Write(bytes, 0, bytes.Length); offset += bytes.Length;
                }
                data.Close(response.ChatId, SkillEditorResourceService.Owner, response.Data.LeaseId);
                return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
        }

        private static void SkillReferenceEditorReadsPublishedSource()
        {
            WithTempPaths(paths =>
            {
                var store = new SkillStore(paths);
                var skill = store.SaveOne(new SkillDefinition { Id = "common.reference_editor", Name = "Editor", Description = "Reference source test.", BodyMarkdown = "# Core" });
                var body = "# Справка\r\n" + new string('ж', 140000) + "\r\n";
                string error; SkillReferenceMetadata metadata;
                AssertTrue(store.TrySaveReference(skill, "references/rules.md", body, out metadata, out error), "reference setup: " + error);
                var file = Path.Combine(skill.StoragePath, "references", "rules.md");
                File.WriteAllBytes(file, new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes(body)).ToArray());
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), store, new ToolStore(paths));
                var catalog = new SkillCatalogService(adapter, executor.CapturePublishedSkills);
                var published = catalog.GetVisibleSkills().Single(item => item.Id == skill.Id);
                var session = NewSession(adapter);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var reader = new SkillEditorResourceService(executor.ResourceGateway, data, catalog);
                    var request = SkillReferenceRequest(session, skill.Id, SkillRevision.Compute(published));
                    var original = reader.Open(session, request, CancellationToken.None);
                    var dto = JObject.FromObject(original);
                    AssertTrue(dto["content"] == null && dto["skill"] == null && dto["result"] == null, "reference reads carry no source/core body or mutation result");
                    AssertTrue(original.Reference.Revision != original.Data.Payload.Sha256, "file BOM hash and published text transport hash are distinct");
                    AssertEqual(0, session.Messages.Count, "editor reads never grant model observations");
                    RuntimeThrows<ResourceRequestException>(() => data.Close("foreign", SkillEditorResourceService.Owner, original.Data.LeaseId));
                    File.WriteAllText(file, "# External unpublished change");
                    var fresh = reader.Open(session, request, CancellationToken.None);
                    AssertEqual(body, ReadSkillSourceDownload(data, fresh), "new reads use the committed catalog, not mutable authoring files");
                    var rejected = executor.ExecuteSkillLibraryReferenceMutation("upsert", skill.Id, request.Path, "# Stale edit", request.ExpectedPackageRevision);
                    AssertEqual(SkillAuthoringOutcomeStatus.Error, rejected.Outcome.Status, "stale editor save rejects disk drift");
                    AssertTrue(!rejected.DispatchPossible, "disk drift fails before dispatch");
                    AssertEqual("# External unpublished change", File.ReadAllText(file), "read and failed save preserve external text");
                    var changed = executor.ExecuteSkillLibraryReferenceMutation("upsert", skill.Id, request.Path, "# New publication",
                        SkillRevision.Compute(store.Load().Single(item => item.Id == skill.Id)));
                    AssertEqual(SkillAuthoringOutcomeStatus.Ok, changed.Outcome.Status, "existing mutation owner publishes the verified reference");
                    AssertEqual(body, ReadSkillSourceDownload(data, original), "an open exact transfer survives later publication");
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() =>
                        reader.Open(session, request, CancellationToken.None)).ErrorCode, "old editor package cannot silently borrow a new publication");
                    request.ExpectedPackageRevision = changed.Package.Revision;
                    AssertEqual("# New publication", ReadSkillSourceDownload(data, reader.Open(session, request, CancellationToken.None)), "fresh package reads its own source");
                    var exact = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = original.Resource, Representation = "text", MaxChars = 128 }).Result;
                    AssertEqual(body.Substring(0, 128), exact.Text, "editor and model share the same retained catalog reference");
                    File.WriteAllText(executor.Payloads.PathFor(original.Data.Payload.Sha256), "corrupt");
                    AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() =>
                        executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = original.Resource, Representation = "text" })).ErrorCode,
                        "corrupt historical source never falls back to the current file");
                }
            });
        }

        private static void SkillEditorReadsPublishedCore()
        {
            WithTempPaths(paths =>
            {
                var store = new SkillStore(paths);
                var skill = store.SaveOne(new SkillDefinition { Id = "common.core_editor", Name = "Core", Description = "Core editor.",
                    BodyMarkdown = "# Core\r\n" + new string('ж', 70000) + "\r\n" });
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), store, new ToolStore(paths));
                var catalog = new SkillCatalogService(adapter, executor.CapturePublishedSkills);
                var published = catalog.GetVisibleSkills();
                var original = published.Single(item => item.Id == skill.Id);
                var metadata = SkillPackageDto.From(original);
                var library = JObject.FromObject(SkillLibraryResponse.From(published));
                AssertTrue(library["skills"].All(item => item["bodyMarkdown"] == null && item["body"]["sha256"] != null), "lists contain body metadata only");
                AssertEqual(TextPatternEngine.Sha256(original.BodyMarkdown), metadata.Body.Sha256, "body metadata identifies exact raw UTF-8, not normalized package hash");
                skill.BodyMarkdown = "# Unpublished drift"; store.SaveOne(skill);
                var session = NewSession(adapter);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var reader = new SkillEditorResourceService(executor.ResourceGateway, data, catalog);
                    var request = SkillReferenceRequest(session, skill.Id, metadata.Revision); request.Path = "";
                    var response = reader.Open(session, request, CancellationToken.None);
                    AssertEqual("", response.Path, "empty path explicitly selects core");
                    AssertTrue(response.Reference == null, "core does not pretend to be a reference file");
                    AssertEqual(metadata.Body.Sha256, response.Data.Payload.Sha256, "core uses the exact published text");
                    AssertEqual(metadata.Body.Characters, response.TotalCharacters, "core extent is complete");
                    AssertEqual(original.BodyMarkdown, ReadSkillSourceDownload(data, response), "core read cannot borrow unpublished authoring text");
                    var builtin = published.First(item => item.BuiltIn);
                    request.SkillId = builtin.Id; request.ExpectedPackageRevision = SkillRevision.Compute(builtin);
                    var builtInResponse = reader.Open(session, request, CancellationToken.None);
                    AssertContains(builtInResponse.Resource.Uri, "/builtin-skills-word/", "builtin uses the same host's published catalog");
                    AssertEqual(builtin.BodyMarkdown, ReadSkillSourceDownload(data, builtInResponse), "builtins share source delivery without granting edit authority");
                    request.Type = SkillReferencePayload.ContractType;
                    RuntimeThrows<ResourceRequestException>(() => reader.Open(session, request, CancellationToken.None));
                    AssertEqual(0, session.Messages.Count, "body hydration does not create model evidence");
                }
            });
        }

        private static void SkillEditorPreservesUnreadBody()
        {
            WithTempPaths(paths =>
            {
                var store = new SkillStore(paths);
                var skill = store.SaveOne(new SkillDefinition { Id = "common.unread_editor", Name = "Unread", Description = "Before", BodyMarkdown = "# Unread\nSource" });
                var executor = new OfficeToolExecutor(FakeOfficeAdapter.ForHost("Word"), new VbaJournalStore(paths), store, new ToolStore(paths));
                var mutation = new SkillLibraryCoreMutation { Kind = "upsert", BaseId = skill.Id, ExpectedRevision = SkillRevision.Compute(skill),
                    PreserveBody = true, Intended = new SkillDefinition { Id = skill.Id, Name = "Unread", Description = "After", BodyMarkdown = null } };
                var saved = executor.ExecuteSkillLibraryMutation(mutation);
                AssertEqual(SkillAuthoringOutcomeStatus.Ok, saved.Outcome.Status, "explicit metadata-only save succeeds");
                AssertEqual(skill.BodyMarkdown, saved.Package.BodyMarkdown, "unread body is preserved by the guarded owner");
                AssertEqual("After", saved.Package.Description, "metadata change is verified");
                var stale = executor.ExecuteSkillLibraryMutation(mutation);
                AssertTrue(stale.Outcome.Status == SkillAuthoringOutcomeStatus.Error && !stale.DispatchPossible, "preserveBody does not bypass the complete package guard");
                mutation.ExpectedRevision = saved.Package.Revision; mutation.Intended.BodyMarkdown = "# Conflicting replacement";
                var ambiguous = executor.ExecuteSkillLibraryMutation(mutation);
                AssertTrue(ambiguous.Outcome.Status == SkillAuthoringOutcomeStatus.Error && !ambiguous.DispatchPossible, "preserve and replacement cannot mix");
                mutation.BaseId = null; mutation.ExpectedRevision = ""; mutation.Intended.Id = "common.new_unread"; mutation.Intended.BodyMarkdown = null;
                var missing = executor.ExecuteSkillLibraryMutation(mutation);
                AssertTrue(missing.Outcome.Status == SkillAuthoringOutcomeStatus.Error && !missing.DispatchPossible, "creation cannot preserve a missing body");
                AssertEqual(skill.BodyMarkdown, store.Load().Single().BodyMarkdown, "rejected requests do not change source");
            });
        }

        private static void SkillReferenceEditorBoundsAndLifetime()
        {
            WithTempPaths(paths =>
            {
                var store = new SkillStore(paths);
                var skill = store.SaveOne(new SkillDefinition { Id = "common.empty_editor", Name = "Empty", Description = "Empty reference test.", BodyMarkdown = "# Core" });
                string error; SkillReferenceMetadata metadata;
                AssertTrue(store.TrySaveReference(skill, "references/rules.md", "", out metadata, out error), "empty setup: " + error);
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), store, new ToolStore(paths));
                var captures = 0;
                var catalog = new SkillCatalogService(adapter, () => { captures++; return executor.CapturePublishedSkills(); });
                var session = NewSession(adapter);
                var request = SkillReferenceRequest(session, skill.Id, SkillRevision.Compute(store.Load().Single()));
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var reader = new SkillEditorResourceService(executor.ResourceGateway, data, catalog);
                    for (var i = 0; i < 2; i++) data.OpenDownload(session, "other", 1, _ => new ResourceDownloadContent { Bytes = new byte[0], ContentType = "text/plain" });
                    RuntimeThrows<ResourceRequestException>(() => reader.Open(session, request, CancellationToken.None));
                    AssertEqual(0, captures, "shared reservation precedes catalog hydration");
                    data.CloseTransfers(session.Id);
                    RuntimeThrows<OperationCanceledException>(() => reader.Open(session, request, new CancellationToken(true)));
                    AssertEqual(0, captures, "pre-cancelled open does not hydrate catalogs");
                    request.ChatId = "foreign";
                    RuntimeThrows<ResourceRequestException>(() => reader.Open(session, request, CancellationToken.None));
                    request.ChatId = session.Id; request.Path = "../rules.md";
                    RuntimeThrows<ResourceRequestException>(() => reader.Open(session, request, CancellationToken.None));
                    request.Path = "references/rules.md";
                    var empty = reader.Open(session, request, CancellationToken.None);
                    AssertEqual(0, empty.TotalCharacters, "empty reference is complete, not a missing body");
                    AssertEqual("", ReadSkillSourceDownload(data, empty), "empty reference uses the same exact transfer");
                }
            });
        }
    }
}
