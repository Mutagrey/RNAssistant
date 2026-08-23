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
            try
            {
                if (!File.Exists(path))
                {
                    return fallback;
                }

                var json = File.ReadAllText(path);
                var value = JsonConvert.DeserializeObject<T>(json);
                return value == null ? fallback : value;
            }
            catch (IOException)
            {
                return fallback;
            }
            catch (UnauthorizedAccessException)
            {
                return fallback;
            }
            catch (JsonException)
            {
                return fallback;
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
