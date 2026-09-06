using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ToolLibraryDocumentationRequest ToolDocumentationRequest(ChatSession session, ToolCatalogEntry tool)
        { return new ToolLibraryDocumentationRequest { Type = ToolLibraryDocumentationRequest.ContractType, ContractVersion = 1,
            ChatId = session.Id, ToolId = tool.Id, ExpectedRevision = ToolAuthoringService.LibraryRevision(tool) }; }

        private static void ToolEditorReadsPublishedDocumentation()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel"); var skills = new SkillStore(paths); var tools = new ToolStore(paths);
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), skills, tools);
                var session = NewSession(adapter); var tool = executor.GetControllerTools().Single(item => item.Id == "common.resources_read");
                var catalog = new ToolCatalogService(adapter, executor);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new ToolEditorResourceService(executor.ResourceGateway, data, catalog);
                    var request = ToolDocumentationRequest(session, tool);
                    var response = editor.OpenDocumentation(session, request, CancellationToken.None);
                    var metadata = JObject.FromObject(response);
                    AssertTrue(metadata["markdown"] == null && metadata["code"] == null && metadata["data"] != null, "only metadata and a shared download lease cross the bridge");
                    AssertEqual("rna://catalog/builtin-tools-excel/common.resources_read/documentation", response.Resource.Uri, "exact builtin documentation child");
                    AssertEqual(0, adapter.VbaBackendCalls.Count, "documentation never discovers or reads Office/VBA");
                    AssertEqual(0, session.Messages.Count, "reading human documentation grants no model evidence");
                    var expected = ToolLibraryDocumentationService.Build(tool);
                    var root = new ResourceRef("rna://catalog/builtin-tools-excel", response.Resource.Revision);
                    var revisions = (IResourceRevisionStore)executor.ResourceAuthority.Store;
                    var view = revisions.GetView(CatalogPublicationService.ScopeId, root, "catalog-state");
                    AssertTrue(view.Parts.Any(part => part.Sha256 == response.Data.Payload.Sha256), "the published root retains documentation CAS parts");
                    RuntimeThrows<ResourceRequestException>(() => data.Close("foreign", ToolEditorResourceService.Owner, response.Data.LeaseId));
                    var changed = tool.Clone(); changed.Description += " A later build.";
                    var publisher = new CatalogPublicationService(executor.ResourceAuthority, new ResourceMutationJournal(paths), tools, skills, () => "{}", adapter);
                    publisher.RegisterBuiltInTools(new[] { changed });
                    AssertTrue(!publisher.ReadPublic(publisher.Current(publisher.BuiltInToolsKind)).Contains("## Аргументы"), "public catalog never expands generated human docs");
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() =>
                        editor.OpenDocumentation(session, request, CancellationToken.None)).ErrorCode, "a different registration publication cannot impersonate current runtime docs");
                    using (var output = new MemoryStream())
                    {
                        for (var offset = 0; offset < response.Data.Payload.ByteLength;)
                        {
                            string mime;
                            var bytes = data.ReadDownload(response.Data.LeaseId, offset,
                                (int)Math.Min(512, response.Data.Payload.ByteLength - offset), CancellationToken.None, out mime);
                            AssertEqual("text/markdown; charset=utf-8", mime, "inert documentation transport");
                            output.Write(bytes, 0, bytes.Length); offset += bytes.Length;
                        }
                        AssertEqual(expected, new UTF8Encoding(false, true).GetString(output.ToArray()), "open exact download survives later publication");
                    }
                    data.Close(session.Id, ToolEditorResourceService.Owner, response.Data.LeaseId);
                    var retained = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = response.Resource, Representation = "text", MaxChars = 64 }).Result;
                    AssertEqual(expected.Substring(0, 64), retained.Text, "historical document shares the normal retained Gateway path");
                }
            });
        }

        private static void ToolEditorDocumentationFailsClosed()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths));
                var session = NewSession(adapter); var tool = executor.GetControllerTools().First();
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new ToolEditorResourceService(executor.ResourceGateway, data, new ToolCatalogService(adapter, executor));
                    var request = ToolDocumentationRequest(session, tool);
                    for (var index = 0; index < 2; index++) data.OpenDownload(session, "other", 1,
                        _ => new ResourceDownloadContent { Bytes = new byte[] { 1 }, ContentType = "text/plain" });
                    RuntimeThrows<ResourceRequestException>(() => editor.OpenDocumentation(session, request, CancellationToken.None));
                    RuntimeThrows<OperationCanceledException>(() => editor.OpenDocumentation(session, request, new CancellationToken(true)));
                    data.CloseTransfers();
                    request.ChatId = "foreign";
                    RuntimeThrows<ResourceRequestException>(() => editor.OpenDocumentation(session, request, CancellationToken.None));
                    request.ChatId = session.Id; request.ExpectedRevision = "stale";
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => editor.OpenDocumentation(session, request, CancellationToken.None)).ErrorCode, "displayed tool revision is required");
                    request = ToolDocumentationRequest(session, tool); request.ToolId = "excel.custom";
                    AssertEqual("RESOURCE_NOT_FOUND", RuntimeThrows<ResourceRequestException>(() => editor.OpenDocumentation(session, request, CancellationToken.None)).ErrorCode, "documentation route cannot borrow custom source");
                    request = ToolDocumentationRequest(session, tool);
                    var response = editor.OpenDocumentation(session, request, CancellationToken.None);
                    data.Close(session.Id, ToolEditorResourceService.Owner, response.Data.LeaseId);
                    File.WriteAllText(executor.Payloads.PathFor(response.Data.Payload.Sha256), "corrupt");
                    AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => editor.OpenDocumentation(session, request, CancellationToken.None)).ErrorCode,
                        "missing/corrupt published documentation never falls back to the live generator");
                    AssertEqual(0, adapter.VbaBackendCalls.Count, "no Office access even on failures");
                }
            });
        }

        private static ToolSourceReadRequest ToolSourceRequest(ChatSession session, ToolCatalogEntry tool)
        { return new ToolSourceReadRequest { Type = ToolSourceReadRequest.ContractType, ContractVersion = 1, ChatId = session.Id,
            ToolId = tool.Id, ExpectedRevision = ToolAuthoringService.LibraryRevision(tool) }; }

        private static string ReadToolSourceDownload(ResourceDataPlaneService data, ToolSourceReadResponse response)
        {
            using (var output = new MemoryStream())
            {
                for (var offset = 0; offset < response.Data.Payload.ByteLength;)
                {
                    string mime;
                    var bytes = data.ReadDownload(response.Data.LeaseId, offset,
                        (int)Math.Min(response.Data.MaxChunkBytes, response.Data.Payload.ByteLength - offset), CancellationToken.None, out mime);
                    AssertEqual("application/json; charset=utf-8", mime, "inert typed source transfer");
                    output.Write(bytes, 0, bytes.Length); offset += bytes.Length;
                }
                data.Close(response.ChatId, ToolEditorResourceService.Owner, response.Data.LeaseId);
                return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
        }

        private static void ToolEditorReadsPublishedSource()
        {
            WithTempPaths(paths =>
            {
                var store = new ToolStore(paths); var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store);
                var batch = ToolUploadBatch("excel.source");
                var saved = executor.ExecuteToolLibraryMutation(new ToolLibraryCoreMutation { Kind = "upsert", Intended = batch.Mutations[0].ToCatalogEntry() });
                AssertEqual(ToolAuthoringOutcomeStatus.Ok, saved.Outcome.Status, "published source fixture");
                var catalog = new ToolCatalogService(adapter, executor); var session = NewSession(adapter);
                var tool = catalog.GetVisibleTools().Single(item => item.Id == "excel.source");
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new ToolEditorResourceService(executor.ResourceGateway, data, catalog);
                    var request = ToolSourceRequest(session, tool);
                    var original = editor.Open(session, request, CancellationToken.None);
                    var metadata = ToolLibraryItemDto.From(tool);
                    AssertEqual(metadata.Source.Sha256, original.Data.Payload.Sha256, "list pins exact source bytes");
                    AssertTrue(JObject.FromObject(metadata)["code"] == null && JObject.FromObject(original)["readme"] == null, "controls contain no source bodies");
                    AssertEqual(1, original.Sources.Count, "one immutable catalog source");
                    var changed = batch.Mutations[0].ToCatalogEntry(); changed.Readme = "# Changed";
                    var update = executor.ExecuteToolLibraryMutation(new ToolLibraryCoreMutation { Kind = "upsert", BaseId = tool.Id,
                        ExpectedRevision = saved.Revision, Intended = changed });
                    AssertEqual(ToolAuthoringOutcomeStatus.Ok, update.Outcome.Status, "publication changed through existing owner");
                    var text = ReadToolSourceDownload(data, original);
                    AssertEqual(batch.Mutations[0].Readme, (string)JObject.Parse(text)["readme"], "open transfer retains original Unicode/CRLF source");
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => editor.Open(session, request, CancellationToken.None)).ErrorCode,
                        "old package cannot borrow a new publication");
                    var historical = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = original.Sources[0], Representation = "text", MaxChars = 64 }).Result;
                    AssertEqual(text.Substring(0, 64), historical.Text, "Gateway retains the same historical child");
                    File.WriteAllText(Path.Combine(store.Load().Single().StoragePath, "README.md"), "unpublished edit");
                    tool = catalog.GetVisibleTools().Single(item => item.Id == tool.Id);
                    AssertEqual("# Changed", (string)JObject.Parse(ReadToolSourceDownload(data, editor.Open(session, ToolSourceRequest(session, tool), CancellationToken.None)))["readme"],
                        "editor never falls back to authoring disk");
                    var builtin = catalog.GetVisibleTools().First(item => item.BuiltIn);
                    var builtinRead = editor.Open(session, ToolSourceRequest(session, builtin), CancellationToken.None);
                    AssertContains(builtinRead.Sources[0].Uri, "rna://catalog/builtin-tools-excel/", "source-owned builtin publication");
                    var builtinBody = JObject.Parse(ReadToolSourceDownload(data, builtinRead));
                    AssertEqual(builtin.ArgumentSchemaJson, (string)builtinBody["argumentSchemaJson"], "exact runtime schema");
                    AssertEqual("", (string)builtinBody["readme"], "human documentation remains a separate view");
                    AssertEqual(0, session.Messages.Count, "editor reads grant no model observations");
                }
            });
        }

        private static void ToolEditorReadsExactDocumentSource()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var tool = CustomTool("Excel", "excel.document_source");
                adapter.SetVbaModule("RNA_Test", tool.Code, "StdModule");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths));
                var catalog = new ToolCatalogService(adapter, executor); var session = NewSession(adapter); executor.BindResourceAuthority(session);
                tool = catalog.GetVisibleTools().Single(item => item.Id == tool.Id);
                AssertEqual("document", tool.Scope, "document-local fixture");
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new ToolEditorResourceService(executor.ResourceGateway, data, catalog); var request = ToolSourceRequest(session, tool);
                    for (var index = 0; index < 2; index++) data.OpenDownload(session, "other", 1, _ => new ResourceDownloadContent { Bytes = new byte[] { 1 }, ContentType = "text/plain" });
                    var reads = adapter.CountVbaCalls(FakeVbaOperation.ReadModule);
                    RuntimeThrows<ResourceRequestException>(() => editor.Open(session, request, CancellationToken.None));
                    RuntimeThrows<OperationCanceledException>(() => editor.Open(session, request, new CancellationToken(true)));
                    AssertEqual(reads, adapter.CountVbaCalls(FakeVbaOperation.ReadModule), "reserve shared capacity/cancel before live reads");
                    data.CloseTransfers();
                    var original = editor.Open(session, request, CancellationToken.None);
                    AssertEqual(VbaResourceProvider.ComponentIdentity(session.DocumentAuthorityId, "RNA_Test").Uri, original.Sources[0].Uri, "exact document authority, no aggregate catalog");
                    RuntimeThrows<ResourceRequestException>(() => data.Close("foreign", ToolEditorResourceService.Owner, original.Data.LeaseId));
                    RuntimeThrows<ResourceRequestException>(() => data.Close(session.Id, SkillEditorResourceService.Owner, original.Data.LeaseId));
                    var changed = tool.Code + "\n' Changed live source";
                    adapter.SetVbaModule("RNA_Test", changed, "StdModule");
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => editor.Open(session, request, CancellationToken.None)).ErrorCode,
                        "live drift fails exact cached source proof");
                    AssertEqual(tool.Code, (string)JObject.Parse(ReadToolSourceDownload(data, original))["components"][0]["code"], "open source is immutable across live drift");
                    var refreshed = catalog.GetVisibleTools().Single(item => item.Id == tool.Id);
                    AssertEqual(changed, (string)JObject.Parse(ReadToolSourceDownload(data, editor.Open(session, ToolSourceRequest(session, refreshed), CancellationToken.None)))["components"][0]["code"],
                        "drift invalidates the document discovery cache for explicit refresh");
                    session.LastRun = new ChatRunRecord { DocumentRuntimeKey = "wrong-runtime" };
                    RuntimeThrows<ResourceRequestException>(() => editor.Open(session, ToolSourceRequest(session, refreshed), CancellationToken.None));
                    AssertEqual(0, session.Messages.Count, "no model observations");
                }
            });
        }

        private static ToolMutationWriteRequest UploadToolMutation(ResourceDataPlaneService data, ToolEditorResourceService editor,
            ChatSession session, byte[] bytes, bool partial = false)
        {
            var lease = editor.BeginUpload(session, new ToolMutationUploadRequest { ChatId = session.Id, ByteLength = bytes.Length }, CancellationToken.None);
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(lease.MaxChunkBytes, bytes.Length - offset);
                using (var body = new MemoryStream(bytes, offset, count)) data.WriteUpload(lease.LeaseId, offset, count, body, CancellationToken.None);
                offset += count;
                if (partial) break;
            }
            using (var sha = SHA256.Create())
                return new ToolMutationWriteRequest { ChatId = session.Id, UploadLeaseId = lease.LeaseId,
                    Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
        }

        private static ToolLibraryMutationBatch ToolUploadBatch(string id)
        {
            var tool = CustomTool("Excel", id);
            tool.Code += "\r\n' Точный исходник 😀\r\n";
            tool.Components[0].Code = tool.Code;
            return new ToolLibraryMutationBatch { Type = ToolLibraryMutationBatch.ContractType, ContractVersion = 1,
                Mutations = new List<ToolCoreMutationPayload> { new ToolCoreMutationPayload {
                    Kind = "upsert", Id = tool.Id, Host = tool.Host, Name = tool.Name, Description = tool.Description,
                    Code = tool.Code, Readme = "# Справка\r\n" + new string('ж', 140000), Enabled = true,
                    Executor = "vba", ArgumentSchemaJson = tool.ArgumentSchemaJson, RequiresConfirmation = true, MutatesDocument = true,
                    RiskLevel = 1, Components = tool.Components.Select(ToolPackageComponentDto.From).ToList() } } };
        }

        private static void ToolEditorUploadUsesMutationOwner()
        {
            WithTempPaths(paths =>
            {
                var store = new ToolStore(paths); var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store);
                var session = NewSession(adapter);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new ToolEditorResourceService(executor.ResourceGateway, data, new ToolCatalogService(adapter, executor)); var batch = ToolUploadBatch("excel.uploaded");
                    var request = UploadToolMutation(data, editor, session, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch)));
                    var prepared = editor.PrepareMutations(session, request, CancellationToken.None);
                    AssertEqual(0, store.Load().Count, "upload preparation never writes a package");
                    AssertEqual(0, executor.CapturePublishedTools().Count, "upload cannot activate a catalog");
                    AssertEqual(batch.Mutations[0].Code, prepared[0].Intended.Code, "code is exact UTF-8 input");
                    AssertEqual(batch.Mutations[0].Code, prepared[0].Intended.Components[0].Code, "typed component source stays native");
                    RuntimeThrows<ResourceRequestException>(() => editor.PrepareMutations(session, request, CancellationToken.None));
                    var saved = executor.ExecuteToolLibraryMutation(prepared[0]);
                    AssertEqual(ToolAuthoringOutcomeStatus.Ok, saved.Outcome.Status, "same guarded mutation owner saves the upload: " + saved.Outcome.Message);
                    AssertEqual(batch.Mutations[0].Readme, executor.CapturePublishedTools().Single().Readme, "verified catalog publication retains exact documentation");
                    var stale = executor.ExecuteToolLibraryMutation(prepared[0]);
                    AssertTrue(stale.Outcome.Status == ToolAuthoringOutcomeStatus.Error && !stale.DispatchPossible, "an uploaded create does not bypass existing-target guards");
                    batch.Mutations[0].BaseId = batch.Mutations[0].Id; batch.Mutations[0].ExpectedRevision = saved.Revision;
                    batch.Mutations[0].Readme = "";
                    request = UploadToolMutation(data, editor, session, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch)));
                    var updated = executor.ExecuteToolLibraryMutation(editor.PrepareMutations(session, request, CancellationToken.None)[0]);
                    AssertEqual(ToolAuthoringOutcomeStatus.Ok, updated.Outcome.Status, "guarded update follows the same path");
                    AssertEqual("", store.Load().Single().Readme, "empty documentation is not an absent upload");
                    AssertEqual(0, session.Messages.Count, "no model observations or chat artifacts are created");
                }
            });
        }

        private static void ToolEditorUploadBounds()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths));
                var session = NewSession(adapter);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new ToolEditorResourceService(executor.ResourceGateway, data, new ToolCatalogService(adapter, executor)); var batch = ToolUploadBatch("excel.bounded");
                    var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch));
                    var request = UploadToolMutation(data, editor, session, bytes);
                    var foreign = NewSession(adapter); foreign.Id = "foreign";
                    RuntimeThrows<ResourceRequestException>(() => editor.PrepareMutations(foreign,
                        new ToolMutationWriteRequest { ChatId = foreign.Id, UploadLeaseId = request.UploadLeaseId, Sha256 = request.Sha256 }, CancellationToken.None));
                    RuntimeThrows<ResourceRequestException>(() => data.CloseUpload(session.Id, request.UploadLeaseId, SkillEditorResourceService.Owner));
                    AssertEqual(1, editor.PrepareMutations(session, request, CancellationToken.None).Count, "foreign chat/consumer cannot destroy the rightful capability");
                    foreach (var mode in new[] { "hash", "partial", "json", "utf8", "shape", "cancel", "batch" })
                    {
                        var input = bytes;
                        if (mode == "json") input = Encoding.UTF8.GetBytes("{} trailing");
                        if (mode == "utf8") input = new byte[] { 255 };
                        if (mode == "shape") input = Encoding.UTF8.GetBytes("{\"unknown\":true}");
                        if (mode == "batch") { batch.Mutations.Add(batch.Mutations[0]); input = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch)); }
                        request = UploadToolMutation(data, editor, session, input, mode == "partial");
                        if (mode == "hash") request.Sha256 = new string('0', 64);
                        if (mode == "cancel") RuntimeThrows<OperationCanceledException>(() => editor.PrepareMutations(session, request, new CancellationToken(true)));
                        else RuntimeThrows<InvalidOperationException>(() => editor.PrepareMutations(session, request, CancellationToken.None));
                        RuntimeThrows<ResourceRequestException>(() => editor.PrepareMutations(session, request, CancellationToken.None));
                    }
                    RuntimeThrows<ResourceRequestException>(() => editor.BeginUpload(session,
                        new ToolMutationUploadRequest { ChatId = session.Id, ByteLength = ToolEditorResourceService.MaximumMutationBytes + 1L }, CancellationToken.None));
                    for (var i = 0; i < 4; i++) editor.BeginUpload(session, new ToolMutationUploadRequest { ChatId = session.Id, ByteLength = 1 }, CancellationToken.None);
                    RuntimeThrows<ResourceRequestException>(() => editor.BeginUpload(session,
                        new ToolMutationUploadRequest { ChatId = session.Id, ByteLength = 1 }, CancellationToken.None));
                    AssertEqual(0, executor.CapturePublishedTools().Count, "invalid input never reaches mutation/publication");
                }
            });
        }
    }
}
