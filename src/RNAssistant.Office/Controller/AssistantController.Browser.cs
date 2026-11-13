using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Persistence;
using System.Threading;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private ConversationMetadata BrowserOwner(BrowserOwnerScope owner)
        {
            if (owner?.Kind != BrowserOwnerKind.Document || string.IsNullOrEmpty(owner.ChatId)) throw new InvalidOperationException("Выберите точный документ и чат Browser.");
            var metadata = LoadDiagnosticsMetadata(owner.ChatId, null);
            if (metadata.DocumentAuthorityId != owner.ScopeId) throw new InvalidOperationException("Контекст документа Browser изменился.");
            return metadata;
        }
        private DocumentHtmlVersionService HtmlVersions()
        { return new DocumentHtmlVersionService(_paths, _chatStore.DocumentArtifacts); }
        internal BrowserResolveResult ResolveBrowserLocator(BrowserResolveRequest request)
        {
            var owner = BrowserOwner(request?.OwnerScope);
            if (request.Locator == "/") return BrowserTargetResolver.ResolveLocalLocator(request.OwnerScope, "/", null);
            return HtmlVersions().Resolve(owner, request);
        }
        internal BrowserVersionState BrowserVersionState(BrowserVersionRequest request)
        { return HtmlVersions().State(BrowserOwner(request?.Target?.OwnerScope), request); }
        internal BrowserVersionPage BrowserVersions(BrowserVersionRequest request)
        { return HtmlVersions().Versions(BrowserOwner(request?.Target?.OwnerScope), request); }
        internal BrowserVersionResult BrowserVersionCommand(BrowserVersionCommand request, CancellationToken token)
        { return HtmlVersions().Command(BrowserOwner(request?.Target?.OwnerScope), request, token); }

        internal BrowserSourcePage ReadBrowserSource(BrowserSourceRequest request, BrowserTargetDescriptor target)
        {
            var metadata = BrowserOwner(request.OwnerScope);
            if (target.OwnerScope.ScopeId != metadata.DocumentAuthorityId || target.ChatId != metadata.Id ||
                target.State != BrowserTargetState.Ready) throw new InvalidOperationException("Контекст исходника Browser изменился.");
            var reference = new ResourceRef(target.ResourceUri);
            var artifact = _chatStore.DocumentArtifacts.Read(metadata, reference, false);
            if (_chatStore.DocumentArtifacts.IsRemoved(metadata, artifact)) throw new InvalidOperationException("Источник удалён из каталога.");
            if (artifact.ContentByteLength > 8 * 1024 * 1024) throw new InvalidOperationException("Исходник превышает лимит просмотра.");
            if (artifact.Kind == ChatArtifactKinds.HtmlWorkspace)
            {
                artifact = _chatStore.DocumentArtifacts.Read(metadata, reference);
                var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
                return BrowserSourceReader.Read(target, request, snapshot.Files.Select(file => file.Path),
                    path => snapshot.Files.Single(file => file.Path == path).Content);
            }
            if (!BrowserTargetResolver.IsHtmlResource(artifact)) throw new InvalidOperationException("Источник не является HTML.");
            var text = new UTF8Encoding(false, true).GetString(_toolExecutor.Payloads.ReadBytes(
                new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, artifact.MimeType).ToBlobReference()));
            return BrowserSourceReader.Read(target, request, new[] { "index.html" }, path => text);
        }

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
