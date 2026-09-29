using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RNAssistant.Core.Storage
{
    internal static class OfficeAttachmentTextExtractor
    {
        private const long MaxXmlPartBytes = 20L * 1024L * 1024L;
        private const long MaxXmlTotalBytes = 80L * 1024L * 1024L;
        private const int MaxParts = 2000;

        internal static string Extract(string fileName, byte[] bytes, int maxChars)
        {
            if (bytes == null || bytes.Length == 0 || bytes.LongLength > AttachmentStore.MaxFileBytes)
                throw new InvalidOperationException("Office attachment exceeds the file limit.");
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            using (var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read))
            {
                if (archive.Entries.Count > MaxParts)
                    throw new InvalidOperationException("Office attachment contains too many parts.");
                var parts = archive.Entries.ToDictionary(entry => entry.FullName.Replace('\\', '/'),
                    entry => entry, StringComparer.OrdinalIgnoreCase);
                if (parts.Values.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .Sum(entry => entry.Length) > MaxXmlTotalBytes)
                    throw new InvalidOperationException("Office attachment XML exceeds the extraction limit.");
                var output = new StringBuilder();
                if (extension == ".docx") ExtractWord(parts, output, maxChars);
                else if (extension == ".xlsx") ExtractExcel(parts, output, maxChars);
                else if (extension == ".pptx") ExtractPowerPoint(parts, output, maxChars);
                else throw new InvalidOperationException("Unsupported Office attachment type.");
                if (output.Length == 0) throw new InvalidOperationException("Office attachment contains no extractable text.");
                return output.ToString().TrimEnd();
            }
        }

        private static void ExtractWord(IDictionary<string, ZipArchiveEntry> parts, StringBuilder output, int limit)
        {
            var document = LoadRequired(parts, "word/document.xml");
            var body = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "body");
            if (body == null) throw new InvalidOperationException("Word document body is missing.");
            foreach (var block in body.Elements())
            {
                if (block.Name.LocalName == "tbl")
                {
                    foreach (var row in block.Elements().Where(node => node.Name.LocalName == "tr"))
                    {
                        var cells = row.Elements().Where(node => node.Name.LocalName == "tc")
                            .Select(cell => string.Join(" / ", cell.Descendants()
                                .Where(node => node.Name.LocalName == "p")
                                .Select(p => string.Concat(p.Descendants().Where(node => node.Name.LocalName == "t")
                                    .Select(t => t.Value))).Where(text => text.Length > 0)));
                        Append(output, string.Join(" | ", cells) + "\n", limit);
                    }
                }
                else if (block.Name.LocalName == "p")
                {
                    var line = string.Concat(block.Descendants().Where(node => node.Name.LocalName == "t")
                        .Select(node => node.Value));
                    if (line.Length > 0) Append(output, line + "\n", limit);
                }
            }
            foreach (var part in parts.Keys.Where(name => name.StartsWith("word/", StringComparison.OrdinalIgnoreCase) &&
                (name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase) ||
                 name == "word/footnotes.xml" || name == "word/endnotes.xml"))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var xml = LoadRequired(parts, part);
                var text = string.Join("\n", xml.Descendants().Where(node => node.Name.LocalName == "p")
                    .Select(p => string.Concat(p.Descendants().Where(node => node.Name.LocalName == "t")
                        .Select(t => t.Value))).Where(value => value.Length > 0));
                if (text.Length > 0) Append(output, "\n[" + part + "]\n" + text + "\n", limit);
            }
        }

        private static void ExtractExcel(IDictionary<string, ZipArchiveEntry> parts, StringBuilder output, int limit)
        {
            var workbook = LoadRequired(parts, "xl/workbook.xml");
            var relationships = LoadRequired(parts, "xl/_rels/workbook.xml.rels");
            var targets = relationships.Descendants().Where(node => node.Name.LocalName == "Relationship")
                .ToDictionary(node => (string)node.Attribute("Id"), node => (string)node.Attribute("Target"));
            var shared = new List<string>();
            if (parts.ContainsKey("xl/sharedStrings.xml"))
            {
                var strings = LoadRequired(parts, "xl/sharedStrings.xml");
                foreach (var item in strings.Descendants().Where(node => node.Name.LocalName == "si"))
                    shared.Add(string.Concat(item.Descendants().Where(node => node.Name.LocalName == "t")
                        .Select(node => node.Value)));
            }
            foreach (var sheet in workbook.Descendants().Where(node => node.Name.LocalName == "sheet"))
            {
                var id = sheet.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
                string target;
                if (id == null || !targets.TryGetValue(id, out target) || string.IsNullOrWhiteSpace(target))
                    throw new InvalidOperationException("Office worksheet relationship is missing.");
                var path = target.TrimStart('/');
                if (!path.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) path = "xl/" + path;
                if (!path.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) || path.Contains(".."))
                    throw new InvalidOperationException("Office worksheet path is invalid.");
                Append(output, "\n[Sheet: " + ((string)sheet.Attribute("name") ?? path) + "]\n", limit);
                var xml = LoadRequired(parts, path);
                foreach (var row in xml.Descendants().Where(node => node.Name.LocalName == "row"))
                {
                    var cells = new List<string>();
                    foreach (var cell in row.Elements().Where(node => node.Name.LocalName == "c"))
                    {
                        var value = cell.Descendants().FirstOrDefault(node => node.Name.LocalName == "v")?.Value;
                        var type = (string)cell.Attribute("t");
                        if (type == "s")
                        {
                            int index;
                            if (!int.TryParse(value, out index) || index < 0 || index >= shared.Count)
                                throw new InvalidOperationException("Office shared string index is invalid.");
                            value = shared[index];
                        }
                        else if (type == "inlineStr")
                            value = string.Concat(cell.Descendants().Where(node => node.Name.LocalName == "t")
                                .Select(node => node.Value));
                        if (value != null) cells.Add(((string)cell.Attribute("r") ?? "cell") + "=" + value);
                    }
                    if (cells.Count > 0) Append(output, string.Join(" | ", cells) + "\n", limit);
                }
            }
        }

        private static void ExtractPowerPoint(IDictionary<string, ZipArchiveEntry> parts, StringBuilder output, int limit)
        {
            var slides = parts.Keys.Where(name => name.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => NumericSuffix(name, "slide"));
            foreach (var slide in slides)
            {
                Append(output, "\n[" + slide + "]\n", limit);
                var xml = LoadRequired(parts, slide);
                foreach (var paragraph in xml.Descendants().Where(node => node.Name.LocalName == "p"))
                {
                    var line = string.Concat(paragraph.Descendants().Where(node => node.Name.LocalName == "t")
                        .Select(node => node.Value));
                    if (line.Length > 0) Append(output, line + "\n", limit);
                }
            }
            foreach (var note in parts.Keys.Where(name => name.StartsWith("ppt/notesSlides/notesSlide", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(name => NumericSuffix(name, "notesSlide")))
            {
                var xml = LoadRequired(parts, note);
                var text = string.Join("\n", xml.Descendants().Where(node => node.Name.LocalName == "p")
                    .Select(p => string.Concat(p.Descendants().Where(node => node.Name.LocalName == "t")
                        .Select(t => t.Value))).Where(value => value.Length > 0));
                if (text.Length > 0) Append(output, "\n[" + note + "]\n" + text + "\n", limit);
            }
        }

        private static XDocument LoadRequired(IDictionary<string, ZipArchiveEntry> parts, string name)
        {
            ZipArchiveEntry entry;
            if (!parts.TryGetValue(name, out entry) || entry.Length > MaxXmlPartBytes)
                throw new InvalidOperationException("Office attachment has a missing or oversized XML part: " + name);
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxXmlPartBytes, MaxCharactersFromEntities = 0 }))
                return XDocument.Load(reader);
        }

        private static void Append(StringBuilder output, string text, int limit)
        {
            if (text.Length > limit - output.Length)
                throw new InvalidOperationException("Complete Office attachment text exceeds the extraction limit.");
            output.Append(text);
        }

        private static int NumericSuffix(string name, string prefix)
        {
            var leaf = Path.GetFileNameWithoutExtension(name);
            int number;
            return int.TryParse(leaf.Substring(prefix.Length), out number) ? number : int.MaxValue;
        }
    }
}
