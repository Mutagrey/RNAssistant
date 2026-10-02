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
        public System.Threading.Tasks.Task<ModelContextResponse> GetModelContextAsync(ModelContextQuery query, System.Threading.CancellationToken token)
        {
            if (query == null || string.IsNullOrWhiteSpace(query.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            var session = LoadAddressedSession(query.ChatId);
            var source = new ChatSession { Id = session.Id, Host = session.Host, DocumentKey = session.DocumentKey, Revision = session.Revision };
            return System.Threading.Tasks.Task.Run(() => new ModelContextInspectorService(_eventStore, _toolExecutor.Payloads, _resourceData)
                .Query(source, query, token), token);
        }

        public System.Threading.Tasks.Task<ModelContextPayloadResponse> GetModelContextPayloadAsync(ModelContextPayloadQuery query, System.Threading.CancellationToken token)
        {
            if (query == null || string.IsNullOrWhiteSpace(query.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            var session = LoadAddressedSession(query.ChatId);
            var source = new ChatSession { Id = session.Id, Host = session.Host, DocumentKey = session.DocumentKey };
            return System.Threading.Tasks.Task.Run(() => new ModelContextInspectorService(_eventStore, _toolExecutor.Payloads, _resourceData)
                .Open(source, query, token), token);
        }

        public PromptContextInspectorResponse InspectPromptContext(
            string chatId,
            string text,
            IReadOnlyList<string> resourceDraftIds,
            bool includeRaw, bool fullRaw = false)
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
                tools = _toolCatalog.GetPublishedVisibleTools(publication.Tools).Where(item => item.Enabled).ToList();
                skills = publishedSkills.Skills;
            }

            Func<PromptContextInspectorResponse> capture = () => new PromptContextInspectorService(_adapter, _paths, _toolExecutor.ResourceAuthority, _toolExecutor.Payloads, _eventStore).Inspect(
                session,
                LoadContext(session),
                settings,
                tools,
                skills,
                attachments,
                text,
                includeRaw, publishedSkills, fullRaw);
            return includeRaw ? new PromptContextInspectorDownloadService(_resourceData)
                .Open(session, capture, System.Threading.CancellationToken.None, fullRaw) : capture();
        }
    }
}
