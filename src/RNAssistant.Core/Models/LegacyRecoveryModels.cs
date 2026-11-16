using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class LegacyChatRecoveryPage
    {
        public string RootPath { get; set; }
        public int Total { get; set; }
        public int? NextOffset { get; set; }
        public IReadOnlyList<LegacyChatRecoveryItem> Items { get; set; }
    }
    public sealed class LegacyChatRecoveryItem
    {
        public string SourceId { get; set; }
        public string ChatId { get; set; }
        public string Title { get; set; }
        public string Host { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public long Revision { get; set; }
        public int MessageCount { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public bool CanImport { get; set; }
        public string ImportError { get; set; }
    }
    public sealed class LegacyChatTranscriptPage
    {
        public string SourceId { get; set; }
        public string ChatId { get; set; }
        public string Title { get; set; }
        public long Revision { get; set; }
        public int MessageStartIndex { get; set; }
        public int TotalMessages { get; set; }
        public int? NextMessageIndex { get; set; }
        public int NextCharacterOffset { get; set; }
        public string Text { get; set; }
    }
    public sealed class LegacyChatImportResult
    {
        public string Status { get; set; }
        public string SourceId { get; set; }
        public long SourceRevision { get; set; }
        public string ChatId { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string WorkspaceId { get; set; }
        public string DocumentAuthorityId { get; set; }
        public int MessageCount { get; set; }
        public long Revision { get; set; }
        public bool TargetDeleted { get; set; }
    }
    // Presentation provenance. It is never an accepted call, input or run fact.
    public sealed class LegacyChatHistoryProvenance
    {
        public string SourceId { get; set; }
        public long SourceRevision { get; set; }
    }
    public sealed class LegacyChatImportException : System.InvalidOperationException
    {
        public string Code { get; private set; }
        public LegacyChatImportException(string code, string message) : base(message) { Code = code; }
    }
    public sealed class LegacyPluginRecoveryPage
    {
        public string RootPath { get; set; }
        public int Total { get; set; }
        public int? NextOffset { get; set; }
        public IReadOnlyList<LegacyPluginRecoveryItem> Items { get; set; }
    }
    public sealed class LegacyPluginRecoveryItem
    {
        public string PluginId { get; set; }
        public string RegistrationRevision { get; set; }
        public string Name { get; set; }
        public string SourceRoot { get; set; }
        public string SourceWorkspaceId { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public bool CanConnect { get; set; }
        public bool CanImport { get; set; }
    }
    public sealed class LegacyPluginImportResult
    {
        public PluginRegistration Registration { get; set; }
        public string RegistrationRevision { get; set; }
        public bool AlreadyImported { get; set; }
        public int SavedVersions { get; set; }
        public int Releases { get; set; }
    }
}
