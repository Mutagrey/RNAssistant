using System;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
    internal sealed class OfficeDocumentGuardException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public OfficeDocumentGuardException(ToolResult mismatch)
            : base(mismatch == null ? "The active Office document could not be verified." : mismatch.Message)
        {
            ErrorCode = mismatch == null || string.IsNullOrWhiteSpace(mismatch.ErrorCode)
                ? "document_identity_unavailable"
                : mismatch.ErrorCode;
            Retryable = mismatch != null && mismatch.Retryable == true;
        }
    }

    internal sealed class OfficeDocumentExecutionExpectation
    {
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string RuntimeDocumentKey { get; set; }
    }

    internal sealed class OfficeDocumentExecutionGuardState
    {
        private readonly AsyncLocal<OfficeDocumentExecutionExpectation> _current =
            new AsyncLocal<OfficeDocumentExecutionExpectation>();

        public OfficeDocumentExecutionExpectation Current { get { return _current.Value; } }

        public IDisposable Begin(string host, string documentKey, string runtimeDocumentKey)
        {
            var previous = _current.Value;
            _current.Value = new OfficeDocumentExecutionExpectation
            {
                Host = host ?? string.Empty,
                DocumentKey = documentKey ?? string.Empty,
                RuntimeDocumentKey = runtimeDocumentKey ?? string.Empty
            };
            return new Scope(delegate { _current.Value = previous; });
        }

        public static ToolResult Validate(
            IOfficeApplicationAdapter adapter,
            OfficeDocumentExecutionExpectation expectation)
        {
            if (adapter == null || expectation == null) return null;
            try
            {
                var hostMatches = string.Equals(
                    expectation.Host,
                    adapter.HostName,
                    StringComparison.OrdinalIgnoreCase);
                var documentMatches = hostMatches && IdentityMatches(
                    expectation.DocumentKey,
                    string.Empty,
                    adapter.DocumentKey,
                    string.Empty);
                var identityMatches = documentMatches || hostMatches && IdentityMatches(
                    string.Empty,
                    expectation.RuntimeDocumentKey,
                    string.Empty,
                    adapter.RuntimeDocumentKey);
                if (hostMatches && identityMatches) return null;
                return ToolResult.Fail(
                    "The Office document bound to this chat is closed or no longer matches the tool target. " +
                    "No Office action was started. Open that document before retrying Office tools; " +
                    "non-Office tools can continue in this chat.",
                    null,
                    "active_document_changed",
                    false);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    "RNAssistant could not verify the active Office document before tool execution: " + ex.Message,
                    null,
                    "document_identity_unavailable",
                    false);
            }
        }

        public static void ThrowIfMismatch(
            IOfficeApplicationAdapter adapter,
            OfficeDocumentExecutionExpectation expectation)
        {
            var mismatch = Validate(adapter, expectation);
            if (mismatch != null) throw new OfficeDocumentGuardException(mismatch);
        }

        internal static bool IdentityMatches(
            string expectedDocumentKey,
            string expectedRuntimeDocumentKey,
            string actualDocumentKey,
            string actualRuntimeDocumentKey)
        {
            var documentMatches = !string.IsNullOrWhiteSpace(expectedDocumentKey) &&
                !string.IsNullOrWhiteSpace(actualDocumentKey) &&
                string.Equals(expectedDocumentKey, actualDocumentKey, StringComparison.OrdinalIgnoreCase);
            var runtimeMatches = !string.IsNullOrWhiteSpace(expectedRuntimeDocumentKey) &&
                !string.IsNullOrWhiteSpace(actualRuntimeDocumentKey) &&
                string.Equals(expectedRuntimeDocumentKey, actualRuntimeDocumentKey, StringComparison.OrdinalIgnoreCase);
            return documentMatches || runtimeMatches;
        }

        internal static bool SessionMatchesAdapter(IOfficeApplicationAdapter adapter, ChatSession session)
        {
            if (adapter == null || session == null) return false;
            try
            {
                return string.Equals(session.Host, adapter.HostName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(session.DocumentKey) &&
                    string.Equals(session.DocumentKey, adapter.DocumentKey, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action _dispose;
            private int _disposed;

            public Scope(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0 && _dispose != null) _dispose();
            }
        }
    }
}
