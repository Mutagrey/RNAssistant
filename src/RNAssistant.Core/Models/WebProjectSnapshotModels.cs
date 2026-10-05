using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    public sealed class WebSnapshotFile
    {
        public string Path { get; private set; }
        public ResourceRef Reference { get; private set; }
        public PayloadRef Payload { get; private set; }

        [JsonConstructor]
        public WebSnapshotFile(string path, ResourceRef reference, PayloadRef payload)
        { Path = path; Reference = reference; Payload = payload; }

        internal bool Matches(PayloadRef payload)
        { return Payload != null && payload != null && Payload.Sha256 == payload.Sha256 &&
            Payload.ByteLength == payload.ByteLength && Payload.Encryption == payload.Encryption &&
            Payload.ProtectionKeyId == payload.ProtectionKeyId; }
    }

    // Only the publication owner constructs this runtime handle after checking the
    // workspace and retained manifest. It authorizes historical reads, not writes.
    public sealed class RetainedWebSnapshot
    {
        public string Id { get; private set; }
        public string WorkspaceId { get; private set; }
        public string EntryPath { get; private set; }
        public string SnapshotSha256 { get; private set; }
        public ResourceRef Reference { get; private set; }
        public PayloadRef Payload { get; private set; }
        public IReadOnlyList<WebSnapshotFile> Files { get; private set; }

        internal RetainedWebSnapshot(string id, string workspaceId, string entryPath,
            string snapshotSha256, ResourceRef reference, PayloadRef payload, IEnumerable<WebSnapshotFile> files)
        {
            Id = id; WorkspaceId = workspaceId; EntryPath = entryPath;
            SnapshotSha256 = snapshotSha256; Reference = reference; Payload = payload;
            Files = Array.AsReadOnly(files.ToArray());
        }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum WebVerificationState { Pending, Passed, Failed, NotRun }

    public sealed class WebVerificationOrigin
    {
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string ToolCallId { get; set; }
    }

    // A projection of the latest append-only resource revision, never a second store.
    public sealed class WebVerificationRecord
    {
        [JsonIgnore] public ResourceRef Reference { get; internal set; }
        [JsonProperty(Required = Required.Always)] public int SchemaVersion { get; set; } = 1;
        [JsonProperty(Required = Required.Always)] public string Id { get; set; }
        [JsonProperty(Required = Required.Always)] public string WorkspaceId { get; set; }
        [JsonProperty(Required = Required.Always)] public string EntryPath { get; set; }
        public string RequestedSnapshotId { get; set; }
        public string SnapshotId { get; set; }
        [JsonProperty(Required = Required.Always)] public bool Historical { get; set; }
        [JsonProperty(Required = Required.Always)] public WebVerificationState State { get; set; }
        [JsonProperty(Required = Required.Always)] public DateTime StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public string SnapshotSha256 { get; set; }
        public string Browser { get; set; }
        public IReadOnlyList<string> CheckedFiles { get; set; } = new string[0];
        public IReadOnlyList<string> Errors { get; set; } = new string[0];
        public IReadOnlyList<string> Hints { get; set; } = new string[0];
        public WebVerificationOrigin Origin { get; set; }
    }

    public sealed class WebVerificationPage
    {
        public IReadOnlyList<WebVerificationRecord> Items { get; internal set; }
        public long Generation { get; internal set; }
        public int Total { get; internal set; }
        public int? NextOffset { get; internal set; }
    }
}
