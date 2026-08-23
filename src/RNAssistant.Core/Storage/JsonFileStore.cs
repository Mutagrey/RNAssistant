using System;
using System.IO;
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

            var json = settings == null
                ? JsonConvert.SerializeObject(value, Formatting.Indented)
                : JsonConvert.SerializeObject(value, Formatting.Indented, settings);
            StorageFileSystem.WriteAllTextAtomic(path, json);
        }
    }
}
