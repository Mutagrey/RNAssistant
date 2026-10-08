using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum WebDiagnosticKind { Screenshot, Trace }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum WebDiagnosticState { Captured, Unavailable }

    // Runtime input to the existing verification publication owner, never model data.
    public sealed class WebDiagnosticCapture
    {
        public WebDiagnosticKind Kind { get; private set; }
        public byte[] Bytes { get; private set; }
        public string Error { get; private set; }

        public WebDiagnosticCapture(WebDiagnosticKind kind, byte[] bytes, string error = null)
        { Kind = kind; Bytes = bytes == null ? null : (byte[])bytes.Clone(); Error = error; }
    }

    public sealed class WebDiagnosticDescriptor
    {
        public WebDiagnosticKind Kind { get; set; }
        public WebDiagnosticState State { get; set; }
        public ResourceRef Reference { get; set; }
        public string View { get; set; }
        public PayloadRef Payload { get; set; }
        public string Error { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum WebBrowserTraceEventKind { Lifecycle, Navigation, Request, Response, Error, Check, Diagnostic }

    public sealed class WebBrowserTraceEntry
    {
        public long Milliseconds { get; set; }
        public WebBrowserTraceEventKind Kind { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
    }

    // Bounded observation journal; not a Playwright trace or an acceptance assertion.
    public sealed class WebBrowserTrace
    {
        public int SchemaVersion { get; set; } = 1;
        public string VerificationId { get; set; }
        public string SnapshotId { get; set; }
        public string SnapshotSha256 { get; set; }
        public WebVerificationState Outcome { get; set; }
        public long DurationMilliseconds { get; set; }
        public bool Truncated { get; set; }
        public int DroppedEntries { get; set; }
        public IReadOnlyList<WebBrowserTraceEntry> Entries { get; set; }
    }
}
