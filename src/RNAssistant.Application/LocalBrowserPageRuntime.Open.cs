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
            internal string Binding, SourceBinding, TabId, SessionId;
            internal BrowserOwnerScope Owner;
            internal BrowserTargetDescriptor Target;
            internal string Locator;
            internal bool Unknown;
            internal long Epoch, Generation;
            internal readonly TaskCompletionSource<BrowserPageStateDto> Completion =
                new TaskCompletionSource<BrowserPageStateDto>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    if (operation.Completion.Task.Status == TaskStatus.RanToCompletion) {
                        Page page;
                        if (!_pages.TryGetValue(operation.TabId, out page) || page.State.SessionId != operation.SessionId) {
                            var closed = Copy(operation.Completion.Task.Result);
                            closed.Status = BrowserPageStatus.Closed; closed.Attached = false;
                            return Task.FromResult(closed);
                        }
                        if (!MatchesAttachment(operation, page.State))
                            throw new InvalidOperationException("The open receipt belongs to a previous Browser page.");
                    }
                    return operation.Completion.Task;
                }
                if (_agentOpens.Values.Any(o => o.Unknown && o.SourceBinding == sourceBinding && o.TabId != null && _pages.ContainsKey(o.TabId)))
                    throw new InvalidOperationException("The previous open is unknown. Close its Browser tab manually before opening again.");
                foreach (var key in _agentOpens.Where(p => p.Value.Completion.Task.IsCompleted &&
                    (!p.Value.Unknown || p.Value.TabId == null || !_pages.ContainsKey(p.Value.TabId)))
                    .Select(p => p.Key).Take(Math.Max(0, _agentOpens.Count - 127)).ToArray()) _agentOpens.Remove(key);
                if (_agentOpens.Count >= 128) throw new InvalidOperationException("Browser open receipt limit reached.");
                operation = new AgentOpen { Binding = binding, SourceBinding = sourceBinding, Owner = request.OwnerScope, Epoch = _openEpoch };
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
                if (!SameOpenTarget(resolved?.Target, request.Target) || resolved.Target.OwnerScope?.Matches(request.OwnerScope) != true)
                    throw new InvalidOperationException(resolved?.Message ?? "The exact source changed before open.");
                BrowserDockOpenRequest dock;
                await _commands.WaitAsync(token).ConfigureAwait(false);
                try {
                    BrowserPageStateDto state = null;
                    Task<BrowserPageStateDto> opening = null;
                    lock (_sync) {
                        if (operation.Epoch != _openEpoch || _disposed) throw new InvalidOperationException("The Browser owner reset before dispatch.");
                        if (_agentOpens.Values.Any(o => o != operation && o.Unknown && o.SourceBinding == operation.SourceBinding &&
                            o.TabId != null && _pages.ContainsKey(o.TabId)))
                            throw new InvalidOperationException("The previous open is unknown. Close its Browser tab manually before opening again.");
                        token.ThrowIfCancellationRequested();
                        operation.Target = resolved.Target; operation.Locator = resolved.Locator;
                        var existing = _pages.Values.Select(p => p.State).FirstOrDefault(p => MatchesOpenSource(operation, p));
                        if (existing == null && _pages.Count >= 16)
                            throw new InvalidOperationException("Открыто 16 страниц. Закройте одну перед открытием новой.");
                        operation.TabId = existing?.BrowserTabId ?? "browser-" + Guid.NewGuid().ToString("N");
                        beforeDispatch?.Invoke(); dispatched = true;
                        if (existing != null) state = Copy(existing);
                        else opening = DeliverAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = operation.TabId,
                            OperationId = request.OperationId, OwnerScope = request.OwnerScope, Locator = resolved.Locator }, token, resolved);
                    }
                    if (opening != null) state = await opening.ConfigureAwait(false);
                    lock (_sync) {
                        Page page;
                        if (operation.Epoch != _openEpoch || !_pages.TryGetValue(operation.TabId, out page) ||
                            page.State.SessionId != state.SessionId || !MatchesOpenSource(operation, page.State))
                            throw new InvalidOperationException("The Browser page changed before Dock placement.");
                        operation.SessionId = page.State.SessionId; operation.Generation = page.State.DocumentGeneration;
                        dock = new BrowserDockOpenRequest { Origin = BrowserDockOpenOrigin.Agent, BrowserTabId = operation.TabId,
                            OperationId = request.OperationId, OwnerScope = request.OwnerScope, Locator = state.Url, Title = state.Title, Page = Copy(page.State) };
                    }
                }
                finally { _commands.Release(); }
                if (operation.Completion.Task.IsCompleted) return;
                _requestDockOpen(dock);
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
        private static bool SameOpenTarget(BrowserTargetDescriptor target, BrowserTargetDescriptor expected)
        {
            return target?.State == BrowserTargetState.Ready && target.Kind == expected.Kind &&
                target.SavedVersionId == expected.SavedVersionId && target.ReleaseId == expected.ReleaseId &&
                target.ResourceUri == expected.ResourceUri && target.ArtifactId == expected.ArtifactId &&
                target.WorkspaceId == expected.WorkspaceId && target.PluginId == expected.PluginId;
        }
        private static bool MatchesOpenSource(AgentOpen operation, BrowserPageStateDto page)
        {
            return page.Status != BrowserPageStatus.Closed && page.ChatBound && page.OwnerScope?.Matches(operation.Owner) == true &&
                page.Url == operation.Locator && SameOpenTarget(page.Target, operation.Target);
        }
        private static bool MatchesAttachment(AgentOpen operation, BrowserPageStateDto page)
        {
            return page.BrowserTabId == operation.TabId && page.SessionId == operation.SessionId &&
                page.DocumentGeneration == operation.Generation && MatchesOpenSource(operation, page);
        }
        private void ValidateAttachment(BrowserCommandPayload request, BrowserPageStateDto page)
        {
            AgentOpen operation;
            if (request.OperationId != null && _agentOpens.TryGetValue(request.OperationId, out operation)) {
                if (operation.Unknown || request.Generation != operation.Generation || !MatchesAttachment(operation, page))
                    throw new InvalidOperationException("Подтверждение относится к другой странице или устарело.");
            }
            else if (request.OperationId != page.OperationId)
                throw new InvalidOperationException("Подтверждение относится к другому открытию.");
        }
        private void ObserveAgentOpens(BrowserCommandPayload request, BrowserPageStateDto state)
        {
            lock (_sync)
            {
                Page page;
                var current = _pages.TryGetValue(state.BrowserTabId, out page) ? page.State : state;
                foreach (var entry in _agentOpens) {
                    var operation = entry.Value;
                    if (operation.SessionId == null || operation.TabId != state.BrowserTabId || operation.Completion.Task.IsCompleted) continue;
                    if (!MatchesAttachment(operation, current)) {
                        operation.Unknown = current.Status != BrowserPageStatus.Closed;
                        operation.Completion.TrySetException(new InvalidOperationException("Browser closed or changed before attachment."));
                    } else if (request?.Action == BrowserCommandKind.Attach && request.OperationId == entry.Key &&
                        MatchesAttachment(operation, state) && current.Attached && request.Bounds?.Visible == true &&
                        (_renderer == null || current.Status == BrowserPageStatus.Ready && request.NativeAttachmentVisible)) {
                        var receipt = Copy(current); receipt.OperationId = entry.Key;
                        operation.Completion.TrySetResult(receipt);
                    }
                }
            }
        }
    }
}
