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
        private static void ArtifactGatewayReadsSearchesAndPages()
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
                ModelContextPolicy = ArtifactModelContextPolicies.ExtractOnDemand,
                MetadataJson = "{\"attachmentId\":\"attachment-text\"}"
            };
            var session = new ChatSession
            {
                Messages = new List<ChatMessage> { message },
                Artifacts = new List<ChatArtifact> { artifact }
            };
            var gateway = new ArtifactGatewayService();

            var listed = gateway.List(session, null, null, 10);
            AssertEqual(1, listed["items"].Count(), "artifact list count");
            AssertTrue(listed["items"][0]["representations"].Values<string>().Contains("text"),
                "artifact list advertises extracted text");

            var first = gateway.Read(session, artifact.Id, "text", 0, 128).Data;
            AssertEqual(128, first["returnedCharacters"].Value<int>(), "artifact read is bounded");
            AssertTrue(first["truncated"].Value<bool>(), "artifact read exposes truncation");
            var second = gateway.Read(session, artifact.Id, "text",
                int.Parse(first["nextCursor"].Value<string>()), 128).Data;
            AssertTrue(second["offset"].Value<int>() > 0, "artifact cursor advances");

            var search = gateway.Search(session, "NEEDLE", null, 10, 256);
            AssertEqual(1, search["matchCount"].Value<int>(), "artifact text search count");
            AssertEqual(artifact.Id, search["matches"][0]["artifactId"].Value<string>(), "artifact search id");
            AssertContains(search["matches"][0]["snippet"].Value<string>(), "NEEDLE", "artifact search snippet");

            var evidence = gateway.BuildSelectedEvidence(session, new[] { artifact.Id }, 256, new AppSettings());
            AssertContains(evidence, "SELECTED_ARTIFACT_EVIDENCE", "selected artifact evidence marker");
            AssertContains(evidence, artifact.Id, "selected artifact evidence citation");

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
            session.Artifacts.Add(new ChatArtifact
            {
                Id = "attachment_direct-image",
                Kind = ChatArtifactKinds.Image,
                SourceMessageId = imageMessage.Id,
                MetadataJson = "{\"attachmentId\":\"direct-image\"}"
            });
            var directIds = gateway.ResolveDirectMediaArtifactIds(
                session,
                new[] { "attachment_direct-image" },
                new[] { imageAttachment });
            AssertEqual("attachment_direct-image", directIds.Single(),
                "direct multimodal media is excluded from duplicate textual evidence");
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
        }

        private static void HistoricalAttachmentsStayReferenceOnly()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var session = NewSession(adapter);
            session.Messages.Add(new ChatMessage
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
            });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Old answer" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "New request" });

            var prompt = new AgentPromptComposer().BuildMessages(
                "New request", adapter, new ToolDefinition[0], new SkillDefinition[0],
                new DocumentContext(), new AppSettings(), session, null);
            var old = prompt.First(message => (message.Content ?? string.Empty).StartsWith("Old request", StringComparison.Ordinal));
            AssertEqual(0, old.Attachments.Count, "historical attachment bodies are removed from replay");
            AssertContains(old.Content, "artifact:attachment_old-text", "historical artifact reference remains");
            AssertTrue(FlattenSimple(prompt).IndexOf("HISTORICAL_BODY_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "historical extracted text is not copied into every prompt");
            AssertTrue(FlattenSimple(prompt).IndexOf("HISTORICAL_ANALYSIS_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "historical media analysis is read through the artifact gateway instead of every prompt");
            var usage = JObject.FromObject(ContextUsageEstimator.FromSession(session, new AppSettings()));
            AssertTrue(usage["usedChars"].Value<int>() < 1000,
                "session usage estimates the virtualized reference rather than the historical body");

            var gateway = new ArtifactGatewayService();
            session.Artifacts.Add(new ChatArtifact
            {
                Id = "attachment_old-text",
                Kind = ChatArtifactKinds.Attachment,
                SourceMessageId = session.Messages[0].Id,
                MetadataJson = "{\"attachmentId\":\"old-text\"}"
            });
            AssertContains(gateway.Read(session, "attachment_old-text", "analysis", 0, 256).Data["content"].Value<string>(),
                "HISTORICAL_ANALYSIS_MUST_NOT_REPLAY", "historical analysis remains available on demand");
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
                            Content = "{\"message\":\"Читаю изображение.\",\"tool_calls\":[{\"id\":\"call_media\",\"name\":\"common.artifacts_read\",\"arguments\":{\"artifactId\":\"attachment_historic-image\",\"representation\":\"media\"}}]}"
                        });
                    }
                    AssertEqual(1, mediaMessages.Count, "media is hydrated for the next model step only");
                    AssertTrue(mediaMessages[0].ArtifactIds.Contains("attachment_historic-image"),
                        "hydrated media retains artifact provenance");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"Изображение прочитано.\",\"tool_calls\":[]}"
                    });
                };
                var tools = executor.GetControllerTools().ToList();
                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
                    "Прочитай старое изображение.",
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();

                AssertEqual("Изображение прочитано.", result.AssistantText, "agent completes after lazy media read");
                AssertEqual(2, calls, "artifact media read requires one follow-up model step");
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
                            Content = "{\"message\":\"Читаю скан.\",\"tool_calls\":[{\"id\":\"call_helper_media\",\"name\":\"common.artifacts_read\",\"arguments\":{\"artifactId\":\"attachment_helper-image\",\"representation\":\"media\"}}]}"
                        });
                    }
                    var evidenceMessage = messages.First(message => message != null && message.ProtocolMessage &&
                        (message.Content ?? string.Empty).StartsWith("ARTIFACT_MEDIA_INPUT", StringComparison.Ordinal));
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

                var result = new AgentRunService(adapter, executor, completion).ExecuteAsync(
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
