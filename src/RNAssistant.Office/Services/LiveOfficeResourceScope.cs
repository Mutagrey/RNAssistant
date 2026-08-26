using System;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class LiveOfficeResourceScope
    {
        private readonly IOfficeApplicationAdapter _adapter;

        public LiveOfficeResourceScope(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException("adapter");
        }

        public T Read<T>(ChatSession session, Func<T> action)
        {
            if (session == null || action == null)
            {
                throw new ResourceRequestException(
                    "A live Office resource requires an active chat session.",
                    "resource_session_required",
                    false);
            }
            var expectation = new OfficeDocumentExecutionExpectation
            {
                Host = session.Host ?? string.Empty,
                DocumentKey = session.DocumentKey ?? string.Empty,
                RuntimeDocumentKey = session.LastRun == null
                    ? string.Empty
                    : session.LastRun.DocumentRuntimeKey ?? string.Empty
            };
            var mismatch = OfficeDocumentExecutionGuardState.Validate(_adapter, expectation);
            if (mismatch != null)
            {
                throw new ResourceRequestException(
                    mismatch.Message,
                    mismatch.ErrorCode ?? "active_document_changed",
                    mismatch.Retryable == true);
            }
            var guard = _adapter as IOfficeDocumentExecutionGuard;
            using (guard == null
                ? null
                : guard.BeginExpectedDocument(
                    expectation.Host,
                    expectation.DocumentKey,
                    expectation.RuntimeDocumentKey))
            {
                try
                {
                    return action();
                }
                catch (OfficeDocumentGuardException ex)
                {
                    throw new ResourceRequestException(ex.Message, ex.ErrorCode, ex.Retryable);
                }
            }
        }

        public string DocumentToken(ChatSession session)
        {
            return Read(session, delegate
            {
                return RNAssistant.Core.Tools.TextPatternEngine.Sha256(
                    (_adapter.HostName ?? string.Empty).ToLowerInvariant() + "\n" +
                    (_adapter.DocumentKey ?? string.Empty).ToLowerInvariant());
            });
        }
    }
}
