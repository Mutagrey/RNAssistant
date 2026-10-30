using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    // Runtime preparation in the existing artifact owner/CAS, never a model argument.
    public sealed class FilePatchPreparation
    {
        public PayloadRef Source { get; set; }
        public PayloadRef Result { get; set; }
        public long SourceCharacters { get; set; }
        [Newtonsoft.Json.JsonIgnore] public List<ResolvedTextEdit> Edits { get; set; }
    }

    public sealed partial class DocumentArtifactStore
    {
        public FilePatchPreparation PrepareFilePatch(ChatSession session, ResourceRef reference, TextPatch patch,
            CancellationToken token = default(CancellationToken))
        {
            var artifact = Read(session, reference, false);
            var encoding = FileEncoding(artifact);
            if (encoding == null) throw new InvalidDataException("Select an authored text file.");
            var source = FilePayload(artifact);
            var parsed = patch;
            try
            {
                List<ResolvedTextEdit> edits;
                using (var raw = _payloads.OpenVerifiedRead(source.ToBlobReference(), long.MaxValue, token))
                using (var reader = OpenFileText(raw, encoding))
                    edits = parsed.Resolve(reader, token).ToList();
                long characters = 0;
                var result = StageFilePayload(artifact.MimeType, output =>
                {
                    using (var raw = _payloads.OpenVerifiedRead(source.ToBlobReference(), long.MaxValue, token))
                    using (var reader = OpenFileText(raw, encoding))
                    {
                        var preamble = FileCodec(encoding, true).GetPreamble();
                        output.Write(preamble, 0, preamble.Length);
                        using (var writer = new StreamWriter(output, FileCodec(encoding, false), 8192, true))
                        {
                            var buffer = new char[8192];
                            foreach (var edit in edits)
                            {
                                characters += TransferFileText(reader, writer, buffer, edit.Offset - characters, null, token);
                                characters += TransferFileText(reader, null, buffer, edit.Before.Length, edit.Before, token);
                                if (edit.After.IndexOf('\0') >= 0) throw new InvalidDataException("The edit contains a NUL character.");
                                writer.Write(edit.After);
                            }
                            characters += TransferFileText(reader, writer, buffer, null, null, token);
                        }
                    }
                }, token);
                return new FilePatchPreparation { Source = source, Result = result, SourceCharacters = characters, Edits = edits };
            }
            catch (DecoderFallbackException error) { throw new InvalidDataException("The file encoding is invalid.", error); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("The edit cannot be encoded without loss.", error); }
        }

        private static PayloadRef FilePayload(ChatArtifact artifact)
        {
            if (artifact?.ContentByteLength == null || string.IsNullOrWhiteSpace(artifact.ContentSha256))
                throw new InvalidDataException("The exact file payload is unavailable.");
            return new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength.Value, artifact.MimeType);
        }

        // Strict supported encodings have one byte representation for each decoded
        // character. Re-encoding preserves every untouched byte, including mixed EOLs.
        private static Encoding FileCodec(string encoding, bool preamble)
        {
            switch (encoding)
            {
                case "utf8": return new UTF8Encoding(false, true);
                case "utf8-bom": return new UTF8Encoding(preamble, true);
                case "utf16-le-bom": return new UnicodeEncoding(false, preamble, true);
                case "utf16-be-bom": return new UnicodeEncoding(true, preamble, true);
                case "utf32-le-bom": return new UTF32Encoding(false, preamble, true);
                case "utf32-be-bom": return new UTF32Encoding(true, preamble, true);
                default: throw new InvalidDataException("The file encoding is unsupported.");
            }
        }

        private static StreamReader OpenFileText(Stream raw, string encoding)
        {
            var preamble = FileCodec(encoding, true).GetPreamble();
            foreach (var value in preamble)
                if (raw.ReadByte() != value) throw new InvalidDataException("The file BOM changed.");
            var reader = new StreamReader(raw, FileCodec(encoding, false), false, 8192, true);
            if (encoding == "utf8" && reader.Peek() == '\uFEFF')
            {
                reader.Dispose();
                throw new InvalidDataException("The file encoding does not match its retained bytes.");
            }
            return reader;
        }

        private static long TransferFileText(TextReader reader, TextWriter writer, char[] buffer,
            long? count, string expected, CancellationToken token)
        {
            if (count < 0) throw new InvalidDataException("Patch ranges overlap.");
            long copied = 0;
            while (!count.HasValue || copied < count.Value)
            {
                token.ThrowIfCancellationRequested();
                var read = reader.Read(buffer, 0, count.HasValue ? (int)Math.Min(buffer.Length, count.Value - copied) : buffer.Length);
                if (read == 0)
                {
                    if (count.HasValue) throw new InvalidDataException("The prepared file range is unavailable.");
                    break;
                }
                for (var i = 0; i < read; i++)
                    if (buffer[i] == '\0' || expected != null && buffer[i] != expected[checked((int)copied + i)])
                        throw new InvalidDataException("The file is not lossless text or its patch range changed.");
                writer?.Write(buffer, 0, read);
                copied += read;
            }
            return copied;
        }

        private PayloadRef StageFilePayload(string mime, Action<Stream> write, CancellationToken token)
        {
            var path = Path.Combine(Path.GetTempPath(), "rna-artifact-patch-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                { write(output); output.Flush(true); }
                return PayloadRef.FromBlob(_payloads.StoreFile(path, mime, token));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private PayloadRef RetainDecodedFile(PayloadRef raw, string encoding, CancellationToken token = default(CancellationToken))
        {
            try
            {
                return StageFilePayload("text/plain; charset=utf-8", output =>
                {
                    using (var input = _payloads.OpenVerifiedRead(raw.ToBlobReference(), long.MaxValue, token))
                    using (var reader = OpenFileText(input, encoding))
                    using (var writer = new StreamWriter(output, new UTF8Encoding(false, true), 8192, true))
                        TransferFileText(reader, writer, new char[8192], null, null, token);
                }, token);
            }
            catch (DecoderFallbackException error) { throw new InvalidDataException("The file encoding is invalid.", error); }
        }
    }
}
