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
        public AgentArsenalResponse GetAgentArsenal(AgentArsenalPayload query)
        {
            if (query == null || string.IsNullOrWhiteSpace(query.ChatId))
                throw new InvalidOperationException("An explicit chat is required.");
            var session = LoadAddressedSession(query.ChatId);
            var receipt = session.LastContextReceipt;
            var selected = session.LastRun?.SelectedSkills ?? new List<SelectedSkill>();
            var snapshot = receipt?.Arsenal;
            var current = snapshot != null && snapshot.RunId == session.LastRun?.RunId;
            var response = new AgentArsenalResponse { ChatId = session.Id, Revision = session.Revision,
                Prepared = !current, SkillCount = selected.Count,
                ToolCount = current ? snapshot.Tools.Count : 0,
                Historical = current && session.LastRun?.KernelState?.Summary != null &&
                    session.LastRun.KernelState.Summary.Lifecycle != RNAssistant.Core.Agent.RunLifecycle.Running &&
                    session.LastRun.KernelState.Summary.Lifecycle != RNAssistant.Core.Agent.RunLifecycle.AwaitingConfirmation,
                Pending = current && (selected.Count != snapshot.Selected.Count || selected.Any(item =>
                    !snapshot.Selected.Any(previous => previous.Id == item.Id && previous.Revision == item.Revision))) };
            if (!_chatSessions.IsCurrentDocument(session))
                response.Restriction = "Office сейчас не подключён к документу этого чата. Операции Office недоступны.";
            long generation;
            if (current && receipt.ResourceGenerations.TryGetValue(CatalogPublicationService.ScopeId.ToString(), out generation) &&
                generation != _toolExecutor.CaptureCatalogGeneration())
                response.Restriction = "Каталог изменился. Доступность и версии будут проверены перед следующим вызовом.";
            var acceptedPolicy = session.LastRun?.ConversationPolicy;
            if (current && acceptedPolicy != null &&
                (acceptedPolicy.Mode != session.Mode || acceptedPolicy.MutationPolicy != session.MutationPolicy))
                response.Restriction = "Политика изменилась. Показан состав прежнего запроса; для выполнения нужна новая задача.";
            if (!current && (session.FormatVersion != ChatSession.CurrentFormatVersion ||
                session.Mode != ChatModes.Agent && session.Mode != ChatModes.Plan))
            {
                response.Prepared = false;
                response.Historical = true;
                response.Restriction = "Этот чат доступен только как история. Создайте новый чат для работы.";
                if (query.IncludeDetails) response.Details = new RNAssistant.Core.Services.AgentArsenal();
                return response;
            }
            if (!query.IncludeDetails && current) return response;
            var publication = _toolExecutor.CaptureCatalogs();
            var metadata = query.IncludeDetails ? publication.Skills.Metadata.Where(item => item.Host == "Common" || item.Host == session.Host).ToArray() :
                new RNAssistant.Core.Services.ArsenalSkill[0];
            var details = AgentArsenalProjection.Build(session, current ? snapshot : null, metadata,
                current ? null : _toolCatalog.GetPublishedVisibleTools(publication.Tools).Where(item => item.Enabled).ToList());
            response.ToolCount = details.Tools.Count;
            if (query.IncludeDetails) response.Details = details;
            return response;
        }

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
            RNAssistant.Core.Services.ConversationRunPolicy.RequireCurrentSession(session);
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
            tools = _toolCatalog.GetPublishedVisibleTools(publication.Tools).Where(item => item.Enabled).ToList();
            skills = publishedSkills.Skills;

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
