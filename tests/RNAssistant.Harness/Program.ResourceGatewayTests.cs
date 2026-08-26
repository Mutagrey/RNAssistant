using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
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
                Attachments = new List<ChatAttachment> { attachment },
                ArtifactIds = new List<string> { "attachment_attachment-text" }
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
            var gateway = new ResourceGatewayService();
            var resourceUri = ChatArtifactResourceProvider.CreateRevisionUri(session, artifact);

            var listed = gateway.List(session, null, null, null, 10);
            AssertEqual(1, listed.Items.Count, "resource list count");
            AssertEqual(resourceUri, listed.Items[0].Reference.Uri, "resource list uses canonical URI");
            AssertTrue(listed.Items[0].Representations.Contains("text"),
                "artifact list advertises extracted text");

            var resolved = gateway.Resolve(session, resourceUri);
            AssertEqual(resourceUri, resolved.Resource.Reference.Uri, "resource resolve is exact");

            var first = gateway.Read(session, resourceUri, "text", 0, 128).Result;
            AssertEqual(128, first.ReturnedCharacters, "resource read is bounded");
            AssertTrue(!first.Complete && first.Truncated, "resource read exposes truncation");
            var second = gateway.Read(session, resourceUri, "text",
                int.Parse(first.NextCursor), 128).Result;
            AssertTrue(second.Offset > 0, "resource cursor advances");

            var search = gateway.Search(session, null, "NEEDLE", null, 10, 256);
            AssertEqual(1, search.Matches.Count, "resource text search count");
            AssertEqual(resourceUri, search.Matches[0].Reference.Uri, "resource search URI");
            AssertContains(search.Matches[0].Snippet, "NEEDLE", "resource search snippet");

            var invalidRepresentationRejected = false;
            try
            {
                gateway.Read(session, resourceUri, "legacy", 0, 128);
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
            var media = gateway.Read(
                session,
                ChatArtifactResourceProvider.CreateRevisionUri(session, imageArtifact),
                "media",
                0,
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
                gateway.Read(
                    session,
                    ChatArtifactResourceProvider.CreateRevisionUri(session, session.Artifacts.Last()),
                    "media",
                    0,
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

            var boundedSource = htmlGateway.Read(
                htmlSession,
                oldScriptResource.Reference.Uri,
                ResourceRepresentations.Source,
                0,
                128).Result;
            AssertEqual(128, boundedSource.ReturnedCharacters, "HTML member read obeys the common bound");
            AssertTrue(boundedSource.Truncated && !string.IsNullOrWhiteSpace(boundedSource.NextCursor),
                "HTML member read exposes the common continuation cursor");

            var dataResource = htmlGateway.List(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.DataKind,
                null,
                10).Items.Single();
            AssertContains(
                htmlGateway.Read(htmlSession, dataResource.Reference.Uri, ResourceRepresentations.Text, 0, 128).Result.Text,
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
                htmlGateway.Read(htmlSession, oldScriptResource.Reference.Uri, ResourceRepresentations.Source, 0, 8000).Result.Text,
                "OLD_NEEDLE",
                "historical HTML member URI remains pinned to its original revision");
            AssertContains(
                htmlGateway.Read(htmlSession, currentScriptResource.Reference.Uri, ResourceRepresentations.Source, 0, 8000).Result.Text,
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
            var structure = htmlGateway.Read(
                htmlSession,
                ChatArtifactResourceProvider.CreateRevisionUri(htmlSession, activeHtmlArtifact),
                ResourceRepresentations.Structure,
                0,
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

                var documentText = gateway.Read(
                    session,
                    document.Reference.Uri,
                    ResourceRepresentations.Text,
                    0,
                    128).Result;
                AssertTrue(!string.IsNullOrWhiteSpace(documentText.Text), "live document text is readable on demand");
                AssertTrue(!string.IsNullOrWhiteSpace(documentText.ContentSha256) &&
                    string.Equals(
                        documentText.ContentSha256,
                        documentText.Resource.Reference.Revision,
                        StringComparison.Ordinal),
                    "live document read carries exact revision evidence");

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

                var component = VbaComponent(executor, session, "ResourceModule");
                AssertTrue(component.Reference.Uri.IndexOf("ResourceModule", StringComparison.OrdinalIgnoreCase) < 0,
                    "VBA component URI does not expose the module name");
                var firstSource = gateway.Read(
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    0,
                    128).Result;
                AssertEqual(128, firstSource.ReturnedCharacters, "VBA source read is bounded");
                AssertTrue(firstSource.Truncated && !string.IsNullOrWhiteSpace(firstSource.NextCursor),
                    "VBA source read exposes continuation");
                AssertTrue(!string.IsNullOrWhiteSpace(firstSource.Resource.Reference.Revision),
                    "VBA source read carries exact revision evidence");

                var sourceSearch = SearchVbaSource(executor, session, "ResourceNeedle");
                AssertTrue(sourceSearch.Matches.Any(match =>
                    string.Equals(match.Title, "ResourceModule", StringComparison.OrdinalIgnoreCase)),
                    "VBA resource search finds source text");
                var metadataSearch = SearchVbaSource(executor, session, "ResourceModule");
                AssertEqual(ResourceRepresentations.Metadata, metadataSearch.Matches.Single().Representation,
                    "VBA resource search discovers component metadata without reading its body");

                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                adapter.DocumentKeyValue = "other-document";
                adapter.RuntimeDocumentKeyValue = "runtime-other-document";
                var blocked = executor.Execute(
                    Command(
                        ResourceToolExecutor.ReadToolId,
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
            session.ActivePlanArtifactId = "artifact_0";
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "latest",
                ArtifactIds = new List<string> { "artifact_19" }
            });

            var prompt = ChatArtifactService.BuildPromptIndex(session, 5000, new AppSettings());
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
                ArtifactIds = new List<string> { "attachment_old-text" },
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
            var historicUri = ChatArtifactResourceProvider.CreateRevisionUri(session, session.Artifacts.Last());

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
                    Attachments = new List<ChatAttachment> { image },
                    ArtifactIds = new List<string> { "attachment_historic-image" }
                };
                var session = NewSession(adapter);
                session.Model = "omni";
                session.Messages.Add(source);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Stored." });
                session.Artifacts.Add(new ChatArtifact
                {
                    Id = "attachment_historic-image",
                    Kind = ChatArtifactKinds.Image,
                    Title = image.FileName,
                    MimeType = image.ContentType,
                    SourceMessageId = source.Id,
                    MetadataJson = "{\"attachmentId\":\"historic-image\"}"
                });
                var resourceUri = ChatArtifactResourceProvider.CreateRevisionUri(session, session.Artifacts.Last());
                var settings = new AppSettings { Model = "omni" };
                settings.ModelCapabilities["omni"] = new ModelCapabilitySettings
                {
                    SupportsImages = true,
                    SupportsAudio = false,
                    MaxContextTokens = 32768
                };
                var calls = 0;
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
                            Content = "{\"message\":\"Читаю изображение.\",\"tool_calls\":[{\"id\":\"call_media\",\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + resourceUri + "\",\"representation\":\"media\"}}]}"
                        });
                    }
                    if (calls == 2)
                    {
                        AssertEqual(1, mediaMessages.Count, "media is hydrated for the next model step only");
                        AssertContains(FlattenSimple(messages), resourceUri, "hydrated media retains resource URI provenance");
                        AssertTrue(mediaMessages[0].ArtifactIds.Contains("attachment_historic-image"),
                            "hydrated media retains artifact provenance");
                        return Task.FromResult(new LlmCompletionResult { Content = "invalid envelope" });
                    }
                    AssertEqual(0, mediaMessages.Count, "format repair does not resend one-shot media");
                    AssertTrue(!messages.Any(message => message != null && !message.ExcludeFromModelContext &&
                        (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal)),
                        "consumed media marker is excluded from later model context");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"Изображение прочитано.\",\"tool_calls\":[]}"
                    });
                };
                var tools = executor.GetControllerTools().ToList();
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                    Attachments = new List<ChatAttachment> { image },
                    ArtifactIds = new List<string> { "attachment_helper-image" }
                };
                var session = NewSession(adapter);
                session.Model = "text-only";
                session.Messages.Add(source);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Stored." });
                session.Artifacts.Add(new ChatArtifact
                {
                    Id = "attachment_helper-image",
                    Kind = ChatArtifactKinds.Image,
                    Title = image.FileName,
                    MimeType = image.ContentType,
                    SourceMessageId = source.Id,
                    MetadataJson = "{\"attachmentId\":\"helper-image\"}"
                });
                var resourceUri = ChatArtifactResourceProvider.CreateRevisionUri(session, session.Artifacts.Last());
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
                            Content = "{\"message\":\"Читаю скан.\",\"tool_calls\":[{\"id\":\"call_helper_media\",\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + resourceUri + "\",\"representation\":\"media\"}}]}"
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

                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
