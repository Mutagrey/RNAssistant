using System;
using System.Collections.Generic;
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
            var offset = ParseCursor(cursor);
            var members = ActiveMembers(session)
                .Where(item => string.IsNullOrWhiteSpace(kind) ||
                    string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Active)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = members.Skip(offset).Take(limit).Select(Describe).ToList();
            var next = offset + selected.Count;
            return new ResourceListPage
            {
                Items = selected,
                Total = members.Count,
                Cursor = offset.ToString(),
                NextCursor = next < members.Count ? next.ToString() : null,
                Truncated = next < members.Count
            };
        }

        public bool TryResolve(ChatSession session, string resourceUri, out ResourceDescriptor descriptor)
        {
            HtmlMember member;
            if (!TryFind(session, resourceUri, out member))
            {
                descriptor = null;
                return false;
            }
            descriptor = Describe(member);
            return true;
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

        private bool TryFind(ChatSession session, string resourceUri, out HtmlMember member)
        {
            member = null;
            ResourceAddress address;
            if (!ResourceUri.TryParse(resourceUri, out address) ||
                !string.Equals(address.Provider, ChatArtifactResourceProvider.ProviderName, StringComparison.Ordinal) ||
                address.Segments.Count != 8 || session == null ||
                !string.Equals(address.Segments[0], session.Id, StringComparison.Ordinal) ||
                !string.Equals(address.Segments[1], "artifact", StringComparison.Ordinal) ||
                !string.Equals(address.Segments[3], "revision", StringComparison.Ordinal) ||
                !string.Equals(address.Segments[5], "member", StringComparison.Ordinal)) return false;
            int revision;
            if (!int.TryParse(address.Segments[4], out revision) || revision < 1) return false;
            var artifact = FindArtifact(session, address.Segments[2]);
            if (artifact == null || Math.Max(1, artifact.Revision) != revision) return false;
            member = Members(session, artifact).FirstOrDefault(item =>
                string.Equals(item.MemberType, address.Segments[6], StringComparison.Ordinal) &&
                string.Equals(item.MemberKey, address.Segments[7], StringComparison.Ordinal));
            return member != null;
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
                    Math.Max(1, member.Artifact.Revision).ToString())
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
            return new ResourceRef(CreateUri(member), Math.Max(1, member.Artifact.Revision).ToString());
        }

        private static string CreateUri(HtmlMember member)
        {
            return ResourceUri.Create(
                ChatArtifactResourceProvider.ProviderName,
                member.Session.Id,
                "artifact",
                member.Artifact.Id,
                "revision",
                Math.Max(1, member.Artifact.Revision).ToString(),
                "member",
                member.MemberType,
                member.MemberKey);
        }

        private static ChatArtifact FindArtifact(ChatSession session, string artifactId)
        {
            return (session == null ? null : session.Artifacts ?? new List<ChatArtifact>())
                .FirstOrDefault(item => item != null &&
                    string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
        }

        private static string MemberKey(string type, string id)
        {
            return TextPatternEngine.Sha256((type ?? string.Empty) + "\n" + (id ?? string.Empty));
        }

        private static string MimeType(string kind)
        {
            if (string.Equals(kind, "html", StringComparison.OrdinalIgnoreCase)) return "text/html";
            if (string.Equals(kind, "css", StringComparison.OrdinalIgnoreCase)) return "text/css";
            if (string.Equals(kind, "script", StringComparison.OrdinalIgnoreCase)) return "application/javascript";
            return "text/plain";
        }

        private static int ParseCursor(string cursor)
        {
            int offset;
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            if (!int.TryParse(cursor, out offset) || offset < 0)
            {
                throw new InvalidOperationException("HTML resource cursor is invalid.");
            }
            return offset;
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
