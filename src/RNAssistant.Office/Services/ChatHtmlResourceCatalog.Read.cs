using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ChatHtmlResourceCatalog
    {
        public ResourceSearchResult Search(
            ChatSession session,
            string query,
            string kind,
            int limit,
            int maxCharsPerMatch)
        {
            query = (query ?? string.Empty).Trim();
            if (query.Length == 0) throw new InvalidOperationException("Resource search query is required.");
            limit = Math.Max(1, Math.Min(20, limit <= 0 ? 10 : limit));
            maxCharsPerMatch = Math.Max(128, Math.Min(2000, maxCharsPerMatch <= 0 ? 600 : maxCharsPerMatch));
            var result = new ResourceSearchResult { Query = query };
            foreach (var member in ActiveMembers(session))
            {
                if (!string.IsNullOrWhiteSpace(kind) &&
                    !string.Equals(member.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                var metadata = member.Title + " " + member.MemberType + " " + member.ContentType;
                var index = metadata.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                var representation = ResourceRepresentations.Metadata;
                var source = metadata;
                if (index < 0)
                {
                    var remaining = MaximumSearchCharacters - result.ScannedCharacters;
                    if (remaining <= 0)
                    {
                        result.ScanTruncated = true;
                        break;
                    }
                    var scanLength = Math.Min(member.Content.Length,
                        Math.Min(MaximumSearchCharactersPerMember, remaining));
                    source = member.Content.Substring(0, scanLength);
                    result.ScannedCharacters += scanLength;
                    if (scanLength < member.Content.Length) result.ScanTruncated = true;
                    index = source.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    representation = member.MemberType == "file"
                        ? ResourceRepresentations.Source
                        : ResourceRepresentations.Text;
                }
                if (index < 0) continue;
                var start = Math.Max(0, index - maxCharsPerMatch / 3);
                result.Matches.Add(new ResourceSearchMatch
                {
                    Reference = Reference(member),
                    Kind = member.Kind,
                    Title = member.Title,
                    Representation = representation,
                    MatchOffset = index,
                    MatchLength = query.Length,
                    SnippetOffset = start,
                    Snippet = source.Substring(start, Math.Min(maxCharsPerMatch, source.Length - start))
                });
                if (result.Matches.Count >= limit) break;
            }
            return result;
        }

        public ResourceReadSelection ReadMember(
            ChatSession session,
            ChatArtifact artifact,
            ResourceReadRequest request,
            ChatArtifactResourceProvider.ChatArtifactAddress address)
        {
            var member = FindRequiredMember(session, artifact, address);
            ResourceReadCursor.ValidatePinned(request, RevisionText(member));
            var representation = request == null ? null : request.Representation;
            var maxChars = request == null ? 0 : request.MaxChars;
            representation = NormalizeRepresentation(representation, member);
            if (representation == ResourceRepresentations.Metadata)
            {
                ResourceReadCursor.RejectCursor(request);
                return new ResourceReadSelection
                {
                    Result = new ResourceReadResult
                    {
                        Resource = Describe(member),
                        Representation = ResourceRepresentations.Metadata,
                        Complete = true
                    },
                    ResourceRefs = new[] { new ResourceRef(request.Reference.Uri, RevisionText(member)) }
                };
            }
            var offset = ResourceReadCursor.ParseImmutable(request);
            return ReadText(member, representation, offset, maxChars);
        }

        public ResourceReadSelection ReadStructure(ChatSession session, ChatArtifact artifact, int offset, int maxChars)
        {
            var snapshot = LoadSnapshot(session, artifact);
            if (snapshot == null)
            {
                throw ChatArtifactResourceProvider.ResourceError(
                    "resource_corrupt",
                    "The HTML workspace revision body is unavailable or invalid.",
                    false,
                    "Select another healthy revision before reading its structure.");
            }
            var content = JsonConvert.SerializeObject(new
            {
                type = "rnassistant.htmlWorkspaceManifest",
                revision = ChatResourceUri.CreateArtifactRevisionUri(session, artifact),
                activeFileId = snapshot.ActiveFileId,
                resources = Members(session, artifact).Select(Describe).ToList()
            });
            return ReadText(new HtmlMember
            {
                Session = session,
                Artifact = artifact,
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Title = artifact.Title,
                Content = content,
                ContentType = "application/json"
            }, ResourceRepresentations.Structure, offset, maxChars);
        }

        private ResourceReadSelection ReadText(HtmlMember member, string representation, int offset, int maxChars)
        {
            offset = Math.Max(0, offset);
            maxChars = Math.Max(128, Math.Min(MaximumReadCharacters, maxChars <= 0 ? 8000 : maxChars));
            if (offset > member.Content.Length)
            {
                throw new InvalidOperationException("Resource read offset exceeds the representation length.");
            }
            var length = Math.Min(maxChars, member.Content.Length - offset);
            var next = offset + length;
            var uri = member.MemberType == null
                ? ChatResourceUri.CreateArtifactRevisionUri(member.Session, member.Artifact)
                : CreateUri(member);
            return new ResourceReadSelection
            {
                Result = new ResourceReadResult
                {
                    Resource = member.MemberType == null ? null : Describe(member),
                    Representation = representation,
                    Text = member.Content.Substring(offset, length),
                    ContentSha256 = TextPatternEngine.Sha256(member.Content),
                    Offset = offset,
                    ReturnedCharacters = length,
                    TotalCharacters = member.Content.Length,
                    NextCursor = next < member.Content.Length
                        ? ResourceReadCursor.CreateImmutable(next)
                        : null,
                    Complete = next >= member.Content.Length,
                    Truncated = next < member.Content.Length,
                    RawContentIncluded = true
                },
                ResourceRefs = new[] { new ResourceRef(uri, RevisionText(member)) }
            };
        }

        private static string NormalizeRepresentation(string value, HtmlMember member)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value == "auto")
            {
                return member.MemberType == "file" ? ResourceRepresentations.Source : ResourceRepresentations.Text;
            }
            if (value == ResourceRepresentations.Metadata) return value;
            if (member.MemberType == "file" && value == ResourceRepresentations.Source) return value;
            if (member.MemberType == "data" && value == ResourceRepresentations.Text) return value;
            throw new InvalidOperationException("Resource representation is unavailable: " + value + ".");
        }
    }
}
