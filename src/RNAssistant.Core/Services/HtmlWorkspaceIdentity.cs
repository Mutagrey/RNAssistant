using System;
using System.Text.RegularExpressions;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class HtmlWorkspaceIdentity
    {
        public static string LogicalId(string snapshotId)
        {
            var match = Regex.Match(snapshotId ?? "", @"^(html_ws_[0-9a-f]{64})_r([1-9][0-9]*)_[0-9a-f]{8}$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return match.Success ? match.Groups[1].Value : null;
        }

        public static ResourceIdentity Identity(ChatSession session, string logicalId)
        {
            if (string.IsNullOrWhiteSpace(session?.DocumentAuthorityId) ||
                !Regex.IsMatch(logicalId ?? "", @"^html_ws_[0-9a-f]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new InvalidOperationException("A document HTML workspace identity is required.");
            return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, logicalId));
        }

        public static ResourceIdentity MemberIdentity(ChatSession session, string logicalId, string memberKey)
        {
            if (string.IsNullOrWhiteSpace(memberKey) || memberKey.Length != 64)
                throw new InvalidOperationException("An exact HTML member identity is required.");
            Identity(session, logicalId);
            return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId,
                logicalId, "member", memberKey));
        }

        public static bool IsMemberIdentity(ResourceIdentity identity)
        {
            ResourceAddress address;
            return identity != null && ResourceUri.TryParse(identity.Uri, out address) &&
                address.Provider == "state" && address.Segments.Count == 5 &&
                address.Segments[0] == "document" &&
                Regex.IsMatch(address.Segments[2], @"^html_ws_[0-9a-f]{64}$",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
                address.Segments[3] == "member";
        }
    }
}
