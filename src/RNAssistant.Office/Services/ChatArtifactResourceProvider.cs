using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatArtifactResourceProvider : IResourceProvider, IResourceMemberResolver, IResourceIdentityResolver
    {
        public const string ProviderName = "chat";
        private const int MaximumListItems = 50;
        private const int MaximumSearchResults = 20;
        private const int MaximumSearchCharacters = 1000000;
        private const int MaximumSearchCharactersPerArtifact = 128000;

        private readonly Func<ChatSession, string, bool> _loadArtifactBody;
        private readonly Func<ChatAttachment, int, string> _readAttachmentText;
        private readonly ChatHtmlResourceCatalog _htmlResources;

        public ChatArtifactResourceProvider(
            Func<ChatSession, string, bool> loadArtifactBody = null,
            Func<ChatAttachment, int, string> readAttachmentText = null)
        {
            _loadArtifactBody = loadArtifactBody;
            _readAttachmentText = readAttachmentText;
            _htmlResources = new ChatHtmlResourceCatalog(loadArtifactBody);
        }

        public string Id { get { return ProviderName; } }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            if (ChatHtmlResourceCatalog.SupportsKind(kind))
            {
                return _htmlResources.List(session, kind, cursor, limit);
            }
            limit = Math.Max(1, Math.Min(MaximumListItems, limit <= 0 ? 20 : limit));
            var filtered = OrderedArtifacts(session)
                .Where(item => _htmlResources.IsReadableRevision(session, item))
                .Where(item => string.IsNullOrWhiteSpace(kind) ||
                    string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var descriptors = filtered.Select(item => Describe(session, item, true)).ToList();
            var cursorBinding = ResourceReadCursor.ListBinding(ProviderName, kind);
            var position = ResourceReadCursor.ParseRevisionBound(cursor, cursorBinding);
            var collectionRevision = ResourceReadCursor.CollectionRevision(descriptors);
            ResourceReadCursor.ValidateContinuation(position, collectionRevision);
            ResourceReadCursor.ValidateCollectionOffset(position, descriptors.Count);
            var offset = position.Offset;
            var items = descriptors.Skip(offset).Take(limit).ToArray();
            var nextOffset = offset + items.Length;
            return new ResourceListPage
            {
                Items = items.ToList(),
                Total = filtered.Count,
                Cursor = ResourceReadCursor.CreateRevisionBound(offset, collectionRevision, cursorBinding),
                NextCursor = nextOffset < filtered.Count
                    ? ResourceReadCursor.CreateRevisionBound(nextOffset, collectionRevision, cursorBinding)
                    : null,
                Truncated = nextOffset < filtered.Count
            };
        }

        public ResourceDescriptor Resolve(ChatSession session, string resourceUri)
        {
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

        public ResourceSearchResult Search(
            ChatSession session,
            string query,
            string kind,
            int limit,
            int maxCharsPerMatch)
        {
            query = (query ?? string.Empty).Trim();
            if (query.Length == 0) throw new InvalidOperationException("Resource search query is required.");
            limit = Math.Max(1, Math.Min(MaximumSearchResults, limit <= 0 ? 10 : limit));
            maxCharsPerMatch = Math.Max(128, Math.Min(2000, maxCharsPerMatch <= 0 ? 600 : maxCharsPerMatch));
            var matches = new List<ResourceSearchMatch>();
            var scannedCharacters = 0;
            var scanTruncated = false;
            if (string.IsNullOrWhiteSpace(kind) || ChatHtmlResourceCatalog.SupportsKind(kind))
            {
                var html = _htmlResources.Search(session, query, kind, limit, maxCharsPerMatch);
                if (ChatHtmlResourceCatalog.SupportsKind(kind)) return html;
                matches.AddRange(html.Matches);
                scannedCharacters += html.ScannedCharacters;
                scanTruncated = html.ScanTruncated;
            }

            foreach (var artifact in OrderedArtifacts(session).Where(item =>
                _htmlResources.IsReadableRevision(session, item)))
            {
                if (matches.Count >= limit) break;
                if (!string.IsNullOrWhiteSpace(kind) &&
                    !string.Equals(artifact.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;

                var metadata = string.Join(" ", new[]
                {
                    artifact.Id,
                    artifact.Kind,
                    artifact.Title,
                    artifact.MimeType,
                    artifact.MetadataJson
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
                    var readLimit = Math.Min(MaximumSearchCharactersPerArtifact, remaining);
                    var text = ReadText(session, artifact, readLimit);
                    if (text == null) continue;
                    scannedCharacters += text.Length;
                    var textIndex = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (textIndex >= 0)
                    {
                        matches.Add(SearchMatch(session, artifact, "text", text, textIndex, query.Length, maxCharsPerMatch));
                    }
                }
            }

            return new ResourceSearchResult
            {
                Query = query,
                Matches = matches,
                ScannedCharacters = scannedCharacters,
                ScanTruncated = scanTruncated
            };
        }

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
        {
            var resourceUri = request == null || request.Reference == null
                ? string.Empty
                : request.Reference.Uri;
            var address = ParseAddress(session, resourceUri);
            var artifact = FindExactArtifact(session, address.ArtifactId);
            EnsureRevision(artifact, address.Revision);
            _htmlResources.ValidateRevision(session, artifact);
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
                    Truncated = nextOffset < content.Length,
                    RawContentIncluded = true,
                    NextCursor = nextOffset < content.Length
                        ? ResourceReadCursor.CreateImmutable(nextOffset, cursorBinding)
                        : null
                },
                ResourceRefs = new[] { exactReference }
            };
        }

        private ResourceDescriptor Describe(ChatSession session, ChatArtifact artifact, bool compact)
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
            if (string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                result.Metadata["memberKinds"] = ChatHtmlResourceCatalog.FileKind + "," + ChatHtmlResourceCatalog.DataKind;
                result.Metadata["memberDiscovery"] = "List this provider with an exact member kind.";
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
                var text = _readAttachmentText == null
                    ? attachment.ExtractedText
                    : _readAttachmentText(attachment, maxChars) ?? string.Empty;
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
                Kind = artifact.Kind ?? "artifact",
                Title = artifact.Title ?? string.Empty,
                Representation = representation,
                MatchOffset = index,
                MatchLength = queryLength,
                SnippetOffset = start,
                Snippet = source.Substring(start, length)
            };
        }

        private static bool HasTextHint(ChatArtifact artifact, ChatAttachment attachment)
        {
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
                    "or run common.resources_find with scope=html and choose the exact member target.",
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
            if (session == null || !string.Equals(actualSessionId, session.Id, StringComparison.Ordinal))
            {
                throw new ResourceRequestException(
                    "The resource belongs to a different chat. Switch to the owning chat or resolve a reference from the active chat.",
                    "active_chat_mismatch",
                    false);
            }
            return address;
        }

        public ResourceRef ResolveIdentity(ChatSession session, ResourceIdentity identity)
        {
            var address = ResourceUri.Parse(identity.Uri);
            if (address.Provider != ProviderName || session == null || address.Segments.Count != 3 && address.Segments.Count != 6 ||
                address.Segments[0] != session.Id || address.Segments[1] != "artifact")
                throw new ResourceRequestException("The resource identity is outside this chat.", "RESOURCE_ACCESS_DENIED", false);
            var artifact = FindExactArtifact(session, address.Segments[2]);
            var exact = ChatResourceUri.CreateArtifactRevision(session, artifact);
            if (address.Segments.Count == 3) return exact;
            return new ResourceRef(ResourceUri.Create(ProviderName, session.Id, "artifact", artifact.Id, "revision",
                exact.Revision, address.Segments[3], address.Segments[4], address.Segments[5]), exact.Revision);
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

        private static IEnumerable<ChatArtifact> OrderedArtifacts(ChatSession session)
        {
            return Artifacts(session)
                .Where(item => !PlanDocumentService.IsRemoved(session, item))
                .OrderByDescending(item => string.Equals(item.Id, session == null ? null : session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => string.Equals(item.Id, session == null ? null : session.ActiveTaskListArtifactId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => string.Equals(item.Id, session == null ? null : session.ActivePlanDocumentArtifactId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.CreatedUtc)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase);
        }

        private static void EnsureNotRemoved(ChatSession session, ChatArtifact artifact, string resourceUri)
        {
            if (!PlanDocumentService.IsRemoved(session, artifact)) return;
            throw new ResourceRequestException(
                "Resource was removed from this chat: " + resourceUri,
                "resource_removed",
                false);
        }

    }
}
