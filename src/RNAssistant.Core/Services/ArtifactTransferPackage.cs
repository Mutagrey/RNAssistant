using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Services
{
    public sealed class ArtifactTransferPackage
    {
        public const int MaximumBytes = 20 * 1024 * 1024;
        [JsonProperty(Required = Required.Always)]
        public int Version { get; set; } = 1;
        public string Title { get; set; }
        public string EntryPath { get; set; }
        public ArtifactTransferOrigin Origin { get; set; }
        public List<ArtifactTransferFile> Files { get; set; } = new List<ArtifactTransferFile>();
        public List<ArtifactTransferData> Data { get; set; } = new List<ArtifactTransferData>();
        public List<string> ExternalDependencies { get; set; } = new List<string>();

        public void Validate()
        {
            if (Version != 1 || string.IsNullOrWhiteSpace(Title) || Title.Length > 200 || Files == null ||
                Files.Count == 0 || Files.Count > 100 || Data == null || Data.Count > 32 || Files.Count + Data.Count > 100 ||
                ExternalDependencies == null || ExternalDependencies.Count > 256)
                throw new InvalidDataException("Неподдерживаемый или неполный HTML-проект.");
            if (Origin != null && ((Origin.ResourceUri?.Length ?? 0) > 2048 || (Origin.ContentSha256?.Length ?? 0) > 64))
                throw new InvalidDataException("Некорректное описание происхождения проекта.");
            long size = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files)
            {
                if (file == null || !SafePath(file.Path) || !paths.Add(file.Path) ||
                    file.Kind != "html" && file.Kind != "css" && file.Kind != "js" && file.Kind != "script" || file.Content == null || file.Content.Length > 300000)
                    throw new InvalidDataException("Некорректный, повторяющийся или слишком большой файл проекта.");
                size += Encoding.UTF8.GetByteCount(file.Content);
            }
            if (!Files.Any(f => f.Path == EntryPath && f.Kind == "html"))
                throw new InvalidDataException("HTML-файл запуска отсутствует.");
            if (Files.Sum(f => (long)f.Content.Length) + Data.Count * 2048 > 1500000) throw new InvalidDataException("Превышен размер редактируемого проекта.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var data in Data)
            {
                if (data == null || string.IsNullOrWhiteSpace(data.Name) || data.Name.Length > 128 || data.Name != data.Name.Trim() || data.Name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || data.Name.Contains("..") || data.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !names.Add(data.Name) ||
                    data.Json == null || data.Json.Length > 2000000 || data.View != "text" && data.View != "table" && data.View != "records" ||
                    data.ViewPath != null && (data.ViewPath.Length > 256 || data.View == "text"))
                    throw new InvalidDataException("Некорректный снимок данных проекта.");
                var parsed = JToken.Parse(data.Json);
                if (data.View != "text") {
                    var rows = (data.ViewPath == null || data.ViewPath == "$" ? parsed : parsed.SelectToken(data.ViewPath, false)) as JArray;
                    if (rows == null || rows.Any(row => !(row is JObject) && !(row is JArray)))
                        throw new InvalidDataException("Структурный снимок должен содержать массив записей.");
                }
                size += Encoding.UTF8.GetByteCount(data.Json);
                foreach (var auxiliary in new[] { data.SchemaJson, data.MappingJson }.Where(value => value != null)) {
                    if (auxiliary.Length > 2000000) throw new InvalidDataException("Зависимость превышает допустимый размер.");
                    JToken.Parse(auxiliary); size += Encoding.UTF8.GetByteCount(auxiliary);
                }
            }
            if (size > MaximumBytes || ExternalDependencies.Any(s => s == null || s.Length > 2048))
                throw new InvalidDataException("Проект превышает допустимый размер.");
        }

        public byte[] Export()
        {
            Validate();
            using (var output = new MemoryStream())
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    long expandedBytes = 0;
                    var manifest = new ArtifactTransferPackage { Title = Title, EntryPath = EntryPath, Origin = Origin, ExternalDependencies = ExternalDependencies };
                    foreach (var file in Files)
                    {
                        expandedBytes += Write(zip, "files/" + file.Path, file.Content);
                        manifest.Files.Add(new ArtifactTransferFile { Path = file.Path, Kind = file.Kind });
                    }
                    for (var i = 0; i < Data.Count; i++)
                    {
                        expandedBytes += Write(zip, "data/" + i + ".json", Data[i].Json);
                        var schemaPath = Data[i].SchemaJson == null ? null : "data/" + i + ".schema.json";
                        var mappingPath = Data[i].MappingJson == null ? null : "data/" + i + ".mapping.json";
                        if (schemaPath != null) expandedBytes += Write(zip, schemaPath, Data[i].SchemaJson);
                        if (mappingPath != null) expandedBytes += Write(zip, mappingPath, Data[i].MappingJson);
                        manifest.Data.Add(new ArtifactTransferData { Name = Data[i].Name, View = Data[i].View, ViewPath = Data[i].ViewPath,
                            SchemaPath = schemaPath, MappingPath = mappingPath });
                    }
                    var manifestJson = JsonConvert.SerializeObject(manifest);
                    if (Encoding.UTF8.GetByteCount(manifestJson) > 256 * 1024) throw new InvalidDataException("Описание проекта превышает допустимый размер.");
                    expandedBytes += Write(zip, "manifest.json", manifestJson);
                    if (expandedBytes > MaximumBytes) throw new InvalidDataException("Распакованный проект превышает 20 МиБ.");
                }
                if (output.Length > MaximumBytes) throw new InvalidDataException("Архив превышает 20 МиБ.");
                return output.ToArray();
            }
        }

        public static ArtifactTransferPackage Import(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaximumBytes) throw new InvalidDataException("Архив превышает 20 МиБ.");
            using (var input = new MemoryStream(bytes, false))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 353 || zip.Entries.Sum(e => e.Length) > MaximumBytes ||
                    zip.Entries.Any(e => !SafePath(e.FullName)) ||
                    zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
                    throw new InvalidDataException("Некорректная структура архива.");
                var result = JsonConvert.DeserializeObject<ArtifactTransferPackage>(Read(zip, "manifest.json", 256 * 1024),
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 32 });
                if (result == null || result.Version != 1 || result.Files == null || result.Data == null || result.Files.Count > 100 || result.Data.Count > 32)
                    throw new InvalidDataException("Версия пакета не поддерживается.");
                if (zip.Entries.Count != 1 + result.Files.Count + result.Data.Count + result.Data.Count(d => d?.SchemaPath != null) + result.Data.Count(d => d?.MappingPath != null))
                    throw new InvalidDataException("Архив содержит незаявленные файлы.");
                foreach (var file in result.Files)
                {
                    if (file == null || !SafePath(file.Path)) throw new InvalidDataException("Некорректный путь.");
                    file.Content = Read(zip, "files/" + file.Path, 1200000);
                }
                for (var i = 0; i < result.Data.Count; i++)
                {
                    if (result.Data[i] == null) throw new InvalidDataException("Отсутствует описание данных.");
                    result.Data[i].Json = Read(zip, "data/" + i + ".json", 8000000);
                    if (result.Data[i].SchemaPath != null) {
                        if (result.Data[i].SchemaPath != "data/" + i + ".schema.json") throw new InvalidDataException("Некорректный путь схемы.");
                        result.Data[i].SchemaJson = Read(zip, result.Data[i].SchemaPath, 8000000);
                    }
                    if (result.Data[i].MappingPath != null) {
                        if (result.Data[i].MappingPath != "data/" + i + ".mapping.json") throw new InvalidDataException("Некорректный путь mapping.");
                        result.Data[i].MappingJson = Read(zip, result.Data[i].MappingPath, 8000000);
                    }
                }
                result.Validate();
                return result;
            }
        }

        public static bool SafePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && path.Length <= 240 && !path.Contains("\\") &&
                !path.Contains(":") && !path.Contains("..") && path == path.Trim() && !path.Any(char.IsControl) && path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");
        }
        private static int Write(ZipArchive zip, string path, string text)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false))) writer.Write(text);
            return Encoding.UTF8.GetByteCount(text);
        }
        private static string Read(ZipArchive zip, string path, int maximum)
        {
            var entry = zip.GetEntry(path);
            if (entry == null || entry.Length > maximum) throw new InvalidDataException("Отсутствует или превышен размер: " + path);
            using (var stream = entry.Open())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                { if (output.Length + count > maximum) throw new InvalidDataException("Превышен размер файла."); output.Write(buffer, 0, count); }
                if (output.Length != entry.Length) throw new InvalidDataException("Неполный файл.");
                return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
        }
    }
    public sealed class ArtifactTransferOrigin
    {
        public string ResourceUri { get; set; }
        public string ContentSha256 { get; set; }
    }
    public sealed class ArtifactImportMetadata
    {
        [JsonProperty("importedFromUri")] public string ImportedFromUri { get; set; }
        [JsonProperty("importedSourceContentSha256")] public string ImportedSourceContentSha256 { get; set; }
        [JsonProperty("activeFileId")] public string ActiveFileId { get; set; }
        [JsonProperty("fileCount")] public int FileCount { get; set; }
        [JsonProperty("dataSourceCount")] public int DataSourceCount { get; set; }
    }
    public sealed class ArtifactTransferFile
    {
        public string Path { get; set; }
        public string Kind { get; set; }
        public string Content { get; set; }
    }
    public sealed class ArtifactTransferData
    {
        public string Name { get; set; }
        public string Json { get; set; }
        public string SchemaPath { get; set; }
        public string MappingPath { get; set; }
        [JsonIgnore] public string SchemaJson { get; set; }
        [JsonIgnore] public string MappingJson { get; set; }
        public string View { get; set; } = "text";
        public string ViewPath { get; set; }
    }
}
