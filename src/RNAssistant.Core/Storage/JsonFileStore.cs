using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RNAssistant.Core.Storage
{
    public sealed class JsonFileStore
    {
        public T Load<T>(string path, T fallback)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                return fallback;
            }
            catch (DirectoryNotFoundException)
            {
                return fallback;
            }

            try
            {
                var value = JsonConvert.DeserializeObject<T>(json);
                if (value == null) throw new InvalidDataException("Stored JSON value is null: " + path);
                return value;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Stored JSON is invalid: " + path, ex);
            }
        }

        public void Save<T>(string path, T value)
        {
            Save(path, value, null);
        }

        public void Save<T>(string path, T value, JsonSerializerSettings settings)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            StorageFileSystem.WriteAtomic(path, tempPath =>
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var textWriter = new StreamWriter(stream, new UTF8Encoding(false)))
                using (var jsonWriter = new JsonTextWriter(textWriter) { Formatting = Formatting.Indented })
                {
                    var serializer = settings == null
                        ? JsonSerializer.CreateDefault()
                        : JsonSerializer.Create(settings);
                    serializer.Serialize(jsonWriter, value);
                }
            });
        }
    }
}
