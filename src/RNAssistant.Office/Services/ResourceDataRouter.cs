using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ResourceStreamResponse
    {
        internal int StatusCode;
        internal string Reason;
        internal Stream Body;
        internal string ContentType = "application/json; charset=utf-8";
        internal string AllowedHeaders = string.Empty;
        internal string Headers { get { return "Content-Type: " + ContentType + "\r\nCache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: null\r\n" + AllowedHeaders +
            "X-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'";
        } }
    }

    // HTTP-shaped WebView response adapter, never an HTTP server or domain owner.
    internal sealed class ResourceDataRouter
    {
        private readonly ResourceDataPlaneService _data;
        internal ResourceDataRouter(ResourceDataPlaneService data) { _data = data; }
        internal ResourceStreamResponse Handle(string method, string url, CancellationToken token, Stream body = null)
        {
            try
            {
                Uri uri;
                if (url == null || url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                    uri.GetLeftPart(UriPartial.Authority) != ResourceDataPlaneService.Origin || !string.IsNullOrEmpty(uri.Fragment))
                    return Failure(403, "RESOURCE_ACCESS_DENIED", "Not a canonical internal resource route.");
                var path = uri.AbsolutePath.Split('/');
                if (System.Text.RegularExpressions.Regex.IsMatch(Uri.UnescapeDataString(url), @"/\.\.?(/|$)"))
                    return Failure(403, "RESOURCE_ACCESS_DENIED", "Path traversal is not a resource route.");
                if (path.Length == 4 && path[1] == "v1" && path[2] == "upload" && IsLeaseId(path[3]))
                    return Upload(method, uri, path[3], body, token);
                if (path.Length == 4 && path[1] == "v1" && path[2] == "download" && IsLeaseId(path[3]))
                    return Download(method, uri, path[3], token);
                if (method != "GET") return Failure(405, "RESOURCE_METHOD_NOT_ALLOWED", "Only GET batch reads are supported on read leases.");
                if (path.Length != 3 || path[1] != "v1" || path[2].Length != 64 ||
                    !IsLeaseId(path[2]))
                    return Failure(403, "RESOURCE_ACCESS_DENIED", "Invalid resource capability route.");
                int offset = 0, limit = ResourceDataPlaneService.MaximumBatchItems;
                System.Collections.Generic.List<string> fields = null;
                var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var field in uri.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = field.Split('=');
                    int value;
                    if (parts.Length != 2 || !keys.Add(parts[0]))
                        return Failure(400, "RESOURCE_CURSOR_INVALID", "Invalid or duplicated bounded batch selector.");
                    if (parts[0] == "fields")
                    {
                        fields = JsonConvert.DeserializeObject<System.Collections.Generic.List<string>>(Uri.UnescapeDataString(parts[1]));
                        if (fields == null || fields.Count > 128 || fields.Exists(name => name == null || name.Length > 128))
                            return Failure(400, "RESOURCE_BATCH_TOO_LARGE", "Invalid bounded field selector.");
                        continue;
                    }
                    if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out value))
                        return Failure(400, "RESOURCE_CURSOR_INVALID", "A bounded numeric selector is required.");
                    if (parts[0] == "offset") offset = value;
                    else if (parts[0] == "limit") limit = value;
                    else return Failure(400, "RESOURCE_VIEW_UNSUPPORTED", "The requested selector is not part of this view capability.");
                }
                string contentType;
                var bytes = _data.Read(path[2], offset, limit, token, fields, out contentType);
                return new ResourceStreamResponse { StatusCode = 200, Reason = "OK", ContentType = contentType, Body = new MemoryStream(bytes, false) };
            }
            catch (ResourceRequestException error) { return Failure(409, error.ErrorCode, error.Message); }
            catch (OperationCanceledException) { return Failure(410, "RESOURCE_LEASE_CLOSED", "The resource read was cancelled."); }
            catch (ObjectDisposedException) { return Failure(410, "RESOURCE_LEASE_CLOSED", "The resource owner has closed."); }
            catch (Exception error) when (error is IOException || error is InvalidOperationException || error is JsonException)
            { return Failure(503, "RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact resource view is unavailable."); }
        }
        private ResourceStreamResponse Upload(string method, Uri uri, string id, Stream body, CancellationToken token)
        {
            if (method != "POST" && method != "OPTIONS")
                return Failure(405, "RESOURCE_METHOD_NOT_ALLOWED", "Upload capabilities only accept POST chunks.");
            int offset, count;
            var error = TransferBounds(uri, ResourceDataPlaneService.MaximumUploadChunkBytes, out offset, out count);
            if (error != null) return error;
            token.ThrowIfCancellationRequested();
            if (method == "OPTIONS")
            {
                _data.ValidateUpload(id);
                return new ResourceStreamResponse { StatusCode = 204, Reason = "No Content", Body = new MemoryStream(),
                    AllowedHeaders = "Access-Control-Allow-Methods: POST\r\nAccess-Control-Allow-Headers: Content-Type\r\n" };
            }
            var result = _data.WriteUpload(id, offset, count, body, token);
            return new ResourceStreamResponse { StatusCode = 200, Reason = "OK",
                Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result)), false) };
        }

        private ResourceStreamResponse Download(string method, Uri uri, string id, CancellationToken token)
        {
            if (method != "GET") return Failure(405, "RESOURCE_METHOD_NOT_ALLOWED", "Download capabilities only accept GET chunks.");
            int offset, count;
            var error = TransferBounds(uri, ResourceDataPlaneService.MaximumDownloadChunkBytes, out offset, out count);
            if (error != null) return error;
            string contentType;
            var bytes = _data.ReadDownload(id, offset, count, token, out contentType);
            return new ResourceStreamResponse { StatusCode = 200, Reason = "OK", ContentType = contentType, Body = new MemoryStream(bytes, false) };
        }

        private static ResourceStreamResponse TransferBounds(Uri uri, int maximumCount, out int offset, out int count)
        {
            offset = -1; count = -1;
            var fields = uri.Query.TrimStart('?').Split('&');
            if (fields.Length != 2) return Failure(400, "RESOURCE_CURSOR_INVALID", "An exact byte offset and count are required.");
            foreach (var field in fields)
            {
                var parts = field.Split('=');
                int value;
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out value))
                    return Failure(400, "RESOURCE_CURSOR_INVALID", "Invalid bounded transfer selector.");
                if (parts[0] == "offset" && offset == -1) offset = value;
                else if (parts[0] == "count" && count == -1) count = value;
                else return Failure(400, "RESOURCE_CURSOR_INVALID", "Unknown or duplicated transfer selector.");
            }
            if (offset < 0 || count < 1 || count > maximumCount)
                return Failure(400, "RESOURCE_BATCH_TOO_LARGE", "Invalid transfer chunk bounds.");
            return null;
        }
        private static bool IsLeaseId(string id)
        { return System.Text.RegularExpressions.Regex.IsMatch(id, "\\A[0-9a-f]{64}\\z"); }
        private static ResourceStreamResponse Failure(int code, string error, string message)
        {
            return new ResourceStreamResponse { StatusCode = code, Reason = "Resource request failed",
                Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new ResourceDataError {
                    Code = error, Message = message })), false) };
        }
    }
}
