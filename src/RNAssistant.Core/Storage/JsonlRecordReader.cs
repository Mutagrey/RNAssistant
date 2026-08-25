using System;
using Newtonsoft.Json;

namespace RNAssistant.Core.Storage
{
    internal enum JsonlRecordErrorKind
    {
        BlankRecord,
        InvalidRecord
    }

    internal sealed class JsonlRecordException : FormatException
    {
        public JsonlRecordErrorKind Kind { get; private set; }

        public JsonlRecordException(JsonlRecordErrorKind kind, Exception innerException)
            : base(kind == JsonlRecordErrorKind.BlankRecord
                ? "The JSONL stream contains a blank record."
                : "The JSONL stream contains an invalid record.", innerException)
        {
            Kind = kind;
        }
    }

    internal sealed class JsonlReadSummary
    {
        public bool HasIncompleteTail { get; set; }
        public long ByteLength { get; set; }
        public long TailNextByteOffset { get; set; }
    }

    internal static class JsonlRecordReader
    {
        public static JsonlReadSummary Read<TRecord>(
            string path,
            long startOffset,
            Func<string, TRecord> parse,
            Action<TRecord, JsonlByteLine> accept)
        {
            if (parse == null) throw new ArgumentNullException("parse");
            if (accept == null) throw new ArgumentNullException("accept");
            var summary = new JsonlReadSummary { TailNextByteOffset = startOffset };
            using (var reader = new JsonlByteReader(path, startOffset))
            {
                summary.ByteLength = reader.Length;
                JsonlByteLine line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line.Text))
                    {
                        if (!line.Terminated)
                        {
                            summary.HasIncompleteTail = true;
                            break;
                        }
                        throw new JsonlRecordException(JsonlRecordErrorKind.BlankRecord, null);
                    }

                    TRecord record;
                    try
                    {
                        record = parse(line.Text);
                    }
                    catch (JsonException ex)
                    {
                        if (!line.Terminated && line.NextOffset == reader.Length)
                        {
                            summary.HasIncompleteTail = true;
                            break;
                        }
                        throw new JsonlRecordException(JsonlRecordErrorKind.InvalidRecord, ex);
                    }

                    accept(record, line);
                    summary.TailNextByteOffset = line.NextOffset;
                    if (!line.Terminated)
                    {
                        summary.HasIncompleteTail = true;
                        break;
                    }
                }
            }
            return summary;
        }
    }
}
