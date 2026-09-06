using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PromptEditorReadsExactSource()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var text = "\ufeff# Exact\r\n" + new string('ж', 40000) + "😀";
                var settings = new AppSettings { SystemPrompt = text, PlanSystemPrompt = "" };
                var loads = 0;
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                    () => { loads++; return settings; }, value => settings = value, paths);
                var metadata = executor.GetPromptLibrary(); loads = 0;
                var controls = SettingsControlsDto.From(settings);
                var projection = JObject.FromObject(new SettingsResponse { Settings = controls, Prompts = metadata });
                AssertEqual(8, metadata.Items.Count, "fixed metadata-only editor catalog");
                AssertTrue(!projection.ToString().Contains(text) && projection["settings"]["SystemPrompt"] == null, "no inline source in settings response");
                AssertTrue(JObject.FromObject(new InitResponse { Settings = controls, Prompts = metadata })["settings"]["PlanSystemPrompt"] == null,
                    "initialization uses the same body-free control type");
                var expectedControls = JObject.FromObject(settings);
                foreach (var key in PromptSettingsService.TemplateKeys.Where(key => key != "systemPromptRole"))
                    expectedControls.Remove(char.ToUpperInvariant(key[0]) + key.Substring(1));
                AssertTrue(JToken.DeepEquals(expectedControls, JObject.FromObject(controls)), "all non-body settings controls are retained");
                AssertTrue(JToken.DeepEquals(JObject.FromObject(settings), JObject.FromObject(controls.ApplyTo(settings))), "typed controls round-trip without losing templates");
                settings.SystemPrompt = "unpublished disk drift";
                var session = NewSession(adapter);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new PromptEditorResourceService(executor.ResourceGateway, data);
                    foreach (var key in new[] { "systemPrompt", "planSystemPrompt" })
                    {
                        var exact = metadata.Items.Single(item => item.Key == key).Resource;
                        var response = editor.Open(session, new PromptSourceReadRequest { ChatId = session.Id, Resource = exact }, CancellationToken.None);
                        AssertTrue(JObject.FromObject(response)["text"] == null, "read bridge returns only metadata and a download capability");
                        string mime;
                        var bytes = response.Data.Payload.ByteLength == 0 ? new byte[0] :
                            data.ReadDownload(response.Data.LeaseId, 0, (int)response.Data.Payload.ByteLength, CancellationToken.None, out mime);
                        AssertEqual(key == "systemPrompt" ? text : "", new UTF8Encoding(false, true).GetString(bytes), "complete exact text, BOM/CRLF/Unicode/empty retained");
                        RuntimeThrows<ResourceRequestException>(() => data.Close("foreign", PromptEditorResourceService.Owner, response.Data.LeaseId));
                        data.Close(session.Id, PromptEditorResourceService.Owner, response.Data.LeaseId);
                    }
                    RuntimeThrows<ResourceRequestException>(() => editor.Open(session, new PromptSourceReadRequest {
                        ChatId = session.Id, Resource = new ResourceRef("rna://catalog/prompts/systemPromptRole", metadata.Publication.Revision) }, CancellationToken.None));
                    RuntimeThrows<OperationCanceledException>(() => editor.Open(session, new PromptSourceReadRequest {
                        ChatId = session.Id, Resource = metadata.Items[0].Resource }, new CancellationToken(true)));
                    var view = ((IResourceRevisionStore)executor.ResourceAuthority.Store).GetRevision(CatalogPublicationService.ScopeId, metadata.Publication);
                    File.WriteAllText(executor.Payloads.PathFor(view.Payload.Sha256), "corrupt");
                    RuntimeThrows<ResourceRequestException>(() => editor.Open(session, new PromptSourceReadRequest {
                        ChatId = session.Id, Resource = metadata.Items.Single(item => item.Key == "chatTitlePrompt").Resource }, CancellationToken.None));
                }
                AssertEqual(0, loads, "source reads never load settings or regenerate defaults");
                AssertEqual(0, session.Messages.Count, "editor reads create no model evidence");
                AssertEqual(0, adapter.VbaBackendCalls.Count, "prompt source never reads Office");
            });
        }

        private static SaveSettingsPayload UploadPromptMutation(ResourceDataPlaneService data, PromptEditorResourceService editor,
            ChatSession session, byte[] bytes, bool partial = false)
        {
            var lease = editor.BeginUpload(session, new PromptMutationUploadRequest { ChatId = session.Id, ByteLength = bytes.Length }, CancellationToken.None);
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(lease.MaxChunkBytes, bytes.Length - offset);
                if (partial) count = Math.Max(1, count - 1);
                using (var body = new MemoryStream(bytes, offset, count)) data.WriteUpload(lease.LeaseId, offset, count, body, CancellationToken.None);
                offset += count; if (partial) break;
            }
            using (var sha = SHA256.Create()) return new SaveSettingsPayload { ChatId = session.Id, UploadLeaseId = lease.LeaseId,
                Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
        }

        private static void PromptEditorUploadAndGuardedSave()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var settings = new AppSettings { Model = "global-model", PlanSystemPrompt = "preserved plan", AgentPromptSchemaVersion = 0 };
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths), () => settings, value => settings = value, paths);
                var metadata = executor.GetPromptLibrary(); var session = NewSession(adapter);
                var text = "\ufeff# Uploaded\r\n" + new string('я', 70000) + "😀";
                var batch = new PromptMutationBatch { Type = PromptMutationBatch.ContractType, ContractVersion = 1,
                    Changes = new[] { new PromptFieldChange { Resource = metadata.Items[0].Resource, Value = text } } };
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new PromptEditorResourceService(executor.ResourceGateway, data);
                    var request = UploadPromptMutation(data, editor, session, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch)));
                    var parsed = editor.ReadMutation(session, request, CancellationToken.None);
                    RuntimeThrows<ResourceRequestException>(() => editor.ReadMutation(session, request, CancellationToken.None));
                    request.Settings = SettingsControlsDto.From(settings); request.ExpectedPromptPublication = metadata.Publication;
                    var saves = 0;
                    executor.SaveSettingsControls(settings, request, parsed, value => { saves++; settings = value; });
                    AssertEqual(text, settings.SystemPrompt, "complete changed field reaches the existing writer");
                    AssertEqual("preserved plan", settings.PlanSystemPrompt, "unloaded omitted fields stay unchanged");
                    AssertEqual("global-model", settings.Model, "global model control survives");
                    AssertEqual(0, settings.AgentPromptSchemaVersion, "ordinary save does not acknowledge prompt review");
                    AssertTrue(executor.GetPromptLibrary().Publication.Revision != metadata.Publication.Revision, "verified save publishes its new exact root");
                    var count = executor.ResourceAuthority.CaptureMany(new[] { CatalogPublicationService.ScopeId }).Get(CatalogPublicationService.ScopeId).Generation;
                    RuntimeThrows<ResourceRequestException>(() => executor.SaveSettingsControls(settings, request, parsed, value => saves++));
                    AssertEqual(1, saves, "stale snapshot never dispatches another write");
                    AssertEqual(count, executor.ResourceAuthority.CaptureMany(new[] { CatalogPublicationService.ScopeId }).Get(CatalogPublicationService.ScopeId).Generation, "stale refusal does not poison the head");
                    AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count, "refused preparation is abandoned before dispatch");
                    request.ExpectedPromptPublication = executor.GetPromptLibrary().Publication;
                    request.Settings = SettingsControlsDto.From(settings);
                    RuntimeThrows<ResourceRequestException>(() => executor.SaveSettingsControls(settings, request, parsed, value => saves++));
                    var priorTools = settings.AgentToolsPrompt;
                    settings.AgentToolsPrompt = "unpublished drift";
                    RuntimeThrows<ResourceRequestException>(() => executor.SaveSettingsControls(settings, request, null, value => saves++));
                    AssertEqual(1, saves, "live drift is not overwritten from a committed snapshot");
                    settings.AgentToolsPrompt = priorTools;
                    request.Settings.Model = "changed control";
                    executor.SaveSettingsControls(settings, request, null, value => settings = value);
                    AssertEqual("changed control", settings.Model, "body-free settings-only saves still apply controls");
                    AssertEqual(request.ExpectedPromptPublication.Revision, executor.GetPromptLibrary().Publication.Revision, "unchanged templates preserve their publication");
                    RuntimeThrows<IOException>(() => executor.SaveSettingsControls(settings, request, null, value => { settings = value; throw new IOException("possible save"); }));
                    AssertEqual("RESOURCE_HEAD_UNKNOWN", RuntimeThrows<ResourceRequestException>(() => executor.GetPromptLibrary()).ErrorCode,
                        "possible effect without successful read-back stays unknown and is not retried");
                }
            });
        }

        private static void PromptEditorRejectsInvalidUploads()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word"); var settings = new AppSettings();
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths), () => settings, value => settings = value, paths);
                var metadata = executor.GetPromptLibrary(); var session = NewSession(adapter);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new PromptEditorResourceService(executor.ResourceGateway, data);
                    var change = new PromptFieldChange { Resource = metadata.Items[0].Resource, Value = "valid" };
                    var batch = new PromptMutationBatch { Type = PromptMutationBatch.ContractType, ContractVersion = 1, Changes = new[] { change } };
                    var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch));
                    var request = UploadPromptMutation(data, editor, session, bytes);
                    var foreign = NewSession(adapter); foreign.Id = "foreign";
                    RuntimeThrows<ResourceRequestException>(() => editor.ReadMutation(foreign, new SaveSettingsPayload {
                        ChatId = foreign.Id, UploadLeaseId = request.UploadLeaseId, Sha256 = request.Sha256 }, CancellationToken.None));
                    RuntimeThrows<ResourceRequestException>(() => data.CloseUpload(session.Id, request.UploadLeaseId, SkillEditorResourceService.Owner));
                    AssertEqual("valid", editor.ReadMutation(session, request, CancellationToken.None).Changes[0].Value, "foreign consumers cannot destroy the rightful upload");
                    foreach (var mode in new[] { "hash", "partial", "json", "utf8", "shape", "duplicate", "oversized", "null", "cancel" })
                    {
                        var input = bytes;
                        if (mode == "json") input = Encoding.UTF8.GetBytes("{} trailing");
                        if (mode == "utf8") input = new byte[] { 255 };
                        if (mode == "shape") input = Encoding.UTF8.GetBytes("{\"unknown\":true}");
                        if (mode == "duplicate") { batch.Changes = new[] { change, change }; input = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch)); }
                        if (mode == "oversized" || mode == "null") { batch.Changes = new[] { change }; change.Value = mode == "null" ? null : new string('x', 100001); input = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch)); }
                        request = UploadPromptMutation(data, editor, session, input, mode == "partial");
                        if (mode == "hash") request.Sha256 = new string('0', 64);
                        if (mode == "cancel") RuntimeThrows<OperationCanceledException>(() => editor.ReadMutation(session, request, new CancellationToken(true)));
                        else RuntimeThrows<InvalidOperationException>(() => editor.ReadMutation(session, request, CancellationToken.None));
                        RuntimeThrows<ResourceRequestException>(() => editor.ReadMutation(session, request, CancellationToken.None));
                    }
                    RuntimeThrows<ResourceRequestException>(() => editor.BeginUpload(session, new PromptMutationUploadRequest {
                        ChatId = session.Id, ByteLength = PromptEditorResourceService.MaximumMutationBytes + 1L }, CancellationToken.None));
                    AssertEqual(metadata.Publication.Revision, executor.GetPromptLibrary().Publication.Revision, "invalid upload never publishes or saves");
                }
            });
        }
    }
}
