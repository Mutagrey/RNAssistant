using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class AttachmentModelRoutingDecision
    {
        public AppSettings Settings { get; set; }
        public string BaseModel { get; set; }
        public string SelectedModel { get; set; }
        public bool RequiresImages { get; set; }
        public bool RequiresAudio { get; set; }

        public bool IsRouted
        {
            get
            {
                return RequiresImages || RequiresAudio;
            }
        }

        public string ProgressMessage
        {
            get
            {
                var modalities = new List<string>();
                if (RequiresImages) modalities.Add("Vision");
                if (RequiresAudio) modalities.Add("Audio");
                return string.Join(" + ", modalities.ToArray()) + " → " + SelectedModel;
            }
        }
    }

    internal static class AttachmentModelRoutingService
    {
        private const int UsablePdfPageTextLength = 20;

        public static AttachmentModelRoutingDecision Select(
            AppSettings source,
            ChatSession session,
            IReadOnlyList<ChatAttachment> attachments)
        {
            var settings = (source ?? new AppSettings()).Clone();
            var baseModel = session == null || string.IsNullOrWhiteSpace(session.Model)
                ? settings.Model
                : session.Model.Trim();
            settings.Model = baseModel;

            var requiresImages = (attachments ?? new ChatAttachment[0]).Any(RequiresVision);
            var requiresAudio = (attachments ?? new ChatAttachment[0]).Any(attachment =>
                attachment != null && string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase));
            var decision = new AttachmentModelRoutingDecision
            {
                Settings = settings,
                BaseModel = baseModel,
                SelectedModel = baseModel,
                RequiresImages = requiresImages,
                RequiresAudio = requiresAudio
            };
            if (!requiresImages && !requiresAudio)
            {
                return decision;
            }

            var priority = (settings.AttachmentModelPriority ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string selected = null;
            if (priority.Count > 0)
            {
                selected = priority.FirstOrDefault(model => Supports(settings, model, requiresImages, requiresAudio));
            }
            else if (Supports(settings, baseModel, requiresImages, requiresAudio))
            {
                selected = baseModel;
            }
            else
            {
                var candidates = KnownModels(settings)
                    .Where(model => Supports(settings, model, requiresImages, requiresAudio))
                    .ToList();
                if (candidates.Count == 1)
                {
                    selected = candidates[0];
                }
                else if (candidates.Count > 1)
                {
                    throw new InvalidOperationException(
                        "Для вложения подходят несколько моделей. Настройте их приоритет в разделе «Модель».");
                }
            }

            if (string.IsNullOrWhiteSpace(selected))
            {
                var required = requiresImages && requiresAudio
                    ? "Vision и Audio"
                    : (requiresImages ? "Vision" : "Audio");
                throw new InvalidOperationException(
                    "Не настроена модель с поддержкой " + required + ". Проверьте возможности моделей и порядок маршрутизации.");
            }

            decision.SelectedModel = selected;
            settings.Model = selected;
            return decision;
        }

        private static bool RequiresVision(ChatAttachment attachment)
        {
            if (attachment == null)
            {
                return false;
            }
            if (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.Equals(attachment.Kind, "pdf", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return attachment.PageTextLengths == null ||
                attachment.PageTextLengths.Count == 0 ||
                attachment.PageCount > attachment.PageTextLengths.Count ||
                attachment.PageTextLengths.Any(length => length < UsablePdfPageTextLength);
        }

        private static bool Supports(
            AppSettings settings,
            string model,
            bool requiresImages,
            bool requiresAudio)
        {
            return !string.IsNullOrWhiteSpace(model) &&
                (!requiresImages || ModelContextBudget.ImageSupport(settings, model) == true) &&
                (!requiresAudio || ModelContextBudget.AudioSupport(settings, model) == true);
        }

        private static IEnumerable<string> KnownModels(AppSettings settings)
        {
            var result = new List<string>();
            if (settings.ModelCapabilities != null) result.AddRange(settings.ModelCapabilities.Keys);
            if (settings.ModelImageSupportOverrides != null) result.AddRange(settings.ModelImageSupportOverrides.Keys);
            if (settings.ModelAudioSupportOverrides != null) result.AddRange(settings.ModelAudioSupportOverrides.Keys);
            return result
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

    }
}
