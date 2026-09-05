using System;
using RNAssistant.Core.Models;
using RNAssistant.Office.Runtime;

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
            var provider = _adapter as IOfficeDocumentSessionProvider;
            var bound = provider == null ? null : provider.DocumentSession;
            if (bound != null)
            {
                if (bound.StaDispatcher == null)
                    throw new ResourceRequestException("The bound document has no owner STA.", "document_session_unavailable", false);
                return DocumentAccessGate.Invoke(bound.StaDispatcher, () => ReadExpected(expectation, action));
            }
            return ReadExpected(expectation, action);
        }

        private T ReadExpected<T>(OfficeDocumentExecutionExpectation expectation, Func<T> action)
        {
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
                if (string.IsNullOrWhiteSpace(session.DocumentAuthorityId))
                    throw new ResourceRequestException("Document authority is not bound.", "RESOURCE_AUTHORITY_NOT_READY", false);
                return session.DocumentAuthorityId;
            });
        }

        public bool MatchesDocumentToken(ChatSession session, string token)
        {
            return Read(session, delegate
            {
                return !string.IsNullOrWhiteSpace(session.DocumentAuthorityId) &&
                    string.Equals(token, session.DocumentAuthorityId, StringComparison.Ordinal);
            });
        }
    }
}
