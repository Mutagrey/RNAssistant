using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed class ResourceGatewayService
    {
        private readonly ResourceProviderRegistry _registry;

        public ResourceGatewayService(
            Func<ChatSession, string, bool> loadArtifactBody = null,
            Func<ChatAttachment, int, string> readAttachmentText = null)
        {
            _registry = new ResourceProviderRegistry(new IResourceProvider[]
            {
                new ChatArtifactResourceProvider(loadArtifactBody, readAttachmentText)
            });
        }

        internal ResourceGatewayService(IEnumerable<IResourceProvider> providers)
        {
            _registry = new ResourceProviderRegistry(providers);
        }

        public ResourceListPage List(ChatSession session, string providerId, string kind, string cursor, int limit)
        {
            var providers = _registry.All();
            if (string.IsNullOrWhiteSpace(providerId) && providers.Count > 1)
            {
                return new ResourceListPage
                {
                    Providers = providers.Select(item => item.Id).ToList(),
                    Items = new List<ResourceDescriptor>(),
                    Total = 0,
                    Cursor = string.Empty,
                    NextCursor = null,
                    Truncated = false
                };
            }
            var provider = SelectProvider(providerId);
            var result = provider.List(session, kind, cursor, limit);
            result.Provider = provider.Id;
            result.Providers = providers.Select(item => item.Id).ToList();
            return result;
        }

        public ResourceResolveResult Resolve(ChatSession session, string resourceUri)
        {
            return new ResourceResolveResult
            {
                Resource = ProviderFor(resourceUri).Resolve(session, resourceUri),
                Complete = true
            };
        }

        public ResourceSearchResult Search(
            ChatSession session,
            string providerId,
            string query,
            string kind,
            int limit,
            int maxCharsPerMatch)
        {
            var provider = SelectProvider(providerId);
            var result = provider.Search(session, query, kind, limit, maxCharsPerMatch);
            result.Provider = provider.Id;
            return result;
        }

        public ResourceReadSelection Read(
            ChatSession session,
            string resourceUri,
            string representation,
            int offset,
            int maxChars)
        {
            return ProviderFor(resourceUri).Read(session, resourceUri, representation, offset, maxChars);
        }

        private IResourceProvider SelectProvider(string providerId)
        {
            if (!string.IsNullOrWhiteSpace(providerId)) return _registry.Get(providerId);
            var providers = _registry.All();
            if (providers.Count == 1) return providers[0];
            throw new InvalidOperationException("provider is required when more than one resource provider is available.");
        }

        private IResourceProvider ProviderFor(string resourceUri)
        {
            ResourceAddress address;
            if (!ResourceUri.TryParse(resourceUri, out address))
            {
                throw new InvalidOperationException("A canonical resource URI is required.");
            }
            return _registry.Get(address.Provider);
        }
    }
}
