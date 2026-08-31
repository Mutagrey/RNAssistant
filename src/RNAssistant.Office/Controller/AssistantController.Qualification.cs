using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Qualification;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public QualificationCatalogResponse GetQualificationCatalog(string chatId, string suite)
        {
            var session = LoadAddressedSession(chatId);
            var normalizedSuite = string.IsNullOrWhiteSpace(suite) ? "quick" : suite.Trim().ToLowerInvariant();
            return new QualificationCatalogResponse
            {
                SchemaVersion = 1,
                Host = session.Host,
                Suite = normalizedSuite,
                Packs = _qualification.List(session.Host, normalizedSuite)
                    .Select(QualificationPackDto.From).Where(item => item != null).ToArray(),
                MissingCoverage = _qualification.MissingCoverage(session.Host, normalizedSuite),
                BuildEvidence = QualificationBuildEvidenceDto.From(_qualification.BuildEvidence)
            };
        }

        public QualificationSessionResponse GetQualificationRun(string chatId, string runId)
        {
            var session = LoadAddressedSession(chatId);
            var run = _qualification.GetLatest(session, runId);
            return QualificationResponse(session, run);
        }

        public async Task<QualificationSessionResponse> StartQualificationAsync(
            string chatId,
            string packId,
            string previousRunId,
            CancellationToken cancellationToken)
        {
            var source = LoadAddressedSession(chatId);
            if (!_chatSessions.IsCurrentDocument(source))
                throw new InvalidOperationException("Откройте исходный документ перед запуском qualification.");
            var pack = _qualification.GetPack(packId);
            ChatSession session;
            using (_chatRuns.ReserveMaintenance())
            {
                session = _chatSessions.CreateChat("Qualification · " + pack.Title);
                _conversationStore.Save(session);
                _chatSessions.NotifySaved(session);
            }
            using (ReserveChatOperation(session))
            {
                session = ReloadReservedSession(session);
                var run = await _qualification.StartAsync(
                    session, pack.Id, previousRunId, cancellationToken).ConfigureAwait(false);
                return QualificationResponse(session, run);
            }
        }

        public async Task<QualificationSessionResponse> AdvanceQualificationAsync(
            string chatId,
            string runId,
            string stepId,
            bool acknowledged,
            bool cancel,
            string note,
            CancellationToken cancellationToken)
        {
            var session = LoadAddressedSession(chatId);
            using (ReserveChatOperation(session))
            {
                session = ReloadReservedSession(session);
                var manualInput = acknowledged ? new QualificationManualInput
                {
                    StepId = stepId,
                    Acknowledged = true,
                    Note = note
                } : null;
                var run = await _qualification.AdvanceAsync(
                    session, runId, manualInput, cancel, cancellationToken).ConfigureAwait(false);
                return QualificationResponse(session, run);
            }
        }

        private QualificationSessionResponse QualificationResponse(
            ChatSession session,
            QualificationRunState run)
        {
            return new QualificationSessionResponse
            {
                SchemaVersion = 1,
                Chat = ChatState(session),
                Run = QualificationRunDto.From(run)
            };
        }

        private void EnsureNotQualificationChat(ChatSession session)
        {
            if (_qualification.IsQualificationChat(session))
                throw new InvalidOperationException(
                    "Этот чат принадлежит Qualification Center. Продолжите проверку через встроенный экран или создайте обычный чат.");
        }
    }
}
