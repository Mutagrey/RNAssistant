using System;
using System.Collections.Generic;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        internal IReadOnlyList<BrowserTargetDescriptor> BrowserTargets(BrowserOwnerScope owner)
        {
            if (owner == null || owner.Kind != BrowserOwnerKind.Document || string.IsNullOrEmpty(owner.ChatId))
                throw new InvalidOperationException("Выберите точный документ и чат Browser.");
            var session = LoadAddressedSession(owner.ChatId);
            if ((session.DocumentAuthorityId ?? session.Id) != owner.ScopeId)
                throw new InvalidOperationException("Контекст документа Browser изменился.");
            var result = new List<BrowserTargetDescriptor>();
            var live = BrowserTargetResolver.Live(session);
            if (live.State != BrowserTargetState.Empty) result.Add(live);
            foreach (var artifact in session.Artifacts)
            {
                if (result.Count >= 200) break;
                if (artifact.Kind != ChatArtifactKinds.HtmlWorkspace && !BrowserTargetResolver.IsHtmlResource(artifact)) continue;
                if (!string.IsNullOrEmpty(artifact.AvailabilityIssue)) continue;
                result.Add(BrowserTargetResolver.Saved(session, artifact, ChatResourceUri.CreateArtifactRevisionUri(session, artifact)));
            }
            return result;
        }
    }
}
