using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RNAssistant.Core.Storage
{
    // Static SVG artwork stays SVG. Only its viewport is normalized; paint and geometry survive.
    public static class PluginIconSvg
    {
        public const int MaximumBytes = 128 * 1024;
        private const string Namespace = "http://www.w3.org/2000/svg";
        private static readonly HashSet<string> Elements = new HashSet<string>((
            "svg g defs path rect circle ellipse line polyline polygon text tspan textPath title desc style " +
            "linearGradient radialGradient stop pattern clipPath mask marker filter use symbol switch view " +
            "feBlend feColorMatrix feComponentTransfer feComposite feConvolveMatrix feDiffuseLighting " +
            "feDisplacementMap feDistantLight feDropShadow feFlood feFuncA feFuncB feFuncG feFuncR " +
            "feGaussianBlur feMerge feMergeNode feMorphology feOffset fePointLight feSpecularLighting " +
            "feSpotLight feTile feTurbulence").Split(' '), StringComparer.Ordinal);

        public static string Normalize(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes)
                Fail("SVG must contain 1–131072 UTF-8 bytes.");
            try
            {
                XElement root;
                using (var reader = XmlReader.Create(new StringReader(PluginPackageFiles.Utf8.GetString(bytes)),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes }))
                    root = XElement.Load(reader);
                if (root.Name.LocalName != "svg") Fail("Return one complete SVG document.");
                var elements = root.DescendantsAndSelf().ToArray();
                if (elements.Length > 2048) Fail("The SVG exceeds 2048 elements.");
                foreach (var element in elements)
                {
                    if (element.Ancestors().Take(65).Count() > 64) Fail("The SVG is nested too deeply.");
                    if (element.Name.NamespaceName != "" && element.Name.NamespaceName != Namespace || !Elements.Contains(element.Name.LocalName))
                        Fail("Unsupported active or non-vector SVG element: " + element.Name.LocalName + ". Use self-contained static SVG.");
                    element.Name = XName.Get(element.Name.LocalName, Namespace);
                    foreach (var attribute in element.Attributes().ToArray())
                    {
                        if (attribute.IsNamespaceDeclaration) { attribute.Remove(); continue; }
                        var name = attribute.Name.LocalName;
                        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                            attribute.Name.NamespaceName != "" && !(attribute.Name.NamespaceName == "http://www.w3.org/1999/xlink" && name == "href") &&
                            !(attribute.Name.Namespace == XNamespace.Xml && (name == "space" || name == "lang")))
                            Fail("Scripts, event handlers and external namespaces are not supported in icons.");
                        if (name == "href") RequireLocalReference(attribute.Value);
                        if (name == "style") CheckCss(attribute.Value);
                        else CheckUrls(attribute.Value);
                    }
                    if (element.Name.LocalName == "style") CheckCss(element.Value);
                    foreach (var instruction in element.Nodes().OfType<XProcessingInstruction>().ToArray()) instruction.Remove();
                    foreach (var comment in element.Nodes().OfType<XComment>().ToArray()) comment.Remove();
                }
                var viewBox = (string)root.Attribute("viewBox");
                if (string.IsNullOrWhiteSpace(viewBox))
                    viewBox = "0 0 " + Dimension((string)root.Attribute("width")) + " " + Dimension((string)root.Attribute("height"));
                var coordinates = Regex.Split(viewBox.Trim(), @"[\s,]+");
                if (coordinates.Length != 4) Fail("viewBox must contain x, y, width and height.");
                var numbers = coordinates.Select(Number).ToArray();
                if (numbers[2] <= 0 || numbers[3] <= 0) Fail("viewBox width and height must be positive.");
                root.SetAttributeValue("viewBox", string.Join(" ", numbers.Select(Format)));
                root.SetAttributeValue("width", "24"); root.SetAttributeValue("height", "24");
                root.SetAttributeValue("preserveAspectRatio", "xMidYMid meet");
                // Root CSS sizing must not override the icon's normalized viewport.
                var style = Regex.Replace((string)root.Attribute("style") ?? "",
                    @"(^|;)\s*(width|height|min-width|min-height|max-width|max-height)\s*:[^;]*", "$1", RegexOptions.IgnoreCase).Trim(' ', ';');
                root.SetAttributeValue("style", (style.Length == 0 ? "" : style + ";") + "width:24px!important;height:24px!important");
                var normalized = root.ToString(SaveOptions.DisableFormatting);
                if (PluginPackageFiles.Utf8.GetByteCount(normalized) > MaximumBytes)
                    Fail("The normalized SVG exceeds 131072 UTF-8 bytes. Reduce the artwork size.");
                return normalized;
            }
            catch (Exception error) when (error is XmlException || error is DecoderFallbackException)
            { throw new PluginException("plugin_icon_invalid", "SVG is not complete, valid UTF-8 XML: " + error.Message); }
        }

        public static string Color(string color)
        {
            if (string.IsNullOrEmpty(color)) return null;
            if (color == "original") return color;
            if (!Regex.IsMatch(color, @"\A#[a-fA-F0-9]{6}\z")) Fail("Choose a color in #RRGGBB format.");
            return color.ToLowerInvariant();
        }

        // One-way decoder for saved outline icons. New records never write path arrays.
        internal static string FromLegacyPaths(IEnumerable<string> paths)
        {
            var values = paths?.ToArray();
            if (values == null || values.Length == 0 || values.Any(string.IsNullOrWhiteSpace)) Fail("Saved outline geometry is missing.");
            var root = new XElement("svg", new XAttribute("viewBox", "0 0 24 24"), new XAttribute("fill", "none"),
                new XAttribute("stroke", "currentColor"), new XAttribute("stroke-width", "1.55"),
                new XAttribute("stroke-linecap", "round"), new XAttribute("stroke-linejoin", "round"),
                values.Select(path => new XElement("path", new XAttribute("d", path))));
            return Normalize(PluginPackageFiles.Utf8.GetBytes(root.ToString(SaveOptions.DisableFormatting)));
        }
        private static void RequireLocalReference(string value)
        {
            if (!Regex.IsMatch(value.Trim(), @"\A#[A-Za-z_][\w.:-]*\z", RegexOptions.CultureInvariant))
                Fail("Icons may reference local SVG IDs only; external resources are not supported.");
        }
        private static void CheckUrls(string value)
        {
            foreach (Match match in Regex.Matches(value, @"url\s*\(([^)]*)\)", RegexOptions.IgnoreCase))
                RequireLocalReference(match.Groups[1].Value.Trim().Trim('\'', '"'));
        }
        private static void CheckCss(string value)
        {
            var css = Regex.Replace(value, @"/\*[\s\S]*?\*/", "");
            if (css.Contains("\\") || Regex.IsMatch(css, @"@import|expression\s*\(|behavior\s*:|-moz-binding", RegexOptions.IgnoreCase))
                Fail("SVG styles must be static and self-contained.");
            CheckUrls(css);
        }
        private static string Dimension(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().EndsWith("%", StringComparison.Ordinal)) return "24";
            var match = Regex.Match(value.Trim(), @"\A([+\-]?(?:\d*\.)?\d+(?:[eE][+\-]?\d+)?)\s*(px|pt|pc|in|cm|mm)?\z");
            if (!match.Success) Fail("Specify a numeric width/height or a viewBox.");
            var factor = match.Groups[2].Value == "pt" ? 96.0 / 72 : match.Groups[2].Value == "pc" ? 16 :
                match.Groups[2].Value == "in" ? 96 : match.Groups[2].Value == "cm" ? 96 / 2.54 : match.Groups[2].Value == "mm" ? 96 / 25.4 : 1;
            return Format(Number(match.Groups[1].Value) * factor);
        }
        private static double Number(string value)
        {
            double number;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsInfinity(number) || double.IsNaN(number))
                Fail("SVG viewport dimensions must be finite numbers.");
            return number;
        }
        private static string Format(double value) { return value.ToString("G17", CultureInfo.InvariantCulture); }
        private static void Fail(string message) { throw new PluginException("plugin_icon_invalid", message); }
    }
}
