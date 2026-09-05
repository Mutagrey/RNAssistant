using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
        private static string VbaUploadHash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static VbaModulePayload UploadVbaSource(ResourceDataPlaneService data, VbaEditorResourceService editor,
            ChatSession session, byte[] bytes, bool complete = true)
        {
            var lease = editor.BeginUpload(session, new VbaEditorUploadRequest { ChatId = session.Id, ByteLength = bytes.Length }, CancellationToken.None);
            for (var offset = 0; complete && offset < bytes.Length;)
            {
                var count = Math.Min(lease.MaxChunkBytes, bytes.Length - offset);
                using (var body = new MemoryStream(bytes, offset, count, false))
                    UploadResponse(new ResourceDataRouter(data).Handle("POST", lease.Url + "?offset=" + offset + "&count=" + count,
                        CancellationToken.None, body), 200);
                offset += count;
            }
            return new VbaModulePayload { ChatId = session.Id, ModuleName = "Module1", UploadLeaseId = lease.LeaseId,
                SourceSha256 = VbaUploadHash(bytes), ExpectedCodeSha256 = new string('b', 64) };
        }

        private static void VbaEditorUploadKeepsMutationOwner()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new VbaEditorResourceService(executor.ResourceGateway, data);
                    var code = "Option Explicit\r\n'" + new string('ж', 140000) + "\r\nSub Uploaded()\r\nEnd Sub\r\n";
                    var blobs = Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length;
                    var request = UploadVbaSource(data, editor, session, Encoding.UTF8.GetBytes(code));
                    AssertEqual(blobs, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length,
                        "transient uploads neither create an attachment nor publish resource authority");
                    AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() =>
                        data.CloseUpload(session.Id, request.UploadLeaseId)).ErrorCode, "attachment owner cannot close VBA upload");
                    AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() =>
                        data.CompleteUpload(session, request.UploadLeaseId, new ChatResourceIngestionService(new AttachmentStore(paths)))).ErrorCode,
                        "attachment ingestion cannot consume a VBA upload");
                    AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() =>
                        editor.ReadUploadedSource(new ChatSession(), request, CancellationToken.None)).ErrorCode, "foreign chat cannot consume source");
                    request.ExpectedCodeSha256 = VbaTextCanonicalizer.LiveCodeSha256(adapter.VbaModuleCode);
                    var source = editor.ReadUploadedSource(session, request, CancellationToken.None);
                    AssertEqual(code, source, "sequential upload preserves exact Unicode, CRLF and final newline");
                    RuntimeThrows<ResourceRequestException>(() => editor.ReadUploadedSource(session, request, CancellationToken.None));
                    AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "consuming bytes cannot dispatch a mutation");
                    var command = Command("common.vba_write_module", "moduleName", request.ModuleName, "code", source, "mode", "updateOnly");
                    command.ExpectedContentSha256 = request.ExpectedCodeSha256;
                    AssertTrue(executor.ExecuteManual(command, tools, new AppSettings(), false, true, session).Success,
                        "uploaded code executes through the existing prepared/read-back mutation owner");
                    AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "one verified replacement");

                    var stale = UploadVbaSource(data, editor, session, Encoding.UTF8.GetBytes("Sub Stale()\nEnd Sub"));
                    stale.ExpectedCodeSha256 = VbaTextCanonicalizer.LiveCodeSha256(adapter.VbaModuleCode);
                    adapter.VbaModuleCode = "Sub External()\nEnd Sub";
                    command.Arguments["code"] = editor.ReadUploadedSource(session, stale, CancellationToken.None);
                    command.ExpectedContentSha256 = stale.ExpectedCodeSha256;
                    AssertTrue(!executor.ExecuteManual(command, tools, new AppSettings(), false, true, session).Success, "upload cannot bypass stale source guard");
                    AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "stale save never dispatches");
                    var create = UploadVbaSource(data, editor, session, Encoding.UTF8.GetBytes("Option Explicit\n"));
                    var createRequest = new VbaCreateModulePayload { ChatId = session.Id, ModuleName = "UploadedForm", ComponentType = "MSForm",
                        UploadLeaseId = create.UploadLeaseId, SourceSha256 = create.SourceSha256 };
                    var createCommand = Command("common.vba_write_module", "moduleName", "UploadedForm", "componentType", "MSForm",
                        "mode", "createOnly", "code", editor.ReadUploadedSource(session, createRequest, CancellationToken.None));
                    AssertTrue(executor.ExecuteManual(createCommand, tools, new AppSettings(), false, true, session).Success,
                        "CodeOnly UserForm create uses the same typed mutation owner");
                    AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.CreateModule), "one create without automatic retry");
                }
            });
        }

        private static void VbaEditorUploadRejectsInvalidSource()
        {
            using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
            {
                var editor = new VbaEditorResourceService(new ResourceGatewayService(), data);
                var session = new ChatSession();
                foreach (var bytes in new[] { new byte[] { 0xff }, Encoding.UTF8.GetBytes(new string('x', 1000001)) })
                {
                    var request = UploadVbaSource(data, editor, session, bytes);
                    RuntimeThrows<ResourceRequestException>(() => editor.ReadUploadedSource(session, request, CancellationToken.None));
                    RuntimeThrows<ResourceRequestException>(() => data.ValidateUpload(request.UploadLeaseId));
                }
                var tampered = UploadVbaSource(data, editor, session, new byte[] { 65 });
                tampered.SourceSha256 = new string('0', 64);
                RuntimeThrows<ResourceRequestException>(() => editor.ReadUploadedSource(session, tampered, CancellationToken.None));
                var unguarded = UploadVbaSource(data, editor, session, new byte[] { 65 });
                unguarded.ExpectedCodeSha256 = null;
                AssertEqual("vba_editor_guard_required", RuntimeThrows<ResourceRequestException>(() =>
                    editor.ReadUploadedSource(session, unguarded, CancellationToken.None)).ErrorCode,
                    "editor saves cannot borrow model evidence when their explicit guard is missing");
                var partial = UploadVbaSource(data, editor, session, new byte[] { 65 }, false);
                AssertEqual("RESOURCE_UPLOAD_INCOMPLETE", RuntimeThrows<ResourceRequestException>(() =>
                    editor.ReadUploadedSource(session, partial, CancellationToken.None)).ErrorCode, "partial upload cannot become code");
                var cancelled = UploadVbaSource(data, editor, session, new byte[] { 65 });
                RuntimeThrows<OperationCanceledException>(() => editor.ReadUploadedSource(session, cancelled, new CancellationToken(true)));
                RuntimeThrows<ResourceRequestException>(() => data.ValidateUpload(cancelled.UploadLeaseId));
                var empty = UploadVbaSource(data, editor, session, new byte[0]);
                AssertEqual(string.Empty, editor.ReadUploadedSource(session, empty, CancellationToken.None), "empty source is exact without a fake body");
                RuntimeThrows<ResourceRequestException>(() => editor.BeginUpload(session,
                    new VbaEditorUploadRequest { ChatId = session.Id, ByteLength = 4000001 }, CancellationToken.None));
                for (var i = 0; i < 4; i++) UploadVbaSource(data, editor, session, new byte[0]);
                AssertEqual("RESOURCE_LEASE_LIMIT", RuntimeThrows<ResourceRequestException>(() =>
                    UploadVbaSource(data, editor, session, new byte[0])).ErrorCode, "VBA shares the existing four-upload cap");
                data.CloseTransfers(session.Id);
                var attachment = data.OpenUpload(session, UploadRequest(session, 1));
                RuntimeThrows<ResourceRequestException>(() => editor.ReadUploadedSource(session, new VbaModulePayload {
                    ChatId = session.Id, ModuleName = "Module1", UploadLeaseId = attachment.LeaseId }, CancellationToken.None));
                data.ValidateUpload(attachment.LeaseId);
            }
        }
    }
}
