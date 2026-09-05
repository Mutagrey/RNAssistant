using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ResourceSchemaValidator
    {
        public static void ValidateFields(IReadOnlyList<SemanticResourceField> fields)
        {
            if (fields == null || fields.Count == 0 || fields.Count > 128 || fields.Any(field => field == null ||
                !Regex.IsMatch(field.Name ?? "", @"\A[a-z][a-z0-9_]{0,63}\z") ||
                !new[] { "string", "number", "integer", "boolean", "date", "any" }.Contains(field.Type, StringComparer.Ordinal) ||
                (field.Unit ?? "").Length > 64) || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Count)
                throw new InvalidOperationException("Schema fields must have unique semantic names and explicit supported types/units.");
        }

        public static void ValidateMapping(IReadOnlyList<SemanticResourceField> fields,
            IReadOnlyList<ResourceFieldMapping> mapping, ResourceTableBatch sample)
        {
            ValidateFields(fields);
            if (mapping == null || mapping.Count != fields.Count || mapping.Any(item => item == null) ||
                mapping.Select(item => item.Field).Distinct(StringComparer.Ordinal).Count() != mapping.Count ||
                fields.Any(field => !mapping.Any(item => item.Field == field.Name)) || sample == null ||
                mapping.Any(item => !sample.Columns.Any(column => column.Key == item.SourceField)))
                throw new InvalidOperationException("Every semantic field must map to an exact structural source field.");
            foreach (var row in sample.Rows)
            foreach (var field in fields)
            {
                var key = mapping.Single(item => item.Field == field.Name).SourceField;
                object value; row.TryGetValue(key, out value);
                var token = value == null ? JValue.CreateNull() : JToken.FromObject(value);
                var valid = token.Type == JTokenType.Null ? field.Nullable :
                    field.Type == "any" || field.Type == "string" && token.Type == JTokenType.String ||
                    field.Type == "number" && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ||
                    field.Type == "integer" && token.Type == JTokenType.Integer || field.Type == "boolean" && token.Type == JTokenType.Boolean ||
                    field.Type == "date" && token.Type == JTokenType.String && IsIsoDate((string)token);
                if (!valid) throw new InvalidOperationException("Source rows do not satisfy semantic field " + field.Name + " (" + field.Type + ").");
            }
        }

        private static bool IsIsoDate(string value)
        {
            DateTimeOffset parsed;
            return value != null && Regex.IsMatch(value, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}(?:T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2}))?\z") &&
                DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed);
        }
    }
}
