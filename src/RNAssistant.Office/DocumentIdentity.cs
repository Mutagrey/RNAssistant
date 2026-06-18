using System;

namespace RNAssistant.Office
{
    public static class DocumentIdentity
    {
        public const string PropertyName = "RNAssistantDocumentId";
        private const int MsoPropertyTypeString = 4;

        public static string ForOfficeDocument(string host, string persistentPath, string runtimeKey, Func<object> customPropertiesFactory)
        {
            if (string.IsNullOrWhiteSpace(persistentPath))
            {
                return runtimeKey;
            }

            var fallback = (host ?? string.Empty) + ":Path:" + persistentPath;
            try
            {
                var properties = customPropertiesFactory == null ? null : customPropertiesFactory();
                if (properties == null)
                {
                    return fallback;
                }

                var existing = ReadProperty(properties, PropertyName);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return Key(host, existing);
                }

                var id = Guid.NewGuid().ToString("N");
                return TryAddProperty(properties, PropertyName, id) ? Key(host, id) : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static string Key(string host, string id)
        {
            return (host ?? string.Empty) + ":DocumentId:" + (id ?? string.Empty).Trim();
        }

        private static string ReadProperty(dynamic properties, string name)
        {
            try
            {
                var property = properties[name];
                return Convert.ToString(property.Value);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryAddProperty(dynamic properties, string name, string value)
        {
            try
            {
                properties.Add(name, false, MsoPropertyTypeString, value);
                return true;
            }
            catch
            {
                try
                {
                    var property = properties[name];
                    property.Value = value;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
