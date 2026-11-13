using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Application;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.WebView
{
    // Transient ownership of existing data-plane leases, bounded by one native page.
    internal sealed class BrowserResourceBinding : IDisposable
    {
        private readonly AssistantController _controller;
        private readonly BrowserContentSession _content;
        private readonly HashSet<string> _leases = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _sync = new object();
        private bool _closed;
        internal BrowserResourceBinding(AssistantController controller, BrowserContentSession content)
        { _controller = controller; _content = content; }
        private int _pending;
        private readonly System.Threading.Tasks.TaskCompletionSource<bool> _drained = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        internal System.Threading.Tasks.Task Drained => _drained.Task;
        internal BrowserResourceControlResponse Control(BrowserResourceControlRequest request, CancellationToken token)
        {
            var response = new BrowserResourceControlResponse { RequestId = request?.RequestId }; var admitted = false;
            try {
                if (request?.Type != "rnassistant-resource-control" || string.IsNullOrEmpty(request.RequestId) || request.RequestId.Length > 64)
                    throw new InvalidOperationException("RESOURCE_CONTROL_INVALID");
                lock (_sync) {
                    if (_closed) throw new ObjectDisposedException(nameof(BrowserResourceBinding));
                    if (_leases.Count + _pending >= 64) throw new InvalidOperationException("RESOURCE_LEASE_LIMIT");
                    _pending++; admitted = true;
                }
                _content.Validate(); token.ThrowIfCancellationRequested();
                if (request.Operation == "close") {
                    lock (_sync) { if (request.LeaseId == null || !_leases.Remove(request.LeaseId)) throw new InvalidOperationException("RESOURCE_LEASE_CLOSED"); }
                    Close(request.LeaseId); response.Ok = true; return response;
                }
                if (request.Operation != "open") throw new InvalidOperationException("RESOURCE_CONTROL_INVALID");
                // Resource reads may dispatch to the host. Never hold the page lock across them.
                var lease = _controller.OpenBrowserResource(_content.Target, request.BindingName, token);
                bool keep; lock (_sync) { keep = !_closed && !token.IsCancellationRequested; if (keep) _leases.Add(lease.LeaseId); }
                if (!keep) { Close(lease.LeaseId); throw new InvalidOperationException("RESOURCE_LEASE_CLOSED"); }
                lease.Url = _content.Origin + "/.rna/resource/" + lease.LeaseId;
                response.Value = lease; response.Ok = true; return response;
            } catch (Exception error) { response.Error = error.Message; return response; }
            finally { if (admitted) lock (_sync) { _pending--; if (_closed && _pending == 0) _drained.TrySetResult(true); } }
        }
        internal bool IsRoute(string url) => url != null && url.StartsWith(_content.Origin + "/.rna/resource/", StringComparison.Ordinal);
        internal ResourceStreamResponse Read(string url, CancellationToken token)
        {
            string tail;
            lock (_sync) {
                if (_closed || !IsRoute(url)) throw new InvalidOperationException("RESOURCE_ACCESS_DENIED");
                tail = url.Substring((_content.Origin + "/.rna/resource/").Length); var id = tail.Split('?')[0];
                if (!_leases.Contains(id) || tail.Contains("#") || tail.Contains("\\")) throw new InvalidOperationException("RESOURCE_LEASE_CLOSED");
            }
            _content.Validate();
            return _controller.HandleResourceData("GET", ResourceDataPlaneService.Origin + "/v1/" + tail, token);
        }
        private void Close(string id)
        {
            _controller.CloseResourceData(new ResourceDataCloseRequest { ChatId = _content.Target.ChatId, WorkspaceId = "browser", LeaseId = id });
        }
        public void Dispose()
        {
            string[] leases;
            lock (_sync) { if (_closed) return; _closed = true; leases = _leases.ToArray(); _leases.Clear(); }
            foreach (var id in leases) try { Close(id); } catch { }
            lock (_sync) { if (_pending == 0) _drained.TrySetResult(true); }
        }
    }
}
