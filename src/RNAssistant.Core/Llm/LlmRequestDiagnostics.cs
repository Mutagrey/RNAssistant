using System;
using System.Diagnostics;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public static class LlmRequestDiagnosticPhases
    {
        public const string Preparing = "preparing";
        public const string Sending = "sending";
        public const string Headers = "headers";
        public const string FirstChunk = "first_chunk";
        public const string Completed = "completed";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";
    }

    public sealed class LlmRequestDiagnosticUpdate
    {
        public string RequestId { get; set; }
        public string Phase { get; set; }
        public string Model { get; set; }
        public bool StreamRequested { get; set; }
        public long ElapsedMs { get; set; }
        public long? PreparationMs { get; set; }
        public long? ResponseHeadersMs { get; set; }
        public long? FirstChunkMs { get; set; }
        public long? TotalMs { get; set; }
        public long? RequestBytes { get; set; }
        public int? StatusCode { get; set; }
        public LlmFailureKind? FailureKind { get; set; }
        public string Error { get; set; }
    }

    internal sealed class LlmRequestDiagnosticsTracker
    {
        private const int SlowRequestMs = 10000;
        private readonly object _sync = new object();
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly AppSettings _settings;
        private readonly Action<LlmRequestDiagnosticUpdate> _requestProgress;
        private readonly Action<LlmRequestDiagnosticUpdate> _globalProgress;
        private readonly Action<string> _debugLog;
        private long? _preparationMs;
        private long? _responseHeadersMs;
        private long? _firstChunkMs;
        private long? _requestBytes;
        private int? _statusCode;
        private int _firstChunkReported;
        private bool _terminal;

        public LlmRequestDiagnosticsTracker(
            AppSettings settings,
            Action<LlmRequestDiagnosticUpdate> requestProgress,
            Action<LlmRequestDiagnosticUpdate> globalProgress,
            Action<string> debugLog)
        {
            _settings = settings ?? new AppSettings();
            _requestProgress = requestProgress;
            _globalProgress = globalProgress;
            _debugLog = debugLog;
            RequestId = Guid.NewGuid().ToString("N").Substring(0, 12);
            Report(LlmRequestDiagnosticPhases.Preparing, null, null);
        }

        public string RequestId { get; private set; }

        public void Sending(long? requestBytes)
        {
            _requestBytes = requestBytes;
            Report(LlmRequestDiagnosticPhases.Sending, null, null);
        }

        public void Headers(int statusCode)
        {
            _statusCode = statusCode;
            Report(LlmRequestDiagnosticPhases.Headers, null, null);
        }

        public void FirstChunk()
        {
            if (Interlocked.Exchange(ref _firstChunkReported, 1) == 0)
            {
                Report(LlmRequestDiagnosticPhases.FirstChunk, null, null);
            }
        }

        public void Completed()
        {
            Report(LlmRequestDiagnosticPhases.Completed, null, null);
        }

        public void Failed(Exception error)
        {
            var requestError = error as LlmRequestException;
            Report(
                error is OperationCanceledException ? LlmRequestDiagnosticPhases.Cancelled : LlmRequestDiagnosticPhases.Failed,
                requestError == null ? (LlmFailureKind?)null : requestError.Kind,
                error == null ? null : error.Message);
        }

        private void Report(string phase, LlmFailureKind? failureKind, string error)
        {
            LlmRequestDiagnosticUpdate update;
            lock (_sync)
            {
                if (_terminal) return;
                var elapsedMs = _watch.ElapsedMilliseconds;
                if (phase == LlmRequestDiagnosticPhases.Sending) _preparationMs = elapsedMs;
                if (phase == LlmRequestDiagnosticPhases.Headers) _responseHeadersMs = elapsedMs;
                if (phase == LlmRequestDiagnosticPhases.FirstChunk) _firstChunkMs = elapsedMs;
                var terminal = phase == LlmRequestDiagnosticPhases.Completed ||
                    phase == LlmRequestDiagnosticPhases.Cancelled ||
                    phase == LlmRequestDiagnosticPhases.Failed;
                _terminal = terminal;
                update = new LlmRequestDiagnosticUpdate
                {
                    RequestId = RequestId,
                    Phase = phase,
                    Model = _settings.Model ?? string.Empty,
                    StreamRequested = _settings.StreamResponses,
                    ElapsedMs = elapsedMs,
                    PreparationMs = _preparationMs,
                    ResponseHeadersMs = _responseHeadersMs,
                    FirstChunkMs = _firstChunkMs,
                    TotalMs = terminal ? (long?)elapsedMs : null,
                    RequestBytes = _requestBytes,
                    StatusCode = _statusCode,
                    FailureKind = failureKind,
                    Error = Bound(error)
                };
            }

            Publish(_requestProgress, update);
            if (!Equals(_requestProgress, _globalProgress)) Publish(_globalProgress, update);
            LogTerminal(update);
        }

        private static void Publish(Action<LlmRequestDiagnosticUpdate> progress, LlmRequestDiagnosticUpdate update)
        {
            try { if (progress != null) progress(update); }
            catch { }
        }

        private void LogTerminal(LlmRequestDiagnosticUpdate update)
        {
            if (_debugLog == null || update == null || !update.TotalMs.HasValue ||
                update.Phase == LlmRequestDiagnosticPhases.Completed && update.TotalMs.Value < SlowRequestMs)
            {
                return;
            }
            try
            {
                _debugLog(
                    "MODEL REQUEST [" + RequestId + "]" +
                    " phase=" + update.Phase +
                    " model=" + Bound(update.Model) +
                    " stream=" + update.StreamRequested +
                    " prepareMs=" + Number(update.PreparationMs) +
                    " headersMs=" + Number(update.ResponseHeadersMs) +
                    " firstChunkMs=" + Number(update.FirstChunkMs) +
                    " totalMs=" + Number(update.TotalMs) +
                    " http=" + (update.StatusCode.HasValue ? update.StatusCode.Value.ToString() : "-") +
                    " failure=" + (update.FailureKind.HasValue ? update.FailureKind.Value.ToString() : "-") +
                    (string.IsNullOrWhiteSpace(update.Error) ? string.Empty : " error=" + update.Error));
            }
            catch { }
        }

        private static string Number(long? value)
        {
            return value.HasValue ? value.Value.ToString() : "-";
        }

        private static string Bound(string value)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 600 ? value : value.Substring(0, 600) + "…";
        }
    }
}
