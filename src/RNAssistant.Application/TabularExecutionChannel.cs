using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    // One native job and one pinned document. No viewer/controller/Office state.
    public sealed class TabularExecutionChannel : IDisposable
    {
        public const int MaximumPayloadCharacters = 263168;
        public const int MaximumMessageCharacters = MaximumPayloadCharacters * 6 + 2048;
        private readonly object _sync = new object();
        private readonly ITabularResultSink _sink;
        private readonly Action<string> _send;
        private readonly Action<TabularExecutionProgress> _progress;
        private readonly Func<bool> _sourceReleased;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly TaskCompletionSource<TabularOutputResult> _completion =
            new TaskCompletionSource<TabularOutputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _drained =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TabularExecutionCommand _start;
        private bool _started, _terminal, _pending, _revoked, _disposed;
        private int _sequence, _records;
        private TabularOutputResult _result;
        public Task<TabularOutputResult> Completion { get { return _completion.Task; } }
        public Task Drained { get { return _drained.Task; } }

        public TabularExecutionChannel(string inputJson, string bundleSha256, int deadlineMs,
            ITabularResultSink sink, Action<string> send, Func<bool> sourceReleased,
            Action<TabularExecutionProgress> progress = null)
        {
            if (string.IsNullOrEmpty(inputJson) || inputJson.Length > 1000000) throw new ArgumentOutOfRangeException(nameof(inputJson));
            if (bundleSha256 == null || bundleSha256.Length != 64) throw new ArgumentException("Expected a runtime pin.", nameof(bundleSha256));
            if (deadlineMs < 1 || deadlineMs > 300000) throw new ArgumentOutOfRangeException(nameof(deadlineMs));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _sourceReleased = sourceReleased ?? throw new ArgumentNullException(nameof(sourceReleased));
            _progress = progress;
            _start = new TabularExecutionCommand { Kind = TabularCommandKind.Start, JobId = Guid.NewGuid().ToString("N"),
                InputJson = inputJson, DeadlineMs = deadlineMs, BundleSha256 = bundleSha256 };
        }

        public async Task ReceiveAsync(string json)
        {
            bool ownsPending = false;
            try
            {
                lock (_sync) { if (_terminal) return; }
                var message = Parse(json);
                lock (_sync)
                {
                    if (_terminal) return; // Late completions can never revive a job.
                    if (message.Version != 1) throw Invalid();
                    if (message.Kind == TabularMessageKind.Ready)
                    {
                        if (_started || message.JobId != "" || !Empty(message)) throw Invalid();
                        _started = true; _send(JsonConvert.SerializeObject(_start)); return;
                    }
                    if (!_started || message.JobId != _start.JobId) throw Invalid();
                    if (message.Kind == TabularMessageKind.Progress)
                    {
                        var p = message.Progress;
                        if (p == null || message.Sequence != 0 || message.RecordCount != 0 || message.PayloadJson != null ||
                            message.ErrorCode != null || message.Message != null || _result != null ||
                            (p.Phase != "reading-xlsx" && p.Phase != "binding" && p.Phase != "extracting" && p.Phase != "validating") ||
                            p.Completed < 0 || p.Completed > 1000000000 || p.Total < 0 || p.Total > 1000000000 ||
                            p.Unit != (p.Phase == "reading-xlsx" ? TabularProgressUnit.Bytes : TabularProgressUnit.Records)) throw Invalid();
                        if (_progress != null) _progress(p);
                        return;
                    }
                    if (message.Kind == TabularMessageKind.Failed)
                    {
                        if (message.Sequence != 0 || message.RecordCount != 0 || message.PayloadJson != null || message.Progress != null ||
                            string.IsNullOrEmpty(message.ErrorCode) || message.ErrorCode.Length > 64 ||
                            string.IsNullOrEmpty(message.Message) || message.Message.Length > 512) throw Invalid();
                        throw new WorkspaceApplicationException(message.ErrorCode, message.Message);
                    }
                    if (_pending || message.Sequence != _sequence + 1 || message.Progress != null || message.ErrorCode != null || message.Message != null)
                        throw Invalid();
                    if (message.Kind == TabularMessageKind.Succeeded)
                    {
                        if (_result == null || message.RecordCount != _records || message.PayloadJson != null || !_sourceReleased()) throw Invalid();
                        _terminal = true; _completion.TrySetResult(_result); _drained.TrySetResult(true); return;
                    }
                    if (_result != null || string.IsNullOrEmpty(message.PayloadJson) || message.PayloadJson.Length > MaximumPayloadCharacters) throw Invalid();
                    if (message.Kind == TabularMessageKind.Batch)
                    {
                        if (message.RecordCount < 1 || message.RecordCount > 64 || _records > 100000 - message.RecordCount) throw Invalid();
                    }
                    else if (message.Kind != TabularMessageKind.Validate || message.RecordCount != _records) throw Invalid();
                    _pending = true; ownsPending = true;
                }
                if (message.Kind == TabularMessageKind.Batch)
                    await _sink.WriteBatchAsync(new TabularOutputBatch(message.Sequence, message.RecordCount, message.PayloadJson), _cancel.Token).ConfigureAwait(false);
                else
                    await _sink.ValidateAsync(new TabularOutputResult(_records, _sequence, message.PayloadJson), _cancel.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_terminal) return;
                    _sequence = message.Sequence;
                    if (message.Kind == TabularMessageKind.Batch) _records += message.RecordCount;
                    else _result = new TabularOutputResult(_records, _sequence - 1, message.PayloadJson);
                    _pending = false; ownsPending = false;
                    _send(JsonConvert.SerializeObject(new TabularExecutionCommand {
                        Kind = TabularCommandKind.Acknowledge, JobId = _start.JobId, Sequence = _sequence }));
                }
            }
            catch (Exception error) { lock (_sync) { if (!_terminal) Stop(error); } }
            finally
            {
                lock (_sync)
                {
                    if (ownsPending) _pending = false;
                    if (_terminal && !_pending) _drained.TrySetResult(true);
                }
            }
        }

        private static bool Empty(TabularExecutionMessage message)
        { return message.Sequence == 0 && message.RecordCount == 0 && message.PayloadJson == null && message.Progress == null && message.ErrorCode == null && message.Message == null; }

        private static TabularExecutionMessage Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaximumMessageCharacters) throw Invalid();
            var message = JsonConvert.DeserializeObject<TabularExecutionMessage>(json, new JsonSerializerSettings {
                MissingMemberHandling = MissingMemberHandling.Error, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                DateParseHandling = DateParseHandling.None, MaxDepth = 4, CheckAdditionalContent = true });
            // The pinned sender writes this one canonical envelope order. Refuse
            // duplicates, omitted fields, numeric enums, coercion and comments.
            if (message == null || JsonConvert.SerializeObject(message) != json) throw Invalid();
            return message;
        }

        private static WorkspaceApplicationException Invalid()
        { return new WorkspaceApplicationException("tabular_protocol_invalid", "Invalid or out-of-order native execution message."); }

        public void Stop(Exception error)
        {
            lock (_sync)
            {
                if (_revoked || _disposed) return;
                _revoked = true;
                _terminal = true;
                try { _cancel.Cancel(); } catch (Exception cancelError) { error = new AggregateException(error, cancelError); }
                try { _sink.Abort(); } catch (Exception abortError) { error = new AggregateException(error, abortError); }
                _completion.TrySetException(error);
                if (!_pending) _drained.TrySetResult(true);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (!_terminal) Stop(new OperationCanceledException("Native execution was closed."));
                _disposed = true;
                // Cancellation registrations owned by a sink may still be running.
                Drained.ContinueWith(ignored => _cancel.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            }
        }
    }
}
