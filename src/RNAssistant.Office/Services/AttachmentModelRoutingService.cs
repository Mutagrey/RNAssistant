using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class AttachmentModelRoute
    {
        public string Model { get; set; }
        public string Modality { get; set; }
        public IReadOnlyList<ChatAttachment> Attachments { get; set; }

        public AttachmentModelRoute()
        {
            Attachments = new ChatAttachment[0];
        }
    }

    internal sealed class AttachmentModelRoutingDecision
    {
        public AppSettings Settings { get; set; }
        public string BaseModel { get; set; }
        public string SelectedModel { get; set; }
        public bool RequiresImages { get; set; }
        public bool RequiresAudio { get; set; }
        public IReadOnlyList<AttachmentModelRoute> Routes { get; set; }
        public IReadOnlyList<ChatAttachment> PrimaryAttachments { get; set; }

        public AttachmentModelRoutingDecision()
        {
            Routes = new AttachmentModelRoute[0];
            PrimaryAttachments = new ChatAttachment[0];
        }

        public bool HasMedia
        {
            get { return RequiresImages || RequiresAudio; }
        }

        public bool NeedsHelperAnalysis
        {
            get { return (Routes ?? new AttachmentModelRoute[0]).Any(route => route != null); }
        }

        public string ProgressMessage
        {
            get
            {
                var routes = (Routes ?? new AttachmentModelRoute[0])
                    .Where(route => route != null)
                    .Select(route =>
                        (string.Equals(route.Modality, "vision", StringComparison.OrdinalIgnoreCase) ? "Vision" : "Audio") +
                        " → " + route.Model)
                    .ToList();
                routes.Add("основная → " + BaseModel);
                return string.Join("; ", routes.ToArray());
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

            var allAttachments = (attachments ?? new ChatAttachment[0])
                .Where(attachment => attachment != null)
                .ToList();
            var visionAttachments = allAttachments.Where(RequiresVision).ToList();
            var audioAttachments = allAttachments.Where(attachment =>
                string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase)).ToList();
            var routes = new List<AttachmentModelRoute>();
            if (visionAttachments.Count > 0)
            {
                var visionModel = SelectModel(settings, baseModel, "Vision", model =>
                    ModelContextBudget.ImageSupport(settings, model) == true);
                if (!string.Equals(visionModel, baseModel, StringComparison.OrdinalIgnoreCase))
                {
                    routes.Add(new AttachmentModelRoute
                    {
                        Model = visionModel,
                        Modality = "vision",
                        Attachments = visionAttachments
                    });
                }
            }
            if (audioAttachments.Count > 0)
            {
                var audioModel = SelectModel(settings, baseModel, "Audio", model =>
                    ModelContextBudget.AudioSupport(settings, model) == true);
                if (!string.Equals(audioModel, baseModel, StringComparison.OrdinalIgnoreCase))
                {
                    routes.Add(new AttachmentModelRoute
                    {
                        Model = audioModel,
                        Modality = "audio",
                        Attachments = audioAttachments
                    });
                }
            }

            var routedIds = new HashSet<string>(
                routes.SelectMany(route => route.Attachments ?? new ChatAttachment[0])
                    .Select(AttachmentIdentity),
                StringComparer.OrdinalIgnoreCase);
            var selectedModels = routes
                .Select(route => route.Model)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new AttachmentModelRoutingDecision
            {
                // The primary turn always keeps the chat model. Media models are helpers only.
                Settings = settings,
                BaseModel = baseModel,
                SelectedModel = selectedModels.Count == 0
                    ? baseModel
                    : string.Join(" + ", selectedModels.ToArray()),
                RequiresImages = visionAttachments.Count > 0,
                RequiresAudio = audioAttachments.Count > 0,
                Routes = routes,
                PrimaryAttachments = allAttachments
                    .Where(attachment => !routedIds.Contains(AttachmentIdentity(attachment)))
                    .ToList()
            };
        }

        internal static bool RequiresVision(ChatAttachment attachment)
        {
            if (attachment == null) return false;
            if (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(attachment.Kind, "pdf", StringComparison.OrdinalIgnoreCase)) return false;
            return attachment.PageTextLengths == null ||
                attachment.PageTextLengths.Count == 0 ||
                attachment.PageCount > attachment.PageTextLengths.Count ||
                attachment.PageTextLengths.Any(length => length < UsablePdfPageTextLength);
        }

        internal static string AttachmentIdentity(ChatAttachment attachment)
        {
            if (attachment == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(attachment.Id)) return "id:" + attachment.Id;
            if (!string.IsNullOrWhiteSpace(attachment.ContentSha256)) return "sha256:" + attachment.ContentSha256;
            return "file:" + (attachment.FileName ?? string.Empty) + "|" + attachment.Size;
        }

        private static string SelectModel(
            AppSettings settings,
            string baseModel,
            string requiredCapability,
            Func<string, bool> supports)
        {
            if (!string.IsNullOrWhiteSpace(baseModel) && supports(baseModel))
            {
                return baseModel;
            }
            var priority = (settings.AttachmentModelPriority ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = priority.FirstOrDefault(supports);
            if (string.IsNullOrWhiteSpace(selected) && priority.Count == 0)
            {
                var candidates = KnownModels(settings).Where(supports).ToList();
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
                throw new InvalidOperationException(
                    "Не настроена модель с поддержкой " + requiredCapability +
                    ". Проверьте возможности моделей и порядок маршрутизации.");
            }
            return selected;
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
