using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TabularMessageKind { Ready, Batch, Validate, Succeeded, Failed, Progress }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TabularCommandKind { Start, Acknowledge }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TabularProgressUnit { Bytes, Records }

    public sealed class TabularExecutionProgress
    {
        [JsonProperty("phase")] public string Phase { get; set; }
        [JsonProperty("completed")] public long Completed { get; set; }
        [JsonProperty("total")] public long? Total { get; set; }
        [JsonProperty("unit")] public TabularProgressUnit Unit { get; set; }
    }

    // Closed transport envelopes. Json is bounded, opaque engine output; native
    // transport does not implement the DSL or treat output as publication authority.
    public sealed class TabularExecutionMessage
    {
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("jobId")] public string JobId { get; set; }
        [JsonProperty("kind")] public TabularMessageKind Kind { get; set; }
        [JsonProperty("sequence")] public int Sequence { get; set; }
        [JsonProperty("recordCount")] public int RecordCount { get; set; }
        [JsonProperty("payloadJson")] public string PayloadJson { get; set; }
        [JsonProperty("progress")] public TabularExecutionProgress Progress { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }

    public sealed class TabularExecutionCommand
    {
        [JsonProperty("version")] public int Version { get; set; } = 1;
        [JsonProperty("jobId")] public string JobId { get; set; }
        [JsonProperty("kind")] public TabularCommandKind Kind { get; set; }
        [JsonProperty("sequence")] public int Sequence { get; set; }
        [JsonProperty("inputJson")] public string InputJson { get; set; }
        [JsonProperty("deadlineMs")] public int DeadlineMs { get; set; }
        [JsonProperty("bundleSha256")] public string BundleSha256 { get; set; }
    }

    public sealed class TabularOutputBatch
    {
        public int Sequence { get; private set; }
        public int RecordCount { get; private set; }
        public string Json { get; private set; }
        internal TabularOutputBatch(int sequence, int count, string json)
        { Sequence = sequence; RecordCount = count; Json = json; }
    }

    public sealed class TabularOutputResult
    {
        public int RecordCount { get; private set; }
        public int BatchCount { get; private set; }
        public string Json { get; private set; }
        internal TabularOutputResult(int count, int batches, string json)
        { RecordCount = count; BatchCount = batches; Json = json; }
    }

    public interface ITabularResultSink
    {
        Task WriteBatchAsync(TabularOutputBatch batch, CancellationToken cancellationToken);
        // Validation leaves ALL output provisional, including after this returns.
        Task ValidateAsync(TabularOutputResult result, CancellationToken cancellationToken);
        // Synchronously revoke staging, even during a pending write/validation.
        // Must be thread-safe, nonblocking and idempotent. Pending work must observe cancellation.
        void Abort();
    }
}
