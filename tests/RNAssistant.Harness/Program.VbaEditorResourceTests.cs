using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void VbaEditorUsesExactResourceSource()
        {
            WithTempPaths(paths =>
            {
                var before = "Option Explicit\r\n'" + new string('ж', 40000) + "\r\nSub Main()\r\nEnd Sub\r\n";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = before };
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new VbaEditorResourceService(executor.ResourceGateway, data);
                    var original = editor.Open(session, "Module1", CancellationToken.None);
                    AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReadModule), "one full live capture for the editor");
                    AssertEqual(before.Length, original.TotalCharacters, "complete source extent");
                    AssertTrue(JObject.FromObject(original)["code"] == null, "no source code in bridge metadata");
                    AssertTrue(original.Data.Payload.Sha256 != original.CodeSha256, "raw transport bytes and normalized VBA write guard are distinct");
                    AssertEqual(0, session.Messages.Count, "editor read does not append a model-visible observation");
                    AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() =>
                        data.Close("foreign", VbaEditorResourceService.Owner, original.Data.LeaseId)).ErrorCode, "foreign chat cannot revoke source");
                    var changed = "Sub Changed()\nEnd Sub";
                    adapter.VbaModuleCode = changed;
                    var fresh = editor.Open(session, "Module1", CancellationToken.None);
                    AssertTrue(fresh.Resource.Revision != original.Resource.Revision, "fresh open publishes the observed live change");
                    data.Close(session.Id, VbaEditorResourceService.Owner, fresh.Data.LeaseId);
                    var reads = adapter.CountVbaCalls(FakeVbaOperation.ReadModule);
                    var historical = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                        Reference = original.Resource, Representation = ResourceRepresentations.Source, MaxChars = 128 }).Result;
                    AssertEqual(before.Substring(0, 128), historical.Text, "shared Gateway can read the old editor snapshot after drift");
                    AssertEqual(reads, adapter.CountVbaCalls(FakeVbaOperation.ReadModule), "historical resource read does not consult live VBA");
                    using (var output = new MemoryStream())
                    {
                        for (var offset = 0; offset < original.Data.Payload.ByteLength;)
                        {
                            string mime;
                            var count = (int)Math.Min(65536, original.Data.Payload.ByteLength - offset);
                            var bytes = data.ReadDownload(original.Data.LeaseId, offset, count, CancellationToken.None, out mime);
                            AssertEqual("text/plain; charset=utf-8", mime, "source delivery is inert");
                            output.Write(bytes, 0, bytes.Length); offset += count;
                        }
                        AssertEqual(before, new UTF8Encoding(false, true).GetString(output.ToArray()), "complete delivered bytes retain exact CRLF and Unicode");
                    }
                    AssertEqual(reads, adapter.CountVbaCalls(FakeVbaOperation.ReadModule), "UI download never recaptures Office");
                    data.Close(session.Id, VbaEditorResourceService.Owner, original.Data.LeaseId);
                    var command = Command("common.vba_write_module", "moduleName", "Module1", "code", "Sub Edited()\nEnd Sub", "mode", "updateOnly");
                    command.ExpectedContentSha256 = original.CodeSha256;
                    var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                    var rejected = executor.ExecuteManual(command, tools, new AppSettings(), false, true, session);
                    AssertTrue(!rejected.Success, "old editor source cannot overwrite a changed module");
                    AssertEqual(changed, adapter.VbaModuleCode, "stale editor save preserves live user changes");
                    command.ExpectedContentSha256 = fresh.CodeSha256;
                    AssertTrue(executor.ExecuteManual(command, tools, new AppSettings(), false, true, session).Success,
                        "the same existing write path accepts the fresh exact source guard");
                    File.WriteAllText(executor.Payloads.PathFor(original.Data.Payload.Sha256), "corrupt");
                    reads = adapter.CountVbaCalls(FakeVbaOperation.ReadModule);
                    AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() =>
                        executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = original.Resource,
                            Representation = ResourceRepresentations.Source, MaxChars = 128 })).ErrorCode, "corrupt old snapshot has no live fallback");
                    AssertEqual(reads, adapter.CountVbaCalls(FakeVbaOperation.ReadModule), "corruption never rebinds to current VBA");
                }
            });
        }

        private static void VbaEditorRejectsIncompleteSource()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new VbaEditorResourceService(executor.ResourceGateway, data);
                    for (var index = 0; index < 2; index++)
                        data.OpenDownload(session, "other", 1, _ => new ResourceDownloadContent { Bytes = new byte[] { 1 }, ContentType = "text/plain" });
                    var reads = adapter.CountVbaCalls(FakeVbaOperation.ReadModule);
                    RuntimeThrows<ResourceRequestException>(() => editor.Open(session, "Module1", CancellationToken.None));
                    AssertEqual(reads, adapter.CountVbaCalls(FakeVbaOperation.ReadModule), "shared capacity is reserved before live capture");
                    data.CloseTransfers();
                    RuntimeThrows<OperationCanceledException>(() => editor.Open(session, "Module1", new CancellationToken(true)));
                    AssertEqual(reads, adapter.CountVbaCalls(FakeVbaOperation.ReadModule), "cancelled open never touches VBA");
                    adapter.VbaModuleCode = new string('x', VbaEditorResourceService.MaximumCharacters + 1);
                    AssertEqual("vba_editor_source_truncated", RuntimeThrows<ResourceRequestException>(() =>
                        editor.Open(session, "Module1", CancellationToken.None)).ErrorCode, "partial source cannot become editable");
                    var identity = VbaResourceProvider.ComponentIdentity(session.DocumentAuthorityId, "Module1");
                    var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId));
                    var head = executor.ResourceAuthority.Store.GetHead(scope, identity);
                    var view = ((IResourceRevisionStore)executor.ResourceAuthority.Store).GetView(scope, head.Revision, ResourceRepresentations.Source);
                    AssertTrue(view.Coverage.Kind != ResourceCoverageKinds.Whole, "truncation cannot publish a complete source snapshot");
                    adapter.QueueVbaModuleSnapshot(new VbaModuleSnapshot { Name = "Module1", ComponentType = "StdModule",
                        Code = adapter.VbaModuleCode, CodeSha256 = VbaTextCanonicalizer.LiveCodeSha256(adapter.VbaModuleCode), Truncated = false });
                    var blobs = Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length;
                    AssertEqual("RESOURCE_BATCH_TOO_LARGE", RuntimeThrows<ResourceRequestException>(() =>
                        editor.Open(session, "Module1", CancellationToken.None)).ErrorCode, "an overproducing backend fails before CAS capture");
                    AssertEqual(blobs, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length,
                        "an oversized claimed-complete source cannot create a CAS publication");
                    RuntimeThrows<ResourceRequestException>(() => editor.Open(session, "Missing", CancellationToken.None));
                    adapter.VbaModuleCode = string.Empty;
                    var empty = editor.Open(session, "Module1", CancellationToken.None);
                    AssertEqual(0, empty.TotalCharacters, "empty module is a complete editable source");
                    AssertEqual(0L, empty.Data.Payload.ByteLength, "empty source uses the same exact transfer");
                    data.Close(session.Id, VbaEditorResourceService.Owner, empty.Data.LeaseId);
                    session.LastRun = new ChatRunRecord { DocumentRuntimeKey = "wrong-runtime" };
                    RuntimeThrows<ResourceRequestException>(() => editor.Open(session, "Module1", CancellationToken.None));
                }
            });
        }
    }
}
