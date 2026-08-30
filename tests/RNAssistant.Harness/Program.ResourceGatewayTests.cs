using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void NativeResourceToolsUseRuntimeForManualAndModelCalls()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                session.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "First", InlineText = "body" });
                var tools = executor.GetControllerTools().ToArray();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), ChatModes.Chat, false);
                Func<string, string, ToolExecutionRecord> execute = (id, arguments) =>
                {
                    var call = new ToolCall("native_" + id, id, arguments);
                    var policy = runtime.Describe(call);
                    AssertTrue(policy != null && policy.IndependentLocalRead && !policy.MayHaveSideEffects,
                        id + " has source-owned read policy");
                    return runtime.ExecuteAsync(new ToolExecutionContext(call, policy, "run", "turn", "step",
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                };

                var listed = execute(ResourceToolCatalog.ListToolId, "{\"provider\":\"chat\"}");
                AssertEqual(ToolExecutionOutcome.Ok, listed.Outcome, "native resource list succeeds in chat mode");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched, listed.Evidence.Dispatch, "provider invocation is recorded");
                AssertEqual(ToolEffectEvidence.None, listed.Evidence.Effect, "read success does not manufacture verified effect");
                var resourceUri = JsonConvert.DeserializeObject<ResourceListPage>(listed.Result.DataJson)
                    .Items.Single().Reference.Uri;
                AssertTrue(resourceUri.StartsWith("rna://", StringComparison.Ordinal),
                    "native list retains canonical resource references");

                var calls = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ResourceToolCatalog.ListToolId] = "{\"provider\":\"chat\"}",
                    [ResourceToolCatalog.ResolveToolId] = JsonConvert.SerializeObject(new { uri = resourceUri }),
                    [ResourceToolCatalog.SearchToolId] = "{\"provider\":\"chat\",\"query\":\"body\"}",
                    [ResourceToolCatalog.ReadToolId] = JsonConvert.SerializeObject(new
                    {
                        uri = resourceUri,
                        representation = ResourceRepresentations.Text,
                        maxChars = 128
                    })
                };
                foreach (var item in calls)
                {
                    var record = item.Key == ResourceToolCatalog.ListToolId ? listed : execute(item.Key, item.Value);
                    AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, item.Key + " uses its native handler");
                    var command = new ToolCommand
                    {
                        ToolId = item.Key,
                        Arguments = JsonConvert.DeserializeObject<Dictionary<string, object>>(item.Value)
                    };
                    var manual = executor.Execute(command, tools, new AppSettings(), false, true, session);
                    AssertTrue(manual.Success, item.Key + " manual path uses the same native handler");
                    AssertEqual(record.Result.DataJson, manual.DataJson,
                        item.Key + " manual and kernel paths share one implementation");
                    AssertTrue(runtime.Describe(new ToolCall("wrong_case", item.Key.ToUpperInvariant(), "{}")) == null,
                        item.Key + " has no case alias");

                    var invalid = new ToolCall("invalid_" + item.Key, item.Key, "{\"unknown\":true}");
                    var policy = runtime.Describe(invalid);
                    var rejected = runtime.ExecuteAsync(new ToolExecutionContext(invalid, policy, "run", "turn", "step",
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome, item.Key + " rejects invalid arguments");
                    AssertEqual(ToolDispatchEvidence.NotDispatched, rejected.Evidence.Dispatch,
                        item.Key + " validates schema before provider access");
                }
                var readRecord = execute(ResourceToolCatalog.ReadToolId, calls[ResourceToolCatalog.ReadToolId]);
                AssertTrue(readRecord.Result.Resources.Any(reference => reference.Uri == resourceUri),
                    "native resource read retains the exact ResourceRef in typed result data");
                var invalidManual = executor.Execute(Command(ResourceToolCatalog.ListToolId, "limit", 51), tools,
                    new AppSettings(), false, true, session);
                AssertTrue(!invalidManual.Success && invalidManual.ErrorCode == "invalid_arguments",
                    "manual command uses the same native validation boundary");

                var hostRuntime = new HostRuntime(adapter, FixturePaths.Value);
                var target = new OfficeDocumentExecutionExpectation
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey
                };
                var backendCalls = adapter.Executed.Count;
                var liveListArguments = "{\"provider\":\"vba\",\"kind\":\"vba-component\"}";
                using (hostRuntime.BeginDocumentAccess(target))
                {
                    var blockedCall = new ToolCall("blocked_live_list", ResourceToolCatalog.ListToolId,
                        liveListArguments);
                    var blockedPolicy = runtime.Describe(blockedCall);
                    var blocked = runtime.ExecuteAsync(new ToolExecutionContext(blockedCall, blockedPolicy, "run", "turn", "step",
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Error, blocked.Outcome,
                        "new native command cannot borrow document access held on the same thread");
                    AssertEqual("tool_mutation_busy", (string)JObject.Parse(blocked.Result.DataJson)["code"],
                        "native live list reports the occupied document gate");
                    AssertEqual(backendCalls, adapter.Executed.Count, "blocked native live list never reaches Office backend");
                }

                var releasedCall = new ToolCall("released_live_list", ResourceToolCatalog.ListToolId,
                    liveListArguments);
                var releasedPolicy = runtime.Describe(releasedCall);
                var released = runtime.ExecuteAsync(new ToolExecutionContext(releasedCall, releasedPolicy, "run", "turn", "step",
                    DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Ok, released.Outcome, "native live list succeeds after document access release");
                AssertTrue(adapter.Executed.Count > backendCalls, "released native live list reaches the Office backend");
            });
        }

        private static void ResourceGatewayReadsSearchesAndPages()
        {
            var attachment = new ChatAttachment
            {
                Id = "attachment-text",
                Kind = "text",
                FileName = "notes.txt",
                ContentType = "text/plain",
                ExtractedText = new string('a', 180) + " NEEDLE " + new string('b', 180),
                ExtractedCharCount = 368
            };
            var message = new ChatMessage
            {
                Id = "source-message",
                Role = "user",
                Content = "Use notes",
                Attachments = new List<ChatAttachment> { attachment }
            };
            var artifact = new ChatArtifact
            {
                Id = "attachment_attachment-text",
                Kind = ChatArtifactKinds.Attachment,
                Title = "notes.txt",
                MimeType = "text/plain",
                SourceMessageId = message.Id,
                MetadataJson = "{\"attachmentId\":\"attachment-text\"}"
            };
            var session = new ChatSession
            {
                Messages = new List<ChatMessage> { message },
                Artifacts = new List<ChatArtifact> { artifact }
            };
            message.ResourceRefs.Add(ArtifactReference(session, artifact));
            var gateway = new ResourceGatewayService();
            var resourceUri = ArtifactUri(session, artifact);

            var listed = gateway.List(session, null, null, null, 10);
            AssertEqual(1, listed.Items.Count, "resource list count");
            AssertEqual(resourceUri, listed.Items[0].Reference.Uri, "resource list uses canonical URI");
            AssertTrue(listed.Items[0].Representations.Contains("text"),
                "artifact list advertises extracted text");

            var pagingSession = new ChatSession();
            pagingSession.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "First", InlineText = "1" });
            pagingSession.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "Second", InlineText = "2" });
            var pagingGateway = new ResourceGatewayService();
            var firstListPage = pagingGateway.List(
                pagingSession, ChatArtifactResourceProvider.ProviderName, ChatArtifactKinds.Markdown, null, 1);
            AssertTrue(!string.IsNullOrWhiteSpace(firstListPage.NextCursor),
                "resource list exposes an opaque continuation");
            AssertTrue(firstListPage.Cursor.StartsWith("r1:", StringComparison.Ordinal),
                "resource list keeps an internal revision-bound cursor");
            var firstListJson = JsonConvert.SerializeObject(firstListPage);
            AssertTrue(firstListJson.IndexOf("\"cursor\"", StringComparison.Ordinal) < 0,
                "resource list does not expose the current-page cursor");
            AssertContains(firstListJson, "\"nextCursor\"",
                "resource list exposes only the usable continuation cursor");
            var readRegistry = new ToolHandlerRegistry();
            var readRegistration = ToolPackSnapshot.Capture(
                ResourceReadToolHandler.Descriptor,
                ResourceReadToolHandler.Policy,
                ResourceReadToolHandler.Binding);
            readRegistry.Register(readRegistration,
                new ResourceReadToolHandler(pagingGateway, pagingSession, null));
            var readRuntime = new ToolRuntime(readRegistry, ChatModes.Chat, false, false);
            var crossCall = new ToolCall("cross_cursor", ResourceToolCatalog.ReadToolId,
                JsonConvert.SerializeObject(new
                {
                    uri = firstListPage.Items[0].Reference.Uri,
                    representation = ResourceRepresentations.Text,
                    cursor = firstListPage.Cursor
                }));
            var crossPolicy = readRuntime.Describe(crossCall);
            var crossRecord = readRuntime.ExecuteAsync(new ToolExecutionContext(
                crossCall, crossPolicy, "run", "turn", "step", DateTime.UtcNow, false, 1),
                CancellationToken.None).GetAwaiter().GetResult();
            var crossOperationCursor = ToolResultUiProjection.Create(crossRecord);
            AssertEqual("resource_cursor_invalid", crossOperationCursor.ErrorCode,
                "list cursor is rejected by resource read");
            AssertTrue(crossOperationCursor.Retryable != true,
                "invalid cross-operation cursor is not retried unchanged");
            AssertContains(crossOperationCursor.Message, "Omit cursor",
                "invalid cursor tells the model how to restart");
            pagingSession.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "Third", InlineText = "3" });
            ResourceRequestException listDrift = null;
            try
            {
                pagingGateway.List(
                    pagingSession,
                    ChatArtifactResourceProvider.ProviderName,
                    ChatArtifactKinds.Markdown,
                    firstListPage.NextCursor,
                    1);
            }
            catch (ResourceRequestException ex)
            {
                listDrift = ex;
            }
            AssertEqual("resource_revision_changed", listDrift == null ? null : listDrift.ErrorCode,
                "resource list continuation fails instead of shifting across collection revisions");

            var resolved = gateway.Resolve(session, resourceUri);
            AssertEqual(resourceUri, resolved.Resource.Reference.Uri, "resource resolve is exact");

            ResourceRequestException pinnedMismatch = null;
            try
            {
                ReadResource(gateway, session, resourceUri, "text", null, 128, "999");
            }
            catch (ResourceRequestException ex)
            {
                pinnedMismatch = ex;
            }
            AssertEqual("resource_revision_mismatch", pinnedMismatch == null ? null : pinnedMismatch.ErrorCode,
                "immutable read rejects a revision token that disagrees with the exact URI");

            var first = ReadResource(gateway, session, resourceUri, "text", null, 128).Result;
            AssertEqual(128, first.ReturnedCharacters, "resource read is bounded");
            AssertTrue(!first.Complete && first.Truncated, "resource read exposes truncation");
            var firstJson = JsonConvert.SerializeObject(first);
            AssertTrue(firstJson.IndexOf("\"offset\"", StringComparison.Ordinal) < 0,
                "resource result keeps continuation offsets opaque");
            AssertContains(firstJson, "\"nextCursor\"", "resource result exposes only the continuation cursor");
            var second = ReadResource(gateway, session, resourceUri, "text",
                first.NextCursor, 128).Result;
            AssertTrue(second.Offset > 0, "resource cursor advances");

            var search = gateway.Search(session, null, "NEEDLE", null, 10, 256);
            AssertEqual(1, search.Matches.Count, "resource text search count");
            AssertEqual(resourceUri, search.Matches[0].Reference.Uri, "resource search URI");
            AssertContains(search.Matches[0].Snippet, "NEEDLE", "resource search snippet");

            var invalidRepresentationRejected = false;
            try
            {
                ReadResource(gateway, session, resourceUri, "legacy", null, 128);
            }
            catch (InvalidOperationException)
            {
                invalidRepresentationRejected = true;
            }
            AssertTrue(invalidRepresentationRejected, "resource reads reject unknown representations");

            var imageAttachment = new ChatAttachment
            {
                Id = "direct-image",
                Kind = "image",
                FileName = "direct.png",
                ContentType = "image/png"
            };
            var imageMessage = new ChatMessage
            {
                Id = "direct-source",
                Role = "user",
                Attachments = new List<ChatAttachment> { imageAttachment }
            };
            session.Messages.Add(imageMessage);
            var imageArtifact = new ChatArtifact
            {
                Id = "attachment_direct-image",
                Kind = ChatArtifactKinds.Image,
                SourceMessageId = imageMessage.Id,
                MetadataJson = "{\"attachmentId\":\"direct-image\"}"
            };
            session.Artifacts.Add(imageArtifact);
            var media = ReadResource(
                gateway,
                session,
                ArtifactUri(session, imageArtifact),
                "media",
                null,
                128);
            AssertTrue(media.Result.HydratedForNextModelStep, "media is hydrated only by an explicit resource read");
            AssertEqual("direct-image", media.ModelAttachments.Single().Id, "media read returns exact attachment");

            session.Artifacts.Add(new ChatArtifact
            {
                Id = "attachment_unmapped",
                Kind = ChatArtifactKinds.Image,
                SourceMessageId = imageMessage.Id
            });
            imageMessage.Attachments.Add(new ChatAttachment { Id = "unmapped", Kind = "image" });
            var implicitAttachmentMappingRejected = false;
            try
            {
                ReadResource(
                    gateway,
                    session,
                    ArtifactUri(session, session.Artifacts.Last()),
                    "media",
                    null,
                    128);
            }
            catch (InvalidOperationException)
            {
                implicitAttachmentMappingRejected = true;
            }
            AssertTrue(implicitAttachmentMappingRejected,
                "artifact media requires explicit attachmentId metadata instead of a legacy id convention");

            var htmlSession = new ChatSession();
            HtmlArtifactToolExecutor.UpsertFile(
                htmlSession,
                "index.html",
                "html",
                "<main>Dashboard</main>",
                true);
            var oldScript = new string('x', 180) + " OLD_NEEDLE";
            HtmlArtifactToolExecutor.UpsertFile(
                htmlSession,
                "scripts/nested/app.js",
                "script",
                oldScript,
                false);
            HtmlArtifactToolExecutor.UpsertDataSource(htmlSession, "rows", "{\"items\":[1,2]}");

            var htmlGateway = new ResourceGatewayService();
            var oldScriptResource = htmlGateway.List(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.FileKind,
                null,
                10).Items.Single(item => item.Title == "scripts/nested/app.js");
            AssertTrue(oldScriptResource.Reference.Uri.IndexOf("scripts", StringComparison.OrdinalIgnoreCase) < 0,
                "HTML member URI does not expose a nested workspace path");
            AssertEqual(ResourceRepresentations.Source, oldScriptResource.Representations.Last(),
                "HTML files advertise source representation");

            var boundedSource = ReadResource(
                htmlGateway,
                htmlSession,
                oldScriptResource.Reference.Uri,
                ResourceRepresentations.Source,
                null,
                128).Result;
            AssertEqual(128, boundedSource.ReturnedCharacters, "HTML member read obeys the common bound");
            AssertTrue(boundedSource.Truncated && !string.IsNullOrWhiteSpace(boundedSource.NextCursor),
                "HTML member read exposes the common continuation cursor");

            var missingMemberRejected = false;
            try
            {
                var address = ResourceUri.Parse(oldScriptResource.Reference.Uri);
                var segments = address.Segments.ToArray();
                segments[7] = new string('0', 64);
                ReadResource(
                    htmlGateway,
                    htmlSession,
                    ResourceUri.Create(ChatArtifactResourceProvider.ProviderName, segments),
                    ResourceRepresentations.Source,
                    null,
                    128);
            }
            catch (KeyNotFoundException)
            {
                missingMemberRejected = true;
            }
            AssertTrue(missingMemberRejected,
                "unknown HTML member URI cannot fall back to the parent artifact");

            var dataResource = htmlGateway.List(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.DataKind,
                null,
                10).Items.Single();
            AssertContains(
                ReadResource(htmlGateway, htmlSession, dataResource.Reference.Uri, ResourceRepresentations.Text, null, 128).Result.Text,
                "items",
                "HTML data is an independently readable text resource");

            HtmlArtifactToolExecutor.UpsertFile(
                htmlSession,
                "scripts/nested/app.js",
                "script",
                "const CURRENT_NEEDLE = true;",
                false);
            var currentScriptResource = htmlGateway.List(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.FileKind,
                null,
                10).Items.Single(item => item.Title == "scripts/nested/app.js");
            AssertTrue(!string.Equals(
                    oldScriptResource.Reference.Uri,
                    currentScriptResource.Reference.Uri,
                    StringComparison.Ordinal),
                "HTML mutation creates a new revision-pinned member URI");
            AssertContains(
                ReadResource(htmlGateway, htmlSession, oldScriptResource.Reference.Uri, ResourceRepresentations.Source, null, 8000).Result.Text,
                "OLD_NEEDLE",
                "historical HTML member URI remains pinned to its original revision");
            AssertContains(
                ReadResource(htmlGateway, htmlSession, currentScriptResource.Reference.Uri, ResourceRepresentations.Source, null, 8000).Result.Text,
                "CURRENT_NEEDLE",
                "current HTML member URI reads the new revision");

            var htmlSearch = htmlGateway.Search(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                "CURRENT_NEEDLE",
                ChatHtmlResourceCatalog.FileKind,
                10,
                128);
            AssertEqual(currentScriptResource.Reference.Uri, htmlSearch.Matches.Single().Reference.Uri,
                "HTML search returns the exact current member URI");

            var activeHtmlArtifact = htmlSession.Artifacts.Single(item => item.Id == htmlSession.ActiveHtmlArtifactId);
            var activeHtmlUri = ArtifactUri(htmlSession, activeHtmlArtifact);
            var activeHtmlDescriptor = htmlGateway.Resolve(htmlSession, activeHtmlUri).Resource;
            AssertEqual("metadata,structure", string.Join(",", activeHtmlDescriptor.Representations.ToArray()),
                "HTML root descriptor does not advertise unsupported text reads");
            var unsupportedHtmlTextRejected = false;
            try
            {
                ReadResource(
                    htmlGateway,
                    htmlSession,
                    activeHtmlUri,
                    ResourceRepresentations.Text,
                    null,
                    8000);
            }
            catch (ResourceRequestException ex)
            {
                unsupportedHtmlTextRejected = string.Equals(
                    ex.ErrorCode,
                    "resource_representation_unavailable",
                    StringComparison.Ordinal);
            }
            AssertTrue(unsupportedHtmlTextRejected,
                "unsupported HTML root text reads return a stable actionable error");
            var structure = ReadResource(
                htmlGateway,
                htmlSession,
                activeHtmlUri,
                ResourceRepresentations.Structure,
                null,
                8000).Result;
            AssertContains(structure.Text, "scripts/nested/app.js", "HTML structure lists member metadata");
            AssertTrue(structure.Text.IndexOf("CURRENT_NEEDLE", StringComparison.Ordinal) < 0,
                "HTML structure does not copy member bodies");
        }

        private static void LiveOfficeAndVbaResourcesAreBoundedAndGuarded()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.SetVbaModule(
                    "ResourceModule",
                    "Option Explicit\nSub ResourceNeedle()\n" + new string('x', 220) + "\nEnd Sub",
                    "StdModule");
                var session = NewSession(adapter);
                var gateway = executor.ResourceGateway;

                var discovery = gateway.List(session, null, null, null, 20);
                AssertEqual("chat,document,vba", string.Join(",", discovery.Providers.ToArray()),
                    "resource discovery exposes the registered providers only");

                var document = gateway.List(
                    session,
                    LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.DocumentKind,
                    null,
                    20).Items.Single();
                AssertTrue(document.Reference.Uri.StartsWith("rna://document/", StringComparison.Ordinal),
                    "live document uses a canonical resource URI");
                AssertTrue(document.Reference.Uri.IndexOf("MockWorkbook", StringComparison.OrdinalIgnoreCase) < 0 &&
                    document.Reference.Uri.IndexOf("C:", StringComparison.OrdinalIgnoreCase) < 0,
                    "live document URI does not expose its title or path");

                for (var index = 0; index < 6; index++)
                {
                    adapter.ExecuteTool(Command("excel.add_sheet", "name", "ResourcePage" + index));
                }
                var documentText = ReadResource(
                    gateway,
                    session,
                    document.Reference.Uri,
                    ResourceRepresentations.Text,
                    null,
                    128).Result;
                AssertTrue(!string.IsNullOrWhiteSpace(documentText.Text), "live document text is readable on demand");
                AssertTrue(!string.IsNullOrWhiteSpace(documentText.ContentSha256) &&
                    string.Equals(
                        documentText.ContentSha256,
                        documentText.Resource.Reference.Revision,
                        StringComparison.Ordinal),
                    "live document read carries exact revision evidence");
                AssertTrue(!string.IsNullOrWhiteSpace(documentText.NextCursor),
                    "long live document read exposes a revision-bound continuation");
                adapter.ExecuteTool(Command("excel.add_sheet", "name", "ResourceDrift"));
                ResourceRequestException documentDrift = null;
                try
                {
                    ReadResource(
                        gateway,
                        session,
                        document.Reference.Uri,
                        ResourceRepresentations.Text,
                        documentText.NextCursor,
                        128);
                }
                catch (ResourceRequestException ex)
                {
                    documentDrift = ex;
                }
                AssertEqual("resource_revision_changed", documentDrift == null ? null : documentDrift.ErrorCode,
                    "live document continuation fails instead of mixing revisions");
                ResourceRequestException pinnedDocumentDrift = null;
                try
                {
                    ReadResource(
                        gateway,
                        session,
                        document.Reference.Uri,
                        ResourceRepresentations.Text,
                        null,
                        128,
                        documentText.ContentSha256);
                }
                catch (ResourceRequestException ex)
                {
                    pinnedDocumentDrift = ex;
                }
                AssertEqual("resource_revision_changed", pinnedDocumentDrift == null ? null : pinnedDocumentDrift.ErrorCode,
                    "live read rejects an explicitly pinned stale revision before returning its first chunk");

                var selection = gateway.List(
                    session,
                    LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.SelectionKind,
                    null,
                    20).Items.Single();
                var selectionSearch = gateway.Search(
                    session,
                    LiveDocumentResourceProvider.ProviderName,
                    "Sales",
                    LiveDocumentResourceProvider.SelectionKind,
                    10,
                    128);
                AssertEqual(selection.Reference.Uri, selectionSearch.Matches.Single().Reference.Uri,
                    "selection search returns its exact live resource URI");
                AssertTrue(!string.IsNullOrWhiteSpace(selectionSearch.Matches.Single().Reference.Revision),
                    "live search result pins the searched content revision");

                var component = VbaComponent(executor, session, "ResourceModule");
                AssertTrue(component.Reference.Uri.IndexOf("ResourceModule", StringComparison.OrdinalIgnoreCase) < 0,
                    "VBA component URI does not expose the module name");
                var firstSource = ReadResource(
                    gateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result;
                AssertEqual(128, firstSource.ReturnedCharacters, "VBA source read is bounded");
                AssertTrue(firstSource.Truncated && !string.IsNullOrWhiteSpace(firstSource.NextCursor),
                    "VBA source read exposes continuation");
                AssertTrue(!string.IsNullOrWhiteSpace(firstSource.Resource.Reference.Revision),
                    "VBA source read carries exact revision evidence");

                var firstComponentPage = gateway.List(
                    session,
                    VbaResourceProvider.ProviderName,
                    VbaResourceProvider.ComponentKind,
                    null,
                    1);
                AssertTrue(!string.IsNullOrWhiteSpace(firstComponentPage.NextCursor),
                    "VBA component list exposes an opaque continuation");
                adapter.SetVbaModule("ResourceListDrift", "Sub Added()\nEnd Sub", "StdModule");
                ResourceRequestException vbaListDrift = null;
                try
                {
                    gateway.List(
                        session,
                        VbaResourceProvider.ProviderName,
                        VbaResourceProvider.ComponentKind,
                        firstComponentPage.NextCursor,
                        1);
                }
                catch (ResourceRequestException ex)
                {
                    vbaListDrift = ex;
                }
                AssertEqual("resource_revision_changed", vbaListDrift == null ? null : vbaListDrift.ErrorCode,
                    "VBA list continuation fails instead of shifting across project revisions");

                var sourceSearch = SearchVbaSource(executor, session, "ResourceNeedle");
                AssertTrue(sourceSearch.Matches.Any(match =>
                    string.Equals(match.Title, "ResourceModule", StringComparison.OrdinalIgnoreCase)),
                    "VBA resource search finds source text");
                var metadataSearch = SearchVbaSource(executor, session, "ResourceModule");
                AssertEqual(ResourceRepresentations.Metadata, metadataSearch.Matches.Single().Representation,
                    "VBA resource search discovers component metadata without reading its body");

                adapter.SetVbaModule(
                    "ResourceModule",
                    "Option Explicit\nSub ResourceNeedleChanged()\n" + new string('y', 220) + "\nEnd Sub",
                    "StdModule");
                ResourceRequestException vbaDrift = null;
                try
                {
                    ReadResource(
                        gateway,
                        session,
                        component.Reference.Uri,
                        ResourceRepresentations.Source,
                        firstSource.NextCursor,
                        128);
                }
                catch (ResourceRequestException ex)
                {
                    vbaDrift = ex;
                }
                AssertEqual("resource_revision_changed", vbaDrift == null ? null : vbaDrift.ErrorCode,
                    "VBA continuation fails instead of mixing source revisions");

                session.LastRun = new ChatRunRecord
                {
                    RunId = "resource-migration-run",
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey
                };
                var previousDocumentKey = session.DocumentKey;
                adapter.DocumentKeyValue = "saved-document";
                AssertTrue(!string.IsNullOrWhiteSpace(ReadResource(
                    gateway,
                    session,
                    document.Reference.Uri,
                    ResourceRepresentations.Text,
                    null,
                    128).Result.Text),
                    "live document URI survives a save while chat migration is deferred");
                AssertContains(ReadResource(
                    gateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result.Text,
                    "ResourceNeedleChanged",
                    "VBA URI survives a save while chat migration is deferred");

                ChatSessionNormalizer.RecordDocumentKeyMigration(
                    session,
                    previousDocumentKey,
                    adapter.DocumentKeyValue);
                session.DocumentKey = adapter.DocumentKeyValue;
                session.Context.DocumentKey = session.DocumentKey;
                AssertTrue(!string.IsNullOrWhiteSpace(ReadResource(
                    gateway,
                    session,
                    document.Reference.Uri,
                    ResourceRepresentations.Text,
                    null,
                    128).Result.Text),
                    "live document URI survives completed document identity migration");
                AssertContains(ReadResource(
                    gateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result.Text,
                    "ResourceNeedleChanged",
                    "VBA URI survives completed document identity migration");

                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var renamed = executor.Execute(
                    Command(
                        "common.vba_write_module",
                        "moduleName", "ResourceModule",
                        "newModuleName", "ResourceModuleRenamed",
                        "mode", "rename"),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(renamed.Success, "VBA resource recovery setup renames the live component");
                var staleComponent = executor.Execute(
                    Command(
                        ResourceToolCatalog.ReadToolId,
                        "uri", component.Reference.Uri,
                        "representation", ResourceRepresentations.Source),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("resource_not_found", staleComponent.ErrorCode,
                    "renamed VBA component returns a stable missing-resource error");
                AssertEqual(true, staleComponent.Retryable,
                    "stale VBA component URI invites fresh discovery");
                AssertContains(staleComponent.Message, "common.resources_list",
                    "stale VBA component URI explains exact recovery");

                adapter.DocumentKeyValue = "other-document";
                adapter.RuntimeDocumentKeyValue = "runtime-other-document";
                var blocked = executor.Execute(
                    Command(
                        ResourceToolCatalog.ReadToolId,
                        "uri", document.Reference.Uri,
                        "representation", ResourceRepresentations.Text),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("active_document_changed", blocked.ErrorCode,
                    "live resource read refuses a different active document");
            });
        }

        private static void ArtifactPromptUsesBoundedWorkingSet()
        {
            var session = new ChatSession();
            for (var index = 0; index < 20; index++)
            {
                session.Artifacts.Add(new ChatArtifact
                {
                    Id = "artifact_" + index,
                    Kind = ChatArtifactKinds.Markdown,
                    Title = "Artifact " + index,
                    RelativePath = "private/path/" + index,
                    InlineText = "body",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(index)
                });
            }
            session.ActivePlanDocumentArtifactId = "artifact_0";
            var latestMessage = new ChatMessage
            {
                Role = "user",
                Content = "latest"
            };
            latestMessage.ResourceRefs.Add(ArtifactReference(session, session.Artifacts.Single(item => item.Id == "artifact_19")));
            session.Messages.Add(latestMessage);

            var prompt = ChatResourcePromptIndex.Build(session, 5000, new AppSettings());
            AssertContains(prompt, "showing=12/20", "artifact prompt exposes bounded working set");
            AssertContains(prompt, "artifact_0", "active artifact remains visible");
            AssertContains(prompt, "artifact_19", "recently referenced artifact remains visible");
            AssertTrue(prompt.IndexOf("private/path", StringComparison.OrdinalIgnoreCase) < 0,
                "artifact prompt does not expose local paths");
            AssertTrue(prompt.IndexOf("policy=", StringComparison.OrdinalIgnoreCase) < 0,
                "artifact prompt has one reference-first rule instead of per-artifact legacy policies");
        }

        private static void HistoricalAttachmentsStayReferenceOnly()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var session = NewSession(adapter);
            var historicMessage = new ChatMessage
            {
                Role = "user",
                Content = "Old request",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment
                    {
                        Id = "old-text",
                        Kind = "text",
                        FileName = "large.txt",
                        ExtractedText = "HISTORICAL_BODY_MUST_NOT_REPLAY",
                        ExtractedCharCount = 50000
                    }
                },
                AttachmentAnalysis = new AttachmentAnalysisContext
                {
                    Content = "HISTORICAL_ANALYSIS_MUST_NOT_REPLAY",
                    AttachmentIds = new List<string> { "old-text" }
                }
            };
            session.Messages.Add(historicMessage);
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Old answer" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "New request" });
            session.Artifacts.Add(new ChatArtifact
            {
                Id = "attachment_old-text",
                Kind = ChatArtifactKinds.Attachment,
                SourceMessageId = historicMessage.Id,
                MetadataJson = "{\"attachmentId\":\"old-text\"}"
            });
            historicMessage.ResourceRefs.Add(ArtifactReference(session, session.Artifacts.Last()));
            var historicUri = ArtifactUri(session, session.Artifacts.Last());

            var prompt = new ConversationPromptComposer().BuildMessages(
                ChatModes.Agent,
                "New request", adapter, new ToolDefinition[0], new SkillDefinition[0],
                new DocumentContext(), new AppSettings(), session, null);
            var old = prompt.First(message => (message.Content ?? string.Empty).StartsWith("Old request", StringComparison.Ordinal));
            AssertEqual(0, old.Attachments.Count, "historical attachment bodies are removed from replay");
            AssertContains(old.Content, "resource:" + historicUri, "historical canonical resource reference remains");
            AssertTrue(old.Content.IndexOf("artifact:attachment_old-text", StringComparison.Ordinal) < 0,
                "model history does not expose a second artifact-id reference channel");
            AssertTrue(old.Content.IndexOf("attachment:old-text", StringComparison.Ordinal) < 0,
                "model history does not duplicate the canonical resource as attachment metadata");
            AssertTrue(FlattenSimple(prompt).IndexOf("HISTORICAL_BODY_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "historical extracted text is not copied into every prompt");
            AssertTrue(FlattenSimple(prompt).IndexOf("HISTORICAL_ANALYSIS_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "historical query-specific media analysis is not replayed into every prompt");
            var usage = JObject.FromObject(ContextUsageEstimator.FromSession(session, new AppSettings()));
            AssertTrue(usage["usedChars"].Value<int>() < 1000,
                "session usage estimates the virtualized reference rather than the historical body");

            var gateway = new ResourceGatewayService();
            var descriptor = gateway.Resolve(session, historicUri).Resource;
            AssertTrue(!descriptor.Representations.Contains("analysis"),
                "query-specific helper analysis is not exposed as a reusable resource representation");

            string compactionInput = null;
            LlmCompletionDelegate completion = (requestSettings, messages, options, stream, cancellationToken) =>
            {
                compactionInput = FlattenSimple(messages);
                return Task.FromResult(new LlmCompletionResult { Content = "{\"summary\":\"References retained.\"}" });
            };
            new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                session,
                new AppSettings(),
                string.Empty,
                true,
                null,
                CancellationToken.None).GetAwaiter().GetResult();
            AssertContains(compactionInput, "resource:" + historicUri,
                "compaction receives the canonical resource reference");
            AssertTrue(compactionInput.IndexOf("HISTORICAL_BODY_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "compaction does not reopen historical attachment bodies");
            AssertTrue(compactionInput.IndexOf("HISTORICAL_ANALYSIS_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "compaction does not inject saved media analysis");
        }

        private static void AgentHydratesArtifactMediaOnlyAfterRead()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var image = new ChatAttachment
                {
                    Id = "historic-image",
                    Kind = "image",
                    FileName = "chart.png",
                    ContentType = "image/png",
                    Size = 4
                };
                var source = new ChatMessage
                {
                    Id = "historic-source",
                    Role = "user",
                    Content = "Earlier image",
                    Attachments = new List<ChatAttachment> { image }
                };
                var session = NewSession(adapter);
                session.Model = "omni";
                session.Messages.Add(source);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Stored.", ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion });
                session.Artifacts.Add(new ChatArtifact
                {
                    Id = "attachment_historic-image",
                    Kind = ChatArtifactKinds.Image,
                    Title = image.FileName,
                    MimeType = image.ContentType,
                    SourceMessageId = source.Id,
                    MetadataJson = "{\"attachmentId\":\"historic-image\"}"
                });
                source.ResourceRefs.Add(ArtifactReference(session, session.Artifacts.Last()));
                var resourceUri = ArtifactUri(session, session.Artifacts.Last());
                var settings = new AppSettings { Model = "omni" };
                settings.ModelCapabilities["omni"] = new ModelCapabilitySettings
                {
                    SupportsImages = true,
                    SupportsAudio = false,
                    MaxContextTokens = 32768
                };
                var calls = 0;
                string materializedPrompt = null;
                LlmCompletionDelegate completion = (requestSettings, messages, options, stream, cancellationToken) =>
                {
                    calls += 1;
                    var mediaMessages = messages.Where(message =>
                        message != null && message.ProtocolMessage &&
                        (message.Attachments ?? new List<ChatAttachment>()).Any(item => item != null && item.Id == image.Id)).ToList();
                    if (calls == 1)
                    {
                        AssertEqual(0, mediaMessages.Count, "historical media is absent before explicit read");
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "{\"message\":\"Читаю изображение.\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + resourceUri + "\",\"representation\":\"media\"}}]}"
                        });
                    }
                    if (calls == 2)
                    {
                        AssertEqual(1, mediaMessages.Count, "media is hydrated for the next model step only");
                        materializedPrompt = JsonConvert.SerializeObject(messages);
                        AssertContains(FlattenSimple(messages), resourceUri, "hydrated media retains resource URI provenance");
                        AssertTrue(ReferencesArtifact(session, mediaMessages[0], "attachment_historic-image"),
                            "hydrated media retains canonical resource provenance");
                        AssertTrue(messages.Any(message => message != null &&
                            string.Equals(message.ToolName, ResourceToolCatalog.ReadToolId, StringComparison.OrdinalIgnoreCase) &&
                            (message.ResourceRefs ?? new List<ResourceRef>()).Any(reference => reference.Uri == resourceUri)),
                            "resource tool result carries the same durable ResourceRef");
                        return Task.FromResult(new LlmCompletionResult { Content = "invalid envelope" });
                    }
                    AssertEqual(1, mediaMessages.Count, "format repair retains media from the same accepted prompt");
                    AssertEqual(materializedPrompt, JsonConvert.SerializeObject(messages.Where(message =>
                        !(message.Content ?? string.Empty).StartsWith("FORMAT_REPAIR:", StringComparison.Ordinal))),
                        "repair does not change the materialized prompt or resource evidence");
                    AssertTrue(messages.Any(message => message != null && !message.ExcludeFromModelContext &&
                        (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal)),
                        "media stays available until the logical model step accepts or fails");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"Изображение прочитано.\",\"tool_calls\":[]}"
                    });
                };
                var tools = executor.GetControllerTools().ToList();
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Прочитай старое изображение.",
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();

                AssertEqual("Изображение прочитано.", result.AssistantText, "agent completes after lazy media read");
                AssertEqual(3, calls, "artifact media read plus one format repair completes");
                AssertTrue(session.Messages.Where(message => message != null && message.ProtocolMessage)
                    .All(message => (message.Attachments ?? new List<ChatAttachment>()).Count == 0),
                    "hydrated media is released after the following model step");
                AssertTrue(session.Messages.Where(message => message != null &&
                    (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal))
                    .All(message => message.ExcludeFromModelContext), "consumed media stays out of later steps");
            });
        }

        private static void AgentRoutesArtifactMediaThroughHelper()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var image = new ChatAttachment
                {
                    Id = "helper-image",
                    Kind = "image",
                    FileName = "scan.png",
                    ContentType = "image/png",
                    Size = 4
                };
                var source = new ChatMessage
                {
                    Id = "helper-source",
                    Role = "user",
                    Content = "Earlier scan",
                    Attachments = new List<ChatAttachment> { image }
                };
                var session = NewSession(adapter);
                session.Model = "text-only";
                session.Messages.Add(source);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Stored.", ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion });
                session.Artifacts.Add(new ChatArtifact
                {
                    Id = "attachment_helper-image",
                    Kind = ChatArtifactKinds.Image,
                    Title = image.FileName,
                    MimeType = image.ContentType,
                    SourceMessageId = source.Id,
                    MetadataJson = "{\"attachmentId\":\"helper-image\"}"
                });
                source.ResourceRefs.Add(ArtifactReference(session, session.Artifacts.Last()));
                var resourceUri = ArtifactUri(session, session.Artifacts.Last());
                var settings = new AppSettings { Model = "text-only" };
                settings.ModelCapabilities["text-only"] = new ModelCapabilitySettings
                {
                    SupportsImages = false,
                    SupportsAudio = false,
                    MaxContextTokens = 32768
                };
                settings.ModelCapabilities["vision-helper"] = new ModelCapabilitySettings
                {
                    SupportsImages = true,
                    SupportsAudio = false,
                    MaxContextTokens = 16384
                };
                settings.AttachmentModelPriority.Add("vision-helper");
                var primaryCalls = 0;
                var helperCalls = 0;
                LlmCompletionDelegate completion = (requestSettings, messages, options, stream, cancellationToken) =>
                {
                    if (string.Equals(requestSettings.Model, "vision-helper", StringComparison.OrdinalIgnoreCase))
                    {
                        helperCalls += 1;
                        AssertEqual(2, messages.Count(), "historical helper receives isolated instruction and request");
                        AssertEqual(1, messages.Last().Attachments.Count, "historical helper receives selected media");
                        return Task.FromResult(new LlmCompletionResult { Content = "Summary\nThe scan contains a total of 42." });
                    }
                    primaryCalls += 1;
                    if (primaryCalls == 1)
                    {
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "{\"message\":\"Читаю скан.\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + resourceUri + "\",\"representation\":\"media\"}}]}"
                        });
                    }
                    var evidenceMessage = messages.First(message => message != null && message.ProtocolMessage &&
                        (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal));
                    AssertTrue(evidenceMessage.AttachmentAnalysis != null, "helper evidence is attached to the protocol message");
                    AssertContains(evidenceMessage.AttachmentAnalysis.Content, "total of 42", "helper evidence reaches primary context");
                    var rawRead = false;
                    new LlmMessageBuilder(delegate
                    {
                        rawRead = true;
                        return new byte[] { 1, 2, 3, 4 };
                    }).Build(messages, requestSettings);
                    AssertTrue(!rawRead, "text-only primary does not reload helper-routed raw media");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"На скане указано 42.\",\"tool_calls\":[]}"
                    });
                };

                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Какое число на старом скане?",
                    session,
                    NewContext(adapter),
                    settings,
                    executor.GetControllerTools().ToList(),
                    (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();

                AssertEqual("На скане указано 42.", result.AssistantText, "agent completes from helper evidence");
                AssertEqual(2, primaryCalls, "primary model keeps normal two-step loop");
                AssertEqual(1, helperCalls, "only the missing vision modality uses a helper");
            });
        }
    }
}
