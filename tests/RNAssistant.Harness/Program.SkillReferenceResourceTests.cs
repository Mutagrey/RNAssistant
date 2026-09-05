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
        private static SkillReferenceReadRequest SkillReferenceRequest(ChatSession session, string id, string revision)
        {
            return new SkillReferenceReadRequest { Type = SkillReferencePayload.ContractType, ContractVersion = 1,
                ChatId = session.Id, SkillId = id, Path = "references/rules.md", ExpectedPackageRevision = revision };
        }

        private static string ReadSkillReferenceDownload(ResourceDataPlaneService data, SkillReferenceReadResponse response)
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
                data.Close(response.ChatId, SkillReferenceResourceService.Owner, response.Data.LeaseId);
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
                    var reader = new SkillReferenceResourceService(executor.ResourceGateway, data, catalog);
                    var request = SkillReferenceRequest(session, skill.Id, SkillRevision.Compute(published));
                    var original = reader.Open(session, request, CancellationToken.None);
                    var dto = JObject.FromObject(original);
                    AssertTrue(dto["content"] == null && dto["skill"] == null && dto["result"] == null, "reference reads carry no source/core body or mutation result");
                    AssertTrue(original.Reference.Revision != original.Data.Payload.Sha256, "file BOM hash and published text transport hash are distinct");
                    AssertEqual(0, session.Messages.Count, "editor reads never grant model observations");
                    RuntimeThrows<ResourceRequestException>(() => data.Close("foreign", SkillReferenceResourceService.Owner, original.Data.LeaseId));
                    File.WriteAllText(file, "# External unpublished change");
                    var fresh = reader.Open(session, request, CancellationToken.None);
                    AssertEqual(body, ReadSkillReferenceDownload(data, fresh), "new reads use the committed catalog, not mutable authoring files");
                    var rejected = executor.ExecuteSkillLibraryReferenceMutation("upsert", skill.Id, request.Path, "# Stale edit", request.ExpectedPackageRevision);
                    AssertEqual(SkillAuthoringOutcomeStatus.Error, rejected.Outcome.Status, "stale editor save rejects disk drift");
                    AssertTrue(!rejected.DispatchPossible, "disk drift fails before dispatch");
                    AssertEqual("# External unpublished change", File.ReadAllText(file), "read and failed save preserve external text");
                    var changed = executor.ExecuteSkillLibraryReferenceMutation("upsert", skill.Id, request.Path, "# New publication",
                        SkillRevision.Compute(store.Load().Single(item => item.Id == skill.Id)));
                    AssertEqual(SkillAuthoringOutcomeStatus.Ok, changed.Outcome.Status, "existing mutation owner publishes the verified reference");
                    AssertEqual(body, ReadSkillReferenceDownload(data, original), "an open exact transfer survives later publication");
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() =>
                        reader.Open(session, request, CancellationToken.None)).ErrorCode, "old editor package cannot silently borrow a new publication");
                    request.ExpectedPackageRevision = changed.Package.Revision;
                    AssertEqual("# New publication", ReadSkillReferenceDownload(data, reader.Open(session, request, CancellationToken.None)), "fresh package reads its own source");
                    var exact = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = original.Resource, Representation = "text", MaxChars = 128 }).Result;
                    AssertEqual(body.Substring(0, 128), exact.Text, "editor and model share the same retained catalog reference");
                    File.WriteAllText(executor.Payloads.PathFor(original.Data.Payload.Sha256), "corrupt");
                    AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() =>
                        executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = original.Resource, Representation = "text" })).ErrorCode,
                        "corrupt historical source never falls back to the current file");
                }
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
                    var reader = new SkillReferenceResourceService(executor.ResourceGateway, data, catalog);
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
                    AssertEqual("", ReadSkillReferenceDownload(data, empty), "empty reference uses the same exact transfer");
                }
            });
        }
    }
}
