using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        // Fixture pixels test the real CAS/provider/Workspace/wire path, not native
        // WebView2 capture or privacy qualification (which remains unavailable).
        private sealed class HarnessBrowserMediaSource : HarnessBrowserReadSource, IBrowserMediaSource
        {
            internal static readonly byte[] Pixels = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6XkAAAAASUVORK5CYII=");
            internal bool Current = true, Privacy = true;
            public Task<BrowserMediaCapture> ReadMediaAsync(BrowserPageHandle target, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new BrowserMediaCapture { Page = target, Png = Pixels, CapturedUtc = DateTime.UtcNow,
                    Title = "Viewport fixture", SourceAddress = "/", Width = 1, Height = 1, ViewportRevision = 1,
                    ControlRevision = 1, DeviceScale = 1, Zoom = 1, PrivacyVerified = Privacy });
            }
            public bool IsCaptureCurrent(BrowserMediaCapture capture) { return Current && capture.Page.Matches(Target); }
        }

        private static void BrowserWorkspaceMediaWire()
        {
            foreach (var vision in new[] { true, false }) WithTempPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var requests = new List<JObject>(); string chatId = null;
                    var server = Task.Run(async () => {
                        try {
                            for (var i = 0; i < 3; i++) {
                                var context = await listener.GetContextAsync().ConfigureAwait(false);
                                using (var reader = new StreamReader(context.Request.InputStream))
                                    requests.Add(JObject.Parse(await reader.ReadToEndAsync().ConfigureAwait(false)));
                                var content = i == 0 ? new ConversationResponse(ConversationResponse.ExecuteAction,
                                    "Inspect the local page viewport.", null, calls: new[] { new ConversationToolCall {
                                        Name = "core.read", Arguments = new JObject { ["type"] = "browser",
                                            ["target"] = ExternalBrowserResourceProvider.Target, ["representation"] = "media" } } }).ToJson()
                                    : i == 1 && vision ? ScriptedVerifiedFinish(new ChatStore(paths).Load(chatId),
                                        "Report the inspected viewport.", "Viewport inspected.", "Read the viewport.", "core.read")
                                    : ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                        "Report the request status.", vision ? "Hello again." : "Viewport media unavailable.",
                                        results: i == 1 ? new[] { new ConversationResultClaim("Inspect the viewport", new[] { "E1" }, ConversationAssessment.NotSatisfied) } : new ConversationResultClaim[0],
                                        outcome: i == 1 ? ConversationOutcome.UnableToComplete : ConversationOutcome.Completed);
                                await SteeringReply(context, content).ConfigureAwait(false);
                            }
                        } catch (HttpListenerException) { } catch (ObjectDisposedException) { }
                    });
                    try {
                        var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "plugin-source"));
                        var source = new HarnessBrowserMediaSource();
                        var browser = new BrowserCapabilityService(paths, () => source);
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "fixture-model",
                            StreamResponses = false, AutoCompressContext = false, SmartChatTitles = false,
                            AgentResponseMode = AgentResponseModes.JsonObject, AutoConfirmToolActions = true,
                            ModelImageSupportOverrides = new Dictionary<string, bool?> { ["fixture-model"] = vision } };
                        using (var service = new WorkspaceConversationService(paths, isPluginSourceWorkspace: _ => true,
                            browserCapabilities: browser)) {
                            chatId = service.CreateSession(workspace, "Browser media").Id;
                            source.Target = new BrowserPageHandle(chatId, "native-fixture", 1, browserTabId: "fixture-tab",
                                ownerScope: new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = workspace.WorkspaceId, ChatId = chatId });
                            var run = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest { SessionId = chatId,
                                Message = "Inspect the Browser viewport." }, settings, () => "", cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token).GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Completed, run.Summary.Lifecycle, "standalone plugin-source Browser run completes");
                        }
                        using (var restarted = new WorkspaceConversationService(paths, isPluginSourceWorkspace: _ => true,
                            browserCapabilities: browser)) {
                            restarted.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest { SessionId = chatId,
                                Message = "Hello again" }, settings, () => "", cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token).GetAwaiter().GetResult();
                        }
                        AssertTrue(server.Wait(5000), "three real model requests drained");
                        var second = requests[1].ToString();
                        AssertEqual(vision, second.Contains("data:image/png;base64," + Convert.ToBase64String(HarnessBrowserMediaSource.Pixels)),
                            "actual next model wire receives exact CAS pixels only for a vision model");
                        if (vision) AssertTrue(second.Contains("Browser observation: "), "wire exposes the semantic retained observation target");
                        AssertTrue(!requests[0].ToString().Contains("data:image/") && !requests[2].ToString().Contains("data:image/"),
                            "pixels are absent before explicit read and after restart");
                        if (!vision) AssertTrue(requests[1]["messages"].Any(item => item["content"]?.Type == JTokenType.String &&
                            ((string)item["content"]).Contains("\"loaded\":false")) && second.Contains("resource_media_unavailable") && !second.Contains("data:image/"),
                            "non-vision media returns explicit unavailable instead of loaded success");
                        var saved = new ChatStore(paths).Load(chatId);
                        AssertTrue(saved.Messages.Where(message => (message.Content ?? "").StartsWith(ResourceMediaMessages.Prefix))
                            .All(message => message.ExcludeFromModelContext && message.Attachments.Count == 0),
                            "one-shot hydration is released durably without deleting CAS observations");
                    } finally { listener.Stop(); server.GetAwaiter().GetResult(); }
                }
            });
        }

        private static void BrowserMediaProvenance()
        {
            WithTempPaths(paths => {
                var session = new ChatSession { Host = "Workspace", WorkspaceId = "fixture-workspace" };
                var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = session.WorkspaceId, ChatId = session.Id };
                var source = new HarnessBrowserMediaSource { Target = new BrowserPageHandle(session.Id, "fixture-page", 1,
                    browserTabId: "fixture-tab", ownerScope: owner) };
                IReadOnlyList<ChatAttachment> attachments = null;
                Func<string, ToolHandlerResult> read = target => {
                    var contribution = new BrowserCapabilityService(paths, () => source).ForOwner(owner, session, new AppSettings(),
                        "run", (_, media) => attachments = media);
                    var call = new ToolCall(Guid.NewGuid().ToString("N"), "core.read", "{}");
                    var context = new ToolExecutionContext(call, new ToolPolicySnapshot(call.Name, "test", ResourceReadToolHandler.Policy),
                        "run", "turn", "step", DateTime.UtcNow, false, 1);
                    return contribution.ResourceRead.ExecuteAsync(new ToolHandlerContext(context, new Dictionary<string, object> {
                        ["type"] = "browser", ["target"] = target, ["representation"] = "media" }), CancellationToken.None).GetAwaiter().GetResult();
                };
                source.Privacy = false;
                AssertTrue(read(source.Target.Target).Result.DataJson.Contains("browser_capture_invalid"), "unverified privacy refuses media");
                source.Privacy = true; source.Current = false;
                AssertTrue(read(source.Target.Target).Result.DataJson.Contains("browser_document_stale"), "changed viewport refuses media");
                source.Current = true;
                var captured = read(source.Target.Target);
                AssertEqual(ToolResultStatus.Ok, captured.Result.Status, "qualified fixture capture retained");
                var savedTarget = (string)JObject.Parse(captured.Result.DataJson)["snapshotTarget"];
                source.Target = null; attachments = null;
                AssertEqual(ToolResultStatus.Ok, read(savedTarget).Result.Status, "historical pixels remain readable after native close and provider recreation");
                AssertTrue(new AttachmentStore(paths).ReadBytes(attachments.Single()).SequenceEqual(HarnessBrowserMediaSource.Pixels),
                    "retained observation rehydrates exact CAS bytes");
            });
        }
    }
}
