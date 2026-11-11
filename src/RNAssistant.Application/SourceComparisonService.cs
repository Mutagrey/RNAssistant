using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    // Owners supply an already validated immutable pair; only the selected member is materialized.
    internal sealed class SourceComparisonMember
    {
        internal string Path, Fingerprint;
        internal long Length;
        internal Func<string> Read;
    }

    internal static class SourceComparisonService
    {
        internal const int MaximumText = 256000, MaximumFiles = 1024;
        internal static void Validate(SourceComparisonRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BeforeId) || request.BeforeId.Length > 4096 ||
                string.IsNullOrWhiteSpace(request.AfterId) || request.AfterId.Length > 4096 || request.Path?.Length > 512)
                throw new InvalidOperationException("Укажите две точные сохранённые версии и файл из их списка.");
        }
        internal static string Utf8(byte[] bytes)
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.IndexOf('\0') >= 0) throw new DecoderFallbackException();
            return text; // Preserve BOM and line endings; the shared TextDiff reports differences.
        }
        internal static SourceComparisonDto Read(SourceComparisonRequest request, string beforeLabel, string afterLabel,
            IEnumerable<SourceComparisonMember> before, IEnumerable<SourceComparisonMember> after)
        {
            Validate(request);
            var left = before.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var right = after.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var paths = left.Keys.Union(right.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (paths.Length > MaximumFiles) throw new InvalidOperationException("Слишком много файлов для сравнения.");
            var result = new SourceComparisonDto { BeforeId = request.BeforeId, AfterId = request.AfterId,
                BeforeLabel = beforeLabel, AfterLabel = afterLabel, Path = request.Path, Complete = true,
                Files = paths.Select(path => new SourceComparisonFileDto { Path = path,
                    Change = !left.ContainsKey(path) ? SourceFileChange.Added : !right.ContainsKey(path) ? SourceFileChange.Removed :
                        left[path].Fingerprint == right[path].Fingerprint ? SourceFileChange.Unchanged : SourceFileChange.Changed }).ToArray() };
            if (request.Path == null) return result;
            SourceComparisonMember a, b; left.TryGetValue(request.Path, out a); right.TryGetValue(request.Path, out b);
            if (a == null && b == null) throw new InvalidOperationException("Файл отсутствует в выбранной паре версий.");
            if (a?.Length > MaximumText || b?.Length > MaximumText) return Limited(result);
            try
            {
                var aText = a == null ? "" : a.Read(); var bText = b == null ? "" : b.Read();
                if (aText == null || bText == null) throw new InvalidOperationException("Исходник недоступен.");
                if (aText.Length > MaximumText || bText.Length > MaximumText) return Limited(result);
                result.BeforeText = aText; result.AfterText = bText;
            }
            catch (DecoderFallbackException)
            { result.Complete = false; result.Message = "Этот файл не является текстом UTF-8. Текстовое сравнение недоступно."; }
            return result;
        }
        private static SourceComparisonDto Limited(SourceComparisonDto result)
        { result.Complete = false; result.Message = "Файл превышает лимит текстового сравнения (256 000 символов; для плагина также байтов на версию). Частичный diff не показан."; return result; }
    }
}
