using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatArtifactResourceProvider : IResourceProvider
    {
        public const string ProviderName = "chat";
        public const int MaximumReadCharacters = 32000;
        private const int DefaultReadCharacters = 8000;
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
                .Where(item => string.IsNullOrWhiteSpace(kind) ||
                    string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var descriptors = filtered.Select(item => Describe(session, item, true)).ToList();
            var position = ResourceReadCursor.ParseRevisionBound(cursor);
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
                Cursor = ResourceReadCursor.CreateRevisionBound(offset, collectionRevision),
                NextCursor = nextOffset < filtered.Count
                    ? ResourceReadCursor.CreateRevisionBound(nextOffset, collectionRevision)
                    : null,
                Truncated = nextOffset < filtered.Count
            };
        }

        public ResourceDescriptor Resolve(ChatSession session, string resourceUri)
        {
            ResourceDescriptor htmlResource;
            if (_htmlResources.TryResolve(session, resourceUri, out htmlResource)) return htmlResource;
            var artifact = FindByUri(session, resourceUri);
            if (artifact == null)
            {
                throw new KeyNotFoundException("Resource not found in the active chat: " + resourceUri);
            }
            EnsureNotRemoved(session, artifact, resourceUri);
            return Describe(session, artifact, false);
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

            foreach (var artifact in OrderedArtifacts(session))
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
            ResourceReadSelection htmlSelection;
            if (_htmlResources.TryRead(
                session,
                request,
                out htmlSelection)) return htmlSelection;
            var resourceUri = request == null || request.Reference == null
                ? string.Empty
                : request.Reference.Uri;
            var artifact = FindByUri(session, resourceUri);
            if (artifact == null)
            {
                throw new KeyNotFoundException("Resource not found in the active chat: " + resourceUri);
            }
            EnsureNotRemoved(session, artifact, resourceUri);
            var exactReference = ChatResourceUri.CreateArtifactRevision(session, artifact);
            var exactUri = exactReference.Uri;
            ResourceReadCursor.ValidatePinned(request, exactReference.Revision);
            var representation = request == null ? null : request.Representation;
            var maxChars = request == null ? 0 : request.MaxChars;
            representation = NormalizeRepresentation(representation, session, artifact);
            maxChars = Math.Max(128, Math.Min(MaximumReadCharacters, maxChars <= 0 ? DefaultReadCharacters : maxChars));

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
            var offset = ResourceReadCursor.ParseImmutable(request);
            if (representation == ResourceRepresentations.Structure)
            {
                var structure = _htmlResources.ReadStructure(session, artifact, offset, maxChars);
                structure.Result.Resource = Describe(session, artifact, false);
                return structure;
            }

            var content = ReadText(session, artifact, int.MaxValue);
            if (string.IsNullOrWhiteSpace(content))
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
                        ? ResourceReadCursor.CreateImmutable(nextOffset)
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
            if (artifact == null || maxChars <= 0) return string.Empty;
            var attachment = FindExactAttachment(session, artifact);
            if (attachment == null && !string.IsNullOrWhiteSpace(AttachmentId(artifact))) return string.Empty;
            if (attachment != null)
            {
                var text = _readAttachmentText == null
                    ? attachment.ExtractedText ?? string.Empty
                    : _readAttachmentText(attachment, maxChars) ?? string.Empty;
                return text.Length <= maxChars ? text : text.Substring(0, maxChars);
            }
            if (string.IsNullOrWhiteSpace(artifact.InlineText) && _loadArtifactBody != null)
            {
                _loadArtifactBody(session, artifact.Id);
            }
            var body = artifact.InlineText ?? string.Empty;
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

        private static ChatArtifact FindByUri(ChatSession session, string resourceUri)
        {
            ResourceAddress address;
            if (!ResourceUri.TryParse(resourceUri, out address) || address.Segments.Count != 5)
            {
                return null;
            }
            string artifactId;
            int revision;
            if (session == null || !ChatResourceUri.TryParseArtifactRevision(
                session.Id, new ResourceRef(resourceUri), out artifactId, out revision)) return null;
            var artifact = Find(session, artifactId);
            return artifact != null && Math.Max(1, artifact.Revision) == revision ? artifact : null;
        }

        private static List<ChatArtifact> Artifacts(ChatSession session)
        {
            return (session == null ? null : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
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
