using System;
using System.Globalization;
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

        public static string SnapshotId(string logicalId, int revision)
        { return logicalId + "_r" + revision.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8); }

        public static ResourceIdentity Identity(ChatSession session, string logicalId)
        {
            if (string.IsNullOrWhiteSpace(session?.DocumentAuthorityId) ||
                !Regex.IsMatch(logicalId ?? "", @"^html_ws_[0-9a-f]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new InvalidOperationException("A document HTML workspace identity is required.");
            return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, logicalId));
        }
    }
}
