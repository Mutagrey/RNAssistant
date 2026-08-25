using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AttachmentImportCommitDelete()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new AttachmentStore(paths);
                var attachment = store.Import("notes.txt", "text/plain", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello attachment")));
                store.SaveDraftMetadata(attachment);
                var drafts = store.LoadDrafts(new[] { attachment.Id });
                AssertEqual(1, drafts.Count, "draft count");
                AssertEqual("hello attachment", drafts[0].ExtractedText, "extracted text");

                var message = new ChatMessage { Role = "user", Content = "analyze", Attachments = drafts };
                store.Commit(message, false);
                AssertTrue(store.ReadBytes(message.Attachments[0]).Length > 0, "committed bytes");
                AssertTrue(File.Exists(Path.Combine(paths.AttachmentDirectory, "staging", attachment.Id + ".meta.json")), "draft retained until durable save");
                store.DeleteDrafts(message);
                AssertTrue(!File.Exists(Path.Combine(paths.AttachmentDirectory, "staging", attachment.Id + ".meta.json")), "draft deleted after durable save");
                AssertTrue(store.ReadBytes(message.Attachments[0]).Length > 0, "committed bytes survive draft cleanup");
                store.DeleteMessage(message);
                AssertTrue(store.ReadBytes(message.Attachments[0]).Length > 0,
                    "logical delete does not remove shared immutable blob");
                AssertTrue(!string.IsNullOrWhiteSpace(message.Attachments[0].ContentSha256),
                    "committed attachment has content hash");
            });
        }

        private static void AttachmentMultimodalApiPayload()
        {
            var bytes = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
            var attachment = new ChatAttachment
            {
                FileName = "photo.jpg",
                ContentType = "image/jpeg",
                Kind = "image",
                Size = bytes.Length
            };
            var payload = new LlmMessageBuilder(delegate { return bytes; }).Build(
                new[] { new ChatMessage { Role = "user", Content = "Что на фото?", Attachments = new List<ChatAttachment> { attachment } } },
                null).Messages;
            var json = JsonConvert.SerializeObject(payload);
            AssertTrue(json.IndexOf("\"type\":\"image_url\"", StringComparison.Ordinal) >= 0, "image content part");
            AssertTrue(json.IndexOf("data:image/jpeg;base64,", StringComparison.Ordinal) >= 0, "image data uri");
            AssertTrue(json.IndexOf("RelativePath", StringComparison.Ordinal) < 0, "metadata not leaked");
        }

        private static void AttachmentImageImportBypassesPdfExtraction()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00 };
                var attachment = new AttachmentStore(paths).Import(
                    "image.png",
                    "image/png",
                    Convert.ToBase64String(png));
                AssertEqual("image", attachment.Kind, "image kind");
                AssertEqual("ready", attachment.Status, "image import status");
                AssertTrue(string.IsNullOrWhiteSpace(attachment.Error), "image import has no pdf extraction error");
            });
        }

        private static void AttachmentRoutingIsRequestScoped()
        {
            var settings = new AppSettings { Model = "global-text" };
            settings.ModelCapabilities["text-only"] = new ModelCapabilitySettings { SupportsImages = false, SupportsAudio = false };
            settings.ModelCapabilities["vision-first"] = new ModelCapabilitySettings { SupportsImages = true, SupportsAudio = false };
            settings.AttachmentModelPriority.Add("vision-first");
            var session = new ChatSession { Model = "text-only" };

            var routed = AttachmentModelRoutingService.Select(
                settings,
                session,
                new[] { new ChatAttachment { Kind = "image", FileName = "clipboard.png" } });
            AssertEqual("vision-first", routed.SelectedModel, "priority vision model");
            AssertEqual("text-only", routed.Settings.Model, "primary request keeps chat model");
            AssertEqual("vision-first", routed.Routes[0].Model, "helper route model");
            AssertEqual(0, routed.PrimaryAttachments.Count, "primary request excludes analyzed image");
            AssertEqual("text-only", session.Model, "session model unchanged");
            AssertEqual("global-text", settings.Model, "stored settings unchanged");
            routed.Settings.ModelCapabilities["text-only"].SupportsImages = true;
            routed.Settings.AttachmentModelPriority.Clear();
            AssertEqual(false, settings.ModelCapabilities["text-only"].SupportsImages.Value, "capability settings cloned deeply");
            AssertEqual(1, settings.AttachmentModelPriority.Count, "model priority cloned deeply");

            var text = AttachmentModelRoutingService.Select(settings, session, null);
            AssertEqual("text-only", text.SelectedModel, "next text request uses chat model");
        }

        private static void AttachmentRoutingCoversPdfAndMixedMedia()
        {
            var settings = new AppSettings { Model = "text-only" };
            settings.ModelCapabilities["text-only"] = new ModelCapabilitySettings { SupportsImages = false, SupportsAudio = false };
            settings.ModelCapabilities["vision"] = new ModelCapabilitySettings { SupportsImages = true, SupportsAudio = false };
            settings.ModelCapabilities["audio"] = new ModelCapabilitySettings { SupportsImages = false, SupportsAudio = true };
            settings.ModelCapabilities["both"] = new ModelCapabilitySettings { SupportsImages = true, SupportsAudio = true };
            settings.AttachmentModelPriority.AddRange(new[] { "vision", "audio", "both" });
            var session = new ChatSession { Model = "text-only" };

            var textPdf = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "pdf", PageCount = 1, PageTextLengths = new List<int> { 100 } }
            });
            AssertEqual("text-only", textPdf.SelectedModel, "text pdf stays on base model");
            AssertEqual(1, textPdf.PrimaryAttachments.Count, "text pdf stays in primary request");

            var scanPdf = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "pdf", PageCount = 1, PageTextLengths = new List<int> { 0 } }
            });
            AssertEqual("vision", scanPdf.SelectedModel, "scanned pdf uses vision priority");

            var audio = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "audio" }
            });
            AssertEqual("audio", audio.SelectedModel, "audio uses audio priority");

            var mixed = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "image" },
                new ChatAttachment { Kind = "audio" }
            });
            AssertEqual("vision + audio", mixed.SelectedModel, "mixed media uses independent helpers");
            AssertEqual(2, mixed.Routes.Count, "mixed media creates two helper routes");
            AssertEqual(0, mixed.PrimaryAttachments.Count, "mixed media is excluded from primary request");

            settings.AttachmentModelPriority.Remove("both");
            var specialized = AttachmentModelRoutingService.Select(settings, session, new[]
            {
                new ChatAttachment { Kind = "image" },
                new ChatAttachment { Kind = "audio" }
            });
            AssertEqual(2, specialized.Routes.Count, "separate helpers do not require a combined model");
        }

        private static async Task AttachmentAnalysisIsolatesMedia()
        {
            var settings = new AppSettings
            {
                Model = "primary",
                MaxTokens = 3000,
                AttachmentHelperMaxTokens = 123,
                AttachmentEvidenceMaxTokens = 64
            };
            settings.ModelCapabilities["primary"] = new ModelCapabilitySettings
            {
                SupportsImages = false,
                SupportsAudio = false,
                MaxContextTokens = 32768
            };
            settings.ModelCapabilities["vision"] = new ModelCapabilitySettings
            {
                SupportsImages = true,
                SupportsAudio = false,
                MaxContextTokens = 16384,
                MaxImagesPerPrompt = 2
            };
            settings.AttachmentModelPriority.Add("vision");
            var attachment = new ChatAttachment
            {
                Id = "image_1",
                Kind = "image",
                FileName = "chart.png",
                ContentType = "image/png",
                Size = 10,
                ContentSha256 = new string('a', 64),
                ContentByteLength = 10
            };
            var session = new ChatSession { Model = "primary" };
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "HISTORY_MUST_NOT_REACH_VISION" });
            var sourceMessage = new ChatMessage
            {
                Role = "user",
                Content = "What trend is visible?",
                Attachments = new List<ChatAttachment> { attachment }
            };
            session.Messages.Add(sourceMessage);
            var routing = AttachmentModelRoutingService.Select(settings, session, sourceMessage.Attachments);
            var calls = 0;
            AppSettings helperSettings = null;
            List<ChatMessage> helperMessages = null;
            var service = new AttachmentAnalysisService((requestSettings, messages, options, stream, cancellationToken) =>
            {
                calls += 1;
                helperSettings = requestSettings;
                helperMessages = messages.ToList();
                return Task.FromResult(new LlmCompletionResult
                {
                    Content = "Summary\nThe chart rises from left to right.\n" + new string('x', 2000)
                });
            });

            var analysis = await service.EnsureAsync(
                sourceMessage.Content,
                session,
                sourceMessage,
                routing,
                null,
                CancellationToken.None);

            AssertEqual("vision", helperSettings.Model, "helper uses vision model");
            AssertEqual(123, helperSettings.MaxTokens, "helper output limit comes from settings");
            AssertEqual(2, helperMessages.Count, "helper receives only instruction and current request");
            AssertTrue(helperMessages.All(message =>
                (message.Content ?? string.Empty).IndexOf("HISTORY_MUST_NOT_REACH_VISION", StringComparison.Ordinal) < 0),
                "helper excludes conversation history");
            AssertEqual(1, helperMessages[1].Attachments.Count, "helper receives current image");
            AssertContains(analysis.Content, "rises from left to right", "analysis stores bounded evidence");
            AssertTrue(
                ModelContextBudget.EstimateTextTokens(analysis.Content, settings) <= 64,
                "primary evidence obeys configured limit");
            AssertContains(
                AttachmentAnalysisService.BuildPrimaryRequest(sourceMessage.Content, analysis),
                "AUXILIARY_ATTACHMENT_EVIDENCE",
                "primary request receives helper evidence");

            var mediaRead = false;
            var primaryPayload = new LlmMessageBuilder(delegate
            {
                mediaRead = true;
                return new byte[] { 1 };
            }).Build(new[] { sourceMessage }, routing.Settings).Messages;
            var primaryJson = JsonConvert.SerializeObject(primaryPayload);
            AssertTrue(!mediaRead, "primary payload does not read analyzed media bytes");
            AssertTrue(primaryJson.IndexOf("image_url", StringComparison.Ordinal) < 0,
                "primary payload excludes raw image");
            AssertContains(primaryJson, "AUXILIARY_ATTACHMENT_EVIDENCE", "primary payload contains evidence");

            await service.EnsureAsync(
                sourceMessage.Content,
                session,
                sourceMessage,
                routing,
                null,
                CancellationToken.None);
            AssertEqual(1, calls, "confirmation continuation reuses persisted analysis");
        }

        private static void AttachmentAnalysisLimitsAreConfigurable()
        {
            var settings = new AppSettings { Model = "primary", MaxTokens = 3000 };
            settings.ModelCapabilities["primary"] = new ModelCapabilitySettings { MaxContextTokens = 32768 };
            settings.ModelCapabilities["vision"] = new ModelCapabilitySettings { MaxOutputTokens = 128 };

            AssertEqual(0, settings.AttachmentHelperMaxTokens, "helper default uses auto mode");
            AssertEqual(0, settings.AttachmentEvidenceMaxTokens, "evidence default uses auto mode");
            AssertEqual(128, AttachmentAnalysisService.ResolveHelperMaxTokens(settings, "vision"),
                "automatic helper limit respects model output capability");
            var expectedAutoEvidence = Math.Max(
                256,
                Math.Min(2048, ModelContextBudget.InputBudgetTokens(settings) / 5));
            AssertEqual(expectedAutoEvidence, AttachmentAnalysisService.ResolveEvidenceMaxTokens(settings),
                "automatic evidence limit keeps the safe context share");

            settings.AttachmentHelperMaxTokens = 80;
            settings.AttachmentEvidenceMaxTokens = 96;
            AssertEqual(80, AttachmentAnalysisService.ResolveHelperMaxTokens(settings, "vision"),
                "custom helper limit is used");
            AssertEqual(96, AttachmentAnalysisService.ResolveEvidenceMaxTokens(settings),
                "custom evidence limit is used");

            settings.AttachmentEvidenceMaxTokens = int.MaxValue;
            AssertEqual(ModelContextBudget.InputBudgetTokens(settings),
                AttachmentAnalysisService.ResolveEvidenceMaxTokens(settings),
                "custom evidence cannot exceed the primary input budget");
        }

        private static async Task MultimodalPrimaryBypassesHelper()
        {
            var settings = new AppSettings { Model = "omni" };
            settings.ModelCapabilities["omni"] = new ModelCapabilitySettings
            {
                SupportsImages = true,
                SupportsAudio = true,
                MaxContextTokens = 32768,
                MaxImagesPerPrompt = 5
            };
            settings.ModelCapabilities["vision-helper"] = new ModelCapabilitySettings
            {
                SupportsImages = true,
                SupportsAudio = false
            };
            settings.AttachmentModelPriority.AddRange(new[] { "vision-helper", "omni" });
            var image = new ChatAttachment
            {
                Id = "direct_image",
                Kind = "image",
                FileName = "direct.png",
                ContentType = "image/png",
                Size = 4
            };
            var audio = new ChatAttachment
            {
                Id = "direct_audio",
                Kind = "audio",
                FileName = "direct.wav",
                ContentType = "audio/wav",
                Size = 4
            };
            var session = new ChatSession { Model = "omni" };
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "NORMAL_HISTORY" });
            var sourceMessage = new ChatMessage
            {
                Role = "user",
                Content = "Analyze both files",
                Attachments = new List<ChatAttachment> { image, audio }
            };
            session.Messages.Add(sourceMessage);
            var routing = AttachmentModelRoutingService.Select(settings, session, sourceMessage.Attachments);

            AssertEqual("omni", routing.Settings.Model, "primary stays on multimodal model");
            AssertEqual(0, routing.Routes.Count, "multimodal primary needs no helper route");
            AssertTrue(!routing.NeedsHelperAnalysis, "helper pass is disabled");
            AssertEqual(2, routing.PrimaryAttachments.Count, "raw current media stays in primary request");

            var helperCalls = 0;
            var service = new AttachmentAnalysisService((requestSettings, messages, options, stream, cancellationToken) =>
            {
                helperCalls += 1;
                return Task.FromResult(new LlmCompletionResult { Content = "unexpected" });
            });
            var analysis = await service.EnsureAsync(
                sourceMessage.Content,
                session,
                sourceMessage,
                routing,
                null,
                CancellationToken.None);
            AssertTrue(analysis == null, "no auxiliary evidence is created");
            AssertEqual(0, helperCalls, "no duplicate model call");

            var prompt = new ChatContextWindowBuilder().BuildPlainMessages(
                sourceMessage.Content,
                session,
                null,
                routing.Settings,
                routing.PrimaryAttachments);
            AssertTrue(prompt.Any(message =>
                (message.Content ?? string.Empty).IndexOf("NORMAL_HISTORY", StringComparison.Ordinal) >= 0),
                "normal conversation history remains in primary context");
            var payload = new LlmMessageBuilder(delegate { return new byte[] { 1, 2, 3, 4 }; })
                .Build(prompt, routing.Settings).Messages;
            var json = JsonConvert.SerializeObject(payload);
            AssertContains(json, "\"type\":\"image_url\"", "multimodal primary receives image");
            AssertContains(json, "\"type\":\"input_audio\"", "multimodal primary receives audio");

            settings.ModelCapabilities["omni"].SupportsAudio = false;
            settings.ModelCapabilities["audio-helper"] = new ModelCapabilitySettings
            {
                SupportsImages = false,
                SupportsAudio = true
            };
            settings.AttachmentModelPriority.Insert(0, "audio-helper");
            var hybrid = AttachmentModelRoutingService.Select(settings, session, sourceMessage.Attachments);
            AssertEqual(1, hybrid.Routes.Count, "only the missing modality uses a helper");
            AssertEqual("audio", hybrid.Routes[0].Modality, "audio is routed to helper");
            AssertEqual(1, hybrid.PrimaryAttachments.Count, "supported image remains in primary request");
            AssertEqual(image.Id, hybrid.PrimaryAttachments[0].Id, "primary keeps the directly supported media");
        }

        private static void AttachmentAudioImportAndApiPayload()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new AttachmentStore(paths);
                var wav = System.Text.Encoding.ASCII.GetBytes("RIFF0000WAVEdata");
                var attachment = store.Import("recording.wav", "audio/wav", Convert.ToBase64String(wav));
                AssertEqual("audio", attachment.Kind, "wav detected by signature");
                AssertEqual("audio/wav", attachment.ContentType, "wav content type normalized");

                var payload = new LlmMessageBuilder(delegate { return wav; }).Build(
                    new[] { new ChatMessage { Role = "user", Content = "Что в записи?", Attachments = new List<ChatAttachment> { attachment } } },
                    null).Messages;
                var json = JsonConvert.SerializeObject(payload);
                AssertContains(json, "\"type\":\"input_audio\"", "audio content part");
                AssertContains(json, Convert.ToBase64String(wav), "audio bytes serialize as base64");
                AssertContains(json, "\"format\":\"wav\"", "audio format");

                var mp3Bytes = new byte[427];
                mp3Bytes[0] = 0x49;
                mp3Bytes[1] = 0x44;
                mp3Bytes[2] = 0x33;
                mp3Bytes[3] = 4;
                mp3Bytes[10] = 0xff;
                mp3Bytes[11] = 0xfb;
                mp3Bytes[12] = 0x90;
                var mp3 = store.Import("recording.mp3", "audio/mpeg", Convert.ToBase64String(mp3Bytes));
                AssertEqual("audio", mp3.Kind, "mp3 detected by signature");
                AssertEqual("audio/mpeg", mp3.ContentType, "mp3 content type normalized");
            });
        }

        private static void AttachmentExtractsPdfText()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var builder = new PdfDocumentBuilder();
                var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                var page = builder.AddPage(300, 300);
                page.AddText("hello pdf attachment", 12, new PdfPoint(20, 250), font);
                var pdf = builder.Build();

                var store = new AttachmentStore(paths);
                var attachment = store.Import("sample.pdf", "application/pdf", Convert.ToBase64String(pdf));
                AssertTrue(
                    (attachment.ExtractedText ?? string.Empty).IndexOf("hello pdf attachment", StringComparison.OrdinalIgnoreCase) >= 0,
                    "pdf extracted text");
                AssertContains(store.ReadExtractedText(attachment), "[PDF page 1]", "pdf page marker");
                AssertEqual(1, attachment.PageCount, "pdf page count");
            });
        }

        private static void AttachmentAcceptsTextFormatsAndEncodings()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new AttachmentStore(paths);
                var source = store.Import("sample.cs", "application/octet-stream",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("class Sample {}")));
                AssertEqual("text", source.Kind, "source kind");
                var unknown = store.Import("sample.customtext", "application/octet-stream",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("plain utf8 content")));
                AssertEqual("text", unknown.Kind, "content-detected text kind");

                var utf16Bytes = System.Text.Encoding.Unicode.GetPreamble()
                    .Concat(System.Text.Encoding.Unicode.GetBytes("ключ: значение"))
                    .ToArray();
                var yaml = store.Import("sample.yaml", "application/octet-stream", Convert.ToBase64String(utf16Bytes));
                AssertContains(store.ReadExtractedText(yaml), "значение", "utf16 text");

                var cp1251 = new byte[] { 0xcf, 0xf0, 0xe8, 0xe2, 0xe5, 0xf2 };
                var log = store.Import("sample.log", "application/octet-stream", Convert.ToBase64String(cp1251));
                AssertEqual("Привет", store.ReadExtractedText(log), "windows-1251 text");
            });
        }

        private static void AttachmentStoresExtractedTextSidecar()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new AttachmentStore(paths);
                var full = new string('x', 5000);
                var attachment = store.Import("large.txt", "text/plain",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(full)));
                AssertEqual(4000, attachment.ExtractedText.Length, "inline preview length");
                AssertEqual(5000, attachment.ExtractedCharCount, "extracted char count");
                AssertEqual(full, store.ReadExtractedText(attachment), "sidecar full text");
                AssertEqual(123, store.ReadExtractedText(attachment, 123).Length, "sidecar bounded read");
            });
        }

        private static void AttachmentBuildsVisualPdfPayload()
        {
            var attachment = new ChatAttachment
            {
                FileName = "scan.pdf",
                ContentType = "application/pdf",
                Kind = "pdf",
                PageCount = 1,
                PageTextLengths = new List<int> { 0 },
                ExtractedText = "[PDF page 1]"
            };
            var providerCalls = 0;
            var providerLimit = 0;
            var builder = new LlmMessageBuilder(
                null,
                delegate { return attachment.ExtractedText; },
                delegate(AppSettings providerSettings, ChatAttachment providerAttachment, int maxImages, System.Threading.CancellationToken cancellationToken)
                {
                    providerCalls += 1;
                    providerLimit = maxImages;
                    return new[]
                    {
                        new ModelImagePart
                        {
                            ContentType = "image/jpeg",
                            Bytes = new byte[] { 0xff, 0xd8, 0xff, 0xd9 },
                            Label = "page 1"
                        }
                    };
                });
            var settings = new AppSettings { Model = "vision" };
            settings.ModelCapabilities["vision"] = new ModelCapabilitySettings
            {
                SupportsImages = true,
                MaxImagesPerPrompt = 1
            };
            var options = new LlmRequestOptions { RunCache = new LlmRunCache() };
            var payload = builder.Build(
                new[] { new ChatMessage { Role = "user", Content = "read", Attachments = new List<ChatAttachment> { attachment } } },
                settings,
                options).Messages;
            builder.Build(
                new[] { new ChatMessage { Role = "user", Content = "read", Attachments = new List<ChatAttachment> { attachment } } },
                settings,
                options);
            AssertContains(JsonConvert.SerializeObject(payload), "\"type\":\"image_url\"", "pdf page image content");
            AssertEqual(1, providerLimit, "pdf provider receives remaining image limit");
            AssertEqual(1, providerCalls, "pdf model image is cached for one run");
        }

        private static void AttachmentRejectsUnsupportedFile()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new AttachmentStore(paths);
                var rejected = false;
                try
                {
                    store.Import("program.exe", "application/octet-stream", Convert.ToBase64String(new byte[] { 1, 2, 3 }));
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                AssertTrue(rejected, "unsupported attachment");

                rejected = false;
                try
                {
                    store.Import("archive.txt", "text/plain", Convert.ToBase64String(new byte[] { 0x50, 0x4b, 0x03, 0x04, 1, 2 }));
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                AssertTrue(rejected, "binary signature rejected");

                rejected = false;
                try
                {
                    store.Import("fake.mp3", "audio/mpeg", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not audio")));
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                AssertTrue(rejected, "spoofed audio rejected");
            });
        }

        private static void AttachmentCleansStaleDrafts()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new AttachmentStore(paths);
                var attachment = store.Import(
                    "old.txt",
                    "text/plain",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("old")));
                store.SaveDraftMetadata(attachment);
                var staging = Path.Combine(paths.AttachmentDirectory, "staging");
                foreach (var path in Directory.GetFiles(staging, attachment.Id + ".*"))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
                }

                new AttachmentStore(paths);
                AssertEqual(0, Directory.GetFiles(staging, attachment.Id + ".*").Length, "stale draft files");
            });
        }
    }
}
