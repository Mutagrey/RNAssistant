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
    // Admission of outline SVG. Only geometry reaches the UI; styling belongs to the icon registry.
    internal static class PluginIconSvg
    {
        internal static string[] Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 4096) Fail("Use 1–4096 UTF-8 bytes.");
            try
            {
                using (var reader = XmlReader.Create(new StringReader(PluginPackageFiles.Utf8.GetString(bytes)),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
                {
                    var root = XElement.Load(reader);
                    if (root.Name.LocalName != "svg" || (string)root.Attribute("viewBox") != "0 0 24 24")
                        Fail("Use an SVG with viewBox=\"0 0 24 24\".");
                    var paths = new List<string>();
                    ReadElement(root, paths, 0);
                    if (paths.Count == 0 || paths.Count > 16) Fail("Use 1–16 visible outline shapes.");
                    return paths.ToArray();
                }
            }
            catch (Exception error) when (error is XmlException || error is DecoderFallbackException)
            { throw new PluginException("plugin_icon_invalid", "The SVG icon is not valid UTF-8 XML."); }
        }

        private static void ReadElement(XElement element, List<string> paths, int depth)
        {
            if (depth > 8 || paths.Count > 16) Fail("The SVG icon is too complex.");
            if (element.Name.NamespaceName != "" && element.Name.NamespaceName != "http://www.w3.org/2000/svg")
                Fail("Only SVG elements are supported.");
            var name = element.Name.LocalName;
            var geometry = name == "svg" && depth == 0 ? "viewBox width height" : name == "g" ? "" :
                name == "path" ? "d" : name == "circle" ? "cx cy r" : name == "ellipse" ? "cx cy rx ry" :
                name == "rect" ? "x y width height rx ry" : name == "line" ? "x1 y1 x2 y2" :
                name == "polyline" || name == "polygon" ? "points" : null;
            if (geometry == null) Fail("Unsupported SVG element: " + name + ". Use outline shapes only.");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration && attribute.Name.LocalName == "xmlns" && attribute.Value == "http://www.w3.org/2000/svg") continue;
                if (attribute.Name.NamespaceName != "") Fail("SVG attribute namespaces are unsupported.");
                var key = attribute.Name.LocalName; var value = attribute.Value;
                if (geometry.Split(' ').Contains(key)) continue;
                if (key == "fill" && value == "none") continue;
                if (key == "stroke" && Regex.IsMatch(value, @"^(currentColor|black|white|#[a-fA-F0-9]{3}|#[a-fA-F0-9]{6})$")) continue;
                if (key == "stroke-width" && Positive(value) <= 4) continue;
                if (key == "stroke-linecap" && new[] { "round", "butt", "square" }.Contains(value)) continue;
                if (key == "stroke-linejoin" && new[] { "round", "miter", "bevel" }.Contains(value)) continue;
                Fail("Unsupported SVG attribute: " + key + ". Use fill=\"none\", a plain stroke and no styles, links or transforms.");
            }
            if (element.Nodes().Any(node => !(node is XElement) && !(node is XComment) &&
                !(node is XText && string.IsNullOrWhiteSpace(((XText)node).Value)))) Fail("SVG text is unsupported.");
            if (name == "svg" || name == "g")
            {
                foreach (var child in element.Elements()) ReadElement(child, paths, depth + 1);
                return;
            }
            if (element.HasElements) Fail("Shapes cannot contain other elements.");
            string path;
            if (name == "path") path = (string)element.Attribute("d");
            else if (name == "line") path = "M" + N(element, "x1") + " " + N(element, "y1") + "L" + N(element, "x2") + " " + N(element, "y2");
            else if (name == "polyline" || name == "polygon")
            {
                var points = Regex.Split(((string)element.Attribute("points") ?? "").Trim(), @"[\s,]+");
                if (points.Length < 4 || points.Length % 2 != 0) Fail("Use complete x,y pairs in points.");
                path = "M" + string.Join(" ", points.Select(point => F(Number(point)))) + (name == "polygon" ? "Z" : "");
            }
            else if (name == "circle" || name == "ellipse")
            {
                double cx = V(element, "cx"), cy = V(element, "cy");
                var rx = Positive((string)element.Attribute(name == "circle" ? "r" : "rx"));
                var ry = name == "circle" ? rx : Positive((string)element.Attribute("ry"));
                path = "M" + F(cx - rx) + " " + F(cy) + "a" + F(rx) + " " + F(ry) + " 0 1 0 " + F(2 * rx) + " 0a" +
                    F(rx) + " " + F(ry) + " 0 1 0 " + F(-2 * rx) + " 0Z";
            }
            else
            {
                double x = V(element, "x"), y = V(element, "y");
                double w = Positive((string)element.Attribute("width")), h = Positive((string)element.Attribute("height"));
                double rx = V(element, "rx", V(element, "ry")), ry = V(element, "ry", rx);
                if (rx < 0 || ry < 0) Fail("Corner radii cannot be negative.");
                rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
                if (rx == 0 || ry == 0) path = "M" + F(x) + " " + F(y) + "h" + F(w) + "v" + F(h) + "h" + F(-w) + "Z";
                else
                {
                    var arc = "a" + F(rx) + " " + F(ry) + " 0 0 1 ";
                    path = "M" + F(x + rx) + " " + F(y) + "h" + F(w - 2 * rx) + arc + F(rx) + " " + F(ry) +
                        "v" + F(h - 2 * ry) + arc + F(-rx) + " " + F(ry) + "h" + F(2 * rx - w) + arc + F(-rx) + " " + F(-ry) +
                        "v" + F(2 * ry - h) + arc + F(rx) + " " + F(-ry) + "Z";
                }
            }
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
                !Regex.IsMatch(path, @"^[Mm]\s*[+\-]?(?:\d|\.)[MmLlHhVvCcSsQqTtAaZzEe0-9\s,.+\-]*$", RegexOptions.CultureInvariant))
                Fail("Use a valid path starting with M, at most 1024 characters.");
            paths.Add(path);
        }

        private static double V(XElement element, string name, double fallback = 0)
        { var value = (string)element.Attribute(name); return value == null ? fallback : Number(value); }
        private static string N(XElement element, string name) { return F(V(element, name)); }
        private static string F(double value) { return value.ToString("0.########", CultureInfo.InvariantCulture); }
        private static double Number(string value)
        {
            double result;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
                double.IsNaN(result) || double.IsInfinity(result) || Math.Abs(result) > 1024) Fail("Use finite SVG coordinates.");
            return result;
        }
        private static double Positive(string value)
        { var result = Number(value); if (result <= 0) Fail("SVG dimensions must be positive."); return result; }
        private static void Fail(string message) { throw new PluginException("plugin_icon_invalid", message); }
    }
}
