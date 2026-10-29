using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatArtifactResourceProvider : IResourceProvider, IResourceMemberResolver, IResourceIdentityResolver, IResourceRawSource
    {
        public const string ProviderName = "chat";
        internal const string DocumentArtifactKind = "document-artifact";
        private const int MaximumListItems = 50;
        private const int MaximumSearchResults = 20;
        // Cross-resource discovery is a bounded prefix scan. Exact selected
        // resource searches use the separate indexed text path.
        private const int MaximumSearchCharacters = 1000000;
        private const int MaximumSearchCharactersPerArtifact = 128000;

        private readonly Func<ChatSession, string, bool> _loadArtifactBody;
        private readonly Func<ChatAttachment, int, string> _readAttachmentText;
        private readonly ChatHtmlResourceCatalog _htmlResources;
        private readonly Func<ChatAttachment, byte[]> _readAttachmentBytes;
        private readonly RNAssistant.Core.Storage.ChatBlobStore _payloads;
        private readonly RNAssistant.Core.Storage.DocumentArtifactStore _documentArtifacts;

        public ChatArtifactResourceProvider(
            Func<ChatSession, string, bool> loadArtifactBody = null,
            Func<ChatAttachment, int, string> readAttachmentText = null,
            RNAssistant.Core.Storage.ChatBlobStore payloads = null, Func<ChatAttachment, byte[]> readAttachmentBytes = null,
            RNAssistant.Core.Storage.DocumentArtifactStore documentArtifacts = null)
        {
            _loadArtifactBody = loadArtifactBody;
            _readAttachmentText = readAttachmentText;
            _htmlResources = new ChatHtmlResourceCatalog(LoadWorkspaceBody, payloads, documentArtifacts);
            _readAttachmentBytes = readAttachmentBytes;
            _payloads = payloads;
            _documentArtifacts = documentArtifacts;
        }

        public string Id { get { return ProviderName; } }

        public byte[] ReadRawSource(ChatSession session, ResourceRef reference)
        {
            session = ProjectSession(session, reference.Uri);
            var address = ParseAddress(session, reference.Uri);
            var artifact = FindExactArtifact(session, address.ArtifactId);
            EnsureRevision(artifact, address.Revision);
            ResourceReadCursor.ValidatePinned(new ResourceReadRequest { Reference = reference }, address.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (address.IsMember) return _htmlResources.ReadRawMember(session, artifact, address);
            EnsureNotRemoved(session, artifact, reference.Uri);
            if (ArtifactViewerService.IsStoredRawSource(artifact))
            {
                var payload = new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength.Value, artifact.MimeType);
                var bytes = _payloads?.ReadBytes(payload.ToBlobReference());
                if (bytes == null)
                    throw new ResourceRequestException("The exact stored original is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                return bytes;
            }
            var attachment = FindExactAttachment(session, artifact);
            if (_readAttachmentBytes == null || !ArtifactViewerService.IsRawSource(artifact, attachment))
                throw new ResourceRequestException("The exact original attachment is unavailable or exceeds the raw view bound.", "RESOURCE_VIEW_UNAVAILABLE", false);
            var original = _readAttachmentBytes(attachment);
            if (original == null || original.LongLength != attachment.ContentByteLength.Value ||
                !string.Equals(ArtifactViewerService.Sha256(original), attachment.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Artifact raw bytes do not match exact revision evidence.");
            return original;
        }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            var binding = ResourceReadCursor.ListBinding(ProviderName, kind);
            var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
            if (ChatHtmlResourceCatalog.SupportsKind(kind))
            {
                var members = ProjectHtmlDiscovery(session);
                return _htmlResources.List(members.Session, kind, cursor, limit, members.SourceRevision, members.UnavailableResources);
            }
            limit = Math.Max(1, Math.Min(MaximumListItems, limit <= 0 ? 20 : limit));
            var discovery = ProjectDiscovery(session, position.Offset, limit);
            ResourceReadCursor.ValidateContinuation(position, discovery.SourceRevision);
            var descriptors = new List<ResourceDescriptor>();
            foreach (var artifact in OrderedArtifacts(discovery.Session).Where(item => string.IsNullOrWhiteSpace(kind) ||
                kind == DocumentArtifactKind && !string.IsNullOrWhiteSpace(item.DocumentAuthorityId) ||
                string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            {
                try { descriptors.Add(Describe(discovery.Session, artifact, true)); }
                catch (Exception ex) when (IsDiscoveryUnavailable(ex)) { discovery.UnavailableResources++; }
            }
            return new ResourceListPage {
                Items = descriptors, Total = discovery.Total,
                TotalIsExact = position.Offset == 0 && discovery.NextOffset == null && descriptors.Count == discovery.Total,
                Cursor = ResourceReadCursor.CreateRevisionBound(position.Offset, discovery.SourceRevision, binding),
                NextCursor = discovery.NextOffset.HasValue ? ResourceReadCursor.CreateRevisionBound(discovery.NextOffset.Value, discovery.SourceRevision, binding) : null,
                Truncated = discovery.NextOffset.HasValue, UnavailableResources = discovery.UnavailableResources };
        }

        public ResourceDescriptor Resolve(ChatSession session, string resourceUri)
        {
            session = ProjectSession(session, resourceUri);
            var address = ParseAddress(session, resourceUri);
            var artifact = FindExactArtifact(session, address.ArtifactId);
            EnsureRevision(artifact, address.Revision);
            _htmlResources.ValidateRevision(session, artifact);
            if (address.IsMember) return _htmlResources.ResolveMember(session, artifact, address);
            EnsureNotRemoved(session, artifact, resourceUri);
            return Describe(session, artifact, false);
        }

        public ResourceDescriptor ResolveMember(ChatSession session, string parentUri,
            string memberPath, string memberType)
        {
            session = ProjectSession(session, parentUri);
            var address = ParseAddress(session, parentUri);
            if (address.IsMember)
            {
                throw ResourceError(
                    "invalid_resource_uri",
                    "parentUri must identify an artifact revision, not one of its members. " +
                    "Copy the parent ResourceRef from the member descriptor.",
                    false,
                    null);
            }
            var artifact = FindExactArtifact(session, address.ArtifactId);
            EnsureRevision(artifact, address.Revision);
            EnsureNotRemoved(session, artifact, parentUri);
            return _htmlResources.ResolveMemberPath(session, artifact, memberPath, memberType);
        }

        internal ResourceDescriptor ResolveMessageAttachmentTarget(ChatSession session, string target)
        {
            var binding = ChatResourceReferenceService.ResolveMessageAttachment(session, target);
            // Resolve only this original through its publication and removal guards.
            // A local message is an address, never a substitute for retained metadata.
            var projected = ProjectSession(session, binding.Reference.Uri);
            var address = ParseAddress(projected, binding.Reference.Uri);
            var artifact = FindExactArtifact(projected, address.ArtifactId);
            EnsureRevision(artifact, address.Revision);
            EnsureNotRemoved(projected, artifact, binding.Reference.Uri);
            var original = FindExactAttachment(projected, artifact);
            var expected = binding.Attachment;
            if (original == null || original.Id != expected.Id || original.FileName != expected.FileName ||
                original.CreatedUtc != expected.CreatedUtc || original.ContentSha256 != expected.ContentSha256 ||
                original.ContentByteLength != expected.ContentByteLength)
                throw new ResourceRequestException("The selected attachment metadata does not match its committed original.",
                    "resource_attachment_binding_invalid", false);
            return Describe(projected, artifact, false);
        }

        internal ResourceDescriptor ResolveHtmlTarget(ChatSession session, string target, bool allowUniqueBase = false)
        {
            if (target == ChatHtmlResourceCatalog.CurrentWorkspaceTarget ||
                target.Contains(" in " + ChatHtmlResourceCatalog.CurrentWorkspaceTarget))
            {
                var current = ProjectHtmlDiscovery(session);
                var artifact = current.Session.Artifacts.SingleOrDefault(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                if (artifact == null)
                    throw new ResourceRequestException("The current HTML workspace is unavailable; discover or select a current workspace.", "resource_target_not_found", true);
                if (target == ChatHtmlResourceCatalog.CurrentWorkspaceTarget)
                    return Describe(current.Session, artifact, true);
                var members = _htmlResources.DescribeMembers(current.Session, artifact)
                    .Where(item => ResourceGatewayService.IntentTarget(item) == target || allowUniqueBase &&
                        ResourceGatewayService.IntentBaseTarget(item) + " in " + ChatHtmlResourceCatalog.CurrentWorkspaceTarget == target).Take(2).ToArray();
                if (members.Length > 1)
                    throw new ResourceRequestException("The HTML member target is ambiguous; use its exact discovered target.", "resource_target_ambiguous", true);
                var member = members.SingleOrDefault();
                if (member == null)
                    throw new ResourceRequestException("The current HTML member is absent or was recreated; discover its current target.", "resource_target_not_found", true);
                return member;
            }
            const int maximumSnapshots = 1000;
            var snapshots = Artifacts(session).Where(item =>
                string.IsNullOrWhiteSpace(item.DocumentAuthorityId) && item.Kind == ChatArtifactKinds.HtmlWorkspace).ToList();
            var localCount = snapshots.Count;
            if (!string.IsNullOrWhiteSpace(session?.DocumentAuthorityId))
            {
                if (_documentArtifacts == null)
                    throw new ResourceRequestException("The document artifact owner is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                var offset = 0;
                do
                {
                    var page = _documentArtifacts.InspectHtmlSnapshotMetadata(session, offset, MaximumListItems);
                    if (page.UnavailableResources > 0 || localCount + page.Total > maximumSnapshots)
                        throw new ResourceRequestException("HTML snapshot metadata is incomplete; target uniqueness cannot be established.", "resource_scope_incomplete", false);
                    snapshots.AddRange(page.Items);
                    if (!page.NextOffset.HasValue) break;
                    offset = page.NextOffset.Value;
                } while (true);
            }
            if (snapshots.Count > maximumSnapshots)
                throw new ResourceRequestException("HTML history exceeds the semantic lookup bound.", "resource_scope_incomplete", false);
            var matches = new List<ResourceDescriptor>();
            foreach (var artifact in snapshots)
            {
                var descriptor = Describe(session, artifact, true);
                descriptor.CurrentTarget = null;
                var workspaceTarget = ResourceGatewayService.IntentTarget(descriptor);
                if (string.Equals(target, workspaceTarget, StringComparison.Ordinal))
                    matches.Add(descriptor);
                else if (target.EndsWith(" in " + workspaceTarget, StringComparison.Ordinal))
                {
                    var exactSession = ProjectSession(session, descriptor.Reference.Uri);
                    var exactArtifact = FindExactArtifact(exactSession, artifact.Id);
                    EnsureNotRemoved(exactSession, exactArtifact, descriptor.Reference.Uri);
                    matches.AddRange(_htmlResources.DescribeMembers(exactSession, exactArtifact)
                        .Where(member => {
                            member.CurrentTarget = null;
                            member.ParentTarget = workspaceTarget;
                            return string.Equals(ResourceGatewayService.IntentTarget(member), target, StringComparison.Ordinal);
                        }));
                }
            }
            if (matches.Count > 1)
                throw new ResourceRequestException("The HTML target is ambiguous. Choose one exact workspace/member target.", "resource_target_ambiguous", false);
            if (matches.Count == 0)
                throw new ResourceRequestException("HTML target was not found. Read the workspace structure and copy its exact member target.", "resource_target_not_found", true);
            var exact = Resolve(session, matches[0].Reference.Uri);
            exact.CurrentTarget = null;
            if (ChatHtmlResourceCatalog.SupportsKind(exact.Kind))
                exact.ParentTarget = target.Substring(target.LastIndexOf(" in HTML workspace: ", StringComparison.Ordinal) + 4);
            return exact;
        }

        internal void EnsureCurrentHtmlReference(ChatSession session, ResourceRef selected)
        {
            var snapshot = ChatResourceUri.ArtifactSnapshot(selected);
            string artifactId;
            if (!ChatResourceUri.TryParseArtifactRevision(snapshot, out _, out artifactId, out _) ||
                session?.ActiveHtmlArtifactId != artifactId)
                throw new ResourceRequestException("The selected HTML workspace changed during this read. Read the working target again.",
                    "resource_revision_changed", true);
            if (string.IsNullOrWhiteSpace(session.DocumentAuthorityId)) return;
            var logicalId = HtmlWorkspaceIdentity.LogicalId(artifactId);
            var current = logicalId == null ? null : _documentArtifacts?.CurrentSnapshot(session,
                HtmlWorkspaceIdentity.Identity(session, logicalId));
            if (current?.Uri != snapshot.Uri || current.Revision != snapshot.Revision)
                throw new ResourceRequestException("The selected HTML workspace changed during this read. Read the working target again.",
                    "resource_revision_changed", true);
        }

        public ResourceSearchResult Search(
            ChatSession session,
            string query,
            string kind,
            int limit,
            int maxCharsPerMatch)
        {
            query = (query ?? string.Empty).Trim();
            var sourceSession = session;
            var membersOnly = ChatHtmlResourceCatalog.SupportsKind(kind);
            string sourceRevision = membersOnly ? null : ProjectDiscovery(sourceSession, 0, 0).SourceRevision;
            var discovery = ProjectHtmlDiscovery(session);
            var htmlDiscovery = discovery;
            session = discovery.Session;
            if (query.Length == 0) throw new InvalidOperationException("Resource search query is required.");
            limit = Math.Max(1, Math.Min(MaximumSearchResults, limit <= 0 ? 10 : limit));
            maxCharsPerMatch = Math.Max(128, Math.Min(2000, maxCharsPerMatch <= 0 ? 600 : maxCharsPerMatch));
            var matches = new List<ResourceSearchMatch>();
            var scannedCharacters = 0;
            var scanTruncated = false;
            var unavailable = discovery.UnavailableResources;
            if (string.IsNullOrWhiteSpace(kind) || ChatHtmlResourceCatalog.SupportsKind(kind))
            {
                var html = _htmlResources.Search(session, query, kind, limit, maxCharsPerMatch);
                ValidateHtmlSearchDiscovery(sourceSession, htmlDiscovery);
                if (ChatHtmlResourceCatalog.SupportsKind(kind))
                {
                    html.UnavailableResources += discovery.UnavailableResources;
                    return html;
                }
                matches.AddRange(html.Matches);
                unavailable += html.UnavailableResources;
                scannedCharacters += html.ScannedCharacters;
                scanTruncated = html.ScanTruncated;
            }

            var sourceOffset = 0;
            for (var sourcePage = 0; sourcePage < 20; sourcePage++)
            {
                discovery = ProjectDiscovery(sourceSession, sourceOffset, 50);
                if (sourceRevision != null && sourceRevision != discovery.SourceRevision)
                    throw new ResourceRequestException("The document changed during search; rediscover the source.", "resource_revision_changed", true);
                sourceRevision = discovery.SourceRevision;
                session = discovery.Session;
                foreach (var artifact in OrderedArtifacts(session))
                {
                    if (!string.IsNullOrWhiteSpace(kind) &&
                        !(kind == DocumentArtifactKind && !string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId)) &&
                        !string.Equals(artifact.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                    // Claims and retained source transcripts are authority-filtered by
                    // the compiler after an explicit read, never raw search snippets.
                    if (artifact.Kind == DocumentArtifactStore.SharedContextKind)
                    {
                        if (matches.Count >= limit) { scanTruncated = true; break; }
                        if ((artifact.Title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                            matches.Add(SearchMatch(session, artifact, "metadata", artifact.Title, artifact.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase), query.Length, maxCharsPerMatch));
                        continue;
                    }
                    if (matches.Count >= limit)
                    {
                        scanTruncated = true;
                        break;
                    }

                    try
                    {
                        var metadata = string.Join(" ", new[]
                        {
                            artifact.Kind,
                            artifact.Title,
                            artifact.MimeType,
                            ArtifactDescription(artifact)
                        }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
                        var metadataIndex = metadata.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (metadataIndex >= 0)
                        {
                            matches.Add(SearchMatch(session, artifact, "metadata", metadata, metadataIndex, query.Length, maxCharsPerMatch));
                        }
                        else
                        {
                            if (string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            var remaining = MaximumSearchCharacters - scannedCharacters;
                            if (remaining <= 0)
                            {
                                scanTruncated = true;
                                break;
                            }
                            if (!string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId) &&
                                (artifact.Kind == ChatArtifactKinds.Markdown || artifact.Kind == ChatArtifactKinds.PlanDocument ||
                                artifact.Kind == ChatArtifactKinds.File && DocumentArtifactStore.FileEncoding(artifact) != null ||
                                artifact.OriginalAttachment != null && HasTextHint(artifact, artifact.OriginalAttachment)))
                            {
                                var indexed = _documentArtifacts.SearchText(session, ChatResourceUri.CreateArtifactRevision(session, artifact),
                                    query, remaining, maxCharsPerMatch);
                                foreach (var match in indexed.Matches) match.Description = ArtifactDescription(artifact);
                                matches.AddRange(indexed.Matches);
                                scannedCharacters += indexed.ScannedCharacters;
                                scanTruncated |= indexed.ScanTruncated;
                                continue;
                            }
                            var readLimit = Math.Min(MaximumSearchCharactersPerArtifact, remaining);
                            // Probe one additional character: a bounded prefix is not a
                            // complete negative search over the resource.
                            var text = ReadText(session, artifact, readLimit == int.MaxValue ? readLimit : readLimit + 1);
                            if (text == null)
                            {
                                var source = FindExactAttachment(session, artifact);
                                if (HasTextHint(artifact, source) || source == null && !string.IsNullOrWhiteSpace(AttachmentId(artifact)))
                                    scanTruncated = true;
                                continue;
                            }
                            var attachment = FindExactAttachment(session, artifact);
                            if (attachment != null && attachment.TextTruncated) scanTruncated = true;
                            if (text.Length > readLimit)
                            {
                                scanTruncated = true;
                                text = text.Substring(0, readLimit);
                            }
                            scannedCharacters += text.Length;
                            var textIndex = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                            if (textIndex >= 0)
                            {
                                matches.Add(SearchMatch(session, artifact, "text", text, textIndex, query.Length, maxCharsPerMatch));
                            }
                        }
                    }
                    catch (Exception ex) when (IsDiscoveryUnavailable(ex)) { discovery.UnavailableResources++; }
                }

                unavailable += discovery.UnavailableResources;
                if (!discovery.NextOffset.HasValue) break;
                if (sourcePage == 19 || matches.Count >= limit || scannedCharacters >= MaximumSearchCharacters)
                { scanTruncated = true; break; }
                sourceOffset = discovery.NextOffset.Value;
            }

            // Includes writes while reading/indexing the LAST source page. A
            // page-to-page check alone cannot detect that race.
            if (sourceRevision != ProjectDiscovery(sourceSession, 0, 0).SourceRevision)
                throw new ResourceRequestException("The document changed during search; rediscover the source.", "resource_revision_changed", true);
            if (string.IsNullOrWhiteSpace(kind)) ValidateHtmlSearchDiscovery(sourceSession, htmlDiscovery);
            return new ResourceSearchResult
            {
                Query = query,
                Matches = matches,
                ScannedCharacters = scannedCharacters,
                ScanTruncated = scanTruncated,
                UnavailableResources = unavailable
            };
        }

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = ProjectSession(session, request?.Reference?.Uri);
            var resourceUri = request == null || request.Reference == null
                ? string.Empty
                : request.Reference.Uri;
            var address = ParseAddress(session, resourceUri);
            var artifact = FindExactArtifact(session, address.ArtifactId);
            EnsureRevision(artifact, address.Revision);
            _htmlResources.ValidateRevision(session, artifact);
            if (request.PageStart.HasValue)
                return ReadPdfPageRangeText(session, request, artifact, address.IsMember, resourceUri, cancellationToken);
            if (request.PageIndex.HasValue)
                return ReadPdfPageText(session, request, artifact, address.IsMember, resourceUri, cancellationToken);
            if (request.Section != null)
            {
                if (address.IsMember || _documentArtifacts == null || string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId) ||
                    request.Representation != null && request.Representation != "auto" && request.Representation != "text" ||
                    !string.IsNullOrEmpty(request.Cursor) || request.ViewPath != null || request.Fields != null || request.RowOffset != 0 || request.MaxRows != 0)
                    throw new ResourceRequestException("Section reads require document Markdown text without other selectors.", "resource_section_unsupported", false);
                EnsureNotRemoved(session, artifact, resourceUri);
                var exact = ChatResourceUri.CreateArtifactRevision(session, artifact);
                ResourceReadCursor.ValidatePinned(request, exact.Revision);
                var generation = _documentArtifacts.InspectCurrentMetadata(session, 0, 0).Generation;
                ResourceReadResult section;
                try { section = _documentArtifacts.ReadMarkdownSection(session, exact, request.Section,
                    request.MaxChars <= 0 ? ResourceReadRequest.MaximumCharacters : Math.Min(ResourceReadRequest.MaximumCharacters, request.MaxChars)); }
                catch (MarkdownSectionException error) { throw new ResourceRequestException(error.Message, error.Code, false); }
                if (_documentArtifacts.InspectCurrentMetadata(session, 0, 0).Generation != generation)
                    throw new ResourceRequestException("The document changed during section reading; rediscover its current resource.", "resource_revision_changed", true);
                section.Resource = Describe(session, artifact, false);
                return new ResourceReadSelection { Result = section, ResourceRefs = new[] { exact } };
            }
            if (address.IsMember) return _htmlResources.ReadMember(session, artifact, request, address);
            EnsureNotRemoved(session, artifact, resourceUri);
            var exactReference = ChatResourceUri.CreateArtifactRevision(session, artifact);
            var exactUri = exactReference.Uri;
            ResourceReadCursor.ValidatePinned(request, exactReference.Revision);
            var representation = request == null ? null : request.Representation;
            var maxChars = request == null ? 0 : request.MaxChars;
            representation = NormalizeRepresentation(representation, session, artifact);
            maxChars = Math.Max(
                1,
                Math.Min(
                    ResourceReadRequest.MaximumCharacters,
                    maxChars <= 0 ? ResourceReadRequest.DefaultCharacters : maxChars));

            if (request.PartNumber.HasValue)
            {
                if (request.PartNumber.Value < 1 || !string.IsNullOrEmpty(request.Cursor) ||
                    request.Section != null || representation != "text" && representation != "source")
                    throw new ResourceRequestException("Invalid text part selection.", "resource_part_invalid", false);
                if (_documentArtifacts != null && !string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId) &&
                    representation == "text" && (artifact.OriginalAttachment != null ||
                    artifact.Kind == ChatArtifactKinds.Markdown || artifact.Kind == ChatArtifactKinds.PlanDocument ||
                    artifact.Kind == ChatArtifactKinds.File && DocumentArtifactStore.FileEncoding(artifact) != null))
                {
                    ResourceReadResult indexed;
                    try { indexed = _documentArtifacts.ReadIndexedTextPart(session, exactReference, request.PartNumber.Value); }
                    catch (System.IO.InvalidDataException error)
                    { throw new ResourceRequestException(error.Message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
                    indexed.Resource = Describe(session, artifact, false);
                    return new ResourceReadSelection { Result = indexed, ResourceRefs = new[] { exactReference } };
                }
                var body = ReadText(session, artifact, int.MaxValue);
                if (body == null)
                    throw new ResourceRequestException("The exact text representation is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                var starts = new List<int> { 0 };
                while (starts[starts.Count - 1] < body.Length)
                {
                    var end = Math.Min(body.Length, starts[starts.Count - 1] + ResourceReadRequest.MaximumCharacters);
                    if (end < body.Length && char.IsHighSurrogate(body[end - 1]) && char.IsLowSurrogate(body[end])) end--;
                    starts.Add(end);
                }
                var partCount = starts.Count - 1;
                if (partCount == 0) partCount = 1;
                if (request.PartNumber.Value > partCount)
                    throw new ResourceRequestException("partNumber is past the end of this representation.", "resource_part_invalid", false);
                var start = starts[request.PartNumber.Value - 1];
                var endPart = request.PartNumber.Value < starts.Count ? starts[request.PartNumber.Value] : 0;
                var partOriginal = FindExactAttachment(session, artifact);
                var partSourceComplete = partOriginal == null || !(partOriginal.TextTruncated ||
                    partOriginal.Kind == "pdf" && partOriginal.PageCount > (partOriginal.PageTextLengths?.Count ?? 0));
                return new ResourceReadSelection
                {
                    Result = new ResourceReadResult
                    {
                        Resource = Describe(session, artifact, false), Representation = representation,
                        ContentSha256 = TextRepresentationSha256(artifact, partOriginal), Text = body.Substring(start, endPart - start),
                        Offset = start, ReturnedCharacters = endPart - start, TotalCharacters = body.Length,
                        PartNumber = request.PartNumber, PartCount = partCount, Complete = false,
                        SourceComplete = partSourceComplete, Truncated = true, RawContentIncluded = true,
                        Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: start, end: endPart)
                    },
                    ResourceRefs = new[] { exactReference }
                };
            }

            if (representation == "metadata")
            {
                ResourceReadCursor.RejectCursor(request);
                return new ResourceReadSelection
                {
                    Result = new ResourceReadResult
                    {
                        Resource = Describe(session, artifact, false),
                        Representation = "metadata",
                        Complete = true,
                        Truncated = false,
                        RawContentIncluded = false
                    },
                    ResourceRefs = new[] { exactReference }
                };
            }
            if (representation == "media")
            {
                ResourceReadCursor.RejectCursor(request);
                var attachment = FindExactAttachment(session, artifact);
                if (!IsModelMedia(attachment))
                {
                    throw new InvalidOperationException("Resource has no image, audio, or visual PDF representation: " + exactUri);
                }
                return new ResourceReadSelection
                {
                    Result = new ResourceReadResult
                    {
                        Resource = Describe(session, artifact, false),
                        Representation = "media",
                        HydratedForNextModelStep = true,
                        Complete = true,
                        Truncated = false,
                        RawContentIncluded = false
                    },
                    ModelAttachments = new[] { attachment },
                    ResourceRefs = new[] { exactReference }
                };
            }
            var cursorBinding = ResourceReadCursor.ReadBinding(exactUri, representation);
            var offset = ResourceReadCursor.ParseImmutable(request, cursorBinding);
            if (representation == ResourceRepresentations.Structure)
            {
                var structure = _htmlResources.ReadStructure(session, artifact, offset, maxChars, cursorBinding);
                structure.Result.Resource = Describe(session, artifact, false);
                return structure;
            }

            if (representation == ResourceRepresentations.Text && _documentArtifacts != null &&
                artifact.Kind == ChatArtifactKinds.File && DocumentArtifactStore.FileEncoding(artifact) != null)
            {
                var indexed = _documentArtifacts.ReadIndexedTextWindow(session, exactReference, offset, maxChars);
                indexed.Resource = Describe(session, artifact, false);
                var next = indexed.Offset + indexed.ReturnedCharacters;
                indexed.NextCursor = indexed.Complete ? null : ResourceReadCursor.CreateImmutable(next, cursorBinding);
                return new ResourceReadSelection { Result = indexed, ResourceRefs = new[] { exactReference } };
            }

            var content = ReadText(session, artifact, int.MaxValue);
            if (content == null)
            {
                throw new InvalidOperationException(
                    "Resource representation is unavailable: " + exactUri + " (" + representation + ").");
            }
            if (offset > content.Length)
            {
                throw new InvalidOperationException("Resource read offset exceeds the representation length.");
            }
            var length = Math.Min(maxChars, content.Length - offset);
            var selected = content.Substring(offset, length);
            var nextOffset = offset + length;
            var original = FindExactAttachment(session, artifact);
            var sourceComplete = original == null || !(original.TextTruncated ||
                original.Kind == "pdf" && original.PageCount > (original.PageTextLengths?.Count ?? 0));
            return new ResourceReadSelection
            {
                Result = new ResourceReadResult
                {
                    Resource = Describe(session, artifact, false),
                    Representation = representation,
                    ContentSha256 = TextRepresentationSha256(artifact, FindExactAttachment(session, artifact)),
                    Offset = offset,
                    ReturnedCharacters = length,
                    TotalCharacters = content.Length,
                    Text = selected,
                    Complete = nextOffset >= content.Length,
                    SourceComplete = sourceComplete,
                    Truncated = nextOffset < content.Length,
                    RawContentIncluded = true,
                    NextCursor = nextOffset < content.Length
                        ? ResourceReadCursor.CreateImmutable(nextOffset, cursorBinding)
                        : null
                },
                ResourceRefs = new[] { exactReference }
            };
        }

        private ResourceReadSelection ReadPdfPageText(ChatSession session, ResourceReadRequest request,
            ChatArtifact artifact, bool isMember, string resourceUri,
            System.Threading.CancellationToken cancellationToken)
        {
            if (isMember || artifact.Kind != ChatArtifactKinds.Attachment)
                throw new ResourceRequestException("Page text is available only for an exact PDF attachment.",
                    "resource_page_unsupported", false);
            EnsureNotRemoved(session, artifact, resourceUri);
            var original = FindExactAttachment(session, artifact);
            if (original?.Kind != "pdf" ||
                ArtifactViewerService.NormalizeMimeType(original.ContentType) != "application/pdf" ||
                ArtifactViewerService.NormalizeMimeType(artifact.MimeType) != "application/pdf")
                throw new ResourceRequestException("Page text is available only for a PDF original.",
                    "resource_page_unsupported", false);
            var exact = ChatResourceUri.CreateArtifactRevision(session, artifact);
            ResourceReadCursor.ValidatePinned(request, exact.Revision);
            cancellationToken.ThrowIfCancellationRequested();
            var source = ReadRawSource(session, exact);
            PdfAttachmentPageText page;
            try { page = PdfAttachmentTextExtractor.ExtractPage(source, request.PageIndex.Value); }
            catch (ArgumentOutOfRangeException)
            {
                throw new ResourceRequestException("pageIndex is outside the PDF's exact page bounds.",
                    "resource_page_selector_invalid", false);
            }
            catch (Exception error) when (!(error is OperationCanceledException) &&
                                          !(error is OutOfMemoryException))
            {
                throw new ResourceRequestException("The exact PDF page text could not be extracted: " + error.Message,
                    "resource_page_unavailable", false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var start = request.PageStartChar;
            int end;
            string selected;
            try { selected = page.SelectWindow(start, request.PageCharCount, out end); }
            catch (ArgumentException error)
            { throw new ResourceRequestException(error.Message, "resource_page_selector_invalid", false); }
            var coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                address: "pdf-page:" + request.PageIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                start: start, end: end);
            return new ResourceReadSelection {
                Result = new ResourceReadResult {
                    Resource = Describe(session, artifact, false), Representation = ResourceRepresentations.Text,
                    ContentSha256 = artifact.ContentSha256,
                    Text = selected, Offset = start,
                    ReturnedCharacters = end - start, TotalCharacters = page.Text.Length,
                    PageIndex = request.PageIndex, PageCount = page.PageCount,
                    ExtractionProfile = PdfAttachmentTextExtractor.PageViewProfile,
                    RetainedViewKey = PdfAttachmentTextExtractor.PageViewProfile + ":" +
                        request.PageIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ":" + start.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ":" + end.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Coverage = coverage,
                    SourceCoverage = start == 0 && end == page.Text.Length
                        ? new ResourceCoverage(ResourceCoverageKinds.PageRange,
                            start: request.PageIndex.Value, end: (long)request.PageIndex.Value + 1)
                        : null,
                    Complete = false, SourceComplete = false, Truncated = true, RawContentIncluded = true
                },
                ResourceRefs = new[] { exact }
            };
        }

        private ResourceReadSelection ReadPdfPageRangeText(ChatSession session, ResourceReadRequest request,
            ChatArtifact artifact, bool isMember, string resourceUri,
            System.Threading.CancellationToken cancellationToken)
        {
            if (isMember || artifact.Kind != ChatArtifactKinds.Attachment)
                throw new ResourceRequestException("Page ranges are available only for an exact PDF attachment.",
                    "resource_page_unsupported", false);
            EnsureNotRemoved(session, artifact, resourceUri);
            var original = FindExactAttachment(session, artifact);
            if (original?.Kind != "pdf" ||
                ArtifactViewerService.NormalizeMimeType(original.ContentType) != "application/pdf" ||
                ArtifactViewerService.NormalizeMimeType(artifact.MimeType) != "application/pdf")
                throw new ResourceRequestException("Page ranges are available only for a PDF original.",
                    "resource_page_unsupported", false);
            var exact = ChatResourceUri.CreateArtifactRevision(session, artifact);
            ResourceReadCursor.ValidatePinned(request, exact.Revision);
            cancellationToken.ThrowIfCancellationRequested();
            var source = ReadRawSource(session, exact);
            PdfAttachmentPageRangeText pages;
            try { pages = PdfAttachmentTextExtractor.ExtractRange(source, request.PageStart.Value,
                request.PageRangeCount, request.MaxChars); }
            catch (ArgumentOutOfRangeException)
            { throw new ResourceRequestException("startPage is outside the PDF's exact page bounds.",
                "resource_page_selector_invalid", false); }
            catch (PdfPageWindowRequiredException error)
            { throw new ResourceRequestException(error.Message, "resource_page_window_required", false); }
            catch (Exception error) when (!(error is OperationCanceledException) &&
                                          !(error is OutOfMemoryException))
            { throw new ResourceRequestException("The exact PDF page range could not be extracted: " + error.Message,
                "resource_page_unavailable", false); }
            cancellationToken.ThrowIfCancellationRequested();
            var coverage = new ResourceCoverage(ResourceCoverageKinds.PageRange,
                start: request.PageStart.Value, end: pages.EndPage);
            return new ResourceReadSelection {
                Result = new ResourceReadResult {
                    Resource = Describe(session, artifact, false), Representation = ResourceRepresentations.Text,
                    ContentSha256 = artifact.ContentSha256, Text = pages.Text,
                    ReturnedCharacters = pages.Text.Length, TotalCharacters = pages.Text.Length,
                    PageCount = pages.PageCount,
                    ExtractionProfile = PdfAttachmentTextExtractor.RangeViewProfile,
                    RetainedViewKey = PdfAttachmentTextExtractor.RangeViewProfile + ":" +
                        request.PageStart.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                        pages.EndPage.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Coverage = coverage, SourceCoverage = coverage,
                    Complete = false, SourceComplete = false, Truncated = true, RawContentIncluded = true
                },
                ResourceRefs = new[] { exact }
            };
        }

        internal ResourceSearchResult SearchOccurrences(ChatSession session, ResourceRef reference,
            string query, int limit, bool matchCase)
        {
            if (_documentArtifacts == null)
                throw new ResourceRequestException("Document text index is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
            try { return _documentArtifacts.SearchTextOccurrences(session, reference, query, limit, matchCase); }
            catch (System.IO.InvalidDataException error)
            { throw new ResourceRequestException(error.Message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
        }

        internal ResourceTextOutline OutlineText(ChatSession session, ResourceRef reference, int fromPart, int limit)
        {
            if (_documentArtifacts == null)
                throw new ResourceRequestException("Document text index is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
            try { return _documentArtifacts.OutlineText(session, reference, fromPart, limit); }
            catch (System.IO.InvalidDataException error)
            { throw new ResourceRequestException(error.Message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
            catch (ArgumentOutOfRangeException error)
            { throw new ResourceRequestException(error.Message, "resource_part_invalid", false); }
        }

        internal static ResourceDescriptor DescribeMetadata(ChatSession session, ChatArtifact artifact)
        {
            var attachment = FindExactAttachment(session, artifact);
            var representations = new List<string> { "metadata" };
            if (string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                representations.Add(ResourceRepresentations.Structure);
            }
            else if (HasTextHint(artifact, attachment)) representations.Add("text");
            if (IsModelMedia(attachment)) representations.Add("media");
            var result = new ResourceDescriptor
            {
                Reference = ChatResourceUri.CreateArtifactRevision(session, artifact),
                Provider = ProviderName,
                Kind = artifact.Kind ?? "artifact",
                Title = artifact.Title ?? string.Empty,
                MimeType = artifact.MimeType,
                Mutable = false,
                ByteLength = artifact.ContentByteLength,
                Representations = representations,
                CreatedUtc = artifact.CreatedUtc
            };
            if ((artifact.Kind == ChatArtifactKinds.File || artifact.Kind == ChatArtifactKinds.Attachment) &&
                representations.Contains(ResourceRepresentations.Text) && attachment?.TextTruncated != true &&
                ResourceGatewayService.TreeFormat(result) != null)
                representations.Add(ResourceRepresentations.Tree);
            if (!string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId)) result.Metadata["scope"] = "document";
            if (artifact.Kind == ChatArtifactKinds.File && DocumentArtifactStore.FileEncoding(artifact) != null)
                result.Metadata["authoredFile"] = "true";
            if (!string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId) && (artifact.Kind == ChatArtifactKinds.Markdown ||
                artifact.Kind == ChatArtifactKinds.PlanDocument || DocumentArtifactStore.IsMarkdownOriginal(attachment) && !attachment.TextTruncated))
                result.Metadata["sectionRead"] = "atx-heading";
            if (string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                result.SnapshotTarget = ResourceGatewayService.IntentTarget(result);
                if (artifact.Id == session.ActiveHtmlArtifactId)
                    result.CurrentTarget = ChatHtmlResourceCatalog.CurrentWorkspaceTarget;
                result.Metadata["memberKinds"] = ChatHtmlResourceCatalog.FileKind + "," + ChatHtmlResourceCatalog.DataKind;
                result.Metadata["memberDiscovery"] = "List this provider with an exact member kind.";
            }
            return result;
        }

        private ResourceDescriptor Describe(ChatSession session, ChatArtifact artifact, bool compact)
        {
            var result = DescribeMetadata(session, artifact);
            if (MarkdownDocumentIdentity.LogicalId(artifact.Id) != null)
                result.Metadata["description"] = (string)JObject.Parse(artifact.MetadataJson ?? "{}")["description"];
            if (_payloads != null)
            {
                result.ViewCapabilities.AddRange(ArtifactViewerService.BinaryViewCapabilities(artifact, FindExactAttachment(session, artifact), _readAttachmentBytes != null));
                result.Representations.AddRange(result.ViewCapabilities.Select(view => view.View));
            }
            if (!compact)
            {
                var parent = Find(session, artifact.ParentArtifactId);
                result.Parent = parent == null
                    ? null
                    : ChatResourceUri.CreateArtifactRevision(session, parent);
                result.Related = (artifact.RelatedArtifactIds ?? new List<string>())
                    .Select(id => Find(session, id))
                    .Where(item => item != null)
                    .Select(item => ChatResourceUri.CreateArtifactRevision(session, item))
                    .ToList();
                result.SourceMessageId = artifact.SourceMessageId;
                result.ContentSha256 = artifact.ContentSha256;
            }
            return result;
        }

        private string NormalizeRepresentation(string value, ChatSession session, ChatArtifact artifact)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            var htmlWorkspace = string.Equals(
                artifact == null ? null : artifact.Kind,
                ChatArtifactKinds.HtmlWorkspace,
                StringComparison.OrdinalIgnoreCase);
            if (value == ResourceRepresentations.Metadata) return value;
            if (htmlWorkspace && (value.Length == 0 || value == "auto" || value == ResourceRepresentations.Structure))
            {
                return ResourceRepresentations.Structure;
            }
            if (!htmlWorkspace && (value == "text" || value == "media")) return value;
            if (value.Length > 0 && value != "auto")
            {
                throw new ResourceRequestException(
                    "Resource representation is unavailable: " + value + ". Use one advertised by the resource descriptor" +
                        (htmlWorkspace ? " (metadata or structure)." : "."),
                    "resource_representation_unavailable",
                    true);
            }
            if (HasTextHint(artifact, FindExactAttachment(session, artifact))) return "text";
            if (IsModelMedia(FindExactAttachment(session, artifact))) return "media";
            return "metadata";
        }

        private string ReadText(ChatSession session, ChatArtifact artifact, int maxChars)
        {
            if ((artifact.Kind == ChatArtifactKinds.PlanDocument || DocumentArtifactStore.IsAuthored(session, ChatResourceUri.CreateArtifactRevision(session, artifact))) && artifact.DocumentAuthorityId == session.DocumentAuthorityId &&
                !string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId))
            {
                if (_documentArtifacts == null) throw new ResourceRequestException("The document artifact owner is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                var exactBody = ReadDocumentArtifact(session, ChatResourceUri.CreateArtifactRevision(session, artifact)).InlineText;
                return exactBody.Length <= maxChars ? exactBody : exactBody.Substring(0, maxChars);
            }
            if (artifact == null || maxChars <= 0) return null;
            var attachment = FindExactAttachment(session, artifact);
            if (attachment == null && !string.IsNullOrWhiteSpace(AttachmentId(artifact))) return null;
            if (attachment != null)
            {
                var hasExtractedText = attachment.ExtractedText != null ||
                    attachment.ExtractedCharCount > 0 ||
                    !string.IsNullOrWhiteSpace(attachment.ExtractedTextSha256) &&
                    attachment.ExtractedTextByteLength.HasValue;
                if (!hasExtractedText) return null;
                if (!string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId))
                {
                    if (_payloads == null || string.IsNullOrWhiteSpace(attachment.ExtractedTextSha256) ||
                        !attachment.ExtractedTextByteLength.HasValue)
                        throw new ResourceRequestException("The retained original text is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                    var exact = _payloads.ReadText(new ChatBlobReference { Sha256 = attachment.ExtractedTextSha256,
                        ByteLength = attachment.ExtractedTextByteLength.Value, ContentType = "text/plain; charset=utf-8" });
                    if (exact == null) throw new ResourceRequestException("The retained original text is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                    return exact.Length <= maxChars ? exact : exact.Substring(0, maxChars);
                }
                var text = _readAttachmentText == null
                    ? attachment.ExtractedText
                    : _readAttachmentText(attachment, maxChars);
                if (text == null) return null;
                return text.Length <= maxChars ? text : text.Substring(0, maxChars);
            }
            if (artifact.InlineText == null && _loadArtifactBody != null)
            {
                _loadArtifactBody(session, artifact.Id);
            }
            var body = artifact.InlineText;
            if (body == null) return null;
            return body.Length <= maxChars ? body : body.Substring(0, maxChars);
        }

        private static ResourceSearchMatch SearchMatch(
            ChatSession session,
            ChatArtifact artifact,
            string representation,
            string source,
            int index,
            int queryLength,
            int maxChars)
        {
            var start = Math.Max(0, index - maxChars / 3);
            var length = Math.Min(maxChars, source.Length - start);
            return new ResourceSearchMatch
            {
                Reference = ChatResourceUri.CreateArtifactRevision(session, artifact),
                CreatedUtc = artifact.CreatedUtc,
                DocumentScoped = !string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId),
                Kind = artifact.Kind ?? "artifact",
                Title = artifact.Title ?? string.Empty,
                Description = ArtifactDescription(artifact),
                Representation = representation,
                MatchOffset = index,
                MatchLength = queryLength,
                SnippetOffset = start,
                Snippet = source.Substring(start, length)
            };
        }

        private static string ArtifactDescription(ChatArtifact artifact)
        {
            return artifact.Kind == ChatArtifactKinds.Markdown
                ? (string)JObject.Parse(artifact.MetadataJson ?? "{}")["description"] : null;
        }

        private static bool HasTextHint(ChatArtifact artifact, ChatAttachment attachment)
        {
            if (artifact?.Kind == DocumentArtifactStore.SharedContextKind) return true;
            if (attachment != null)
            {
                return attachment.ExtractedCharCount > 0 ||
                    !string.IsNullOrWhiteSpace(attachment.ExtractedText) ||
                    !string.IsNullOrWhiteSpace(attachment.ExtractedTextSha256);
            }
            if (artifact == null) return false;
            if (!string.IsNullOrWhiteSpace(AttachmentId(artifact))) return false;
            if (!string.IsNullOrWhiteSpace(artifact.InlineText)) return true;
            return !string.IsNullOrWhiteSpace(artifact.ContentSha256) &&
                (string.Equals(artifact.Kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(artifact.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(artifact.Kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(artifact.Kind, ChatArtifactKinds.File, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(artifact.MimeType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(artifact.MimeType, "application/javascript", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(artifact.MimeType, "application/xml", StringComparison.OrdinalIgnoreCase) ||
                     artifact.MimeType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true) ||
                 string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(artifact.Kind, ChatArtifactKinds.Compaction, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(artifact.Kind, ChatArtifactKinds.ToolResult, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(artifact.Kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase));
        }

        private static string TextRepresentationSha256(ChatArtifact artifact, ChatAttachment attachment)
        {
            return attachment != null
                ? attachment.ExtractedTextSha256
                : artifact == null ? null : artifact.ContentSha256;
        }

        private static bool IsModelMedia(ChatAttachment attachment)
        {
            return attachment != null &&
                (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(attachment.Kind, "pdf", StringComparison.OrdinalIgnoreCase));
        }

        internal static ChatAttachment FindExactAttachment(ChatSession session, ChatArtifact artifact)
        {
            if (session == null || artifact == null) return null;
            if (!string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId))
            {
                var original = artifact.OriginalAttachment;
                return artifact.DocumentAuthorityId == session.DocumentAuthorityId && original != null &&
                    original.ContentSha256 == artifact.ContentSha256 && original.ContentByteLength == artifact.ContentByteLength
                    ? original : null;
            }
            var attachmentId = AttachmentId(artifact);
            if (string.IsNullOrWhiteSpace(attachmentId) || string.IsNullOrWhiteSpace(artifact.SourceMessageId)) return null;
            var messages = (session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null && string.Equals(
                    message.Id,
                    artifact.SourceMessageId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (messages.Count != 1) return null;
            var attachments = (messages[0].Attachments ?? new List<ChatAttachment>())
                .Where(item => item != null && string.Equals(
                    item.Id,
                    attachmentId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (attachments.Count != 1) return null;
            var attachment = attachments[0];
            if (string.IsNullOrWhiteSpace(artifact.ContentSha256) ||
                string.IsNullOrWhiteSpace(attachment.ContentSha256) ||
                !string.Equals(artifact.ContentSha256, attachment.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
                !artifact.ContentByteLength.HasValue ||
                !attachment.ContentByteLength.HasValue ||
                artifact.ContentByteLength.Value != attachment.ContentByteLength.Value ||
                string.Equals(attachment.Status, "error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attachment.Status, "missing", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return attachment;
        }

        private static string AttachmentId(ChatArtifact artifact)
        {
            if (artifact == null) return string.Empty;
            try
            {
                var metadata = JObject.Parse(artifact.MetadataJson ?? "{}");
                var value = (string)metadata["attachmentId"];
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch (JsonException)
            {
            }
            return string.Empty;
        }

        private static ChatArtifact Find(ChatSession session, string artifactId)
        {
            return Artifacts(session).FirstOrDefault(item =>
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
        }

        internal static ChatArtifactAddress ParseAddress(ChatSession session, string resourceUri)
        {
            ResourceAddress parsed;
            if (!ResourceUri.TryParse(resourceUri, out parsed) ||
                !string.Equals(parsed.Provider, ProviderName, StringComparison.Ordinal) ||
                (parsed.Segments.Count != 5 && parsed.Segments.Count != 8) ||
                !string.Equals(parsed.Segments[1], "artifact", StringComparison.Ordinal) ||
                !string.Equals(parsed.Segments[3], "revision", StringComparison.Ordinal) ||
                parsed.Segments.Count == 8 &&
                    (!string.Equals(parsed.Segments[5], "member", StringComparison.Ordinal) ||
                     (!string.Equals(parsed.Segments[6], "file", StringComparison.Ordinal) &&
                      !string.Equals(parsed.Segments[6], "data", StringComparison.Ordinal))))
            {
                throw new ResourceRequestException(
                    "The chat resource URI has an invalid canonical shape. Copy the exact URI returned by a resource descriptor " +
                    "or run core.find with scope=html and choose the exact member target.",
                    "invalid_resource_uri",
                    false);
            }
            string actualSessionId;
            string artifactId;
            int revision;
            if (!ChatResourceUri.TryParseArtifactRevision(
                new ResourceRef(resourceUri), out actualSessionId, out artifactId, out revision))
            {
                throw new ResourceRequestException(
                    "The chat resource URI has an invalid artifact revision. Copy an exact revision-pinned URI returned by a resource descriptor.",
                    "invalid_resource_uri",
                    false);
            }
            var address = new ChatArtifactAddress
            {
                ArtifactId = artifactId,
                Revision = revision,
                MemberType = parsed.Segments.Count == 8 ? parsed.Segments[6] : null,
                MemberKey = parsed.Segments.Count == 8 ? parsed.Segments[7] : null
            };
            if (session == null || actualSessionId != session.Id && actualSessionId != session.DocumentAuthorityId)
            {
                throw new ResourceRequestException(
                    "The resource belongs to a different chat. Switch to the owning chat or resolve a reference from the active chat.",
                    "active_chat_mismatch",
                    false);
            }
            var candidate = (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item => item?.Id == artifactId);
            if (candidate != null && (candidate.DocumentAuthorityId ?? session.Id) != actualSessionId)
                throw new ResourceRequestException("The artifact URI does not match its owner.", "RESOURCE_ACCESS_DENIED", false);
            return address;
        }

        public ResourceRef ResolveIdentity(ChatSession session, ResourceIdentity identity)
        {
            var address = ResourceUri.Parse(identity.Uri);
            if (address.Provider != ProviderName || session == null || address.Segments.Count != 3 && address.Segments.Count != 6 ||
                address.Segments[0] != session.Id && address.Segments[0] != session.DocumentAuthorityId || address.Segments[1] != "artifact")
                throw new ResourceRequestException("The resource identity is outside this chat.", "RESOURCE_ACCESS_DENIED", false);
            if (address.Segments[0] == session.DocumentAuthorityId)
            {
                if (_documentArtifacts == null) throw new ResourceRequestException("The document artifact owner is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                try
                {
                    var reference = _documentArtifacts.ResolveSnapshotIdentity(session,
                        new ResourceIdentity(ResourceUri.Create(ProviderName, session.DocumentAuthorityId, "artifact", address.Segments[2])));
                    session = ProjectDocumentSession(session, null, new[] { _documentArtifacts.Read(session, reference, false) });
                }
                catch (System.IO.InvalidDataException ex)
                { throw new ResourceRequestException(ex.Message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
            }
            var artifact = FindExactArtifact(session, address.Segments[2]);
            var exact = ChatResourceUri.CreateArtifactRevision(session, artifact);
            if (ResourceUri.Parse(exact.Uri).Segments[0] != address.Segments[0])
                throw new ResourceRequestException("The artifact identity does not match its owner.", "RESOURCE_ACCESS_DENIED", false);
            if (address.Segments.Count == 3) return exact;
            return new ResourceRef(ResourceUri.Create(ProviderName, artifact.DocumentAuthorityId ?? session.Id, "artifact", artifact.Id, "revision",
                exact.Revision, address.Segments[3], address.Segments[4], address.Segments[5]), exact.Revision);
        }

        internal ChatArtifact ResolveArtifact(ChatSession session, string resourceUri)
        {
            session = ProjectSession(session, resourceUri);
            var address = ParseAddress(session, resourceUri);
            if (address.IsMember) throw new ResourceRequestException("An artifact revision is required.", "invalid_resource_uri", false);
            var artifact = FindExactArtifact(session, address.ArtifactId);
            EnsureRevision(artifact, address.Revision);
            if (ChatResourceUri.CreateArtifactRevisionUri(session, artifact) != resourceUri)
                throw new ResourceRequestException("The artifact revision is not canonical.", "invalid_resource_uri", false);
            return artifact;
        }

        private ArtifactDiscoveryProjection ProjectDiscovery(ChatSession session, int offset = 0, int limit = 50)
        {
            try { return BuildDiscoveryProjection(session, offset, limit); }
            catch (System.IO.InvalidDataException ex)
            { throw new ResourceRequestException(ex.Message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
            catch (ArgumentException ex)
            { throw new ResourceRequestException(ex.Message, "resource_revision_changed", true); }
        }

        private ArtifactDiscoveryProjection BuildDiscoveryProjection(ChatSession session, int offset, int limit)
        {
            if (_documentArtifacts == null && !string.IsNullOrWhiteSpace(session?.DocumentAuthorityId))
                throw new ResourceRequestException("The document artifact owner is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
            var local = Artifacts(session).Where(item => string.IsNullOrWhiteSpace(item.DocumentAuthorityId)).ToList();
            var localPage = local.Skip(offset).Take(limit).ToList();
            var documentOffset = Math.Max(0, offset - local.Count);
            var retained = _documentArtifacts != null && !string.IsNullOrWhiteSpace(session?.DocumentAuthorityId)
                ? _documentArtifacts.InspectCurrentMetadata(session, documentOffset, limit - localPage.Count) : null;
            var total = local.Count + (retained?.Total ?? 0);
            if (offset > total) throw new ArgumentException("The discovery page exceeds its source.");
            var next = retained == null ? offset + localPage.Count
                : local.Count + (retained.NextOffset ?? retained.Total);
            if (offset < local.Count && localPage.Count == limit) next = offset + localPage.Count;
            var projected = CopyDiscoverySession(session, localPage.Concat(retained?.Items ?? new ChatArtifact[0]).ToList());
            var localRevision = TextPatternEngine.Sha256(string.Join("\n", local.Select(item =>
                JsonConvert.SerializeObject(new { item.Id, item.Revision, item.Title, item.CreatedUtc, item.MetadataJson, item.AvailabilityIssue }))));
            var result = new ArtifactDiscoveryProjection { Session = projected,
                UnavailableResources = retained?.UnavailableResources ?? 0, Total = total, NextOffset = next < total ? (int?)next : null,
                SourceRevision = TextPatternEngine.Sha256(session?.Id + ":" + session?.DocumentAuthorityId + ":" + retained?.Generation + ":" + localRevision) };
            FilterDiscoveryAvailability(result);
            return result;
        }

        private ArtifactDiscoveryProjection ProjectHtmlDiscovery(ChatSession session)
        {
            var artifact = Artifacts(session).SingleOrDefault(item => item.Id == session?.ActiveHtmlArtifactId);
            var result = new ArtifactDiscoveryProjection { Session = CopyDiscoverySession(session, new List<ChatArtifact>()) };
            if (artifact == null)
            {
                if (!string.IsNullOrWhiteSpace(session?.ActiveHtmlArtifactId)) result.UnavailableResources = 1;
                return result;
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId))
                {
                    if (_documentArtifacts == null) throw new System.IO.InvalidDataException("The document artifact owner is unavailable.");
                    var current = _documentArtifacts.CurrentSnapshot(session, HtmlWorkspaceIdentity.Identity(session, HtmlWorkspaceIdentity.LogicalId(artifact.Id)));
                    if (current?.Uri != ChatResourceUri.CreateArtifactRevisionUri(session, artifact))
                        throw new System.IO.InvalidDataException("Refresh the selected HTML workspace before discovering members.");
                    artifact = _documentArtifacts.Read(session, current, false);
                    if (_documentArtifacts.CurrentSnapshot(session, HtmlWorkspaceIdentity.Identity(session, HtmlWorkspaceIdentity.LogicalId(artifact.Id)))?.Revision != current.Revision)
                        throw new ResourceRequestException("The document changed during member discovery.", "resource_revision_changed", true);
                    result.SourceRevision = current.Uri + ":" + current.Revision;
                }
                result.Session.Artifacts.Add(artifact);
                FilterDiscoveryAvailability(result);
            }
            catch (Exception ex) when (IsDiscoveryUnavailable(ex)) { result.UnavailableResources++; }
            return result;
        }

        private void ValidateHtmlSearchDiscovery(ChatSession session, ArtifactDiscoveryProjection before)
        {
            var after = ProjectHtmlDiscovery(session);
            if (before.SourceRevision != after.SourceRevision || before.UnavailableResources != after.UnavailableResources ||
                before.Session.ActiveHtmlArtifactId != after.Session.ActiveHtmlArtifactId)
                throw new ResourceRequestException("The selected HTML workspace changed during search; refresh and rediscover it.", "resource_revision_changed", true);
        }

        private static ChatSession CopyDiscoverySession(ChatSession session, List<ChatArtifact> artifacts)
        {
            return new ChatSession { Id = session?.Id, DocumentAuthorityId = session?.DocumentAuthorityId,
                Artifacts = artifacts, Messages = session?.Messages, HtmlWorkspace = session?.HtmlWorkspace,
                HtmlWorkspaceRecovery = session?.HtmlWorkspaceRecovery, ActiveHtmlArtifactId = session?.ActiveHtmlArtifactId,
                ActivePlanDocumentArtifactId = session?.ActivePlanDocumentArtifactId, ActiveTaskListArtifactId = session?.ActiveTaskListArtifactId };
        }

        private void FilterDiscoveryAvailability(ArtifactDiscoveryProjection result)
        {
            var healthy = new List<ChatArtifact>();
            foreach (var artifact in result.Session.Artifacts)
            {
                try
                {
                    if (_documentArtifacts?.IsRemoved(result.Session, artifact) == true) continue;
                    if (artifact.AvailabilityIssue != null || !_htmlResources.IsReadableRevision(result.Session, artifact))
                    { result.UnavailableResources++; continue; }
                    healthy.Add(artifact);
                }
                catch (Exception ex) when (IsDiscoveryUnavailable(ex)) { result.UnavailableResources++; }
            }
            result.Session.Artifacts = healthy;
        }

        private static bool IsDiscoveryUnavailable(Exception ex)
        {
            return ex is System.IO.InvalidDataException || ex is System.IO.IOException || ex is JsonException || ex is UnauthorizedAccessException ||
                ex is ResourceRequestException;
        }

        private sealed class ArtifactDiscoveryProjection
        {
            internal ChatSession Session;
            internal string SourceRevision;
            internal int UnavailableResources;
            internal int Total;
            internal int? NextOffset;
        }

        private ChatSession ProjectSession(ChatSession session, string exactUri)
        {
            try { return ProjectDocumentSession(session, exactUri); }
            catch (System.IO.InvalidDataException ex)
            { throw new ResourceRequestException(ex.Message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
        }

        private bool LoadWorkspaceBody(ChatSession session, string artifactId)
        {
            var artifact = (session.Artifacts ?? new List<ChatArtifact>()).SingleOrDefault(item => item.Id == artifactId);
            if (!string.IsNullOrEmpty(artifact?.DocumentAuthorityId))
            {
                if (_documentArtifacts == null) throw new ResourceRequestException("The document artifact owner is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                artifact.InlineText = ReadDocumentArtifact(session, ChatResourceUri.CreateArtifactRevision(session, artifact)).InlineText;
                return artifact.InlineText != null;
            }
            return _loadArtifactBody?.Invoke(session, artifactId) == true;
        }

        private ChatArtifact ReadDocumentArtifact(ChatSession session, ResourceRef exact)
        {
            try { return _documentArtifacts.Read(session, exact); }
            catch (System.IO.InvalidDataException ex)
            { throw new ResourceRequestException(ex.Message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
        }

        private ChatSession ProjectDocumentSession(ChatSession session, string exactUri, IEnumerable<ChatArtifact> discovered = null)
        {
            if (_documentArtifacts == null || string.IsNullOrWhiteSpace(session?.DocumentAuthorityId)) return session;
            IEnumerable<ChatArtifact> originals;
            if (exactUri == null) originals = discovered ?? throw new ArgumentException("An exact discovery selection is required.");
            else
            {
                string owner, id;
                int revision;
                originals = ChatResourceUri.TryParseArtifactRevision(new ResourceRef(exactUri), out owner, out id, out revision) &&
                    owner == session.DocumentAuthorityId
                    ? new[] { _documentArtifacts.Read(session, ChatResourceUri.ArtifactSnapshot(new ResourceRef(exactUri))) }
                    : new ChatArtifact[0];
            }
            return new ChatSession
            {
                Id = session.Id, DocumentAuthorityId = session.DocumentAuthorityId,
                Artifacts = (session.Artifacts ?? new List<ChatArtifact>()).Where(item => item != null &&
                    string.IsNullOrWhiteSpace(item.DocumentAuthorityId)).Concat(originals).ToList(),
                Messages = session.Messages, HtmlWorkspace = session.HtmlWorkspace, HtmlWorkspaceRecovery = session.HtmlWorkspaceRecovery,
                ActiveHtmlArtifactId = session.ActiveHtmlArtifactId, ActivePlanDocumentArtifactId = session.ActivePlanDocumentArtifactId,
                ActiveTaskListArtifactId = session.ActiveTaskListArtifactId
            };
        }

        private static ChatArtifact FindExactArtifact(
            ChatSession session,
            string artifactId)
        {
            var matches = (session == null ? null : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && string.Equals(
                    item.Id,
                    artifactId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (matches.Count == 0)
            {
                throw ResourceError(
                    "artifact_not_found",
                    "The artifact does not exist in the active chat.",
                    false,
                    "List chat resources again and use an exact returned URI.");
            }
            if (matches.Count > 1)
            {
                throw ResourceError(
                    "resource_corrupt",
                    "The artifact identity is ambiguous in the active chat.",
                    false,
                    "Do not retry this URI; start a new chat or repair the persisted stream.");
            }
            return matches[0];
        }

        private static void EnsureRevision(ChatArtifact artifact, int revision)
        {
            if (artifact != null && Math.Max(1, artifact.Revision) == revision) return;
            throw ResourceError(
                "revision_not_found",
                "The requested artifact revision does not exist.",
                false,
                "Use an exact revision URI returned by resource discovery or mutation output.");
        }

        internal static ResourceRequestException ResourceError(
            string code,
            string message,
            bool retryable,
            string recoveryHint)
        {
            return new ResourceRequestException(message + (string.IsNullOrWhiteSpace(recoveryHint)
                ? string.Empty : " " + recoveryHint), code, retryable);
        }

        internal sealed class ChatArtifactAddress
        {
            internal string ArtifactId;
            internal int Revision;
            internal string MemberType;
            internal string MemberKey;
            internal bool IsMember { get { return !string.IsNullOrWhiteSpace(MemberType); } }
        }

        private static List<ChatArtifact> Artifacts(ChatSession session)
        {
            return (session == null ? null : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .ToList();
        }

        private IEnumerable<ChatArtifact> OrderedArtifacts(ChatSession session)
        {
            return Artifacts(session)
                .Where(IsDiscoverableArtifact)
                .Where(item => !PlanDocumentService.IsRemoved(session, item))
                .OrderByDescending(item => string.Equals(item.Id, session == null ? null : session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => string.Equals(item.Id, session == null ? null : session.ActiveTaskListArtifactId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => string.Equals(item.Id, session == null ? null : session.ActivePlanDocumentArtifactId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.CreatedUtc)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase);
        }

        internal static bool IsDiscoverableArtifact(ChatArtifact artifact)
        {
            // Compaction checkpoints are runtime replay state. Their structured
            // claims are projected separately after authority filtering.
            return artifact != null && !string.Equals(
                artifact.Kind,
                ChatArtifactKinds.Compaction,
                StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureNotRemoved(ChatSession session, ChatArtifact artifact, string resourceUri)
        {
            if (!PlanDocumentService.IsRemoved(session, artifact) && _documentArtifacts?.IsRemoved(session, artifact) != true) return;
            throw new ResourceRequestException(
                "Resource was removed from the document: " + resourceUri,
                "resource_removed",
                false);
        }

    }
}
