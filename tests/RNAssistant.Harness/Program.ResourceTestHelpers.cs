using System;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ResourceRef ArtifactReference(ChatSession session, ChatArtifact artifact)
        {
            return ChatResourceUri.CreateArtifactRevision(session, artifact);
        }

        private static string ArtifactUri(ChatSession session, ChatArtifact artifact)
        {
            return ChatResourceUri.CreateArtifactRevisionUri(session, artifact);
        }

        private static bool ReferencesArtifact(ChatSession session, ChatMessage message, string artifactId)
        {
            return ChatResourceUri.CurrentArtifactIds(
                session,
                message == null ? null : message.ResourceRefs).Contains(artifactId, StringComparer.OrdinalIgnoreCase);
        }

        private static ResourceRef HtmlCheckpoint(ChatSession session, string artifactId)
        {
            return ChatResourceUri.ResolveArtifactRevision(session, artifactId);
        }

        private static ResourceListPage ListVbaComponents(OfficeToolExecutor executor, ChatSession session)
        {
            return executor.ResourceGateway.List(
                session,
                VbaResourceProvider.ProviderName,
                VbaResourceProvider.ComponentKind,
                null,
                50);
        }

        private static ResourceDescriptor VbaComponent(
            OfficeToolExecutor executor,
            ChatSession session,
            string moduleName)
        {
            return ListVbaComponents(executor, session).Items.Single(item => string.Equals(
                item.Title,
                moduleName,
                StringComparison.OrdinalIgnoreCase));
        }

        private static ResourceReadResult ReadVbaSource(
            OfficeToolExecutor executor,
            ChatSession session,
            string moduleName,
            int offset = 0,
            int maxChars = 32000)
        {
            var component = VbaComponent(executor, session, moduleName);
            return executor.ResourceGateway.Read(
                session,
                component.Reference.Uri,
                ResourceRepresentations.Source,
                offset,
                maxChars).Result;
        }

        private static ResourceSearchResult SearchVbaSource(
            OfficeToolExecutor executor,
            ChatSession session,
            string query,
            int limit = 20)
        {
            return executor.ResourceGateway.Search(
                session,
                VbaResourceProvider.ProviderName,
                query,
                VbaResourceProvider.ComponentKind,
                limit,
                600);
        }
    }
}
