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
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static HtmlWorkspaceFilePayload UploadHtmlSource(ResourceDataPlaneService data, HtmlWorkspaceEditorResourceService editor,
            ChatSession session, byte[] bytes, bool partial = false)
        {
            var lease = editor.BeginUpload(session, new HtmlWorkspaceMutationUploadRequest { ChatId = session.Id, ByteLength = bytes.Length }, CancellationToken.None);
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(lease.MaxChunkBytes, bytes.Length - offset);
                if (partial) count = Math.Max(1, count - 1);
                using (var body = new MemoryStream(bytes, offset, count)) data.WriteUpload(lease.LeaseId, offset, count, body, CancellationToken.None);
                offset += count; if (partial) break;
            }
            using (var sha = SHA256.Create()) return new HtmlWorkspaceFilePayload { ChatId = session.Id, UploadLeaseId = lease.LeaseId,
                Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(),
                ExpectedActiveHtmlArtifactId = session.ActiveHtmlArtifactId ?? "", Path = "index.html", Kind = "html", SetActive = true };
        }

        private static HtmlWorkspaceDataPayload HtmlDataRequest(HtmlWorkspaceFilePayload uploaded)
        {
            return new HtmlWorkspaceDataPayload { ChatId = uploaded.ChatId, UploadLeaseId = uploaded.UploadLeaseId, Sha256 = uploaded.Sha256,
                ExpectedActiveHtmlArtifactId = uploaded.ExpectedActiveHtmlArtifactId, Name = "items" };
        }

        private static void HtmlEditorUploadUsesExistingCommitOwner()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var persisted = 0;
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                    persistResourceFacts: saved => persisted++);
                var session = NewSession(adapter);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new HtmlWorkspaceEditorResourceService(executor, data);
                    var text = "\ufeff<main>\r\n" + new string('я', 140000) + "😀</main>";
                    var request = UploadHtmlSource(data, editor, session, Encoding.UTF8.GetBytes(text));
                    var wire = JObject.FromObject(request);
                    AssertTrue(wire["content"] == null && wire["json"] == null && wire["uploadLeaseId"] != null, "body-free typed mutation controls");
                    editor.SaveFile(session, request, CancellationToken.None);
                    AssertEqual(text, session.HtmlWorkspace.Files.Single().Content, "complete multi-chunk UTF-8/BOM/CRLF reaches the existing writer unchanged");
                    AssertEqual(1, persisted, "existing publication owner persists once");
                    var first = session.ActiveHtmlArtifactId;
                    var scope = executor.ResourceAuthority.Scope(session, false);
                    var identity = ResourceStateProvider.Identity(scope, "html-workspace");
                    var head = executor.ResourceAuthority.Store.Capture(scope).GetHead(identity);
                    AssertEqual(HeadKnowledge.Known, head.Knowledge, "verified write publishes the whole logical workspace");
                    RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, request, CancellationToken.None));
                    var empty = UploadHtmlSource(data, editor, session, new byte[0]);
                    editor.SaveFile(session, empty, CancellationToken.None);
                    var attempts = File.ReadAllLines(Path.Combine(paths.ResourceAuthorityDirectory, "mutation-attempts.jsonl"))
                        .Select(line => JsonConvert.DeserializeObject<MutationAttempt>(line)).ToArray();
                    var prepared = attempts.First(item => item.State == MutationAttemptState.Prepared && item.ExpectedRevision == head.Revision.Revision);
                    var intent = JObject.Parse(executor.Payloads.ReadText(prepared.Payload.ToBlobReference()));
                    AssertEqual(first, intent["expectedActiveHtmlArtifactId"].Value<string>(), "durable CAS intent retains the editor guard beside the logical expected revision");
                    AssertEqual("", intent["content"].Value<string>(), "even empty replacement is a complete CAS-backed mutation intent");
                    AssertEqual("", session.HtmlWorkspace.Files.Single().Content, "empty file is an explicit complete replacement");
                    AssertTrue(session.ActiveHtmlArtifactId != first, "one new revision, no in-place historical overwrite");
                    var json = "{\r\n  \"items\": [\"Ж😀\"]\r\n}\n";
                    var dataRequest = HtmlDataRequest(UploadHtmlSource(data, editor, session, Encoding.UTF8.GetBytes(json)));
                    editor.SaveData(session, dataRequest, CancellationToken.None);
                    AssertEqual(1, session.HtmlWorkspace.DataSources.Count, "JSON goes through the existing bound-resource owner");
                    AssertTrue(session.Artifacts.Any(item => item.InlineText == json && item.MimeType == "application/json"), "exact JSON artifact is retained by the existing domain");
                    AssertEqual(3, persisted, "file/create/data share the same commit barrier");
                    AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count, "all writes reach terminal publication");
                    AssertEqual(0, adapter.VbaBackendCalls.Count, "HTML editing never calls Office");
                }
            });
        }

        private static void HtmlEditorRejectsInvalidUploadsAndStaleDrafts()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths));
                var session = NewSession(adapter);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var editor = new HtmlWorkspaceEditorResourceService(executor, data);
                    Func<string, HtmlWorkspaceFilePayload> upload = value => UploadHtmlSource(data, editor, session, Encoding.UTF8.GetBytes(value));
                    editor.SaveFile(session, upload("before"), CancellationToken.None);
                    var original = session.ActiveHtmlArtifactId;
                    var scope = executor.ResourceAuthority.Scope(session, false);
                    var identity = ResourceStateProvider.Identity(scope, "html-workspace");
                    var generation = executor.ResourceAuthority.Store.Capture(scope).Generation;
                    var partial = UploadHtmlSource(data, editor, session, Encoding.UTF8.GetBytes("partial"), true);
                    AssertEqual("RESOURCE_UPLOAD_INCOMPLETE", RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, partial, CancellationToken.None)).ErrorCode, "partial source cannot dispatch");
                    RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, partial, CancellationToken.None));
                    var hash = upload("tampered"); hash.Sha256 = new string('0', 64);
                    RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, hash, CancellationToken.None));
                    var utf8 = UploadHtmlSource(data, editor, session, new byte[] { 0xc0, 0xaf });
                    RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, utf8, CancellationToken.None));
                    var invalidPath = upload("text"); invalidPath.Path = "../outside.html";
                    RuntimeThrows<InvalidOperationException>(() => editor.SaveFile(session, invalidPath, CancellationToken.None));
                    var tooLarge = upload(new string('x', 300001));
                    RuntimeThrows<InvalidOperationException>(() => editor.SaveFile(session, tooLarge, CancellationToken.None));
                    RuntimeThrows<ResourceRequestException>(() => editor.BeginUpload(session, new HtmlWorkspaceMutationUploadRequest {
                        ChatId = session.Id, ByteLength = HtmlWorkspaceEditorResourceService.MaximumSourceBytes + 1 }, CancellationToken.None));
                    var invalidJson = HtmlDataRequest(upload("not json"));
                    RuntimeThrows<JsonReaderException>(() => editor.SaveData(session, invalidJson, CancellationToken.None));
                    var emptyJson = HtmlDataRequest(upload(""));
                    RuntimeThrows<InvalidOperationException>(() => editor.SaveData(session, emptyJson, CancellationToken.None));
                    var missingGuard = upload("missing"); missingGuard.ExpectedActiveHtmlArtifactId = null;
                    RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, missingGuard, CancellationToken.None));
                    var cancelled = upload("cancelled");
                    RuntimeThrows<OperationCanceledException>(() => editor.SaveFile(session, cancelled, new CancellationToken(true)));
                    RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, cancelled, CancellationToken.None));
                    var foreign = upload("foreign"); var other = NewSession(adapter); other.Id = "foreign-chat";
                    foreign.ChatId = other.Id;
                    RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(other, foreign, CancellationToken.None));
                    RuntimeThrows<ResourceRequestException>(() => data.CloseUpload(session.Id, foreign.UploadLeaseId, "prompt-editor"));
                    foreign.ChatId = session.Id; data.CloseUpload(session.Id, foreign.UploadLeaseId, HtmlWorkspaceEditorResourceService.Owner);
                    AssertEqual(original, session.ActiveHtmlArtifactId, "all refused requests preserve the existing workspace");
                    AssertEqual(generation, executor.ResourceAuthority.Store.Capture(scope).Generation, "refusal never poisons a known head");
                    AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count, "failed validation abandons prepared attempts before dispatch");
                    var stale = upload("stale");
                    editor.SaveFile(session, upload("newer"), CancellationToken.None);
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, stale, CancellationToken.None)).ErrorCode, "stale whole-workspace guard fails closed");
                    var unknown = upload("unknown"); executor.ResourceAuthority.ReportExternalDrift(scope, identity);
                    AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => editor.SaveFile(session, unknown, CancellationToken.None)).ErrorCode, "an exact artifact id cannot override Unknown authority");
                    AssertEqual("newer", session.HtmlWorkspace.Files.Single().Content, "no stale or unknown write is dispatched");
                    AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count, "stale/unknown refusals also terminate their preparations");
                }
            });
        }

        private static void HtmlEditorBridgeRejectsInlineBodies()
        {
            var controller = new AssistantController(); var bridge = new AssistantWebBridge(controller, null); var token = BridgeToken(bridge);
            Func<string, JObject, JObject> send = (action, payload) => JObject.Parse(bridge.HandleMessageAsync(new JObject {
                ["id"] = Guid.NewGuid().ToString("N"), ["type"] = action, ["bridgeToken"] = token, ["payload"] = payload }.ToString()).GetAwaiter().GetResult());
            var controls = new JObject { ["chatId"] = "html-chat", ["expectedActiveHtmlArtifactId"] = "", ["uploadLeaseId"] = new string('a', 64), ["sha256"] = new string('b', 64) };
            foreach (var action in new[] { "saveHtmlWorkspaceFile", "saveHtmlWorkspaceData" })
            {
                AssertTrue(send(action, controls)["ok"].Value<bool>(), "body-free controls reach the typed bridge owner");
                var legacy = (JObject)controls.DeepClone(); legacy[action.EndsWith("File") ? "content" : "json"] = "inline";
                AssertTrue(!send(action, legacy)["ok"].Value<bool>(), "inline source has no fallback");
                var missing = (JObject)controls.DeepClone(); missing.Remove("expectedActiveHtmlArtifactId");
                AssertTrue(!send(action, missing)["ok"].Value<bool>(), "missing guard is not an empty-workspace guard");
            }
            AssertTrue(send("beginHtmlWorkspaceMutationUpload", new JObject { ["chatId"] = "html-chat", ["byteLength"] = 0 })["ok"].Value<bool>(), "empty upload open is typed");
            AssertTrue(send("cancelHtmlWorkspaceMutationUpload", new JObject { ["chatId"] = "html-chat", ["leaseId"] = new string('a', 64) })["ok"].Value<bool>(), "typed close is routed");
            using (var registry = new BridgeRequestCancellationRegistry())
                foreach (var action in new[] { "beginHtmlWorkspaceMutationUpload", "saveHtmlWorkspaceFile", "saveHtmlWorkspaceData" })
                    AssertTrue(registry.Create(action, action) != null, "each in-flight HTML request is cancellable");
        }
    }
}
