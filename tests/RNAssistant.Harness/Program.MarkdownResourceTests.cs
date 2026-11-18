using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void MarkdownResourceWorkspaceSections()
        {
            WithTempPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "markdown-project"));
                Directory.CreateDirectory(Path.Combine(workspace.RootPath, "notes"));
                const string source = "# Root\r\nintro\r\n\r\nChild **title**\r\n--------------\r\nbody 😀\r\n### Nested\r\ntext\r\n# Tail\r\nend\r\n";
                var path = Path.Combine(workspace.RootPath, "notes", "sample.markdown");
                File.WriteAllText(path, source, new UTF8Encoding(false, true));
                var provider = new WorkspaceFileResourceProvider(new WorkspaceFileService(paths), workspace);
                var map = provider.OutlineMarkdown("notes/sample.markdown", 1, 20);
                var headings = map.Parts.SelectMany(part => part.Symbols).ToArray();
                AssertTrue(map.SymbolsComplete && map.SourceComplete && map.ExactReference.IsExact,
                    "workspace heading map belongs to one complete captured source");
                AssertEqual(4, headings.Length, "workspace outline includes ATX and Setext headings");
                AssertEqual("Root", headings[1].ParentHeading, "outline exposes the parent heading");
                AssertEqual(2, headings[1].Level.Value, "Setext level is shared with the renderer");
                AssertEqual(4, headings[1].Line.Value, "outline keeps source lines");
                var read = provider.ReadMarkdownSection("notes/sample.markdown", "Child title", 2000);
                var expected = source.Substring(source.IndexOf("Child", StringComparison.Ordinal),
                    source.IndexOf("# Tail", StringComparison.Ordinal) - source.IndexOf("Child", StringComparison.Ordinal));
                AssertEqual(expected, read.Result.Text, "section includes nested headings and preserves original markup/CRLF");
                AssertTrue(read.Result.Complete && read.Evidence.Single().Coverage.Kind == ResourceCoverageKinds.CharacterRange &&
                    !read.Evidence.Single().Complete && read.Result.CompleteViewPayload == null,
                    "selected section never grants a whole-file read guard");
                var retained = new ChatBlobStore(paths).ReadText(read.Evidence.Single().Payload.ToBlobReference());
                var handler = new WorkspaceResourceHandler(provider, null, () => null, false, null, 2000);
                var execution = new ToolRuntimeFixture().Context(toolId: ResourceToolCatalog.ReadToolId);
                var args = new Dictionary<string, object> { ["target"] = "notes/sample.markdown", ["section"] = "Child title", ["representation"] = "text" };
                var result = handler.ExecuteAsync(new ToolHandlerContext(execution, args), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok, result.Result.Status, "active workspace handler accepts section reads");
                var projection = JObject.Parse(result.Result.DataJson);
                AssertTrue((bool)projection["selectionComplete"] && !(bool)projection["complete"] && !(bool)projection["sourceComplete"],
                    "model sees complete selected text and partial source evidence");
                AssertEqual("Child title", result.ResourceEvidence.Single().ReadMetadata.RequestedSection,
                    "requested section survives evidence metadata");

                File.WriteAllText(path, "# Duplicate\none\n# Duplicate\ntwo\n", new UTF8Encoding(false, true));
                AssertEqual("resource_section_ambiguous", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.ReadMarkdownSection("notes/sample.markdown", "Duplicate", 2000)).Code,
                    "repeated headings never select an arbitrary first occurrence");
                AssertEqual(expected, new ChatBlobStore(paths).ReadText(read.Evidence.Single().Payload.ToBlobReference()),
                    "later live edits preserve the retained section evidence");
                AssertEqual(retained, expected, "retained payload is the selected source, not rendered HTML");
                File.WriteAllText(path, new string('x', MarkdownAnalysisService.MaximumCharacters + 1), new UTF8Encoding(false, true));
                AssertEqual("markdown_too_large", RuntimeThrows<WorkspaceFileException>(() =>
                    provider.OutlineMarkdown("notes/sample.markdown", 1, 20)).Code,
                    "large workspace files do not return a false empty complete outline");
                AssertEqual(10, provider.ReadCharacters("notes/sample.markdown", 0, 10).Result.Text.Length,
                    "ordinary bounded reads remain available above the structure limit");

                var ports = typeof(WorkspaceConversationService).GetNestedType("WorkspacePorts", BindingFlags.NonPublic);
                var schema = JObject.Parse((string)ports.GetMethod("ResourceSchema", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { false, true, false, 2000, false }));
                string error;
                AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject { ["target"] = "notes/sample.markdown", ["section"] = "Root" }, schema, false, out error),
                    "published workspace schema admits the new selector");
                AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject { ["target"] = "notes/sample.markdown", ["section"] = "Root", ["startChar"] = 0, ["charCount"] = 10 }, schema, false, out error),
                    "schema rejects combined section and character selectors");
            });
        }

        private static void MarkdownResourceArtifactOutline()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) => {
                var session = NewSession(adapter); session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var documentArtifacts = new DocumentArtifactStore(executor.ResourceAuthority.Store,
                    executor.ResourceAuthority.Revisions, executor.Payloads);
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(FixturePaths.Value), documentArtifacts);
                const string source = "# Root\n\nSetext **title**\n---------------\nbody\n\n```md\n# Fake\n```\n\n# Tail\nend";
                var draft = ingestion.Stage(ConversationMetadata.From(session), "outline.md", "text/markdown", Encoding.UTF8.GetBytes(source));
                var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                session.Messages.Add(message); ingestion.CommitAndLink(session, message, 0);
                var provider = new ChatArtifactResourceProvider(payloads: executor.Payloads, documentArtifacts: documentArtifacts);
                var selected = provider.List(session, "document-artifact", null, 50).Items.Single(item => item.Title == "outline.md");
                var map = documentArtifacts.OutlineText(session, selected.Reference, 1, 20);
                var headings = map.Parts.SelectMany(part => part.Symbols).ToArray();
                AssertTrue(map.SymbolsComplete && headings.Length == 3, "artifact index uses the same complete AST and ignores fences");
                AssertEqual("Setext title", headings[1].Name, "artifact map uses visible title semantics");
                AssertEqual("Root", headings[1].ParentHeading, "artifact map retains hierarchy");
                var read = documentArtifacts.ReadMarkdownSection(session, selected.Reference, headings[1].Name, 2000);
                AssertTrue(read.Text.StartsWith("Setext **title**", StringComparison.Ordinal) && !read.Text.Contains("# Tail"),
                    "artifact section and outline address the same original range");
                AssertEqual(ResourceCoverageKinds.CharacterRange, read.Coverage.Kind, "artifact section retains partial-source coverage");
                var gatewayItems = executor.ResourceGateway.List(session, "chat", "document-artifact", null, 50).Items;
                AssertEqual(1, gatewayItems.Count, "native gateway discovers the uploaded Markdown artifact");
                var documentItems = executor.ResourceGateway.Find(session, null, "document");
                var documentItem = documentItems.Items.Single(item => item.Title == "outline.md");
                AssertTrue(documentItem.Scope == "document",
                    "document discovery contains the uploaded Markdown artifact: count=" + documentItems.Items.Count);
                var target = documentItem.Target;
                var runtime = executor.CreateNativeRuntime(session, executor.GetControllerTools().ToList(), new AppSettings(), ChatModes.Agent, false);
                var native = ExecuteHtmlNative(runtime, ResourceToolCatalog.OutlineToolId, new JObject { ["target"] = target });
                AssertEqual(ToolExecutionOutcome.Ok, native.Outcome, "current native runtime dispatches the outline handler");
                AssertEqual(0, native.ResourceEvidence.Count, "outline navigation creates no source-read evidence");

            });
        }
    }
}
