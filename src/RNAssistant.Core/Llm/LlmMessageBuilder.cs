using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    internal sealed class LlmMessageBuilder
    {
        private readonly Func<ChatAttachment, byte[]> _attachmentReader;
        private readonly Func<ChatAttachment, string> _attachmentTextReader;
        private readonly Func<AppSettings, ChatAttachment, IReadOnlyList<ModelImagePart>> _modelImageProvider;

        public LlmMessageBuilder(
            Func<ChatAttachment, byte[]> attachmentReader = null,
            Func<ChatAttachment, string> attachmentTextReader = null,
            Func<AppSettings, ChatAttachment, IReadOnlyList<ModelImagePart>> modelImageProvider = null)
        {
            _attachmentReader = attachmentReader;
            _attachmentTextReader = attachmentTextReader;
            _modelImageProvider = modelImageProvider;
        }

        public LlmApiMessageBuildResult Build(IEnumerable<ChatMessage> messages, AppSettings settings)
        {
            var build = new LlmApiMessageBuildResult();
            if (messages == null)
            {
                return build;
            }
            var messageList = messages.ToList();
            var remainingAttachmentTokens = Math.Max(
                0,
                ModelContextBudget.InputBudgetTokens(settings) -
                ModelContextBudget.EstimateMessagesTokens(messageList, false) -
                EstimatePdfImageTokens(messageList, settings));

            foreach (var message in messageList)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Role))
                {
                    continue;
                }

                if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    build.Messages.Add(new JObject
                    {
                        ["role"] = message.Role,
                        ["content"] = string.IsNullOrEmpty(message.Content) ? null : message.Content,
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
                var text = AppendExtractedText(message.Content ?? string.Empty, attachments, ref remainingAttachmentTokens);
                var imageParts = new List<ModelImagePart>();
                var audioAttachments = attachments
                    .Where(attachment => attachment != null && string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var imageLimit = ModelContextBudget.MaxImagesPerPrompt(settings);
                foreach (var attachment in attachments.Where(a => a != null && a.Kind == "image"))
                {
                    imageParts.AddRange(ReadModelImages(settings, attachment));
                }
                foreach (var attachment in attachments.Where(a => a != null && a.Kind == "pdf"))
                {
                    if (imageParts.Count >= imageLimit)
                    {
                        break;
                    }
                    imageParts.AddRange(ReadModelImages(settings, attachment).Take(imageLimit - imageParts.Count));
                }
                if (imageParts.Count > imageLimit)
                {
                    imageParts = imageParts.Take(imageLimit).ToList();
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
                    var bytes = _attachmentReader == null ? null : _attachmentReader(audioAttachment);
                    if (bytes == null || bytes.Length == 0)
                    {
                        throw new InvalidOperationException("Attachment file is missing: " + (audioAttachment.FileName ?? audioAttachment.Id));
                    }
                    parts.Add(new
                    {
                        type = "input_audio",
                        input_audio = new
                        {
                            data = Convert.ToBase64String(bytes),
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
                var attachments = message == null ? null : message.Attachments;
                var ordinary = (attachments ?? new List<ChatAttachment>()).Count(attachment => attachment != null && attachment.Kind == "image");
                var remaining = Math.Max(0, maxImages - ordinary);
                foreach (var pdf in (attachments ?? new List<ChatAttachment>()).Where(attachment => attachment != null && attachment.Kind == "pdf"))
                {
                    if (remaining <= 0) break;
                    var pages = Math.Min(remaining, Math.Max(1, pdf.PageCount));
                    count += pages;
                    remaining -= pages;
                }
            }
            return count * ModelContextBudget.EstimatedImageTokens;
        }

        private IEnumerable<ModelImagePart> ReadModelImages(AppSettings settings, ChatAttachment attachment)
        {
            var supplied = _modelImageProvider == null ? null : _modelImageProvider(settings, attachment);
            if (supplied != null && supplied.Count > 0)
            {
                return supplied.Where(part => part != null).ToList();
            }
            if (attachment == null || attachment.Kind != "image")
            {
                return new ModelImagePart[0];
            }
            var bytes = _attachmentReader == null ? null : _attachmentReader(attachment);
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("Attachment file is missing: " + (attachment.FileName ?? attachment.Id));
            }
            return new[]
            {
                new ModelImagePart { Bytes = bytes, ContentType = attachment.ContentType, Label = attachment.FileName }
            };
        }

        private string AppendExtractedText(
            string content,
            IEnumerable<ChatAttachment> attachments,
            ref int remainingTokens)
        {
            var builder = new StringBuilder(content ?? string.Empty);
            foreach (var attachment in attachments ?? new ChatAttachment[0])
            {
                var extracted = attachment == null
                    ? string.Empty
                    : (_attachmentTextReader == null ? attachment.ExtractedText : _attachmentTextReader(attachment));
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    continue;
                }
                var selected = TruncateToEstimatedTokens(extracted, remainingTokens);
                var selectedTokens = ModelContextBudget.EstimateTextTokens(selected);
                remainingTokens = Math.Max(0, remainingTokens - selectedTokens);
                builder.AppendLine();
                builder.AppendLine();
                builder.AppendLine("[Attachment: " + attachment.FileName + "]");
                builder.Append(selected);
                if (attachment.TextTruncated || selected.Length < extracted.Length)
                {
                    builder.AppendLine();
                    builder.Append("[Content truncated]");
                }
                builder.AppendLine();
                builder.Append("[End attachment]");
            }
            return builder.ToString();
        }

        private static string TruncateToEstimatedTokens(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0)
            {
                return string.Empty;
            }
            if (ModelContextBudget.EstimateTextTokens(text) <= maxTokens)
            {
                return text;
            }
            var low = 0;
            var high = text.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (ModelContextBudget.EstimateTextTokens(text.Substring(0, middle)) <= maxTokens)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return text.Substring(0, low);
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
