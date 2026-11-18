using System;
using System.Data.Common;

namespace RNAssistant.Office.Domains.Excel
{
    // Connection display names do not identify a query. Parse the standard
    // connection-string grammar, including quoted semicolons and escaped quotes.
    public static class ExcelPowerQueryConnection
    {
        public const string MashupProvider = "Microsoft.Mashup.OleDb.1";

        public static bool TryGetMashupLocation(string connection, out string location)
        {
            location = null;
            if (string.IsNullOrWhiteSpace(connection)) return false;
            var text = connection.Trim();
            if (text.StartsWith("OLEDB;", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(6);
            try
            {
                var builder = new DbConnectionStringBuilder { ConnectionString = text };
                object provider;
                object value;
                if (!builder.TryGetValue("Provider", out provider) ||
                    !string.Equals(Convert.ToString(provider), MashupProvider,
                        StringComparison.OrdinalIgnoreCase) ||
                    !builder.TryGetValue("Location", out value)) return false;
                location = Convert.ToString(value);
                if (string.IsNullOrEmpty(location))
                {
                    location = null;
                    return false;
                }
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static string BuildMashupConnection(string queryName)
        {
            if (string.IsNullOrWhiteSpace(queryName))
                throw new ArgumentException("A query name is required.", nameof(queryName));
            var builder = new DbConnectionStringBuilder
            {
                ["Provider"] = MashupProvider,
                ["Data Source"] = "$Workbook$",
                ["Location"] = queryName,
                ["Extended Properties"] = string.Empty
            };
            return "OLEDB;" + builder.ConnectionString;
        }

        public static string BuildSelectCommand(string queryName)
        {
            if (string.IsNullOrWhiteSpace(queryName))
                throw new ArgumentException("A query name is required.", nameof(queryName));
            return "SELECT * FROM [" + queryName.Replace("]", "]]") + "]";
        }
    }
}
