using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    public sealed partial class LocalBrowserPageRuntime
    {
        private readonly Func<BrowserOwnerScope, IReadOnlyList<BrowserTargetDescriptor>> _sourceCatalog;
        private readonly Action<BrowserDockOpenRequest> _requestDockOpen;
        private long _openEpoch;
        private readonly Dictionary<string, AgentOpen> _agentOpens = new Dictionary<string, AgentOpen>(StringComparer.Ordinal);
        private sealed class AgentOpen
        {
            internal string Binding, SourceBinding, TabId, OperationId;
            internal bool Unknown;
            internal long Epoch;
            internal TaskCompletionSource<BrowserPageStateDto> Completion = new TaskCompletionSource<BrowserPageStateDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public IReadOnlyList<BrowserOpenableTarget> OpenableTargetsFor(BrowserOwnerScope owner)
        {
            if (owner == null || owner.Kind == BrowserOwnerKind.User || string.IsNullOrWhiteSpace(owner.ChatId)) return new BrowserOpenableTarget[0];
            lock (_sync) { if (_disposed) return new BrowserOpenableTarget[0]; }
            var items = (_sourceCatalog?.Invoke(owner) ?? new BrowserTargetDescriptor[0])
                .Where(t => t?.OwnerScope != null && t.OwnerScope.Matches(owner) && t.State == BrowserTargetState.Ready && t.Kind != BrowserTargetKind.Home).Take(100).ToArray();
            return items.Select(target => new BrowserOpenableTarget { Target = target,
                SemanticTarget = "Browser source: " + new string((target.Title ?? "HTML").Take(120).ToArray()) + " · " + (target.SavedVersionId == null ? "working" : "saved") +
                    " · " + new string((target.RelativePath ?? target.EntryPath ?? "HTML").Take(96).ToArray()) }).ToArray();
        }
        private static string SourceBinding(BrowserAgentOpenRequest request)
        { return request.OwnerScope.Kind + "\n" + request.OwnerScope.ScopeId + "\n" + request.OwnerScope.ChatId + "\n" + request.Target.DisplayLocator + "\n" + request.Target.SavedVersionId; }
        public Task<BrowserPageStateDto> OpenAgentAsync(BrowserAgentOpenRequest request, Action beforeDispatch, CancellationToken token)
        {
            if (request?.Target == null || request.OwnerScope == null || !Key(request.RunId) || !Key(request.CallId) || !Key(request.OperationId) ||
                string.IsNullOrWhiteSpace(request.OwnerScope.ChatId) || request.OwnerScope.Kind == BrowserOwnerKind.User ||
                request.Target.OwnerScope == null || !request.Target.OwnerScope.Matches(request.OwnerScope) || _requestDockOpen == null)
                throw new InvalidOperationException("An exact run, source owner and Dock route are required.");
            AgentOpen operation; var sourceBinding = SourceBinding(request); var binding = sourceBinding + "\n" + request.RunId + "\n" + request.CallId;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LocalBrowserPageRuntime));
                if (_agentOpens.TryGetValue(request.OperationId, out operation)) {
                    if (operation.Binding != binding) throw new InvalidOperationException("The open operation belongs to another call or owner.");
                    if (operation.Unknown) throw new InvalidOperationException("The original open result is unknown; close its tab manually.");
                    Page page;
                    if (operation.Completion.Task.Status == TaskStatus.RanToCompletion && _operations.TryGetValue(request.OperationId, out page)) return Task.FromResult(Copy(page.State));
                    return operation.Completion.Task;
                }
                if (_agentOpens.Values.Any(o => o.Unknown && o.SourceBinding == sourceBinding && _pages.ContainsKey(o.TabId)))
                    throw new InvalidOperationException("The previous open is unknown. Close its Browser tab manually before opening again.");
                foreach (var key in _agentOpens.Where(p => p.Value.Completion.Task.IsCompleted && !_pages.ContainsKey(p.Value.TabId)).Select(p => p.Key).Take(Math.Max(0, _agentOpens.Count - 127)).ToArray()) _agentOpens.Remove(key);
                if (_agentOpens.Count >= 128) throw new InvalidOperationException("Browser open receipt limit reached.");
                operation = new AgentOpen { Binding = binding, SourceBinding = sourceBinding, TabId = "browser-" + Guid.NewGuid().ToString("N"), OperationId = request.OperationId, Epoch = _openEpoch };
                _agentOpens.Add(request.OperationId, operation);
            }
            _ = CompleteOpenAsync(operation, request, beforeDispatch, token);
            return operation.Completion.Task;
        }
        private async Task CompleteOpenAsync(AgentOpen operation, BrowserAgentOpenRequest request, Action beforeDispatch, CancellationToken token)
        {
            var dispatched = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!OpenableTargetsFor(request.OwnerScope).Any(t => t.Target.DisplayLocator == request.Target.DisplayLocator && t.Target.SavedVersionId == request.Target.SavedVersionId))
                    throw new InvalidOperationException("The source is no longer available in this conversation.");
                var resolved = _resolve(new BrowserResolveRequest { OwnerScope = request.OwnerScope, Locator = request.Target.DisplayLocator });
                if (resolved?.Target?.State != BrowserTargetState.Ready || resolved.Target.SavedVersionId != request.Target.SavedVersionId)
                    throw new InvalidOperationException(resolved?.Message ?? "The exact source changed before open.");
                Task<BrowserPageStateDto> opening;
                lock (_sync) {
                    if (operation.Epoch != _openEpoch || _disposed) throw new InvalidOperationException("The Browser owner reset before dispatch.");
                    token.ThrowIfCancellationRequested(); beforeDispatch?.Invoke(); dispatched = true;
                    opening = ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = operation.TabId,
                    OperationId = request.OperationId, OwnerScope = request.OwnerScope, Locator = request.Target.DisplayLocator }, token);
                }
                var state = await opening.ConfigureAwait(false);
                _requestDockOpen(new BrowserDockOpenRequest { BrowserTabId = operation.TabId, OperationId = request.OperationId,
                    OwnerScope = request.OwnerScope, Locator = state.Url, Title = state.Title });
                var deadline = Task.Delay(TimeSpan.FromSeconds(10), token);
                if (await Task.WhenAny(operation.Completion.Task, deadline).ConfigureAwait(false) != operation.Completion.Task) {
                    token.ThrowIfCancellationRequested(); throw new TimeoutException("Dock did not acknowledge the exact visible Browser tab.");
                }
            }
            catch (Exception error)
            {
                lock (_sync) { if (operation.Completion.TrySetException(error)) operation.Unknown = dispatched; }
            }
        }
        public void BlockAgentOpen(string operationId)
        { lock (_sync) { AgentOpen operation; if (operationId != null && _agentOpens.TryGetValue(operationId, out operation)) operation.Unknown = true; } }
        private void ObserveAgentOpen(BrowserCommandPayload request, BrowserPageStateDto state)
        {
            AgentOpen operation;
            lock (_sync)
            {
                if (!_agentOpens.TryGetValue(state.OperationId, out operation) || operation.TabId != state.BrowserTabId || operation.Completion.Task.IsCompleted) return;
                if (state.Status == BrowserPageStatus.Closed) operation.Completion.TrySetException(new InvalidOperationException("Browser closed before attachment."));
                else if (request.Action == BrowserCommandKind.Attach && state.Attached && (_renderer == null || state.Status == BrowserPageStatus.Ready) && request.Bounds?.Visible == true && ValidBounds(request.Bounds))
                    operation.Completion.TrySetResult(Copy(state));
            }
        }
    }
}
