using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        public LegacyChatImportResult ImportLegacyRecoveryChat(string sourceId, long expectedRevision,
            IConversationStore destination, DocumentAuthorityRegistry identities,
            CancellationToken token = default(CancellationToken))
        {
            var router = destination as ConversationJournalRouter;
            if (router == null || identities == null || expectedRevision < 1 ||
                !string.Equals(Path.GetFullPath(router.RootPath), Path.GetFullPath(_paths.Root), StringComparison.OrdinalIgnoreCase))
                throw LegacyImportFailure("identity_invalid", "Перенос требует текущего хранилища и точной исходной ревизии.");
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                ChatSession source; Newtonsoft.Json.Linq.JObject snapshot;
                try
                {
                    var saved = ReadLegacyRecovery(ResolveRecoverySource(sourceId), true, token);
                    if (saved.Item1.Revision != expectedRevision)
                        throw LegacyImportFailure("source_changed", "Старая история изменилась. Обновите список перед переносом.");
                    snapshot = saved.Item2;
                    source = snapshot.ToObject<ChatSession>();
                    if (source == null || !ChatSession.IsReadableFormat(source.FormatVersion) || source.Id != saved.Item1.Id ||
                        source.Messages == null || source.Messages.Any(message => message == null) || source.Messages.Count > 200000)
                        throw new InvalidDataException("Неподдерживаемый или неполный снимок истории.");
                    // The header count is the visible transcript count. Legacy
                    // protocol/tool/runtime rows are retained in the snapshot but
                    // intentionally excluded from that count.
                    var visibleMessages = source.Messages.Count(message => !message.ProtocolMessage);
                    if (visibleMessages != saved.Item1.MessageCount)
                        throw new InvalidDataException("Неполный снимок истории: видимых сообщений " + visibleMessages +
                            ", в заголовке " + saved.Item1.MessageCount + ".");
                    source.Artifacts = source.Artifacts ?? new System.Collections.Generic.List<ChatArtifact>();
                    source.ArtifactLinks = source.ArtifactLinks ?? new System.Collections.Generic.List<ChatArtifactLink>();
                    var receipt = router.ReadLegacyImport(source.Id, sourceId, expectedRevision, token);
                    if (receipt != null) return receipt;
                    ResolveLegacyImportIdentity(source, identities);
                }
                catch (LegacyChatImportException) { throw; }
                catch (Exception error) when (LegacyRecoveryFiles.ReadFailure(error) || error is NotSupportedException)
                { throw LegacyImportFailure("unavailable", "История не перенесена: " + LegacyRecoveryFiles.Error(error)); }
                // Legacy reading is finished. Only existing native owners write below.
                return router.ImportLegacyChat(source, snapshot, sourceId, expectedRevision, token);
            }
        }

        internal static LegacyChatImportException LegacyImportFailure(string code, string message)
        { return new LegacyChatImportException("legacy_chat_import_" + code, message); }

        private void ResolveLegacyImportIdentity(ChatSession source, DocumentAuthorityRegistry identities)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Host) || string.IsNullOrWhiteSpace(source.DocumentKey))
                throw LegacyImportFailure("identity_invalid", "В старой истории отсутствует точная идентичность чата.");
            if (source.Host == "Workspace")
            {
                if (source.WorkspaceId != source.DocumentKey || source.DocumentAuthorityId != null ||
                    new WorkspaceStore(_paths).OpenConnectedHistory(source.WorkspaceId).WorkspaceId != source.WorkspaceId)
                    throw LegacyImportFailure("identity_invalid", "Исходный workspace не подтверждён сохранённым подключением.");
                return;
            }
            if (source.WorkspaceId != null)
                throw LegacyImportFailure("identity_invalid", "Неоднозначная область старого чата.");
            // Read the existing identity owner; never bind to whichever document
            // happens to be active while the user opens recovery.
            using (identities.AcquireBackupLease())
            {
                identities.ValidateBackupState();
                var entries = identities.List().Where(item => string.Equals(item.Host, source.Host, StringComparison.OrdinalIgnoreCase));
                if (source.DocumentAuthorityId != null) entries = entries.Where(item => item.AuthorityId == source.DocumentAuthorityId);
                else if (!string.IsNullOrWhiteSpace(source.DocumentPath))
                    entries = entries.Where(item => string.Equals(item.Locator, Path.GetFullPath(source.DocumentPath), StringComparison.OrdinalIgnoreCase));
                else throw LegacyImportFailure("identity_invalid", "У старого чата нет подтверждённой области документа.");
                var selected = entries.Take(2).ToArray();
                if (selected.Length != 1)
                    throw LegacyImportFailure("identity_invalid", "Область старого документа не найдена или неоднозначна.");
                source.DocumentAuthorityId = selected[0].AuthorityId;
            }
        }
    }
}
