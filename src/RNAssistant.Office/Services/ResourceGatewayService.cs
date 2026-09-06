using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ResourceGatewayService
    {
        private readonly ResourceProviderRegistry _registry;
        private readonly Func<ChatSession, IDisposable> _beginLiveOfficeRead;
        private readonly ResourceAuthorityService _authority;
        private readonly ArtifactViewerService _mediaViews;
        internal ResourceAuthorityService Authority { get { return _authority; } }
        internal ResourceAuthorityScopeId ScopeFor(ChatSession session, ResourceRef reference)
        { return _authority.ScopeFor(session, reference, ProviderFor(reference.Uri) is ILiveOfficeResourceProvider); }

        internal ResourceAuthoritySnapshotSet CaptureAuthorityFor(ChatSession session, IEnumerable<ResourceDescriptor> resources)
        {
            var references = resources.SelectMany(item => new[] { item.Reference }.Concat(
                item.Dependencies.Where(dependency => dependency.Kind != "immutable-snapshot").Select(dependency => dependency.Resource)));
            return _authority.CaptureMany(references.Select(reference => ScopeFor(session, reference)).Distinct().ToArray());
        }

        internal void RequireCurrent(ChatSession session, ResourceDescriptor resource, string view, ResourceAuthoritySnapshotSet frozen)
        {
            var scope = ScopeFor(session, resource.Reference);
            var snapshot = frozen.Get(scope);
            // A metadata-only currentness query, not a published complete-body observation.
            var evidence = new ResourceEvidence("data-plane-read", scope, resource.Reference, view,
                resource.Coverage, false, snapshot.Generation, dependencies: resource.Dependencies,
                immutable: ResourceAuthorityService.IsImmutable(scope, resource));
            var projection = new EvidenceStateReducer().Reduce(evidence, frozen);
            if (projection.State == EvidenceState.Current) return;
            var head = snapshot.GetHead(resource.Reference.Identity);
            var code = projection.State == EvidenceState.Unknown ? "RESOURCE_EFFECT_UNKNOWN" :
                projection.State == EvidenceState.Unavailable ? "RESOURCE_SNAPSHOT_UNAVAILABLE" :
                head?.Knowledge == HeadKnowledge.Known && head.Revision.Revision != resource.Reference.Revision ? "RESOURCE_REVISION_CHANGED" : "RESOURCE_DEPENDENCY_STALE";
            throw new ResourceRequestException("The selected head or its dependencies are not current. Open an exact historical revision explicitly or reconcile the source.", code, false);
        }

        public ResourceGatewayService(
            Func<ChatSession, string, bool> loadArtifactBody = null,
            Func<ChatAttachment, int, string> readAttachmentText = null)
            : this(null, null, null, loadArtifactBody, readAttachmentText, null, null)
        {
        }

        internal ResourceGatewayService(
            IOfficeApplicationAdapter adapter,
            IVbaResourceSource vbaSource,
            VbaJournalStore vbaJournalStore,
            Func<ChatSession, string, bool> loadArtifactBody = null,
            Func<ChatAttachment, int, string> readAttachmentText = null,
            Func<ChatSession, IDisposable> beginLiveOfficeRead = null,
            ResourceAuthorityService authority = null, CatalogPublicationService catalogs = null,
            Func<ChatAttachment, byte[]> readAttachmentBytes = null)
        {
            var providers = new List<IResourceProvider>
            {
                new ChatArtifactResourceProvider(loadArtifactBody, readAttachmentText, authority?.Payloads,
                    authority?.Payloads != null && readAttachmentBytes != null)
            };
            if (authority?.Payloads != null)
            {
                providers.Add(new ResourceStateProvider(authority, authority.Payloads));
                providers.Add(new ContextResourceProvider(authority, authority.Payloads));
            }
            if (catalogs != null) providers.Add(new CatalogResourceProvider(catalogs, authority));
            if (adapter != null)
            {
                providers.Add(new LiveDocumentResourceProvider(adapter, authority?.Payloads));
                var excel = adapter as RNAssistant.Office.Domains.Excel.IExcelBackendProvider;
                if (excel?.ExcelReadBackend != null) providers.Add(new ExcelResourceProvider(adapter, excel.ExcelReadBackend, authority?.Payloads));
                if (vbaSource != null && VbaResourceProvider.SupportsHost(adapter.HostName))
                {
                    providers.Add(new VbaResourceProvider(adapter, vbaSource, vbaJournalStore, authority?.Payloads));
                }
            }
            _registry = new ResourceProviderRegistry(providers);
            _beginLiveOfficeRead = beginLiveOfficeRead;
            _authority = authority;
            if (readAttachmentBytes != null) _mediaViews = new ArtifactViewerService(this, readAttachmentBytes);
        }

        internal ResourceGatewayService(IEnumerable<IResourceProvider> providers)
            : this(providers, null)
        {
        }

        internal ResourceGatewayService(IEnumerable<IResourceProvider> providers,
            ResourceAuthorityService authority)
        {
            _registry = new ResourceProviderRegistry(providers);
            _authority = authority;
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
            if (_authority != null)
                foreach (var descriptor in result.Items)
                    _authority.ApplyHead(descriptor, session, provider is ILiveOfficeResourceProvider);
            return result;
        }

        public ResourceResolveResult Resolve(ChatSession session, string resourceUri)
        {
            var provider = ProviderFor(resourceUri);
            var resolved = WithProvider(provider, session, delegate
            {
                return new ResourceResolveResult
                {
                    Resource = provider.Resolve(session, resourceUri),
                    Complete = true
                };
            });
            if (_authority != null) _authority.ApplyHead(resolved.Resource, session,
                provider is ILiveOfficeResourceProvider);
            return resolved;
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
            var result = WithProvider(provider, session, delegate
            {
                return new ResourceResolveResult
                {
                    Resource = resolver.ResolveMember(session, parentUri, memberPath, memberType),
                    Complete = true
                };
            });
            if (_authority != null) _authority.ApplyHead(result.Resource, session, provider is ILiveOfficeResourceProvider);
            return result;
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
                if (_authority != null) _authority.CaptureMany(new[] { _authority.Scope(session, provider is ILiveOfficeResourceProvider) });
                var found = provider.Search(session, query, kind, limit, maxCharsPerMatch);
                if (_authority != null && provider is ILiveOfficeResourceProvider)
                {
                    foreach (var scan in found.Scans)
                        _authority.PublishRead(session, new ResourceReadSelection { Result = scan }, null, true);
                    foreach (var match in found.Matches)
                    {
                        if (match.Representation == ResourceRepresentations.Metadata)
                        {
                            var descriptor = new ResourceDescriptor { Reference = match.Reference };
                            _authority.ApplyHead(descriptor, session, true);
                            match.Reference = descriptor.Reference;
                            continue;
                        }
                        var scan = found.Scans.SingleOrDefault(item =>
                            item.Resource.Reference.Uri == match.Reference.Uri && item.Representation == match.Representation);
                        if (scan == null)
                            throw new ResourceRequestException("Search matches require a captured source view.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                        match.Reference = scan.Resource.Reference.Copy();
                        var observation = new ResourceReadResult {
                            Resource = new ResourceDescriptor { Reference = match.Reference, Provider = provider.Id, Kind = match.Kind, Title = match.Title },
                            Representation = match.Representation, ContentSha256 = scan.ContentSha256,
                            AuthorityGeneration = scan.AuthorityGeneration,
                            Text = match.Snippet, Offset = match.SnippetOffset, ReturnedCharacters = (match.Snippet ?? string.Empty).Length,
                            Complete = false };
                        match.Evidence = Evidence(session, observation);
                    }
                }
                return found;
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
            var live = provider is ILiveOfficeResourceProvider;
            // Structural owners validate the caller's exact continuation before a
            // floating artifact identity can be resolved to its current revision.
            if (request.Representation == "table" || request.Representation == "records")
            {
                if (_authority?.Payloads == null) throw new ResourceRequestException("Canonical snapshot storage is required for structural views.", "RESOURCE_VIEW_UNAVAILABLE", false);
                var derived = ResourceDerivedViewService.TryRead(this, _authority, session, request);
                if (derived != null) return derived;
                return new ResourceStructuredViewService(this, _authority).Read(session, request, live);
            }
            var identityResolver = provider as IResourceIdentityResolver;
            if (identityResolver != null && !request.Reference.IsExact && request.Reference.Uri == request.Reference.Identity.Uri)
            {
                request = new ResourceReadRequest { Reference = identityResolver.ResolveIdentity(session, request.Reference.Identity),
                    Representation = request.Representation, Cursor = request.Cursor, MaxChars = request.MaxChars,
                    MaxRows = request.MaxRows, RowOffset = request.RowOffset, ViewPath = request.ViewPath, Fields = request.Fields };
            }
            if (IsBinaryView(request.Representation)) return ReadBinaryView(session, request);
            var retained = _authority == null ? null : _authority.ReadRetained(session, request, live);
            if (retained != null) return retained;
            return WithProvider(provider, session, delegate
            {
                var providerRequest = _authority == null ? request : _authority.PrepareRead(session, request, live);
                var result = provider.Read(session, providerRequest);
                return _authority == null ? result : _authority.PublishRead(session, result, request, live);
            });
        }

        internal IReadOnlyList<ResourceEvidence> Evidence(ChatSession session, ResourceReadResult result)
        {
            if (_authority != null) _authority.RetainView(session, result,
                ProviderFor(result.Resource.Reference.Uri) is ILiveOfficeResourceProvider);
            return _authority == null ? new ResourceEvidence[0] :
                _authority.Observe(session, result, ProviderFor(result.Resource.Reference.Uri) is ILiveOfficeResourceProvider);
        }

        private T WithProvider<T>(IResourceProvider provider, ChatSession session, Func<T> action)
        {
            if (provider is ILiveOfficeResourceProvider && _authority == null)
                throw new ResourceRequestException("Live resources require the shared logical revision authority.", "RESOURCE_AUTHORITY_NOT_READY", true);
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
                    "Runtime preparation requires one canonical resource reference.",
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
                    "Unknown resource provider in the runtime-bound reference: " + address.Provider + ".",
                    "invalid_resource_uri",
                    false);
            }
        }
    }
}
