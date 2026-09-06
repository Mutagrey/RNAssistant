using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    // Transient access/batch ownership only. Revisions, bodies and freshness belong
    // to the gateway, its providers, the authority journal and the existing CAS.
    internal sealed partial class ResourceDataPlaneService : IDisposable
    {
        internal const string Origin = "https://rnassistant.local-resource";
        internal const int MaximumBatchBytes = 8 * 1024 * 1024;
        internal const int MaximumBatchItems = 32000;
        internal const int MaximumBinaryChunkBytes = 256 * 1024;
        private readonly ResourceGatewayService _gateway;
        private readonly Func<string, string, bool> _ownerIsActive;
        private readonly Func<DateTime> _utcNow;
        private readonly Timer _expiryTimer;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Access> _access = new Dictionary<string, Access>(StringComparer.Ordinal);
        private readonly List<Opening> _openings = new List<Opening>();
        private bool _disposed;

        internal ResourceDataPlaneService(ResourceGatewayService gateway, Func<string, string, bool> ownerIsActive = null,
            Func<DateTime> utcNow = null)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _ownerIsActive = ownerIsActive; _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _expiryTimer = new Timer(_ => { lock (_sync) { if (!_disposed) Expire(); } },
                null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        internal ResourceDataOpenResponse Open(ChatSession session, string workspaceId, ResourceRef reference, string view,
            string viewPath = null, CancellationToken cancellationToken = default(CancellationToken),
            string initialCursor = null, Action<ResourceReadResult> validate = null)
        {
            if (session == null || string.IsNullOrWhiteSpace(workspaceId) || reference == null ||
                _ownerIsActive != null && !_ownerIsActive(session.Id, workspaceId))
                throw Error("RESOURCE_ACCESS_DENIED", "An explicit workspace owner and bound resource are required.");
            cancellationToken.ThrowIfCancellationRequested();
            var opening = new Opening { Owner = session.Id + ":" + workspaceId,
                ReservedBytes = ResourceGatewayService.IsBinaryView(view) ? ArtifactViewerService.MaximumRawBytes : 0 };
            lock (_sync)
            {
                EnsureActive(); Expire();
                if (LeaseCount >= 64) throw Error("RESOURCE_LEASE_LIMIT", "Close unused resource handles before opening more.");
                if (OpeningCount >= 4) throw Error("RESOURCE_BACKPRESSURE", "Only four resource opens may be in flight.");
                if (TransferBytes + opening.ReservedBytes > RNAssistant.Core.Storage.AttachmentStore.MaxMessageBytes)
                    throw Error("RESOURCE_BACKPRESSURE", "The shared binary/transfer budget is occupied.");
                _openings.Add(opening);
            }
            try
            {
                ResourceReadSelection first;
                using (DocumentAccessGate.BeginOperation())
                    first = _gateway.Read(session, new ResourceReadRequest { Reference = reference.Copy(),
                        Representation = view, ViewPath = viewPath, Cursor = initialCursor, MaxChars = MaximumBatchItems, MaxRows = 500 });
                cancellationToken.ThrowIfCancellationRequested();
                if (first?.Result?.Resource?.Reference == null || !first.Result.Resource.Reference.IsExact)
                    throw Error("RESOURCE_REVISION_UNAVAILABLE", "The provider did not establish an exact view revision.");
                if (!reference.IsExact && _gateway.Authority != null)
                    _gateway.RequireCurrent(session, first.Result.Resource, first.Result.Representation,
                        _gateway.CaptureAuthorityFor(session, new[] { first.Result.Resource }));
                validate?.Invoke(first.Result);
                var id = NewLeaseId();
                var lease = new ResourceLease(id, first.Result.Resource.Reference, new[] { first.Result.Representation },
                    first.Result.Resource.Coverage ?? ResourceCoverage.Whole(), session.Id + ":" + workspaceId, _utcNow().AddMinutes(10));
                // Never call the controller/owner while holding the lease lock.
                if (_ownerIsActive != null && !_ownerIsActive(session.Id, workspaceId))
                    throw Error("RESOURCE_LEASE_EXPIRED", "The resource owner closed during capture.");
                lock (_sync)
                {
                    EnsureActive();
                    Expire();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (opening.Cancelled)
                        throw Error("RESOURCE_LEASE_EXPIRED", "The resource owner closed during capture.");
                    _access.Add(id, new Access { Lease = lease, Session = session, WorkspaceId = workspaceId, First = first,
                        ViewPath = viewPath, Offset = first.Result.Offset, Cursor = initialCursor,
                        ReservedBytes = first.Result.Binary?.Payload.ByteLength ?? 0 });
                    opening.ReservedBytes = 0;
                }
                return new ResourceDataOpenResponse { LeaseId = id, Url = Origin + "/v1/" + id,
                    Descriptor = first.Result.Resource, View = first.Result.Representation, ViewPath = viewPath ?? "$", ExpiresUtc = lease.ExpiresUtc,
                    MaxBatchBytes = first.Result.Binary == null ? MaximumBatchBytes : MaximumBinaryChunkBytes,
                    MaxBatchItems = first.Result.Binary == null ? MaximumBatchItems : MaximumBinaryChunkBytes, Binary = first.Result.Binary };
            }
            finally { lock (_sync) _openings.Remove(opening); }
        }

        internal byte[] Read(string leaseId, int offset, int limit, CancellationToken cancellationToken,
            IReadOnlyList<string> fields = null)
        {
            string contentType;
            return Read(leaseId, offset, limit, cancellationToken, fields, out contentType);
        }

        internal byte[] Read(string leaseId, int offset, int limit, CancellationToken cancellationToken,
            IReadOnlyList<string> fields, out string contentType)
        {
            contentType = "application/json; charset=utf-8";
            Access access;
            bool binaryRead;
            ResourceReadSelection first;
            lock (_sync)
            {
                EnsureActive(); Expire();
                if (!_access.TryGetValue(leaseId ?? string.Empty, out access))
                    throw Error("RESOURCE_LEASE_EXPIRED", "The resource lease is unknown, closed or expired.");
                if (!access.Serial.Wait(0)) throw Error("RESOURCE_BACKPRESSURE", "Only one batch may be in flight per resource handle.");
                access.Busy = true;
                first = access.First;
                binaryRead = first?.Result?.Binary != null;
            }
            try
            {
                if (limit < 1 || limit > (binaryRead ? MaximumBinaryChunkBytes : MaximumBatchItems) || offset < 0)
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "The requested batch exceeds the negotiated bounds.");
                cancellationToken.ThrowIfCancellationRequested();
                if (access.Cancelled || access.Lease.ExpiresUtc <= _utcNow() ||
                    _ownerIsActive != null && !_ownerIsActive(access.Session.Id, access.WorkspaceId))
                    throw Error("RESOURCE_LEASE_EXPIRED", "The resource lease is closed or expired.");
                if (first?.Result?.Binary != null)
                {
                    if (fields != null && fields.Count != 0)
                        throw Error("RESOURCE_VIEW_INVALID", "Binary views do not accept record selectors.");
                    if (access.Done || offset != access.Offset)
                        throw Error("RESOURCE_CURSOR_INVALID", "Binary chunks must continue this exact lease in sequence.");
                    var payload = first.Result.Binary.Payload;
                    var binaryBytes = access.BinaryBytes ?? _gateway.Authority.Payloads.ReadBytes(payload.ToBlobReference());
                    if (binaryBytes == null || binaryBytes.LongLength != payload.ByteLength)
                        throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The pinned binary payload is unavailable.");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (access.Cancelled) throw Error("RESOURCE_LEASE_EXPIRED", "The resource handle was closed during the read.");
                    contentType = payload.ContentType;
                    var count = (int)Math.Min(limit, payload.ByteLength - offset);
                    var chunk = new byte[count];
                    Buffer.BlockCopy(binaryBytes, offset, chunk, 0, count);
                    lock (_sync)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (access.Cancelled || access.Lease.ExpiresUtc <= _utcNow())
                            throw Error("RESOURCE_LEASE_EXPIRED", "The binary lease closed during capture.");
                        access.Offset += count; access.Done = access.Offset == payload.ByteLength;
                        access.BinaryBytes = access.Done ? null : binaryBytes;
                        if (access.Done) access.ReservedBytes = 0;
                    }
                    return chunk;
                }
                if (access.Done || offset != access.Offset)
                    throw Error("RESOURCE_CURSOR_INVALID", "Read offsets must continue this exact lease in sequence.");
                ResourceReadSelection selected;
                if (first != null && limit >= Count(first.Result) && (fields == null || fields.Count == 0))
                { selected = first; access.First = null; }
                else
                {
                    access.First = null;
                    using (DocumentAccessGate.BeginOperation())
                        selected = _gateway.Read(access.Session, new ResourceReadRequest {
                            Reference = access.Lease.Resource.Copy(), Representation = access.Lease.Views[0],
                            Cursor = access.Cursor, MaxChars = limit, MaxRows = limit, RowOffset = offset,
                            Fields = fields?.ToList(), ViewPath = access.ViewPath });
                }
                var result = selected.Result;
                if (result.Resource.Reference.Revision != access.Lease.Resource.Revision ||
                    result.Offset != access.Offset || Count(result) > limit)
                    throw Error("RESOURCE_REVISION_CHANGED", "The provider could not continue the pinned resource view.");
                var batch = new ResourceDataBatch { Resource = access.Lease.Resource.Copy(), View = result.Representation,
                    Text = result.Text, Offset = access.Offset, NextOffset = access.Offset + Count(result),
                    Done = result.Complete, Coverage = result.Coverage,
                    Rows = result.Table?.Rows, Columns = result.Table?.Columns };
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch));
                if (bytes.Length > MaximumBatchBytes) throw Error("RESOURCE_BATCH_TOO_LARGE", "The provider batch exceeds its byte limit.");
                cancellationToken.ThrowIfCancellationRequested();
                if (access.Cancelled) throw Error("RESOURCE_LEASE_EXPIRED", "The resource handle was closed during the read.");
                access.Offset = checked((int)batch.NextOffset); access.Cursor = result.NextCursor; access.Done = result.Complete;
                return bytes;
            }
            catch { if (binaryRead) { lock (_sync) CancelAccess(access); } throw; }
            finally
            {
                lock (_sync) { access.Busy = false; if (access.Cancelled) _access.Remove(access.Lease.LeaseId); }
                access.Serial.Release();
            }
        }

        internal void Close(string sessionId, string workspaceId, string leaseId)
        {
            lock (_sync)
            {
                Download download;
                if (_downloads.TryGetValue(leaseId ?? string.Empty, out download))
                {
                    if (download.ChatId != sessionId || download.WorkspaceId != workspaceId)
                        throw Error("RESOURCE_ACCESS_DENIED", "The download belongs to another owner.");
                    CancelDownload(download); return;
                }
                Access access;
                if (!_access.TryGetValue(leaseId ?? string.Empty, out access)) return;
                if (access.Lease.Owner != sessionId + ":" + workspaceId)
                    throw Error("RESOURCE_ACCESS_DENIED", "The resource handle belongs to another workspace.");
                CancelAccess(access);
            }
        }

        internal void CloseWorkspace(string sessionId, string workspaceId)
        {
            lock (_sync)
            {
                foreach (var opening in _openings.Where(item => item.Owner == sessionId + ":" + workspaceId)) opening.Cancelled = true;
                foreach (var download in _downloads.Values.Where(item => item.ChatId == sessionId && item.WorkspaceId == workspaceId).ToArray()) CancelDownload(download);
                foreach (var access in _access.Values.Where(item => item.Lease.Owner == sessionId + ":" + workspaceId).ToArray())
                    Close(sessionId, workspaceId, access.Lease.LeaseId);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _expiryTimer.Dispose();
                foreach (var opening in _openings) opening.Cancelled = true;
                foreach (var access in _access.Values.ToArray()) CancelAccess(access);
                foreach (var upload in _uploads.Values.ToArray()) CancelUpload(upload);
                foreach (var download in _downloads.Values.ToArray()) CancelDownload(download);
            }
        }
        private void Expire()
        {
            foreach (var access in _access.Values.Where(item => item.Lease.ExpiresUtc <= _utcNow()).ToArray())
                CancelAccess(access);
            foreach (var upload in _uploads.Values.Where(item => item.ExpiresUtc <= _utcNow()).ToArray()) CancelUpload(upload);
            foreach (var download in _downloads.Values.Where(item => item.ExpiresUtc <= _utcNow()).ToArray()) CancelDownload(download);
        }
        private static string NewLeaseId()
        {
            var token = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            return BitConverter.ToString(token).Replace("-", string.Empty).ToLowerInvariant();
        }
        private void EnsureActive() { if (_disposed) throw new ObjectDisposedException(nameof(ResourceDataPlaneService)); }
        private static int Count(ResourceReadResult result) { return result.Table == null ? result.ReturnedCharacters : result.Table.Rows.Count; }
        private static ResourceRequestException Error(string code, string message) { return new ResourceRequestException(message, code, false); }
        private void CancelAccess(Access access)
        {
            access.Cancelled = true; access.First = null; access.BinaryBytes = null;
            if (!access.Busy) _access.Remove(access.Lease.LeaseId);
        }
        private sealed class Opening { internal string Owner; internal bool Cancelled; internal long ReservedBytes; }
        private sealed class Access
        {
            internal string ViewPath;
            internal ResourceLease Lease;
            internal ChatSession Session;
            internal string WorkspaceId;
            internal ResourceReadSelection First;
            internal string Cursor;
            internal int Offset;
            internal bool Done;
            internal bool Busy;
            internal long ReservedBytes;
            internal byte[] BinaryBytes;
            internal volatile bool Cancelled;
            internal readonly SemaphoreSlim Serial = new SemaphoreSlim(1, 1);
        }
    }
}
