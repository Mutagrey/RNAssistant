using System;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal static class HtmlWorkspaceActionGuard
    {
        public static void Validate(ChatSession session, HtmlWorkspaceActionPayload request)
        {
            if (session == null || request == null || string.IsNullOrWhiteSpace(request.ChatId) || request.ChatId != session.Id)
                throw new ResourceRequestException("Требуется явно указанный чат HTML workspace.", "RESOURCE_ACCESS_DENIED", false);
            if (!request.ExpectedSessionRevision.HasValue || request.ExpectedSessionRevision.Value != session.Revision ||
                request.ExpectedActiveHtmlArtifactId == null ||
                !string.Equals(request.ExpectedActiveHtmlArtifactId, session.ActiveHtmlArtifactId ?? string.Empty, StringComparison.Ordinal))
                throw new ResourceRequestException("HTML или состояние чата изменилось. Обновите workspace и повторите действие.",
                    "RESOURCE_REVISION_CHANGED", false);
        }
    }
}
