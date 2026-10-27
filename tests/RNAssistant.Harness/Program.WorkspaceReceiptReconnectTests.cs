using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PluginsReceiptReconnect()
        {
            WithPluginPaths(paths =>
            {
                using (var service = new PluginApplicationService(paths))
                using (var bridge = new AssistantWebBridge(new AssistantController(paths), ignored => { }) { Plugins = service })
                {
                    var workspaceId = service.CreateProjectWorkspace(new ProjectConnectionRequest {
                        RootPath = Path.Combine(paths.Root, "receipt-project") }).SelectedWorkspaceId;
                    var chat = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                    var other = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = workspaceId });
                    var workspace = new WorkspaceStore(paths).OpenConnected(workspaceId);
                    var session = service.Conversations.GetSession(workspace, chat.SessionId);
                    var store = new ChatStore(paths);
                    var journal = new ConversationInputJournal(new ChatEventStoreAdapter(store));
                    var input = new ConversationInput { Id = Guid.NewGuid().ToString("N"), OperationId = "lost-reply",
                        AcceptedRunId = Guid.NewGuid().ToString("N"), RequestFingerprint = "fixture-intent",
                        Revision = 1, Text = "Received input", Delivery = InputDelivery.Queue,
                        Status = ConversationInputStatus.Delivering, DocumentRuntimeKey = workspaceId, CreatedUtc = DateTime.UtcNow };
                    journal.Append(session, new ConversationInputEvent { Input = input });
                    var query = new ProjectChatRequest { WorkspaceId = workspaceId, SessionId = session.Id, ReceiptRequestId = input.OperationId };
                    var revision = session.Revision;
                    var accepted = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "getProjectChat", query);
                    AssertTrue((bool)accepted["ok"] && !(bool)accepted["payload"]["inputReceipt"]["DeliveryRecorded"],
                        "admission alone does not acknowledge delivery");
                    AssertEqual(revision, store.Load(session.Id).Revision, "receipt lookup does not reconcile or run input");
                    query.ReceiptRequestId = "not-accepted";
                    var unknown = WorkspaceBridgeCall(bridge, BridgeToken(bridge), "getProjectChat", query);
                    AssertTrue((bool)unknown["ok"] && unknown["payload"]["inputReceipt"] == null, "unknown ID has no invented receipt");
                    query.ReceiptRequestId = input.OperationId; query.SessionId = other.SessionId;
                    AssertTrue(WorkspaceBridgeCall(bridge, BridgeToken(bridge), "getProjectChat", query)["payload"]["inputReceipt"] == null,
                        "another chat cannot acknowledge the original input");
                    query.SessionId = session.Id;
                    var user = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text, RunId = input.AcceptedRunId };
                    session.Messages.Add(user); store.Commit(session, ConversationCommit.UserInput(user));
                    input.Status = ConversationInputStatus.Applied; input.Revision++;
                    journal.Append(session, new ConversationInputEvent { Input = input });
                    revision = session.Revision;
                    using (var reopened = new PluginApplicationService(paths))
                    using (var newBridge = new AssistantWebBridge(new AssistantController(paths), ignored => { }) { Plugins = reopened })
                    {
                        var applied = WorkspaceBridgeCall(newBridge, BridgeToken(newBridge), "getProjectChat", query);
                        var receipt = applied["payload"]["inputReceipt"];
                        AssertTrue((bool)applied["ok"] && (bool)receipt["DeliveryRecorded"], "delivery reconnects after application restart");
                        AssertEqual((string)accepted["payload"]["inputReceipt"]["AcceptedCursor"], (string)receipt["AcceptedCursor"],
                            "the original accepted identity is preserved");
                        AssertTrue(JToken.DeepEquals(receipt, WorkspaceBridgeCall(newBridge, BridgeToken(newBridge), "getProjectChat", query)["payload"]["inputReceipt"]),
                            "repeat lookup is stable");
                        AssertEqual(revision, store.Load(session.Id).Revision, "read-only reconnect appends no events and starts no model");
                        query.ReceiptRequestId = new string('x', 129);
                        AssertTrue(!(bool)WorkspaceBridgeCall(newBridge, BridgeToken(newBridge), "getProjectChat", query)["ok"], "query identity is bounded");
                        var fixture = Environment.GetEnvironmentVariable("RNA_WORKSPACE_RECONNECT_FIXTURE");
                        if (!string.IsNullOrWhiteSpace(fixture))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture)));
                            File.WriteAllText(fixture, new JObject { ["accepted"] = accepted, ["applied"] = applied, ["unknown"] = unknown }
                                .ToString(Formatting.Indented));
                        }
                    }
                }
            });
        }
    }
}
