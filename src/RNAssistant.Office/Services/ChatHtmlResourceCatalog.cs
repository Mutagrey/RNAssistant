using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ChatHtmlResourceCatalog
    {
        public const string FileKind = "html-file";
        public const string DataKind = "html-data";
        private const int MaximumItems = 50;
        private const int MaximumReadCharacters = 32000;
        private const int MaximumSearchCharacters = 1000000;
        private const int MaximumSearchCharactersPerMember = 128000;

        private readonly Func<ChatSession, string, bool> _loadArtifactBody;

        public ChatHtmlResourceCatalog(Func<ChatSession, string, bool> loadArtifactBody)
        {
            _loadArtifactBody = loadArtifactBody;
        }

        public static bool SupportsKind(string kind)
        {
            return string.Equals(kind, FileKind, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, DataKind, StringComparison.OrdinalIgnoreCase);
        }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            limit = Math.Max(1, Math.Min(MaximumItems, limit <= 0 ? 20 : limit));
            var members = ActiveMembers(session)
                .Where(item => string.IsNullOrWhiteSpace(kind) ||
                    string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Active)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var descriptors = members.Select(Describe).ToList();
            var position = ResourceReadCursor.ParseRevisionBound(cursor);
            var collectionRevision = ResourceReadCursor.CollectionRevision(descriptors);
            ResourceReadCursor.ValidateContinuation(position, collectionRevision);
            ResourceReadCursor.ValidateCollectionOffset(position, descriptors.Count);
            var offset = position.Offset;
            var selected = descriptors.Skip(offset).Take(limit).ToList();
            var next = offset + selected.Count;
            return new ResourceListPage
            {
                Items = selected,
                Total = members.Count,
                Cursor = ResourceReadCursor.CreateRevisionBound(offset, collectionRevision),
                NextCursor = next < members.Count
                    ? ResourceReadCursor.CreateRevisionBound(next, collectionRevision)
                    : null,
                Truncated = next < members.Count
            };
        }

        public ResourceDescriptor ResolveMember(
            ChatSession session,
            ChatArtifact artifact,
            ChatArtifactResourceProvider.ChatArtifactAddress address)
        {
            return Describe(FindRequiredMember(session, artifact, address));
        }

        private HtmlMember FindRequiredMember(
            ChatSession session,
            ChatArtifact artifact,
            ChatArtifactResourceProvider.ChatArtifactAddress address)
        {
            if (address == null || !address.IsMember)
            {
                throw new ArgumentException("An exact member address is required.", "address");
            }
            var members = RequiredMembers(session, artifact);
            var member = members.FirstOrDefault(item =>
                string.Equals(item.MemberType, address.MemberType, StringComparison.Ordinal) &&
                string.Equals(item.MemberKey, address.MemberKey, StringComparison.Ordinal));
            if (member != null) return member;
            if (!IsCanonicalMemberKey(address.MemberKey))
            {
                throw ChatArtifactResourceProvider.ResourceError(
                    "noncanonical_member_uri",
                    "The member URI contains a human-readable path instead of the opaque canonical member key.",
                    false,
                    "Call common.resources_resolve with parentUri and memberPath, or copy a member URI from mutation/list output.");
            }
            throw ChatArtifactResourceProvider.ResourceError(
                "member_not_found",
                "The requested member does not exist in this artifact revision.",
                false,
                "Resolve the member path under the exact parent revision or list that member kind again.");
        }

        public ResourceDescriptor ResolveMemberPath(
            ChatSession session,
            ChatArtifact artifact,
            string memberPath,
            string memberType)
        {
            memberPath = (memberPath ?? string.Empty).Trim();
            memberType = (memberType ?? string.Empty).Trim().ToLowerInvariant();
            if (memberPath.Length == 0 ||
                memberType.Length > 0 && memberType != "file" && memberType != "data")
            {
                throw ChatArtifactResourceProvider.ResourceError(
                    "invalid_resource_uri",
                    "A valid memberPath and optional file/data memberType are required.",
                    false,
                    "Use the exact path/name shown in the artifact structure.");
            }
            var matches = RequiredMembers(session, artifact)
                .Where(item => (memberType.Length == 0 || string.Equals(
                        item.MemberType,
                        memberType,
                        StringComparison.Ordinal)) &&
                    string.Equals(item.Title, memberPath, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (matches.Count == 1) return Describe(matches[0]);
            if (matches.Count > 1)
            {
                throw ChatArtifactResourceProvider.ResourceError(
                    "member_ambiguous",
                    "More than one member has this path/name.",
                    false,
                    "Repeat resolution with memberType set to file or data.");
            }
            throw ChatArtifactResourceProvider.ResourceError(
                "member_not_found",
                "No member with this path/name exists in the artifact revision.",
                false,
                "Read the parent structure or list its member kind and use an exact returned path.");
        }

        internal IReadOnlyList<ResourceDescriptor> DescribeMembers(ChatSession session, ChatArtifact artifact)
        {
            return RequiredMembers(session, artifact).Select(Describe).ToList();
        }

        internal bool IsReadableRevision(ChatSession session, ChatArtifact artifact)
        {
            return artifact == null || !string.Equals(
                    artifact.Kind,
                    ChatArtifactKinds.HtmlWorkspace,
                    StringComparison.OrdinalIgnoreCase)
                ? true
                : LoadSnapshot(session, artifact) != null;
        }

        internal void ValidateRevision(
            ChatSession session,
            ChatArtifact artifact)
        {
            if (IsReadableRevision(session, artifact)) return;
            throw ChatArtifactResourceProvider.ResourceError(
                "resource_corrupt",
                "The HTML workspace revision body is unavailable or invalid.",
                false,
                "Select another healthy revision; do not reconstruct member URIs manually.");
        }

        private IEnumerable<HtmlMember> ActiveMembers(ChatSession session)
        {
            var artifact = FindArtifact(session, session == null ? null : session.ActiveHtmlArtifactId);
            return Members(session, artifact);
        }

        private IEnumerable<HtmlMember> Members(ChatSession session, ChatArtifact artifact)
        {
            var snapshot = LoadSnapshot(session, artifact);
            if (snapshot == null) return new HtmlMember[0];
            return Members(session, artifact, snapshot);
        }

        private static IEnumerable<HtmlMember> Members(
            ChatSession session,
            ChatArtifact artifact,
            HtmlWorkspaceSnapshot snapshot)
        {
            var members = new List<HtmlMember>();
            members.AddRange((snapshot.Files ?? new List<HtmlWorkspaceFile>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => new HtmlMember
                {
                    Session = session,
                    Artifact = artifact,
                    MemberType = "file",
                    MemberKey = MemberKey("file", item.Id),
                    Kind = FileKind,
                    Title = item.Path ?? item.Id,
                    ContentType = MimeType(item.Kind),
                    Content = item.Content ?? string.Empty,
                    Active = string.Equals(item.Id, snapshot.ActiveFileId, StringComparison.OrdinalIgnoreCase),
                    CreatedUtc = item.CreatedUtc,
                    UpdatedUtc = item.UpdatedUtc
                }));
            members.AddRange((snapshot.DataSources ?? new List<HtmlWorkspaceDataSource>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => new HtmlMember
                {
                    Session = session,
                    Artifact = artifact,
                    MemberType = "data",
                    MemberKey = MemberKey("data", item.Id),
                    Kind = DataKind,
                    Title = item.Name ?? item.Id,
                    ContentType = "application/json",
                    Content = item.Json ?? "{}",
                    CreatedUtc = item.CreatedUtc,
                    UpdatedUtc = item.UpdatedUtc,
                    Binding = item.Binding
                }));
            return members;
        }

        private IReadOnlyList<HtmlMember> RequiredMembers(
            ChatSession session,
            ChatArtifact artifact)
        {
            if (artifact == null || !string.Equals(
                artifact.Kind,
                ChatArtifactKinds.HtmlWorkspace,
                StringComparison.OrdinalIgnoreCase))
            {
                throw ChatArtifactResourceProvider.ResourceError(
                    "member_not_found",
                    "This artifact revision has no HTML workspace members.",
                    false,
                    "Resolve the artifact root and use one of its advertised representations.");
            }
            var snapshot = LoadSnapshot(session, artifact);
            if (snapshot == null)
            {
                throw ChatArtifactResourceProvider.ResourceError(
                    "resource_corrupt",
                    "The HTML workspace revision body is unavailable or invalid.",
                    false,
                    "Select another healthy revision; do not reconstruct member URIs manually.");
            }
            return Members(session, artifact, snapshot).ToList();
        }

        private HtmlWorkspaceSnapshot LoadSnapshot(ChatSession session, ChatArtifact artifact)
        {
            if (artifact == null ||
                !string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.IsNullOrWhiteSpace(artifact.InlineText) && _loadArtifactBody != null)
            {
                _loadArtifactBody(session, artifact.Id);
            }
            try
            {
                return string.IsNullOrWhiteSpace(artifact.InlineText)
                    ? null
                    : JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static ResourceDescriptor Describe(HtmlMember member)
        {
            var descriptor = new ResourceDescriptor
            {
                Reference = Reference(member),
                Provider = ChatArtifactResourceProvider.ProviderName,
                Kind = member.Kind,
                Title = member.Title ?? string.Empty,
                MimeType = member.ContentType,
                Mutable = false,
                ByteLength = Encoding.UTF8.GetByteCount(member.Content ?? string.Empty),
                CreatedUtc = member.CreatedUtc,
                ContentSha256 = TextPatternEngine.Sha256(member.Content ?? string.Empty),
                Parent = new ResourceRef(
                    ChatResourceUri.CreateArtifactRevisionUri(member.Session, member.Artifact),
                    RevisionText(member))
            };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(member.MemberType == "file"
                ? ResourceRepresentations.Source
                : ResourceRepresentations.Text);
            descriptor.Metadata["name"] = member.Title ?? string.Empty;
            descriptor.Metadata["revisionArtifactId"] = member.Artifact.Id;
            descriptor.Metadata["active"] = member.Active ? "true" : "false";
            if (member.Binding != null)
            {
                descriptor.Metadata["bindingStatus"] = member.Binding.Status ?? string.Empty;
                descriptor.Metadata["refreshPolicy"] = member.Binding.RefreshPolicy ?? string.Empty;
            }
            return descriptor;
        }

        private static ResourceRef Reference(HtmlMember member)
        {
            return new ResourceRef(CreateUri(member), RevisionText(member));
        }

        private static string CreateUri(HtmlMember member)
        {
            return ResourceUri.Create(
                ChatArtifactResourceProvider.ProviderName,
                member.Session.Id,
                "artifact",
                member.Artifact.Id,
                "revision",
                RevisionText(member),
                "member",
                member.MemberType,
                member.MemberKey);
        }

        private static ChatArtifact FindArtifact(ChatSession session, string artifactId)
        {
            var matches = (session == null ? null : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null &&
                    string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static string MemberKey(string type, string id)
        {
            return TextPatternEngine.Sha256((type ?? string.Empty) + "\n" + (id ?? string.Empty));
        }

        private static bool IsCanonicalMemberKey(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length == 64 &&
                value.All(character => character >= '0' && character <= '9' ||
                    character >= 'a' && character <= 'f');
        }

        private static string RevisionText(HtmlMember member)
        {
            return Math.Max(1, member.Artifact.Revision).ToString(CultureInfo.InvariantCulture);
        }

        private static string MimeType(string kind)
        {
            if (string.Equals(kind, "html", StringComparison.OrdinalIgnoreCase)) return "text/html";
            if (string.Equals(kind, "css", StringComparison.OrdinalIgnoreCase)) return "text/css";
            if (string.Equals(kind, "script", StringComparison.OrdinalIgnoreCase)) return "application/javascript";
            return "text/plain";
        }

        private sealed class HtmlMember
        {
            public ChatSession Session { get; set; }
            public ChatArtifact Artifact { get; set; }
            public string MemberType { get; set; }
            public string MemberKey { get; set; }
            public string Kind { get; set; }
            public string Title { get; set; }
            public string ContentType { get; set; }
            public string Content { get; set; }
            public bool Active { get; set; }
            public DateTime CreatedUtc { get; set; }
            public DateTime UpdatedUtc { get; set; }
            public HtmlWorkspaceDataBinding Binding { get; set; }
        }
    }
}
