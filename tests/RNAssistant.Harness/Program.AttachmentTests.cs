using System;
using System.Collections.Generic;
using System.IO;
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
            });
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
            });
        }
    }
}
