using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

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

            IReadOnlyList<ToolDefinition> tools = new ToolDefinition[0];
            IReadOnlyList<SkillDefinition> skills = new SkillDefinition[0];
            if (ChatModes.Normalize(session.Mode) == ChatModes.Agent)
            {
                tools = _toolCatalog.GetVisibleTools().Where(item => item.Enabled).ToList();
                skills = _skillCatalog.GetVisibleSkills().Where(item => item.Enabled).ToList();
            }

            return new PromptContextInspectorService(_adapter, _paths).Inspect(
                session,
                LoadContext(session),
                settings,
                tools,
                skills,
                attachments,
                text,
                includeRaw);
        }
    }
}
