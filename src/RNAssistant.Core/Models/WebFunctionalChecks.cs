using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum WebCheckOperation
    {
        Click, TextEquals, TextContains, NumberEquals, SelectValue, InputValue,
        UploadCsv, TableEquals, BarChartEquals, DownloadCsvEquals
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum WebCheckStatus { Passed, Failed, NotRun }

    public sealed class WebCheckStep
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; private set; }
        [JsonProperty("operation", Required = Required.Always)] public WebCheckOperation Operation { get; private set; }
        [JsonProperty("selector", Required = Required.Always)] public string Selector { get; private set; }
        [JsonProperty("expected")] public string Expected { get; private set; }

        [JsonConstructor]
        public WebCheckStep(string id, WebCheckOperation operation, string selector, string expected = null)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(char.IsControl) ||
                !Enum.IsDefined(typeof(WebCheckOperation), operation) || string.IsNullOrWhiteSpace(selector) ||
                selector.Length > 256 || operation != WebCheckOperation.Click && (expected == null || expected.Length > 512) ||
                operation == WebCheckOperation.Click && expected != null)
                throw new ArgumentException("Invalid web check step (id, operation, selector or expected text).");
            Id = id; Operation = operation; Selector = selector; Expected = expected;
            if (operation == WebCheckOperation.TextContains && string.IsNullOrWhiteSpace(expected) ||
                operation == WebCheckOperation.NumberEquals && !Number(expected, out _) ||
                (operation == WebCheckOperation.TableEquals || operation == WebCheckOperation.DownloadCsvEquals) && Csv(expected) == null ||
                operation == WebCheckOperation.BarChartEquals && !BarValues(expected, out _))
                throw new ArgumentException("Invalid web check expectation for " + operation + ".");
        }

        [JsonIgnore] public bool IsAssertion { get { return Operation != WebCheckOperation.Click &&
            Operation != WebCheckOperation.SelectValue && Operation != WebCheckOperation.InputValue &&
            Operation != WebCheckOperation.UploadCsv; } }

        // Rechecked by the publication owner: a caller's Passed flag alone is
        // insufficient. Expected/actual CSV are bounded values, not file paths.
        public bool Accepts(string actual)
        {
            if (Operation == WebCheckOperation.Click) return actual == null;
            if (actual == null || actual.Length > 700) return false;
            if (Operation == WebCheckOperation.TextContains)
                return actual.IndexOf(Expected, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Operation == WebCheckOperation.NumberEquals)
                return Number(actual, out var number) && Number(Expected, out var expectedNumber) && number == expectedNumber;
            if (Operation == WebCheckOperation.TableEquals || Operation == WebCheckOperation.DownloadCsvEquals)
            {
                var expectedRows = Csv(Expected); var actualRows = Csv(actual);
                return actualRows != null && actualRows.Count == expectedRows.Count &&
                    actualRows.Select((row, i) => row.Length == expectedRows[i].Length &&
                        row.Select((cell, j) => cell == expectedRows[i][j] || Number(cell, out var left) &&
                            Number(expectedRows[i][j], out var right) && left == right).All(equal => equal)).All(equal => equal);
            }
            if (Operation == WebCheckOperation.BarChartEquals)
            {
                BarValues(Expected, out var values);
                var bars = Csv(actual);
                if (bars == null || bars.Count != values.Length || bars.Any(bar => bar.Length != 2 ||
                    bar.Any(size => !Number(size, out var value) || value <= 0))) return false;
                // A vertical or horizontal SVG bar chart must scale all values
                // consistently in row order. Equal decorative rectangles fail.
                return Enumerable.Range(0, 2).Any(axis => {
                    Number(bars[0][axis], out var first);
                    var scale = first / values[0];
                    return bars.Select((bar, i) => {
                        Number(bar[axis], out var size);
                        return Math.Abs(size / values[i] / scale - 1) <= .03;
                    }).All(equal => equal);
                });
            }
            return actual == Expected;
        }

        private static bool Number(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool BarValues(string text, out double[] values)
        {
            var cells = text.Split(','); values = new double[cells.Length];
            if (cells.Length == 0 || cells.Length > 12) return false;
            for (var i = 0; i < cells.Length; i++)
                if (!Number(cells[i], out values[i]) || values[i] <= 0) return false;
            return true;
        }

        private static List<string[]> Csv(string text)
        {
            if (text == null || text.Length > 700) return null;
            if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = text.Substring(1);
            var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder();
            var quoted = false; var closed = false;
            for (var index = 0; index < text.Length; index++)
            {
                var ch = text[index];
                if (quoted)
                {
                    if (ch != '"') cell.Append(ch);
                    else if (index + 1 < text.Length && text[index + 1] == '"') { cell.Append('"'); index++; }
                    else { quoted = false; closed = true; }
                    continue;
                }
                if (ch == ',' || ch == '\r' || ch == '\n')
                {
                    row.Add(cell.ToString()); cell.Clear(); closed = false;
                    if (ch != ',')
                    {
                        if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                        rows.Add(row.ToArray()); row.Clear();
                    }
                }
                else if (ch == '"' && cell.Length == 0 && !closed) quoted = true;
                else if (closed || ch == '"') return null;
                else cell.Append(ch);
                if (rows.Count > 12 || row.Count > 8) return null;
            }
            if (quoted) return null;
            if (cell.Length != 0 || closed || row.Count != 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
            return rows.Count <= 12 && rows.All(cells => cells.Length <= 8) ? rows : null;
        }
    }

    // Caller-owned acceptance input, frozen before the run; never loaded from
    // writable project files by the tool or replaced by model arguments.
    public sealed class WebFunctionalChecks
    {
        public const int MaximumSteps = 32;
        [JsonProperty("entryPath", Required = Required.Always)] public string EntryPath { get; private set; }
        [JsonProperty("steps", Required = Required.Always)] public IReadOnlyList<WebCheckStep> Steps { get; private set; }
        [JsonIgnore] public string Sha256 { get { return TextPatternEngine.Sha256(JsonConvert.SerializeObject(this)); } }

        [JsonConstructor]
        public WebFunctionalChecks(string entryPath, IEnumerable<WebCheckStep> steps)
        {
            var bounded = steps?.Take(MaximumSteps + 1).ToArray();
            if (string.IsNullOrWhiteSpace(entryPath) || entryPath.Length > 512 || entryPath.StartsWith("/", StringComparison.Ordinal) ||
                entryPath.Any(char.IsControl) || entryPath.IndexOfAny(new[] { '\\', ':', '?', '#', '%' }) >= 0 ||
                entryPath.Split('/').Any(part => part.Length == 0 || part == "." || part == "..") ||
                !entryPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !entryPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                bounded == null || bounded.Length == 0 || bounded.Length > MaximumSteps || bounded.Any(step => step == null) ||
                bounded.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != bounded.Length ||
                !bounded.Any(step => step.IsAssertion))
                throw new ArgumentException("Web checks require a relative HTML entry and 1–32 distinct steps including an assertion.");
            EntryPath = entryPath; Steps = Array.AsReadOnly(bounded);
            if (Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(this)) > 32768)
                throw new ArgumentException("Web checks exceed 32 KiB.");
        }

        public IReadOnlyList<WebCheckResult> NotRun()
        { return Steps.Select(step => new WebCheckResult(step.Id, WebCheckStatus.NotRun)).ToArray(); }

        public bool Matches(IReadOnlyList<WebCheckResult> results)
        {
            return results != null && results.Count == Steps.Count && results.Select((result, index) =>
                result != null && result.Id == Steps[index].Id && Enum.IsDefined(typeof(WebCheckStatus), result.Status) &&
                (result.Status != WebCheckStatus.Passed || Steps[index].Accepts(result.Actual))).All(value => value);
        }
    }

    public sealed class WebCheckResult
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; private set; }
        [JsonProperty("status", Required = Required.Always)] public WebCheckStatus Status { get; private set; }
        [JsonProperty("actual")] public string Actual { get; private set; }
        [JsonProperty("error")] public string Error { get; private set; }

        [JsonConstructor]
        public WebCheckResult(string id, WebCheckStatus status, string actual = null, string error = null)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !Enum.IsDefined(typeof(WebCheckStatus), status) ||
                actual?.Length > 700 || error?.Length > 700 || status == WebCheckStatus.Passed && error != null)
                throw new ArgumentException("Invalid web check result.");
            Id = id; Status = status; Actual = actual; Error = error;
        }
    }
}
