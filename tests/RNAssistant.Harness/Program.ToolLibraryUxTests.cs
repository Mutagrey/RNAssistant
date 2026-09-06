using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ToolLibraryDocumentationIsUiOnly()
        {
            var documented = new HashSet<string>(StringComparer.Ordinal);
            foreach (var host in new[]
            {
                "Excel", "Word", "PowerPoint", "Outlook"
            })
            {
                WithTempExecutor(FakeOfficeAdapter.ForHost(host),
                    (executor, adapter) =>
                    {
                        var tools = executor.GetHostTools()
                            .Concat(executor.GetControllerTools())
                            .Where(tool => tool != null && tool.BuiltIn)
                            .ToArray();
                        foreach (var tool in tools)
                        {
                            var revision = ToolAuthoringService
                                .LibraryRevision(tool);
                            var promptDescription = ConversationPromptComposer
                                .BuildDescription(tool);
                            var readme = tool.Readme;
                            var markdown =
                                ToolLibraryDocumentationService.Build(tool);

                            AssertContains(markdown, "# `" + tool.Id + "`",
                                tool.Id + " documentation id");
                            AssertContains(markdown, "## Аргументы",
                                tool.Id + " arguments documentation");
                            AssertContains(markdown,
                                "## Безопасность и эффект",
                                tool.Id + " policy documentation");
                            AssertContains(markdown,
                                "## Безопасная проверка в Library",
                                tool.Id + " test recipe");
                            JObject schema;
                            string error;
                            AssertTrue(ToolSchemaSupport.TryParse(
                                tool, out schema, out error),
                                tool.Id + " schema remains valid");
                            foreach (var property in
                                ((JObject)schema["properties"]).Properties())
                            {
                                AssertContains(markdown,
                                    "`" + property.Name + "`",
                                    tool.Id + " documents " + property.Name);
                            }
                            AssertEqual(readme, tool.Readme,
                                tool.Id + " source readme unchanged");
                            AssertEqual(revision,
                                ToolAuthoringService.LibraryRevision(tool),
                                tool.Id + " library revision unchanged");
                            AssertEqual(promptDescription,
                                ConversationPromptComposer.BuildDescription(tool),
                                tool.Id + " model description unchanged");
                            var projection = ToolLibraryResponse.From(
                                new[] { tool });
                            var metadata = JObject.FromObject(projection.Tools[0]);
                            AssertTrue(metadata["readme"] == null && metadata["code"] == null && metadata["components"] == null &&
                                metadata["argumentSchemaJson"] == null && metadata["source"] != null,
                                tool.Id + " source bodies absent from compact list");
                            documented.Add(tool.Id);
                        }
                    });
            }
            AssertTrue(documented.Count >= 60,
                "all effective built-in families have UI documentation");
        }

        private static void ToolLibraryContinuationKeepsRuntimeStateInternal()
        {
            var source = new ChatSession
            {
                Id = "chat-library",
                Host = "Excel",
                DocumentKey = "book"
            };
            var service = new ToolLibraryTestSessionService();
            ChatSession isolated = null;
            var first = new ToolInvocation
            {
                ToolId = CapabilityToolCatalog.ReadToolId,
                Arguments = new Dictionary<string, object>
                {
                    ["id"] = "common.sample_skill",
                    ["referencePath"] = "references/details.md",
                    ["action"] = "read"
                }
            };
            var firstResult = service.Execute(source, first, session =>
            {
                isolated = session;
                return ToolRunResult.Ok("chunk", new JObject
                {
                    ["kind"] = "reference",
                    ["id"] = "common.sample_skill",
                    ["path"] = "references/details.md",
                    ["skillRevision"] = "skill-revision",
                    ["revision"] = "reference-revision",
                    ["progressCharacters"] = 24000,
                    ["complete"] = false,
                    ["hasMore"] = true,
                    ["content"] = "part one"
                }.ToString(Newtonsoft.Json.Formatting.None));
            });
            AssertEqual("ok", firstResult.Status,
                "first semantic Library read succeeds");
            AssertTrue(isolated != null &&
                !ReferenceEquals(source, isolated),
                "Library continuation uses an isolated session");
            AssertEqual(0, source.Messages.Count,
                "active chat history is unchanged");

            var next = new ToolInvocation
            {
                ToolId = CapabilityToolCatalog.ReadToolId,
                Arguments = new Dictionary<string, object>
                {
                    ["id"] = "common.sample_skill",
                    ["referencePath"] = "references/details.md",
                    ["action"] = "next"
                }
            };
            var nextResult = service.Execute(source, next, session =>
            {
                AssertTrue(ReferenceEquals(isolated, session),
                    "next reuses only the matching isolated session");
                ToolResultWireReadResult previous;
                string error;
                AssertTrue(ToolResultHistoryReader.TryRead(
                    session.Messages.Last(), out previous, out error),
                    "previous chunk is strict internal Tool Result v1");
                AssertEqual(CapabilityToolCatalog.ReadToolId, previous.Name,
                    "continuation history keeps exact tool id");
                return ToolRunResult.Ok("complete", new JObject
                {
                    ["kind"] = "reference",
                    ["id"] = "common.sample_skill",
                    ["path"] = "references/details.md",
                    ["complete"] = true,
                    ["hasMore"] = false,
                    ["content"] = "part two"
                }.ToString(Newtonsoft.Json.Formatting.None));
            });
            AssertEqual("ok", nextResult.Status,
                "matching semantic next succeeds");

            var executed = false;
            var exhausted = service.Execute(source, next, session =>
            {
                executed = true;
                return ToolRunResult.Ok("unexpected");
            });
            AssertEqual("capability_continuation_missing",
                exhausted.ErrorCode,
                "complete continuation cannot be reused");
            AssertTrue(!executed,
                "missing continuation stops before runtime dispatch");
            AssertEqual(0, source.Messages.Count,
                "opaque continuation evidence never enters active chat");
        }
    }
}
