using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RNAssistant.Office
{
    public static class DocumentIdentity
    {
        public const string PropertyName = "RNAssistantDocumentId";

        public static string ForOfficeDocument(string host, string persistentPath, string runtimeKey, Func<object> customPropertiesFactory)
        {
            var fallback = string.IsNullOrWhiteSpace(persistentPath)
                ? runtimeKey
                : (host ?? string.Empty) + ":Path:" + persistentPath.Trim();
            if (string.IsNullOrWhiteSpace(persistentPath))
            {
                return fallback;
            }

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
                return fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public static string RuntimeKey(string host, object document)
        {
            var prefix = (host ?? string.Empty) + ":Runtime:";
            if (document == null)
            {
                return prefix + "none";
            }

            IntPtr identity = IntPtr.Zero;
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT && Marshal.IsComObject(document))
                {
#pragma warning disable CA1416
                    identity = Marshal.GetIUnknownForObject(document);
#pragma warning restore CA1416
                    return prefix + identity.ToInt64().ToString("x");
                }
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (NotSupportedException)
            {
            }
            finally
            {
                if (identity != IntPtr.Zero)
                {
                    Marshal.Release(identity);
                }
            }

            return prefix + RuntimeHelpers.GetHashCode(document).ToString("x");
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

    }
}
