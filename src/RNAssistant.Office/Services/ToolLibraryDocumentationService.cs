using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    // Human-facing documentation is generated from source-owned registrations.
    // It is deliberately not stored on ToolCatalogEntry and therefore cannot
    // enter model descriptors, callable-pack revisions or token accounting.
    internal static class ToolLibraryDocumentationService
    {
        internal const int MaximumBytes = 2 * 1024 * 1024;
        internal static string Build(ToolCatalogEntry tool)
        {
            if (tool == null || !tool.BuiltIn ||
                string.IsNullOrWhiteSpace(tool.Id))
                throw new InvalidOperationException(
                    "Built-in tool documentation requires an exact tool.");
            if (tool.Policy == null)
                throw new InvalidOperationException(
                    "Built-in tool documentation requires runtime policy authority.");

            JObject schema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError))
                throw new InvalidOperationException(
                    "Built-in tool schema is invalid: " + schemaError);

            var text = new StringBuilder();
            text.Append("# `").Append(Code(tool.Id)).AppendLine("`");
            text.AppendLine();
            text.AppendLine("## Назначение");
            text.AppendLine();
            text.AppendLine(Plain(tool.Description, "Описание отсутствует."));
            AppendOptionalParagraph(text, "Используйте, когда", tool.UseWhen);
            AppendOptionalParagraph(text, "Не используйте, когда", tool.DoNotUseWhen);

            text.AppendLine();
            text.AppendLine("## Цель и контекст");
            text.AppendLine();
            text.Append("- Приложение: `").Append(Code(tool.Host ?? "Common"))
                .AppendLine("`.");
            text.Append("- Область: `").Append(Code(tool.Scope ?? "global"))
                .AppendLine("`; цель выбирается только семантическими аргументами ниже.");
            text.Append("- Доступные режимы: ")
                .Append(string.Join(", ", tool.Policy.AllowedModes
                    .Select(mode => "`" + Code(mode) + "`")))
                .AppendLine(".");

            text.AppendLine();
            text.AppendLine("## Аргументы");
            text.AppendLine();
            AppendArguments(text, schema);
            AppendVariants(text, schema);

            text.AppendLine();
            text.AppendLine("## Безопасность и эффект");
            text.AppendLine();
            text.Append("- Эффект: **").Append(Effect(tool.Policy.Effect))
                .AppendLine("**.");
            text.Append("- Подтверждение: ")
                .Append(tool.Policy.RequiresConfirmation
                    ? "обязательно перед dispatch"
                    : "не требуется")
                .AppendLine(".");
            text.Append("- Проверка результата: ")
                .Append(tool.Policy.Verification == ToolVerification.Tool
                    ? "tool выполняет собственный read-back/verification"
                    : "отдельная проверка контрактом не заявлена")
                .AppendLine(".");
            text.Append("- Уровень риска: `")
                .Append(tool.Policy.RiskLevel).AppendLine("`.");

            text.AppendLine();
            text.AppendLine("## Результат и частые ошибки");
            text.AppendLine();
            text.AppendLine("Library показывает строгий ToolRunResult v1: статус, сообщение и typed JSON data. Статус `ok` подтверждает успешное завершение runtime, но сам по себе не доказывает изменение документа; для write-tools учитывайте verification/effect evidence.");
            text.AppendLine();
            text.AppendLine("Частые причины ошибки: отсутствует обязательный аргумент, неверен JSON-тип или ограничение, семантическая цель неоднозначна, активный Office target изменился, capability недоступна либо пользователь отклонил подтверждение. Не подставляйте URI, UUID, revision, hash, cursor или guard: эти значения принадлежат runtime.");

            text.AppendLine();
            text.AppendLine("## Ограничения");
            text.AppendLine();
            text.AppendLine(Plain(tool.Limitations,
                "Дополнительные ограничения не объявлены; действуют schema, policy и текущая доступность Office target."));

            text.AppendLine();
            text.AppendLine("## Безопасная проверка в Library");
            text.AppendLine();
            text.AppendLine("1. Заполните только показанные семантические поля; необязательные значения оставьте в режиме «Не передавать».");
            text.AppendLine("2. Нажмите «Проверить». Для mutation это валидирует вызов без dispatch; read-only tool может выполнить безопасное локальное чтение.");
            text.AppendLine("3. Перед «Запустить» ещё раз проверьте цель и ожидаемый эффект. Если требуется confirmation, подтвердите точный подготовленный вызов.");
            text.AppendLine("4. Для paged reference read используйте кнопку «Далее» только после `hasMore=true`; opaque cursor остаётся внутри runtime.");
            return text.ToString().Trim();
        }

        private static void AppendArguments(StringBuilder text, JObject schema)
        {
            var properties = schema["properties"] as JObject;
            if (properties == null || !properties.Properties().Any())
            {
                text.AppendLine("Аргументы отсутствуют.");
                return;
            }
            foreach (var property in properties.Properties())
            {
                AppendArgument(text, property.Name,
                    property.Value as JObject ?? new JObject(),
                    Requirement(schema, property.Name), 0);
            }
        }

        private static void AppendArgument(StringBuilder text, string path,
            JObject schema, string requirement, int depth)
        {
            var indent = new string(' ', depth * 2);
            text.Append(indent).Append("- `").Append(Code(path)).Append("` — ")
                .Append(requirement).Append(", тип ")
                .Append(TypeText(schema)).AppendLine(".");
            var description = (string)schema["description"];
            if (!string.IsNullOrWhiteSpace(description))
                text.Append(indent).Append("  ").AppendLine(Plain(description, null));
            var constraints = Constraints(schema);
            if (constraints.Count > 0)
                text.Append(indent).Append("  Ограничения: ")
                    .Append(string.Join("; ", constraints)).AppendLine(".");

            var properties = schema["properties"] as JObject;
            foreach (var child in properties == null
                ? Enumerable.Empty<JProperty>() : properties.Properties())
            {
                AppendArgument(text, path + "." + child.Name,
                    child.Value as JObject ?? new JObject(),
                    Requirement(schema, child.Name), depth + 1);
            }
            var items = schema["items"] as JObject;
            if (items != null && (items["properties"] is JObject ||
                items["description"] != null))
            {
                AppendArgument(text, path + "[]", items,
                    "элемент массива", depth + 1);
            }
        }

        private static void AppendVariants(StringBuilder text, JObject schema)
        {
            var variants = schema["anyOf"] as JArray;
            if (variants == null || variants.Count == 0) return;
            text.AppendLine();
            text.AppendLine("Допустимые сочетания:");
            var index = 1;
            foreach (var variant in variants.OfType<JObject>())
            {
                var details = new List<string>();
                var properties = variant["properties"] as JObject;
                foreach (var property in properties == null
                    ? Enumerable.Empty<JProperty>() : properties.Properties())
                {
                    var propertySchema = property.Value as JObject;
                    if (propertySchema == null) continue;
                    if (propertySchema["const"] != null)
                        details.Add("`" + Code(property.Name) + "`=" +
                            InlineJson(propertySchema["const"]));
                    else if (propertySchema["enum"] is JArray &&
                        ((JArray)propertySchema["enum"]).Count == 1)
                        details.Add("`" + Code(property.Name) + "`=" +
                            InlineJson(((JArray)propertySchema["enum"])[0]));
                }
                var required = (variant["required"] as JArray ?? new JArray())
                    .Values<string>().Select(value => "`" + Code(value) + "`")
                    .ToArray();
                if (required.Length > 0)
                    details.Add("обязательны " + string.Join(", ", required));
                text.Append("- Вариант ").Append(index++).Append(": ")
                    .Append(details.Count == 0
                        ? "см. ограничения полей"
                        : string.Join("; ", details))
                    .AppendLine(".");
            }
        }

        private static string Requirement(JObject parent, string name)
        {
            var required = parent["required"] as JArray ?? new JArray();
            if (required.Values<string>().Contains(name, StringComparer.Ordinal))
                return "обязательный";
            var variants = parent["anyOf"] as JArray;
            if (variants != null && variants.OfType<JObject>().Any(variant =>
                (variant["required"] as JArray ?? new JArray())
                    .Values<string>().Contains(name, StringComparer.Ordinal)))
                return "условно обязательный";
            return "необязательный";
        }

        private static string TypeText(JObject schema)
        {
            var values = new List<string>();
            var type = schema["type"];
            if (type != null && type.Type == JTokenType.String)
                values.Add((string)type);
            else if (type is JArray)
                values.AddRange(((JArray)type).Values<string>());
            var alternatives = schema["anyOf"] as JArray;
            foreach (var alternative in alternatives == null
                ? Enumerable.Empty<JObject>() : alternatives.OfType<JObject>())
            {
                var candidate = TypeText(alternative);
                if (!string.IsNullOrWhiteSpace(candidate))
                    values.AddRange(candidate.Split(new[] { " / " },
                        StringSplitOptions.RemoveEmptyEntries));
            }
            values = values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToList();
            return values.Count == 0 ? "JSON value" : string.Join(" / ",
                values.Select(value => "`" + Code(value) + "`"));
        }

        private static List<string> Constraints(JObject schema)
        {
            var result = new List<string>();
            if (schema["const"] != null)
                result.Add("const " + InlineJson(schema["const"]));
            if (schema["enum"] is JArray)
                result.Add("enum " + string.Join(", ",
                    ((JArray)schema["enum"]).Select(InlineJson)));
            if (schema["default"] != null)
                result.Add("default " + InlineJson(schema["default"]));
            AppendConstraint(result, schema, "minimum", "min");
            AppendConstraint(result, schema, "maximum", "max");
            AppendConstraint(result, schema, "minLength", "minLength");
            AppendConstraint(result, schema, "maxLength", "maxLength");
            AppendConstraint(result, schema, "minItems", "minItems");
            AppendConstraint(result, schema, "maxItems", "maxItems");
            return result;
        }

        private static void AppendConstraint(ICollection<string> target,
            JObject schema, string property, string label)
        {
            if (schema[property] != null)
                target.Add(label + " " + InlineJson(schema[property]));
        }

        private static string Effect(ToolEffect effect)
        {
            switch (effect)
            {
                case ToolEffect.Read: return "чтение";
                case ToolEffect.Write: return "изменение";
                case ToolEffect.External: return "внешний эффект";
                default: return "неклассифицированный эффект";
            }
        }

        private static void AppendOptionalParagraph(StringBuilder text,
            string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            text.AppendLine();
            text.Append("**").Append(label).Append(":** ")
                .AppendLine(Plain(value, null));
        }

        private static string InlineJson(JToken value)
        {
            return "`" + Code(value == null
                ? "null" : value.ToString(Formatting.None)) + "`";
        }

        private static string Plain(string value, string fallback)
        {
            var normalized = (value ?? string.Empty)
                .Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length == 0 ? fallback ?? string.Empty : normalized;
        }

        private static string Code(string value)
        {
            return (value ?? string.Empty).Replace("`", "'");
        }
    }
}
