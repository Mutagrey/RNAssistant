using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    // Office prompt preparation only; all evidence filtering and request assembly
    // are performed by the single Core compiler.
    internal static class ModelContextPreview
    {
        internal static List<ChatMessage> BuildPreview(this ModelContextCompiler compiler, string mode, string userText, IOfficeApplicationAdapter adapter,
            IReadOnlyList<ToolCatalogEntry> tools, IReadOnlyList<SkillDefinition> skills, DocumentContext context,
            AppSettings settings, ChatSession session, IReadOnlyList<ChatAttachment> attachments,
            bool replayCurrentUserInHistory = false, int historyBudgetTokens = 0, JObject capabilityCatalog = null,
            ModelAuthoritySnapshot authority = null, Action<ContextReceipt> recordReceipt = null)
        {
            var required = new ConversationPromptComposer().BuildRequiredMessages(mode, userText, adapter,
                tools, skills, null, settings, session, null, true, 0, capabilityCatalog);
            var history = PromptBudgetComposer.ConversationHistory(session, true, !replayCurrentUserInHistory);
            if (!replayCurrentUserInHistory) history.Add(new ChatMessage { Role = "user", Content = userText,
                Attachments = (attachments ?? new ChatAttachment[0]).ToList() });
            authority = authority ?? new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                CallableToolPack.Create(mode, session?.Host, null, tools).Revision, new SkillCatalogSnapshot(skills), null,
                session?.Revision ?? 0);
            var budget = historyBudgetTokens > 0 ? historyBudgetTokens : ModelContextBudget.InputBudgetTokens(settings);
            var workingSet = ContextWorkingSet.Restore(session, history, authority, settings,
                Math.Max(0, Math.Min(budget / 3, budget - ContextWorkingSet.EstimateCost(required.Concat(history), settings))));
            history.AddRange(workingSet.Messages);
            var snapshot = compiler.Compile(authority, required, history, context?.Notes, tools, settings, budget, retainedBodies: workingSet.IncludedBodies, evictedBodies: workingSet.OmittedBodies);
            recordReceipt?.Invoke(snapshot.Receipt);
            return snapshot.Messages.ToList();
        }

    }
}
