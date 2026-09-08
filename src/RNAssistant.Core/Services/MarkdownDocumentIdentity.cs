using System;
using System.Text.RegularExpressions;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class MarkdownDocumentIdentity
    {
        public static string LogicalId(string snapshotId)
        {
            var match = Regex.Match(snapshotId ?? "", @"^(artifact_md_[0-9a-f]{64})_r[1-9][0-9]*_[0-9a-f]{8}$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return match.Success ? match.Groups[1].Value : null;
        }

        public static ResourceIdentity Identity(ChatSession session, string logicalId)
        {
            if (string.IsNullOrWhiteSpace(session?.DocumentAuthorityId) ||
                !Regex.IsMatch(logicalId ?? "", @"^artifact_md_[0-9a-f]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new InvalidOperationException("A document-owned Markdown identity is required.");
            return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, logicalId));
        }
    }
}
