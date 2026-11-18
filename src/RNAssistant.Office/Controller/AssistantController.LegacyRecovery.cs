using System;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        // Explicit archive reads never enter the live conversation/router or replay a run.
        public LegacyChatRecoveryPageDto ListLegacyChats(LegacyRecoveryListRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return LegacyChatRecoveryPageDto.From(_legacyChatStore.ReadLegacyRecoveryChats(
                request.Offset, request.Limit, token));
        }

        public LegacyChatTranscriptPageDto ReadLegacyTranscript(LegacyTranscriptRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return LegacyChatTranscriptPageDto.From(_legacyChatStore.ReadLegacyRecoveryTranscript(
                request.SourceId, request.ExpectedRevision, request.MessageIndex,
                request.CharacterOffset, request.MaxCharacters, token));
        }

        public LegacyPluginRecoveryPageDto ListLegacyPlugins(LegacyRecoveryListRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return LegacyPluginRecoveryPageDto.From(new LegacyPluginRecoveryService(
                _paths, _settingsService.LoadStorageProtector).List(request.Offset, request.Limit, token));
        }

        public LegacyChatImportResultDto ImportLegacyChat(LegacyChatImportRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return LegacyChatImportResultDto.From(_legacyChatStore.ImportLegacyRecoveryChat(
                request.SourceId, request.ExpectedRevision, _conversationRouter, _documentAuthorityRegistry, token));
        }

        public LegacyChatImportResultDto GetLegacyChatImport(LegacyChatImportReadRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return LegacyChatImportResultDto.From(_conversationRouter.ReadLegacyImport(
                request.ChatId, request.SourceId, request.ExpectedRevision, token));
        }

        public LegacyChatMigrationStatusDto StartLegacyChatMigration()
        { return _legacyChatMigration.Start(); }

        public LegacyChatMigrationStatusDto GetLegacyChatMigrationStatus()
        { return _legacyChatMigration.Status(); }

        public LegacyChatMigrationStatusDto CancelLegacyChatMigration()
        { return _legacyChatMigration.Cancel(); }

        internal LegacyPluginRecoveryItem ResolveLegacyPluginSource(LegacyPluginReconnectRequest request,
            CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new LegacyPluginRecoveryService(_paths, _settingsService.LoadStorageProtector)
                .ResolveSource(request.PluginId, token);
        }
    }
}
