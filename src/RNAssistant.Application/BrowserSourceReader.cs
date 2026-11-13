using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    // Only already-authorized members supplied by the domain owner; no filesystem lookup.
    public static class BrowserSourceReader
    {
        public static BrowserSourcePage Read(BrowserTargetDescriptor target, BrowserSourceRequest request,
            IEnumerable<string> members, Func<string, string> read)
        {
            if (request == null || request.Offset < 0 || request.Count < 1 || request.Count > 32000 || request.RequestGeneration < 0)
                throw new ArgumentException("Некорректная страница исходника.");
            var paths = members.Take(513).ToArray();
            if (paths.Length == 0 || paths.Length > 512 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                throw new InvalidOperationException("Состав источников недоступен или превышает 512 файлов.");
            var path = request.Path ?? target.EntryPath ?? paths[0];
            if (!paths.Contains(path, StringComparer.Ordinal)) throw new InvalidOperationException("Файл не входит в выбранный источник.");
            var text = read(path);
            if (text == null || text.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Исходник превышает лимит текстового просмотра.");
            string revision;
            using (var hash = SHA256.Create()) revision = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            if (request.ExpectedRevision != null && request.ExpectedRevision != revision)
                throw new InvalidOperationException("Исходник изменился. Обновите просмотр перед чтением следующей страницы.");
            if (request.Offset > text.Length) throw new InvalidOperationException("Страница исходника больше не существует.");
            return new BrowserSourcePage { Target = target, Files = paths, Path = path, Offset = request.Offset,
                Text = text.Substring(request.Offset, Math.Min(request.Count, text.Length - request.Offset)),
                TotalCharacters = text.Length, Revision = revision, RequestGeneration = request.RequestGeneration };
        }
    }
}
