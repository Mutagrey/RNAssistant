using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class HtmlProjectSnapshotV1
    {
        public int ProtocolVersion { get; set; } = 1;
        public string WorkspaceId { get; set; }
        public string EntryPath { get; set; }
        public string DefinitionFingerprint { get; set; }
        public IReadOnlyList<HtmlProjectSnapshotMember> Members { get; set; }
    }

    public sealed class HtmlProjectSnapshotMember
    {
        public string Path { get; set; }
        public ResourceRef ExactRef { get; set; }
        public string Sha256 { get; set; }
        public long ByteLength { get; set; }
        public string Mime { get; set; }
    }
}
