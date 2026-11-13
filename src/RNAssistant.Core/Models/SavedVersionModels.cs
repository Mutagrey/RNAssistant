using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    public enum SavedVersionActorKind { User, Run, Legacy }
    public sealed class SavedVersionActor
    {
        public SavedVersionActorKind Kind { get; set; }
        public string ChatId { get; set; }
        public string RunId { get; set; }
    }

    // Metadata records of the existing plugin/artifact owner. Content remains in
    // its immutable release/revision; a catalog entry is never its byte identity.
    public sealed class SavedVersionRecord
    {
        public string VersionId { get; set; }
        public long Ordinal { get; set; }
        public string Label { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string ContentFingerprint { get; set; }
        public ResourceRef ContentReference { get; set; }
        public string ManifestVersion { get; set; }
        public SavedVersionActor Actor { get; set; }
        [JsonIgnore] public bool Removed { get; internal set; }
    }

    public sealed class SavedVersionCatalogState
    {
        public long LastOrdinal { get; set; }
        public string LatestAvailableVersionId { get; set; }
        public string LastSavedFingerprint { get; set; }
        public ResourceRef MigrationCutoff { get; set; }
        public bool MigrationComplete { get; set; }
        [JsonIgnore] public string StateRevision { get; internal set; }
    }

    public sealed class SavedVersionPage
    {
        public IReadOnlyList<SavedVersionRecord> Items { get; internal set; }
        public string Cursor { get; internal set; }
        public string StateRevision { get; internal set; }
    }

    public sealed class SavedVersionOperation
    {
        public string OperationId { get; set; }
        public string Intent { get; set; }
        public string Outcome { get; set; }
        public string VersionId { get; set; }
        [JsonIgnore] public SavedVersionRecord Version { get; internal set; }
    }
}
