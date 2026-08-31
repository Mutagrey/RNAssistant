using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed class ResourceGatewayService
    {
        private readonly ResourceProviderRegistry _registry;
        private readonly Func<ChatSession, IDisposable> _beginLiveOfficeRead;

        public ResourceGatewayService(
            Func<ChatSession, string, bool> loadArtifactBody = null,
            Func<ChatAttachment, int, string> readAttachmentText = null)
            : this(null, null, null, loadArtifactBody, readAttachmentText, null)
        {
        }

        internal ResourceGatewayService(
            IOfficeApplicationAdapter adapter,
            IVbaResourceSource vbaSource,
            VbaJournalStore vbaJournalStore,
            Func<ChatSession, string, bool> loadArtifactBody = null,
            Func<ChatAttachment, int, string> readAttachmentText = null,
            Func<ChatSession, IDisposable> beginLiveOfficeRead = null)
        {
            var providers = new List<IResourceProvider>
            {
                new ChatArtifactResourceProvider(loadArtifactBody, readAttachmentText)
            };
            if (adapter != null)
            {
                providers.Add(new LiveDocumentResourceProvider(adapter));
                if (vbaSource != null && VbaResourceProvider.SupportsHost(adapter.HostName))
                {
                    providers.Add(new VbaResourceProvider(adapter, vbaSource, vbaJournalStore));
                }
            }
            _registry = new ResourceProviderRegistry(providers);
            _beginLiveOfficeRead = beginLiveOfficeRead;
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
                ResourceReadCursor.RejectCursor(cursor);
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
            kind = (kind ?? string.Empty).Trim().ToLowerInvariant();
            if (kind.Length == 0) kind = null;
            var result = WithProvider(provider, session, delegate
            {
                return provider.List(session, kind, cursor, limit);
            });
            result.Provider = provider.Id;
            result.Providers = providers.Select(item => item.Id).ToList();
            return result;
        }

        public ResourceResolveResult Resolve(ChatSession session, string resourceUri)
        {
            var provider = ProviderFor(resourceUri);
            return WithProvider(provider, session, delegate
            {
                return new ResourceResolveResult
                {
                    Resource = provider.Resolve(session, resourceUri),
                    Complete = true
                };
            });
        }

        public ResourceResolveResult ResolveMember(ChatSession session, string parentUri,
            string memberPath, string memberType)
        {
            var provider = ProviderFor(parentUri);
            var resolver = provider as IResourceMemberResolver;
            if (resolver == null)
            {
                throw new ResourceRequestException(
                    "This resource provider does not expose path-addressable members. " +
                    "Resolve the exact URI returned by resource discovery.",
                    "resource_member_resolve_unsupported",
                    false);
            }
            return WithProvider(provider, session, delegate
            {
                return new ResourceResolveResult
                {
                    Resource = resolver.ResolveMember(session, parentUri, memberPath, memberType),
                    Complete = true
                };
            });
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
            var result = WithProvider(provider, session, delegate
            {
                return provider.Search(session, query, kind, limit, maxCharsPerMatch);
            });
            result.Provider = provider.Id;
            return result;
        }

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
        {
            if (request == null || request.Reference == null || string.IsNullOrWhiteSpace(request.Reference.Uri))
            {
                throw new ResourceRequestException(
                    "A canonical resource reference is required.",
                    "resource_reference_required",
                    true);
            }
            var provider = ProviderFor(request.Reference.Uri);
            return WithProvider(provider, session, delegate
            {
                return provider.Read(session, request);
            });
        }

        private T WithProvider<T>(IResourceProvider provider, ChatSession session, Func<T> action)
        {
            if (!(provider is ILiveOfficeResourceProvider) || _beginLiveOfficeRead == null)
            {
                return action();
            }
            using (_beginLiveOfficeRead(session))
            {
                return action();
            }
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
                throw new ResourceRequestException(
                    "A canonical resource URI is required. Copy an exact rna:// URI from a tool result or resource descriptor.",
                    "invalid_resource_uri",
                    false);
            }
            try
            {
                return _registry.Get(address.Provider);
            }
            catch (KeyNotFoundException)
            {
                throw new ResourceRequestException(
                    "Unknown resource provider in URI: " + address.Provider +
                    ". Use an exact URI returned by common.resources_list or a mutation result.",
                    "invalid_resource_uri",
                    false);
            }
        }
    }
}
