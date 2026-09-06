using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
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
                    var editor = new ToolEditorResourceService(data); var batch = ToolUploadBatch("excel.uploaded");
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
                    var editor = new ToolEditorResourceService(data); var batch = ToolUploadBatch("excel.bounded");
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
