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
            catch (JsonException)
            {
                return fallback;
            }
        }

        public void Save<T>(string path, T value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(value, Formatting.Indented);
            File.WriteAllText(path, json);
        }
    }
}
