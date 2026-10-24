using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Shared admission for DOM evidence. The caller must bind the trusted exact
    // source separately; this payload never grants file or mutation authority.
    public static class HtmlVisualSelectionPolicy
    {
        public const int MaximumBodyBytes = 20480;

        public static string ReadCapturedBody(byte[] bytes)
        {
            ValidateBody(bytes);
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        public static HtmlVisualAnnotation ReadAnnotation(byte[] bytes)
        {
            var envelope = ValidateBody(bytes);
            return new HtmlVisualAnnotation { Selection = envelope["selection"].ToObject<HtmlVisualSelection>(),
                Comment = (string)envelope["comment"] ?? "",
                AnnotationNumber = (int?)envelope["annotationNumber"] ?? 0 };
        }

        private static JObject ValidateBody(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBodyBytes)
                throw new InvalidOperationException("Снимок HTML-элемента недействителен.");
            JObject envelope;
            try
            {
                envelope = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
                if ((string)envelope["type"] != "rnassistant.html-visual-selection" ||
                    (int?)envelope["version"] != 1 || envelope["selection"]?.Type != JTokenType.Object ||
                    envelope["source"]?.Type != JTokenType.String ||
                    !bytes.SequenceEqual(Capture(envelope["selection"].ToObject<HtmlVisualSelection>(),
                        (string)envelope["source"], (string)envelope["comment"],
                        (int?)envelope["annotationNumber"] ?? 0)))
                    throw new InvalidOperationException("Снимок HTML-элемента недействителен.");
            }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException ||
                error is FormatException || error is InvalidCastException || error is ArgumentException)
            { throw new InvalidOperationException("Снимок HTML-элемента недействителен.", error); }
            return envelope;
        }

        public static byte[] Capture(HtmlVisualSelection selection, string sourceTitle,
            string comment = null, int annotationNumber = 0)
        {
            if (annotationNumber < 0 || annotationNumber > 1000000 ||
                (annotationNumber == 0 && comment != null) ||
                (annotationNumber > 0 && (comment == null || comment.Length > 2048)))
                throw new InvalidOperationException("Комментарий аннотации недействителен.");
            if (selection == null || !Tag(selection.Tag) || selection.Selector == null ||
                selection.Selector.Length > 1024 || selection.StableUiId == null || selection.StableUiId.Length > 128 ||
                selection.TextExcerpt == null || selection.TextExcerpt.Length > 512 ||
                selection.Attributes == null || selection.Attributes.Count > 12 ||
                selection.Ancestry == null || selection.Ancestry.Count > 8 ||
                (selection.StableUiId.Length > 0 && (!selection.Attributes.ContainsKey("data-ui-id") ||
                    selection.Attributes["data-ui-id"] != selection.StableUiId)) ||
                !Attributes(selection.Attributes) || selection.Ancestry.Any(item =>
                    item == null || !Tag(item.Tag) || item.Attributes == null ||
                    item.Attributes.Count > 12 || !Attributes(item.Attributes)) ||
                (selection.Selector.Length == 0 && selection.StableUiId.Length == 0) ||
                selection.Rect == null || selection.Viewport == null ||
                !Number(selection.Rect.X) || !Number(selection.Rect.Y) ||
                !Number(selection.Rect.Width) || !Number(selection.Rect.Height) ||
                !Number(selection.Viewport.Width) || !Number(selection.Viewport.Height) ||
                !Number(selection.Viewport.Dpr) || selection.Rect.Width < 0 || selection.Rect.Height < 0 ||
                selection.Viewport.Width <= 0 ||
                selection.Viewport.Height <= 0 || selection.Viewport.Dpr <= 0)
                throw new InvalidOperationException("Снимок HTML-элемента недействителен.");
            var serialized = JsonConvert.SerializeObject(selection);
            if (Encoding.UTF8.GetByteCount(serialized) > 16384)
                throw new InvalidOperationException("Снимок HTML-элемента слишком велик.");
            var title = (sourceTitle ?? "HTML-проект").Trim();
            if (title.Length == 0) title = "HTML-проект";
            if (title.Length > 256) title = title.Substring(0, 256);
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
                type = "rnassistant.html-visual-selection", version = 1,
                source = title, evidence = "untrusted preview DOM; source mapping unverified", selection,
                comment, annotationNumber = annotationNumber > 0 ? (int?)annotationNumber : null
            }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            if (bytes.Length > MaximumBodyBytes)
                throw new InvalidOperationException("Снимок HTML-элемента слишком велик.");
            return bytes;
        }

        private static bool Tag(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32 || value[0] < 'a' || value[0] > 'z') return false;
            return value.Skip(1).All(character => character >= 'a' && character <= 'z' ||
                character >= '0' && character <= '9' || character == '-');
        }

        private static bool Attributes(Dictionary<string, string> attributes)
        {
            return attributes.All(item =>
                (item.Key == "id" || item.Key == "class" || item.Key == "role" ||
                 item.Key == "data-ui-id" || item.Key == "data-testid") &&
                item.Value != null && item.Value.Length <= 128 &&
                !Regex.IsMatch(item.Value, "password|secret|token|api.?key|bearer|credential|authorization|eyJ[A-Za-z0-9_-]{16,}|sk-[A-Za-z0-9_-]{12,}", RegexOptions.IgnoreCase));
        }

        private static bool Number(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 10000000; }
    }
}
