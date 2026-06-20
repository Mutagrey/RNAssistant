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
                Summary = "Pinned summary",
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
            AssertEqual("Pinned summary", clonedContext.Summary, "context summary");
            AssertEqual("Original note", clonedContext.Notes[0].Text, "context note text");
            context.Notes[0].Text = "Changed";
            AssertEqual("Original note", clonedContext.Notes[0].Text, "context clone independent");

            var sourceMessage = new ChatMessage
            {
                Id = "message-1",
                Role = "assistant",
                Content = "Done",
                PromptTokens = 10,
                CompletionTokens = 2,
                TotalTokens = 12,
                UsageJson = "{\"total\":12}",
                Activity = new ChatActivity
                {
                    Kind = "tool",
                    Title = "Write table",
                    Status = "completed",
                    ToolId = "excel.write_table",
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
            AssertEqual(12, clonedMessages[0].TotalTokens, "message tokens");
            AssertTrue(!object.ReferenceEquals(sourceMessage.Activity, clonedMessages[0].Activity), "activity cloned");
            AssertTrue(!object.ReferenceEquals(sourceMessage.Activity.Children[0], clonedMessages[0].Activity.Children[0]), "activity child cloned");
            AssertEqual("Write table", clonedMessages[0].Activity.Title, "activity title");
            sourceMessage.Content = "Changed";
            sourceMessage.Activity.Title = "Changed activity";
            AssertEqual("Done", clonedMessages[0].Content, "message clone independent");
            AssertEqual("Write table", clonedMessages[0].Activity.Title, "activity clone independent");
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
            ContextService.UpsertContextNote(context, note);
            AssertEqual(1, context.Notes.Count, "note count after insert");
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
                DetailsJson = "{\"range\":\"A1\"}"
            };
            ContextService.UpsertContextNote(context, replacement);
            AssertEqual(1, context.Notes.Count, "note count after update");
            AssertEqual("Changed", context.Notes[0].Title, "updated note title");
            AssertEqual("second", context.Notes[0].Text, "updated note text");
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
            AssertEqual("abc", ContextService.TrimForContext("abc", 10), "short trim");
            AssertEqual("abc\n...[truncated]", ContextService.TrimForContext("abcdef", 3), "long trim");
            AssertEqual(string.Empty, ContextService.TrimForContext(null, 3), "null trim");
        }

        private static void BridgeUsesTypedRunToolPayload()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b1\",\"type\":\"runTool\",\"payload\":{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\",\"count\":2,\"enabled\":true,\"values\":[[\"A\"]]},\"dryRun\":true}}")
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

        private static void BridgeUsesTypedSendChatPayloadAndProgress()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b2\",\"type\":\"sendChat\",\"payload\":{\"chatId\":\"chat-1\",\"text\":\"hello\"}}")
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
            AssertEqual(2, progressMessages.Count, "send chat event count");
            var chatState = JObject.Parse(progressMessages[1]);
            AssertEqual("chatState", chatState["type"].Value<string>(), "chat state event type");
            AssertEqual("chat-1", chatState["payload"]["activeChatId"].Value<string>(), "chat state active id");
        }

        private static void BridgeUsesTypedSettingsPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b3\",\"type\":\"saveSettings\",\"payload\":{\"settings\":{\"model\":\"gpt-test\"},\"apiKey\":\"secret\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("gpt-test", controller.LastSettings.Model, "settings model");
            AssertEqual("secret", controller.LastApiKey, "api key");
        }

        private static void BridgeUsesTypedToolAndSkillPayloads()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var toolsResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b6\",\"type\":\"saveTools\",\"payload\":{\"tools\":[{\"Id\":\"excel.custom\",\"Host\":\"Excel\",\"Executor\":\"pipeline\",\"Enabled\":true}]}}")
                .GetAwaiter()
                .GetResult();
            var skillsResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b7\",\"type\":\"saveSkills\",\"payload\":{\"skills\":[{\"Id\":\"common.review\",\"Host\":\"Common\",\"BodyMarkdown\":\"# Review\",\"Enabled\":true}]}}")
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
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b4\",\"type\":\"addTextContext\",\"payload\":{\"chatId\":\"chat-2\",\"kind\":\"note\",\"title\":\"T\",\"reference\":\"R\",\"text\":\"Body\",\"detailsJson\":\"{}\"}}")
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

        private static void BridgeUsesTypedVbaPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b5\",\"type\":\"saveVbaModule\",\"payload\":{\"moduleName\":\"Module1\",\"code\":\"Sub Main()\\nEnd Sub\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("Module1", controller.LastModuleName, "module name");
            AssertContains(controller.LastModuleCode, "Sub Main", "module code");
        }
    }
}
