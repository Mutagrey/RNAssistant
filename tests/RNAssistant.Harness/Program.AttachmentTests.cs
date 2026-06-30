using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
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
                store.Commit("session", message);
                AssertTrue(store.ReadBytes(message.Attachments[0]).Length > 0, "committed bytes");
                store.DeleteMessage(message);
                AssertTrue(store.ReadBytes(message.Attachments[0]) == null, "deleted bytes");
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
            var client = new LlmClient(delegate { return "key"; }, delegate { return bytes; });
            var method = typeof(LlmClient).GetMethod("ToApiMessages", BindingFlags.Instance | BindingFlags.NonPublic);
            var payload = method.Invoke(client, new object[]
            {
                new[] { new ChatMessage { Role = "user", Content = "Что на фото?", Attachments = new List<ChatAttachment> { attachment } } }
            });
            var json = JsonConvert.SerializeObject(payload);
            AssertTrue(json.IndexOf("\"type\":\"image_url\"", StringComparison.Ordinal) >= 0, "image content part");
            AssertTrue(json.IndexOf("data:image/jpeg;base64,", StringComparison.Ordinal) >= 0, "image data uri");
            AssertTrue(json.IndexOf("RelativePath", StringComparison.Ordinal) < 0, "metadata not leaked");
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
            var client = new LlmClient(
                delegate { return "key"; },
                null,
                delegate { return attachment.ExtractedText; },
                delegate
                {
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
            var method = typeof(LlmClient).GetMethod("ToApiMessages", BindingFlags.Instance | BindingFlags.NonPublic);
            var payload = method.Invoke(client, new object[]
            {
                new[] { new ChatMessage { Role = "user", Content = "read", Attachments = new List<ChatAttachment> { attachment } } }
            });
            AssertContains(JsonConvert.SerializeObject(payload), "\"type\":\"image_url\"", "pdf page image content");
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
