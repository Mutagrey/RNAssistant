using System;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
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
                var identityMatches = !string.IsNullOrWhiteSpace(expectation.RuntimeDocumentKey)
                    ? string.Equals(
                        expectation.RuntimeDocumentKey,
                        adapter.RuntimeDocumentKey,
                        StringComparison.OrdinalIgnoreCase)
                    : string.Equals(
                        expectation.DocumentKey,
                        adapter.DocumentKey,
                        StringComparison.OrdinalIgnoreCase);
                if (hostMatches && identityMatches) return null;
                return ToolResult.Fail(
                    "The active Office document changed before tool execution. No action was started; return to the original document and make a new request.",
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
