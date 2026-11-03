using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal static class HtmlWorkspaceTargets
    {
        internal static string Workspace(DocumentArtifactStore owner, ChatSession session, ChatArtifact artifact)
        {
            var logical = HtmlWorkspaceIdentity.LogicalId(artifact.Id);
            var origin = artifact.Revision == 1 ? artifact : owner.HtmlWorkspaceOrigin(session, logical);
            return ResourceGatewayService.IntentTarget(new ResourceDescriptor {
                Kind = ChatArtifactKinds.HtmlWorkspace, Title = origin.Title, CreatedUtc = origin.CreatedUtc });
        }
        internal static string Snapshot(ChatArtifact artifact)
        {
            return ResourceGatewayService.IntentTarget(new ResourceDescriptor {
                Kind = ChatArtifactKinds.HtmlWorkspace, Title = artifact.Title, CreatedUtc = artifact.CreatedUtc })
                .Replace("HTML workspace: ", "HTML snapshot: ");
        }
        internal static bool IsCurrent(DocumentArtifactStore owner, ChatSession session, ChatArtifact artifact)
        {
            var logical = HtmlWorkspaceIdentity.LogicalId(artifact.Id);
            if (owner == null || logical == null || artifact.DocumentAuthorityId != session.DocumentAuthorityId) return false;
            var current = owner.CurrentSnapshot(session, HtmlWorkspaceIdentity.Identity(session, logical));
            return current?.Uri == ChatResourceUri.CreateArtifactRevisionUri(session, artifact);
        }
        internal static string Member(string kind, string name, DateTime? created, string parent)
        { return ResourceGatewayService.IntentTarget(new ResourceDescriptor { Kind = kind, Title = name, CreatedUtc = created, ParentTarget = parent }); }
    }
}
