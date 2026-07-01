using System;

namespace RNAssistant.Office
{
    public static class DocumentIdentity
    {
        public const string PropertyName = "RNAssistantDocumentId";
        private const int MsoPropertyTypeString = 4;

        public static string ForOfficeDocument(string host, string persistentPath, string runtimeKey, Func<object> customPropertiesFactory)
        {
            var fallback = string.IsNullOrWhiteSpace(persistentPath)
                ? runtimeKey
                : (host ?? string.Empty) + ":Path:" + persistentPath;
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

        private static string ReadProperty(object properties, string name)
        {
            try
            {
                dynamic value = properties;
                var property = value[name];
                return Convert.ToString(property.Value);
            }
            catch
            {
                try
                {
                    var item = properties.GetType().GetProperty("Item");
                    var property = item == null ? null : item.GetValue(properties, new object[] { name });
                    var value = property == null ? null : property.GetType().GetProperty("Value");
                    return Convert.ToString(value == null ? null : value.GetValue(property, null));
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private static bool TryAddProperty(object properties, string name, string value)
        {
            try
            {
                dynamic target = properties;
                target.Add(name, false, MsoPropertyTypeString, value);
                return true;
            }
            catch
            {
                try
                {
                    var add = properties.GetType().GetMethod("Add");
                    if (add != null)
                    {
                        add.Invoke(properties, new object[] { name, false, MsoPropertyTypeString, value });
                        return true;
                    }
                }
                catch
                {
                }

                try
                {
                    dynamic target = properties;
                    var property = target[name];
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
