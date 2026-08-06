using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    internal sealed class LlmMessageBuilder
    {
        private readonly Func<ChatAttachment, byte[]> _attachmentReader;
        private readonly LlmAttachmentTextReader _attachmentTextReader;
        private readonly LlmModelImageProvider _modelImageProvider;

        public LlmMessageBuilder(
            Func<ChatAttachment, byte[]> attachmentReader = null,
            LlmAttachmentTextReader attachmentTextReader = null,
            LlmModelImageProvider modelImageProvider = null)
        {
            _attachmentReader = attachmentReader;
            _attachmentTextReader = attachmentTextReader;
            _modelImageProvider = modelImageProvider;
        }

        public LlmApiMessageBuildResult Build(
            IEnumerable<ChatMessage> messages,
            AppSettings settings,
            LlmRequestOptions requestOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var build = new LlmApiMessageBuildResult();
            if (messages == null)
            {
                return build;
            }
            var messageList = messages as IList<ChatMessage> ?? messages.ToList();
            var runCache = requestOptions == null ? null : requestOptions.RunCache;
            var remainingAttachmentTokens = Math.Max(
                0,
                ModelContextBudget.InputBudgetTokens(settings) -
                ModelContextBudget.EstimateMessagesTokens(messageList, false) -
                EstimatePdfImageTokens(messageList, settings));
            var remainingImages = ModelContextBudget.MaxImagesPerPrompt(settings);

            foreach (var message in messageList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message == null || message.ExcludeFromModelContext || string.IsNullOrWhiteSpace(message.Role))
                {
                    continue;
                }

                if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    build.Messages.Add(new JObject
                    {
                        ["role"] = message.Role,
                        ["content"] = message.Content ?? string.Empty,
                        ["tool_calls"] = new JArray(message.ToolCalls.Select(call => new JObject
                        {
                            ["id"] = call.Id,
                            ["type"] = string.IsNullOrWhiteSpace(call.Type) ? "function" : call.Type,
                            ["function"] = new JObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                            }
                        }))
                    });
                    build.EstimatedPromptTokens += ModelContextBudget.EstimateMessageTokens(message, false);
                    continue;
                }

                if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(message.ToolCallId))
                    {
                        throw new InvalidOperationException("A role=tool message requires ToolCallId.");
                    }
                    var toolMessage = new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = message.ToolCallId,
                        ["content"] = message.Content ?? string.Empty
                    };
                    if (!string.IsNullOrWhiteSpace(message.ToolName)) toolMessage["name"] = message.ToolName;
                    build.Messages.Add(toolMessage);
                    build.EstimatedPromptTokens += ModelContextBudget.EstimateMessageTokens(message, false);
                    continue;
                }

                var attachments = message.Attachments ?? new List<ChatAttachment>();
                var text = AppendExtractedText(message.Content ?? string.Empty, attachments, ref remainingAttachmentTokens, runCache, cancellationToken);
                var imageParts = new List<ModelImagePart>();
                var audioAttachments = attachments
                    .Where(attachment => attachment != null && string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var attachment in attachments.Where(a => a != null && string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (remainingImages <= 0) break;
                    var selected = ReadModelImages(settings, attachment, remainingImages, runCache, cancellationToken)
                        .Take(remainingImages)
                        .ToList();
                    imageParts.AddRange(selected);
                    remainingImages -= selected.Count;
                }
                foreach (var attachment in attachments.Where(a => a != null && string.Equals(a.Kind, "pdf", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (remainingImages <= 0) break;
                    var selected = ReadModelImages(settings, attachment, remainingImages, runCache, cancellationToken)
                        .Take(remainingImages)
                        .ToList();
                    imageParts.AddRange(selected);
                    remainingImages -= selected.Count;
                }
                if (imageParts.Count == 0 && audioAttachments.Count == 0)
                {
                    var unreadablePdf = attachments.FirstOrDefault(a =>
                        a != null && a.Kind == "pdf" &&
                        (a.PageTextLengths == null || a.PageTextLengths.Count == 0 || a.PageTextLengths.All(length => length < 20)));
                    if (unreadablePdf != null)
                    {
                        throw new InvalidOperationException(
                            unreadablePdf.FileName + ": PDF contains no usable text and the selected model does not support visual PDF pages.");
                    }
                    build.Messages.Add(new { role = message.Role, content = text });
                    build.EstimatedPromptTokens += 4 +
                        ModelContextBudget.EstimateTextTokens(message.Role) +
                        ModelContextBudget.EstimateTextTokens(text);
                    continue;
                }

                var parts = new List<object> { new { type = "text", text = text } };
                foreach (var image in imageParts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (image == null || image.Bytes == null || image.Bytes.Length == 0)
                    {
                        continue;
                    }
                    parts.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = "data:" + image.ContentType + ";base64," + Convert.ToBase64String(image.Bytes) }
                    });
                    build.HasImages = true;
                }
                foreach (var audioAttachment in audioAttachments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = ReadAttachmentBytes(audioAttachment, runCache);
                    if (bytes == null || bytes.Length == 0)
                    {
                        throw new InvalidOperationException("Attachment file is missing: " + (audioAttachment.FileName ?? audioAttachment.Id));
                    }
                    parts.Add(new
                    {
                        type = "input_audio",
                        input_audio = new
                        {
                            data = bytes,
                            format = AudioFormat(audioAttachment)
                        }
                    });
                    build.HasAudio = true;
                    build.EstimatedPromptTokens += ModelContextBudget.EstimateAudioTokens(bytes.LongLength);
                }
                build.Messages.Add(new { role = message.Role, content = parts });
                build.EstimatedPromptTokens += 4 + ModelContextBudget.EstimateTextTokens(message.Role) + ModelContextBudget.EstimateTextTokens(text) +
                    imageParts.Count * ModelContextBudget.EstimatedImageTokens;
            }

            return build;
        }

        private static string AudioFormat(ChatAttachment attachment)
        {
            var contentType = attachment == null ? string.Empty : attachment.ContentType ?? string.Empty;
            var extension = attachment == null ? string.Empty : Path.GetExtension(attachment.FileName ?? string.Empty);
            if (contentType.IndexOf("wav", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return "wav";
            }
            if (contentType.IndexOf("mpeg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return "mp3";
            }
            throw new InvalidOperationException(
                (attachment == null ? "Audio attachment" : attachment.FileName) + ": supported audio formats are MP3 and WAV.");
        }

        private static int EstimatePdfImageTokens(IEnumerable<ChatMessage> messages, AppSettings settings)
        {
            if (!ModelContextBudget.SupportsImages(settings))
            {
                return 0;
            }
            var maxImages = ModelContextBudget.MaxImagesPerPrompt(settings);
            var count = 0;
            foreach (var message in messages ?? new ChatMessage[0])
            {
                if (message == null || message.ExcludeFromModelContext) continue;
                var attachments = message == null ? null : message.Attachments;
                var ordinary = Math.Min(
                    Math.Max(0, maxImages - count),
                    (attachments ?? new List<ChatAttachment>()).Count(attachment =>
                        attachment != null && string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase)));
                count += ordinary;
                var remaining = Math.Max(0, maxImages - count);
                foreach (var pdf in (attachments ?? new List<ChatAttachment>()).Where(attachment =>
                    attachment != null && string.Equals(attachment.Kind, "pdf", StringComparison.OrdinalIgnoreCase)))
                {
                    if (remaining <= 0) break;
                    var pages = Math.Min(remaining, Math.Max(1, pdf.PageCount));
                    count += pages;
                    remaining -= pages;
                }
                if (count >= maxImages) break;
            }
            return count * ModelContextBudget.EstimatedImageTokens;
        }

        private IEnumerable<ModelImagePart> ReadModelImages(
            AppSettings settings,
            ChatAttachment attachment,
            int maxImages,
            LlmRunCache runCache,
            CancellationToken cancellationToken)
        {
            var key = AttachmentKey(attachment) + "|model=" + (settings == null ? string.Empty : settings.Model) + "|images=" + maxImages;
            IReadOnlyList<ModelImagePart> cached;
            if (runCache != null && runCache.TryGetModelImages(key, out cached))
            {
                return cached;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var supplied = _modelImageProvider == null
                ? null
                : _modelImageProvider(settings, attachment, maxImages, cancellationToken);
            if (supplied != null && supplied.Count > 0)
            {
                cached = supplied.Where(part => part != null).Take(maxImages).ToList();
                if (runCache != null) runCache.StoreModelImages(key, cached);
                return cached;
            }
            if (attachment == null || !string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                return new ModelImagePart[0];
            }
            var bytes = ReadAttachmentBytes(attachment, runCache);
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("Attachment file is missing: " + (attachment.FileName ?? attachment.Id));
            }
            cached = new[]
            {
                new ModelImagePart { Bytes = bytes, ContentType = attachment.ContentType, Label = attachment.FileName }
            };
            if (runCache != null) runCache.StoreModelImages(key, cached);
            return cached;
        }

        private string AppendExtractedText(
            string content,
            IEnumerable<ChatAttachment> attachments,
            ref int remainingTokens,
            LlmRunCache runCache,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder(content ?? string.Empty);
            foreach (var attachment in attachments ?? new ChatAttachment[0])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remainingTokens <= 0) break;
                var maxChars = (int)Math.Min(1000000L, Math.Max(64L, (long)remainingTokens * 3L + 16L));
                var cacheKey = AttachmentKey(attachment) + "|text";
                string extracted = null;
                var cached = runCache != null && runCache.TryGetAttachmentText(cacheKey, out extracted);
                if (!cached ||
                    extracted.Length < maxChars && attachment != null && attachment.ExtractedCharCount > extracted.Length)
                {
                    extracted = attachment == null
                        ? string.Empty
                        : (_attachmentTextReader == null ? attachment.ExtractedText : _attachmentTextReader(attachment, maxChars));
                    if (runCache != null) runCache.StoreAttachmentText(cacheKey, extracted);
                }
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    continue;
                }
                var selected = ModelContextBudget.TruncateText(extracted, remainingTokens);
                var selectedTokens = ModelContextBudget.EstimateTextTokens(selected);
                remainingTokens = Math.Max(0, remainingTokens - selectedTokens);
                builder.AppendLine();
                builder.AppendLine();
                builder.AppendLine("[Attachment: " + attachment.FileName + "]");
                builder.Append(selected);
                if (attachment.TextTruncated ||
                    attachment.ExtractedCharCount > extracted.Length ||
                    selected.Length < extracted.Length)
                {
                    builder.AppendLine();
                    builder.Append("[Content truncated]");
                }
                builder.AppendLine();
                builder.Append("[End attachment]");
            }
            return builder.ToString();
        }

        private byte[] ReadAttachmentBytes(ChatAttachment attachment, LlmRunCache runCache)
        {
            var key = AttachmentKey(attachment) + "|bytes";
            byte[] bytes;
            if (runCache != null && runCache.TryGetAttachmentBytes(key, out bytes)) return bytes;
            bytes = _attachmentReader == null ? null : _attachmentReader(attachment);
            if (runCache != null && bytes != null) runCache.StoreAttachmentBytes(key, bytes);
            return bytes;
        }

        private static string AttachmentKey(ChatAttachment attachment)
        {
            if (attachment == null) return "missing";
            return (attachment.Id ?? string.Empty) + "|" +
                (attachment.RelativePath ?? string.Empty) + "|" +
                attachment.Size + "|" + attachment.ExtractedCharCount;
        }

    }

    internal sealed class LlmApiMessageBuildResult
    {
        public List<object> Messages { get; private set; } = new List<object>();
        public bool HasImages { get; set; }
        public bool HasAudio { get; set; }
        public int EstimatedPromptTokens { get; set; }
    }
}
