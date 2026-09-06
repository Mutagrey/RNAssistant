using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public PromptContextInspectorResponse InspectPromptContext(
            string chatId,
            string text,
            IReadOnlyList<string> resourceDraftIds,
            bool includeRaw)
        {
            var session = LoadAddressedSession(chatId);
            var settings = ResolveChatSettings(session);
            var attachments = _chatResourceIngestion.LoadDrafts(session, resourceDraftIds);
            var invalidAttachment = attachments.FirstOrDefault(item => item != null && item.Status == "error");
            if (invalidAttachment != null)
            {
                throw new InvalidOperationException(invalidAttachment.FileName + ": " + invalidAttachment.Error);
            }

            IReadOnlyList<ToolCatalogEntry> tools = new ToolCatalogEntry[0];
            IReadOnlyList<SkillDefinition> skills = new SkillDefinition[0];
            var publication = _toolExecutor.CaptureCatalogs();
            settings = PromptSettingsService.ApplyPublishedTemplates(settings, publication.PromptsJson);
            var publishedSkills = _toolExecutor.CaptureSkills(publication);
            if (ChatModes.Normalize(session.Mode) != ChatModes.Chat)
            {
                tools = _toolCatalog.GetVisibleTools(publication.Tools).Where(item => item.Enabled).ToList();
                skills = publishedSkills.Skills;
            }

            Func<PromptContextInspectorResponse> capture = () => new PromptContextInspectorService(_adapter, _paths, _toolExecutor.ResourceAuthority, _toolExecutor.Payloads).Inspect(
                session,
                LoadContext(session),
                settings,
                tools,
                skills,
                attachments,
                text,
                includeRaw, publishedSkills);
            return includeRaw ? new PromptContextInspectorDownloadService(_resourceData)
                .Open(session, capture, System.Threading.CancellationToken.None) : capture();
        }
    }
}
