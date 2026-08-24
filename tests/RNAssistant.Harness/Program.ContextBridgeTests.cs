using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ChatCloneServicePreservesValues()
        {
            var context = new DocumentContext
            {
                Host = "Excel",
                DocumentKey = "doc",
                Title = "Harness.xlsx",
                UpdatedUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            };
            context.Notes.Add(new ContextNote
            {
                Id = "note-1",
                Host = "Excel",
                Kind = "selection",
                Title = "Cells",
                Reference = "A1",
                Source = "Sheet1!A1",
                Text = "Original note",
                Preview = "Original",
                DetailsJson = "{\"range\":\"A1\"}",
                CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

            var clonedContext = ChatCloneService.CloneContext(context);
            AssertTrue(!object.ReferenceEquals(context, clonedContext), "context cloned");
            AssertTrue(!object.ReferenceEquals(context.Notes[0], clonedContext.Notes[0]), "context note cloned");
            AssertEqual("Original note", clonedContext.Notes[0].Text, "context note text");
            context.Notes[0].Text = "Changed";
            AssertEqual("Original note", clonedContext.Notes[0].Text, "context clone independent");

            var sourceWorkspace = new HtmlWorkspace
            {
                ActiveFileId = "index.html",
                Files = new List<HtmlWorkspaceFile>
                {
                    new HtmlWorkspaceFile { Id = "index.html", Path = "index.html", Kind = "html", Content = "original html" }
                },
                DataSources = new List<HtmlWorkspaceDataSource>
                {
                    new HtmlWorkspaceDataSource { Id = "rows", Name = "rows", Json = "{\"value\":1}" }
                },
                History = new List<HtmlWorkspaceSnapshot> { new HtmlWorkspaceSnapshot { Label = "old" } }
            };
            var clonedWorkspace = ChatCloneService.CloneWorkspaceForFork(sourceWorkspace);
            AssertEqual("index.html", clonedWorkspace.ActiveFileId, "workspace active file cloned");
            AssertEqual(0, clonedWorkspace.History.Count, "workspace fork excludes transient undo history");
            AssertTrue(!object.ReferenceEquals(sourceWorkspace.Files[0], clonedWorkspace.Files[0]), "workspace file cloned");
            AssertTrue(!object.ReferenceEquals(sourceWorkspace.DataSources[0], clonedWorkspace.DataSources[0]), "workspace data cloned");
            sourceWorkspace.Files[0].Content = "changed html";
            sourceWorkspace.DataSources[0].Json = "{\"value\":2}";
            AssertEqual("original html", clonedWorkspace.Files[0].Content, "workspace file clone independent");
            AssertEqual("{\"value\":1}", clonedWorkspace.DataSources[0].Json, "workspace data clone independent");

            var snapshot = HtmlWorkspaceCopyService.CaptureSnapshot(clonedWorkspace, "checkpoint");
            var restoredWorkspace = HtmlWorkspaceCopyService.CreateWorkspaceFromSnapshot(snapshot);
            snapshot.Files[0].Content = "changed snapshot";
            AssertEqual("original html", restoredWorkspace.Files[0].Content, "restored workspace is independent from snapshot");

            var sourceMessage = new ChatMessage
            {
                Id = "message-1",
                Role = "assistant",
                Content = "Done",
                ExcludeFromModelContext = true,
                ProtocolMessage = true,
                ToolCallId = "call-1",
                ToolName = "rna_excel_read_range",
                ToolResultRole = ToolResultRoles.Tool,
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall { Id = "call-1", Type = "function", Name = "rna_excel_read_range", ArgumentsJson = "{}" }
                },
                ArtifactIds = new List<string> { "plan-1" },
                HtmlWorkspaceCheckpointId = "html-2",
                PromptTokens = 10,
                CompletionTokens = 2,
                TotalTokens = 12,
                UsageJson = "{\"total\":12}",
                RunId = "run-1",
                Sequence = 4,
                Activity = new ChatActivity
                {
                    RunId = "run-1",
                    Sequence = 5,
                    Kind = "tool",
                    Title = "Write table",
                    Status = "failed",
                    ExecutionStatus = "partial_failure",
                    ErrorCode = "pipeline_partial_failure",
                    Retryable = false,
                    ToolId = "excel.write_range",
                    RuntimeGuardJson = "{\"version\":1}",
                    Children = new List<ChatActivity>
                    {
                        new ChatActivity { Kind = "tool", Title = "Nested", Status = "completed", ToolId = "excel.add_sheet" }
                    }
                },
                CreatedUtc = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc)
            };
            var clonedMessages = ChatCloneService.CloneMessages(new[] { sourceMessage });
            AssertEqual(1, clonedMessages.Count, "message count");
            AssertTrue(!object.ReferenceEquals(sourceMessage, clonedMessages[0]), "message cloned");
            AssertEqual("message-1", clonedMessages[0].Id, "message id");
            AssertEqual("assistant", clonedMessages[0].Role, "message role");
            AssertTrue(clonedMessages[0].ExcludeFromModelContext, "message context exclusion");
            AssertTrue(clonedMessages[0].ProtocolMessage, "protocol marker cloned");
            AssertEqual("call-1", clonedMessages[0].ToolCallId, "tool call id cloned");
            AssertEqual(ToolResultRoles.Tool, clonedMessages[0].ToolResultRole, "tool result role cloned");
            AssertTrue(!object.ReferenceEquals(sourceMessage.ToolCalls[0], clonedMessages[0].ToolCalls[0]), "tool call cloned");
            AssertEqual("plan-1", clonedMessages[0].ArtifactIds[0], "artifact reference cloned");
            AssertEqual("html-2", clonedMessages[0].HtmlWorkspaceCheckpointId, "html checkpoint cloned");
            AssertEqual(12, clonedMessages[0].TotalTokens, "message tokens");
            AssertEqual("run-1", clonedMessages[0].RunId, "message run id");
            AssertEqual(4, clonedMessages[0].Sequence, "message sequence");
            AssertTrue(!object.ReferenceEquals(sourceMessage.Activity, clonedMessages[0].Activity), "activity cloned");
            AssertTrue(!object.ReferenceEquals(sourceMessage.Activity.Children[0], clonedMessages[0].Activity.Children[0]), "activity child cloned");
            AssertEqual("Write table", clonedMessages[0].Activity.Title, "activity title");
            AssertEqual("pipeline_partial_failure", clonedMessages[0].Activity.ErrorCode, "activity error code");
            AssertEqual(false, clonedMessages[0].Activity.Retryable, "activity retryable");
            AssertEqual("{\"version\":1}", clonedMessages[0].Activity.RuntimeGuardJson, "activity runtime guard cloned");
            AssertEqual("run-1", clonedMessages[0].Activity.RunId, "activity run id");
            AssertEqual(5, clonedMessages[0].Activity.Sequence, "activity sequence");
            sourceMessage.Content = "Changed";
            sourceMessage.Activity.Title = "Changed activity";
            AssertEqual("Done", clonedMessages[0].Content, "message clone independent");
            AssertEqual("Write table", clonedMessages[0].Activity.Title, "activity clone independent");

            var artifacts = new[]
            {
                new ChatArtifact { Id = "plan-1", Kind = ChatArtifactKinds.Markdown, Title = "Note", InlineText = "{}", RelatedArtifactIds = new List<string> { "related" } },
                new ChatArtifact { Id = "html-1", Kind = ChatArtifactKinds.HtmlWorkspace, Title = "HTML v1", InlineText = "large-v1" },
                new ChatArtifact { Id = "html-2", Kind = ChatArtifactKinds.HtmlWorkspace, Title = "HTML v2", ParentArtifactId = "html-1", InlineText = "large-v2" },
                new ChatArtifact { Id = "related", Kind = ChatArtifactKinds.Markdown, Title = "Related" },
                new ChatArtifact { Id = "unused", Kind = ChatArtifactKinds.File, Title = "Unused" }
            };
            var clonedArtifacts = ChatCloneService.CloneArtifactsForMessages(artifacts, clonedMessages);
            AssertEqual(4, clonedArtifacts.Count, "fork clones referenced artifacts, relations, and html parent chain only");
            AssertTrue(clonedArtifacts.Any(artifact => artifact.Id == "html-1"), "html parent revision retained");
            AssertTrue(clonedArtifacts.Any(artifact => artifact.Id == "related"), "related artifact retained");
            AssertTrue(!clonedArtifacts.Any(artifact => artifact.Id == "unused"), "unreachable artifact omitted from fork");
            var dto = ChatArtifactDto.From(artifacts);
            AssertTrue(string.IsNullOrEmpty(dto.First(item => item.Id == "html-2").InlineText), "bridge omits heavyweight html snapshot body");
            AssertEqual("{}", dto.First(item => item.Id == "plan-1").InlineText, "bridge includes bounded plan payload");
            var artifactPrompt = ChatArtifactService.BuildPromptIndex(new ChatSession
            {
                Artifacts = artifacts.ToList(),
                ActiveHtmlArtifactId = "html-2"
            }, 1000);
            AssertContains(artifactPrompt, "CHAT_ARTIFACT_INDEX", "prompt exposes artifact metadata index");
            AssertContains(artifactPrompt, "html-2", "prompt exposes active html artifact reference");
            AssertTrue(artifactPrompt.IndexOf("large-v2", StringComparison.Ordinal) < 0, "prompt does not inline html snapshot bodies");
        }

        private static void ForkedAttachmentArtifactTracksCopiedFile()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new AttachmentStore(paths);
                var source = new ChatSession();
                var message = new ChatMessage { Role = "user", Content = "Use attachment" };
                var attachment = store.Import(
                    "notes.txt",
                    "text/plain",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("fork data")));
                message.Attachments.Add(attachment);
                source.Messages.Add(message);
                store.Commit(message);
                ChatArtifactService.LinkMessageArtifacts(source, 0);

                var fork = new ChatSession
                {
                    Messages = ChatCloneService.CloneMessages(source.Messages)
                };
                fork.Artifacts = ChatCloneService.CloneArtifactsForMessages(source.Artifacts, fork.Messages);
                var sourceHash = fork.Messages[0].Attachments[0].ContentSha256;
                store.CloneMessageAttachments(fork.Messages[0]);
                ChatArtifactService.LinkMessageArtifacts(fork, 0);

                var clonedAttachment = fork.Messages[0].Attachments[0];
                var clonedArtifact = fork.Artifacts.Single(item => item.Id == "attachment_" + clonedAttachment.Id);
                AssertEqual(attachment.Id, clonedAttachment.Id, "fork preserves stable attachment id");
                AssertEqual(sourceHash, clonedAttachment.ContentSha256, "fork reuses immutable attachment blob");
                AssertEqual(clonedAttachment.ContentSha256, clonedArtifact.ContentSha256, "fork artifact points at shared blob");
                AssertTrue(store.ReadBytes(clonedAttachment).Length > 0, "forked attachment blob exists");

                var unsafeMessage = ChatCloneService.CloneMessages(source.Messages)[0];
                unsafeMessage.Attachments[0].Id = "../escape";
                unsafeMessage.Attachments.Add(null);
                store.CloneMessageAttachments(unsafeMessage);
                AssertTrue(unsafeMessage.Attachments[0].Id.All(char.IsLetterOrDigit),
                    "fork replaces an unsafe persisted attachment id");
            });
        }

        private static void ContextServiceNormalizesAndUpserts()
        {
            var adapter = new FakeOfficeAdapter();
            var service = new ContextService(adapter);
            var session = new ChatSession
            {
                Host = "Excel",
                DocumentKey = "doc",
                DocumentTitle = "Harness.xlsx",
                Title = "Chat title",
                Context = new DocumentContext { Notes = null }
            };

            var context = service.LoadContext(session);
            AssertEqual("Excel", context.Host, "context host");
            AssertEqual("doc", context.DocumentKey, "context document key");
            AssertEqual("Chat title", context.Title, "context title");
            AssertTrue(context.Notes != null, "notes initialized");

            var note = new ContextNote
            {
                Id = "",
                Host = "",
                Kind = "",
                Title = "",
                Reference = "A1",
                Source = "",
                Text = "first",
                Preview = "",
                DetailsJson = "{\"range\":\"A1\"}"
            };
            service.NormalizeContextNote(note, "selection");
            ContextNormalizer.UpsertContextNote(context, note);
            AssertEqual(1, context.Notes.Count, "note count after insert");
            var originalCreatedUtc = context.Notes[0].CreatedUtc;
            AssertEqual("Excel", context.Notes[0].Host, "note host");
            AssertEqual("selection", context.Notes[0].Kind, "note kind");
            AssertEqual("Harness.xlsx", context.Notes[0].Title, "note title");
            AssertEqual("A1", context.Notes[0].Source, "note source");

            var replacement = new ContextNote
            {
                Host = "Excel",
                Kind = "selection",
                Title = "Changed",
                Reference = "A1",
                Source = "A1",
                Text = "second",
                Preview = "second",
                DetailsJson = "{\"range\":\"A1\",\"updated\":true}"
            };
            ContextNormalizer.UpsertContextNote(context, replacement);
            AssertEqual(1, context.Notes.Count, "note count after update");
            AssertEqual("Changed", context.Notes[0].Title, "updated note title");
            AssertEqual("second", context.Notes[0].Text, "updated note text");
            AssertEqual(originalCreatedUtc, context.Notes[0].CreatedUtc, "created time preserved on update");
        }

        private static void ContextNormalizerUsesCoreModelsOnly()
        {
            var normalizer = new ContextNormalizer("Excel", "doc", "Harness.xlsx");
            var session = new ChatSession
            {
                Host = "",
                DocumentKey = "",
                DocumentTitle = "Harness.xlsx",
                Title = "Chat title",
                Context = new DocumentContext { Notes = null }
            };

            var context = normalizer.LoadContext(session);
            AssertEqual("Excel", context.Host, "context host fallback");
            AssertEqual("doc", context.DocumentKey, "context document key fallback");
            AssertEqual("Chat title", context.Title, "context title fallback");
            AssertTrue(context.Notes != null, "notes initialized");

            var note = new ContextNote { Reference = "A1", Text = "abcdef" };
            normalizer.NormalizeContextNote(note, "selection");
            AssertEqual("Excel", note.Host, "note host fallback");
            AssertEqual("selection", note.Kind, "note kind fallback");
            AssertEqual("Harness.xlsx", note.Title, "note title fallback");
            AssertEqual("abcdef", ContextNormalizer.TrimForContext("abcdef", 10), "core trim short");
            AssertEqual("abc\n...[truncated]", ContextNormalizer.TrimForContext("abcdef", 3), "core trim long");
        }

        private static void ContextServiceTrimsText()
        {
            AssertEqual("abc", ContextNormalizer.TrimForContext("abc", 10), "short trim");
            AssertEqual("abc\n...[truncated]", ContextNormalizer.TrimForContext("abcdef", 3), "long trim");
            AssertEqual(string.Empty, ContextNormalizer.TrimForContext(null, 3), "null trim");
        }

        private static void BridgeUsesTypedRunToolPayload()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b1\",\"type\":\"runTool\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\",\"count\":2,\"enabled\":true,\"values\":[[\"A\"]]},\"dryRun\":true}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("b1", response["id"].Value<string>(), "bridge response id");
            AssertTrue(response["payload"]["Success"].Value<bool>(), "bridge payload success");
            AssertEqual("excel.add_sheet", controller.LastToolId, "tool id");
            AssertContains(controller.LastArgumentsJson, "Report", "tool args");
            AssertEqual(2, JObject.Parse(controller.LastArgumentsJson)["count"].Value<int>(), "integer tool arg");
            AssertEqual(true, JObject.Parse(controller.LastArgumentsJson)["enabled"].Value<bool>(), "bool tool arg");
            AssertEqual("[[\"A\"]]", JObject.Parse(controller.LastArgumentsJson)["values"].Value<string>(), "nested tool arg");
            AssertTrue(controller.LastDryRun, "dry run");
            AssertEqual(1, progressMessages.Count, "progress count");
            AssertEqual("progress", JObject.Parse(progressMessages[0])["type"].Value<string>(), "progress type");
        }

        private static void BridgeRejectsMissingToken()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"bad\",\"type\":\"runTool\",\"payload\":{\"toolId\":\"excel.add_sheet\",\"arguments\":{},\"dryRun\":true}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(!response["ok"].Value<bool>(), "bridge rejects missing token");
            AssertContains(response["error"].Value<string>(), "bridge token", "bridge token error");
        }

        private static void BridgeInitReturnsToken()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);

            AssertTrue(!string.IsNullOrWhiteSpace(token), "bridge token returned");
        }

        private static void BridgeUsesTypedSendChatPayloadAndProgress()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b2\",\"type\":\"sendChat\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-1\",\"text\":\"hello\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("ok", response["payload"]["message"].Value<string>(), "chat response message");
            AssertEqual("hello", controller.LastChatText, "chat text");
            AssertEqual("chat-1", controller.LastChatId, "chat id");
            var progress = JObject.Parse(progressMessages[0]);
            AssertEqual("b2", progress["id"].Value<string>(), "progress id");
            AssertEqual("thinking", progress["payload"]["phase"].Value<string>(), "progress phase");
            AssertEqual("Testing progress", progress["payload"]["activity"]["Title"].Value<string>(), "progress activity title");
            AssertEqual(3, progressMessages.Count, "send chat event count");
            var stream = JObject.Parse(progressMessages[1]);
            AssertEqual("streaming", stream["payload"]["phase"].Value<string>(), "stream phase");
            AssertEqual("Hel", stream["payload"]["contentDelta"].Value<string>(), "stream content delta");
            var chatState = JObject.Parse(progressMessages[2]);
            AssertEqual("chatState", chatState["type"].Value<string>(), "chat state event type");
            AssertEqual("chat-1", chatState["payload"]["activeChatId"].Value<string>(), "chat state active id");
        }

        private static void BridgeUsesTypedEditMessagePayloadAndProgress()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"edit1\",\"type\":\"editMessage\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-2\",\"id\":\"message-7\",\"index\":3,\"text\":\"edited text\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "edit response ok");
            AssertEqual("edited text", controller.LastChatText, "edit text");
            AssertEqual("chat-2", controller.LastChatId, "edit chat id");
            AssertEqual("message-7", response["payload"]["activeChatModel"].Value<string>(), "edit stub payload");
            AssertEqual(2, progressMessages.Count, "edit event count");
            AssertEqual("thinking", JObject.Parse(progressMessages[0])["payload"]["phase"].Value<string>(), "edit progress phase");
            AssertEqual("chatState", JObject.Parse(progressMessages[1])["type"].Value<string>(), "edit chat state event");
        }

        private static void BridgeCompactsAddressedChatContext()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"compact1\",\"type\":\"compactChatContext\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-compact\"}}")
                .GetAwaiter().GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "compact response ok");
            AssertEqual("chat-compact", controller.LastChatId, "compact targets addressed chat");
            AssertEqual(1, progressMessages.Count, "compact progress count");
            var progress = JObject.Parse(progressMessages[0]);
            AssertEqual("compacted", progress["payload"]["phase"].Value<string>(), "compact progress phase");
            AssertEqual("compaction", progress["payload"]["activity"]["Kind"].Value<string>(), "compact activity kind");
        }

        private static void BridgeConfirmProgressCarriesChatAndRunIds()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"confirm1\",\"type\":\"confirmAgentTool\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-confirm\",\"pendingId\":\"pending-1\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "confirm response ok");
            AssertEqual("chat-confirm", controller.LastChatId, "confirm chat id");
            AssertTrue(!string.IsNullOrWhiteSpace(controller.LastRunId), "confirm run id forwarded");
            AssertEqual(1, progressMessages.Count, "confirm progress count");
            var progress = JObject.Parse(progressMessages[0]);
            AssertEqual("chat-confirm", progress["payload"]["chatId"].Value<string>(), "confirm progress chat id");
            AssertEqual(controller.LastRunId, progress["payload"]["runId"].Value<string>(), "confirm progress run id");
            AssertEqual("executing", progress["payload"]["phase"].Value<string>(), "confirm progress phase");
        }

        private static void BridgeUsesTypedChatModePayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"mode1\",\"type\":\"setChatMode\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-1\",\"mode\":\"agent\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "mode response ok");
            AssertEqual("chat-1", controller.LastChatId, "mode chat id");
            AssertEqual("agent", controller.LastChatMode, "mode payload");
            AssertEqual("agent", response["payload"]["activeChatMode"].Value<string>(), "mode response");
        }

        private static void BridgeUsesTypedChatReasoningPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"reasoning1\",\"type\":\"setChatReasoning\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-1\",\"enabled\":true}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "reasoning response ok");
            AssertEqual("chat-1", controller.LastChatId, "reasoning chat id");
            AssertTrue(controller.LastChatReasoning, "reasoning payload");
            AssertTrue(response["payload"]["activeChatReasoning"].Value<bool>(), "reasoning response");
        }

        private static void BridgeUsesTypedSettingsPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b3\",\"type\":\"saveSettings\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"settings\":{\"model\":\"gpt-test\",\"uiTheme\":\"dark\",\"debugModelTraffic\":true,\"screenCaptureProtectionEnabled\":false,\"reasoningRequestMode\":\"custom_json\",\"reasoningCustomJson\":\"{\\\"thinking\\\":{\\\"budget\\\":4096}}\",\"systemPromptRole\":\"system\",\"agentResponseMode\":\"json_schema\",\"toolResultRole\":\"tool\",\"fallbackToJsonObject\":false,\"maxAgentFormatRetries\":4,\"historyIntegrityMode\":\"hmac_sha256\",\"historyEncryptionMode\":\"aes256_cbc_hmac_sha256\",\"historyKeySource\":\"custom_secret\",\"modelImageSupportOverrides\":{\"gpt-test\":true},\"modelAudioSupportOverrides\":{\"gpt-audio\":true}},\"apiKey\":\"secret\",\"historySecret\":\"portable secret\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("gpt-test", controller.LastSettings.Model, "settings model");
            AssertEqual(UiThemes.Dark, controller.LastSettings.UiTheme, "settings theme");
            AssertTrue(controller.LastSettings.DebugModelTraffic, "settings model traffic debug flag");
            AssertTrue(!controller.LastSettings.ScreenCaptureProtectionEnabled, "screen capture protection setting");
            AssertEqual(ReasoningRequestModes.CustomJson, controller.LastSettings.ReasoningRequestMode, "settings custom reasoning mode");
            AssertContains(controller.LastSettings.ReasoningCustomJson, "budget", "settings custom reasoning json");
            AssertEqual("system", controller.LastSettings.SystemPromptRole, "system prompt role");
            AssertEqual(AgentResponseModes.JsonSchema, controller.LastSettings.AgentResponseMode, "agent response mode");
            AssertEqual(ToolResultRoles.Tool, controller.LastSettings.ToolResultRole, "tool result role");
            AssertTrue(!controller.LastSettings.FallbackToJsonObject, "json schema fallback setting");
            AssertEqual(4, controller.LastSettings.MaxAgentFormatRetries, "format retry limit");
            AssertEqual(true, controller.LastSettings.ModelImageSupportOverrides["gpt-test"].Value, "model image override");
            AssertEqual(true, controller.LastSettings.ModelAudioSupportOverrides["gpt-audio"].Value, "model audio override");
            AssertEqual("secret", controller.LastApiKey, "api key");
            AssertEqual(HistoryIntegrityModes.HmacSha256, controller.LastSettings.HistoryIntegrityMode, "history integrity mode");
            AssertEqual(HistoryEncryptionModes.Aes256CbcHmacSha256, controller.LastSettings.HistoryEncryptionMode, "history encryption mode");
            AssertEqual(HistoryKeySources.CustomSecret, controller.LastSettings.HistoryKeySource, "history key source");
            AssertEqual("portable secret", controller.LastHistorySecret, "history secret");

            var runtimeLog = JObject.Parse(bridge.HandleMessageAsync(
                "{\"id\":\"log1\",\"type\":\"getRuntimeLog\",\"bridgeToken\":\"" + token + "\",\"payload\":{}}")
                .GetAwaiter().GetResult());
            AssertEqual("runtime log", runtimeLog.SelectToken("payload.content").Value<string>(), "runtime log bridge response");
        }

        private static void BridgeRunsModelCompatibilityTest()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"compat1\",\"type\":\"testModelCompatibility\",\"bridgeToken\":\"" + token + "\",\"payload\":{}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "compatibility bridge response ok");
            AssertTrue(response["payload"]["compatible"].Value<bool>(), "compatibility result forwarded");
            AssertEqual("user_role", response["payload"]["checks"][0]["id"].Value<string>(), "typed compatibility check forwarded");
        }

        private static void BridgeUsesTypedDocumentPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"doc1\",\"type\":\"activateDocument\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"documentKey\":\"forecast-doc\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "document activation response ok");
            AssertEqual("forecast-doc", response["payload"]["activeChatId"].Value<string>(), "document key payload");

            var deleteResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"doc2\",\"type\":\"deleteDocument\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"host\":\"Excel\",\"documentKey\":\"forecast-doc\"}}")
                .GetAwaiter()
                .GetResult();
            AssertTrue(JObject.Parse(deleteResponseJson)["ok"].Value<bool>(), "document delete response ok");
            AssertEqual("Excel", controller.LastDocumentHost, "document delete host");
        }

        private static void BridgeUsesTypedToolAndSkillPayloads()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var toolsResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b6\",\"type\":\"saveTools\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"tools\":[{\"Id\":\"excel.custom\",\"Host\":\"Excel\",\"Executor\":\"pipeline\",\"Enabled\":true}]}}")
                .GetAwaiter()
                .GetResult();
            var skillsResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b7\",\"type\":\"saveSkills\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"skills\":[{\"Id\":\"common.review\",\"Host\":\"Common\",\"BodyMarkdown\":\"# Review\",\"Enabled\":true}]}}")
                .GetAwaiter()
                .GetResult();

            AssertTrue(JObject.Parse(toolsResponseJson)["ok"].Value<bool>(), "tools bridge response ok");
            AssertTrue(JObject.Parse(skillsResponseJson)["ok"].Value<bool>(), "skills bridge response ok");
            AssertEqual("excel.custom", JArray.Parse(controller.LastToolsJson)[0]["Id"].Value<string>(), "tool id");
            AssertEqual("common.review", JArray.Parse(controller.LastSkillsJson)[0]["Id"].Value<string>(), "skill id");
        }

        private static void BridgeUsesTypedContextPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b4\",\"type\":\"addTextContext\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-2\",\"kind\":\"note\",\"title\":\"T\",\"reference\":\"R\",\"text\":\"Body\",\"detailsJson\":\"{}\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("chat-2", controller.LastChatId, "chat id");
            AssertEqual("note", controller.LastContextKind, "context kind");
            AssertEqual("T", controller.LastContextTitle, "context title");
            AssertEqual("R", controller.LastContextReference, "context reference");
            AssertEqual("Body", controller.LastContextText, "context text");
        }

        private static void BridgeUsesTypedPromptContextInspectorPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"context-inspect\",\"type\":\"inspectPromptContext\",\"bridgeToken\":\"" + token +
                "\",\"payload\":{\"chatId\":\"chat-context\",\"text\":\"draft\",\"attachmentIds\":[\"file-1\"],\"includeRaw\":true}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "prompt context inspector response ok");
            AssertEqual("chat-context", response["payload"]["chatId"].Value<string>(), "inspector chat id");
            AssertEqual("{}", response["payload"]["rawRequestJson"].Value<string>(), "raw flag reaches typed payload");
        }

        private static void BridgeUsesTypedVbaPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var readResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b5-read\",\"type\":\"getVbaModule\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"moduleName\":\"Module2\"}}")
                .GetAwaiter()
                .GetResult();
            AssertTrue(JObject.Parse(readResponseJson)["ok"].Value<bool>(), "VBA read bridge response ok");
            AssertEqual("Module2", controller.LastModuleName, "read module name");

            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b5\",\"type\":\"saveVbaModule\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"moduleName\":\"Module1\",\"code\":\"Sub Main()\\nEnd Sub\",\"expectedCodeSha256\":\"save123\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("Module1", controller.LastModuleName, "module name");
            AssertContains(controller.LastModuleCode, "Sub Main", "module code");
            AssertEqual("save123", controller.LastModuleHash, "save module hash");

            var createResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b5-create\",\"type\":\"createVbaModule\",\"bridgeToken\":\"" + token +
                "\",\"payload\":{\"moduleName\":\"UserForm1\",\"componentType\":\"MSForm\",\"code\":\"Option Explicit\"}}")
                .GetAwaiter()
                .GetResult();
            AssertTrue(JObject.Parse(createResponseJson)["ok"].Value<bool>(), "VBA create bridge response ok");
            AssertEqual("UserForm1", controller.LastModuleName, "create module name");
            AssertEqual("MSForm", controller.LastModuleType, "create module type");
            AssertContains(controller.LastModuleCode, "Option Explicit", "create module code");

            var deleteResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b5-delete\",\"type\":\"deleteVbaModule\",\"bridgeToken\":\"" + token +
                "\",\"payload\":{\"moduleName\":\"Module3\"}}")
                .GetAwaiter()
                .GetResult();
            AssertTrue(JObject.Parse(deleteResponseJson)["ok"].Value<bool>(), "VBA delete bridge response ok");
            AssertEqual("Module3", controller.LastModuleName, "delete module name");

            var runResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b5-run\",\"type\":\"runVbaMacro\",\"bridgeToken\":\"" + token +
                "\",\"payload\":{\"macroName\":\"Module1.Main\"}}")
                .GetAwaiter()
                .GetResult();
            AssertTrue(JObject.Parse(runResponseJson)["ok"].Value<bool>(), "VBA run bridge response ok");
            AssertEqual("Module1.Main", controller.LastModuleName, "run macro name");
        }

        private static void BridgeReportsModelConnectionDiagnostics()
        {
            var controller = new AssistantController();
            var messages = new List<string>();
            using (var bridge = new AssistantWebBridge(controller, messages.Add))
            {
                var token = BridgeToken(bridge);
                var responseJson = bridge.HandleMessageAsync(
                    "{\"id\":\"ping1\",\"type\":\"testModelConnection\",\"bridgeToken\":\"" + token + "\",\"payload\":{}}")
                    .GetAwaiter().GetResult();

                var response = JObject.Parse(responseJson);
                AssertTrue(response["ok"].Value<bool>(), "model connection bridge response ok");
                AssertTrue(response["payload"]["success"].Value<bool>(), "model connection succeeds");
                AssertEqual("modelDiagnostics", JObject.Parse(messages[0])["type"].Value<string>(), "diagnostics event type");
                AssertEqual("completed", JObject.Parse(messages[0])["payload"]["phase"].Value<string>(), "diagnostics phase");
            }
        }

        private static void BridgeReportsCasMaintenance()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var health = JObject.Parse(bridge.HandleMessageAsync(
                "{\"id\":\"cas1\",\"type\":\"getCasHealth\",\"bridgeToken\":\"" + token + "\",\"payload\":{}}")
                .GetAwaiter().GetResult());
            var collected = JObject.Parse(bridge.HandleMessageAsync(
                "{\"id\":\"cas2\",\"type\":\"collectCasGarbage\",\"bridgeToken\":\"" + token + "\",\"payload\":{}}")
                .GetAwaiter().GetResult());

            AssertTrue(health["ok"].Value<bool>(), "CAS health bridge response ok");
            AssertTrue(health.SelectToken("payload.reachabilityComplete").Value<bool>(), "CAS reachability forwarded");
            AssertTrue(collected["ok"].Value<bool>(), "CAS GC bridge response ok");
            AssertTrue(collected.SelectToken("payload.completed").Value<bool>(), "CAS GC result forwarded");
        }

        private static void BridgeUsesTypedHtmlWorkspaceDeletePayloads()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var fileResponse = bridge.HandleMessageAsync(
                "{\"id\":\"html1\",\"type\":\"deleteHtmlWorkspaceFile\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-html\",\"path\":\"scripts/app.js\"}}")
                .GetAwaiter()
                .GetResult();
            var dataResponse = bridge.HandleMessageAsync(
                "{\"id\":\"html2\",\"type\":\"deleteHtmlWorkspaceData\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-html\",\"name\":\"sales\"}}")
                .GetAwaiter()
                .GetResult();

            AssertTrue(JObject.Parse(fileResponse)["ok"].Value<bool>(), "html file delete bridge response ok");
            AssertTrue(JObject.Parse(dataResponse)["ok"].Value<bool>(), "html data delete bridge response ok");
            AssertEqual("chat-html", controller.LastChatId, "html delete chat id");
            AssertEqual("scripts/app.js", controller.LastHtmlPath, "html delete file path");
            AssertEqual("sales", controller.LastHtmlDataName, "html delete data name");
        }

        private static void BridgeUsesTypedHtmlNetworkPayloads()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var allow = bridge.HandleMessageAsync(
                "{\"id\":\"net1\",\"type\":\"allowHtmlNetworkOrigin\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"origin\":\"https://example.test\"}}")
                .GetAwaiter().GetResult();
            var fetch = bridge.HandleMessageAsync(
                "{\"id\":\"net2\",\"type\":\"htmlFetch\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"url\":\"https://example.test/data\",\"method\":\"POST\",\"headers\":{\"Content-Type\":\"application/json\"},\"body\":\"{}\"}}")
                .GetAwaiter().GetResult();

            AssertTrue(JObject.Parse(allow)["ok"].Value<bool>(), "html origin bridge response ok");
            AssertTrue(JObject.Parse(fetch)["ok"].Value<bool>(), "html fetch bridge response ok");
            AssertEqual(200, JObject.Parse(fetch)["payload"]["status"].Value<int>(), "html fetch status");
        }

        private static void BridgeCancelsAddressedChatRun()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"cancel1\",\"type\":\"cancelChatRun\",\"bridgeToken\":\"" + token + "\",\"payload\":{\"chatId\":\"chat-a\",\"runId\":\"run-a\"}}")
                .GetAwaiter().GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "cancel run bridge response ok");
            AssertTrue(response["payload"]["cancelled"].Value<bool>(), "addressed run cancelled");
            AssertEqual("chat-a", controller.LastChatId, "cancel run chat id");
        }

        private static string BridgeToken(AssistantWebBridge bridge)
        {
            var initJson = bridge.HandleMessageAsync("{\"id\":\"init\",\"type\":\"init\",\"payload\":{}}")
                .GetAwaiter()
                .GetResult();
            return JObject.Parse(initJson)["payload"]["bridgeToken"].Value<string>();
        }
    }
}
