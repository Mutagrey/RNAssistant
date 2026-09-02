using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void R61BuiltInContractInventory()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var observed = new List<R61ObservedTool>();
                foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
                {
                    var adapter = FakeOfficeAdapter.ForHost(host);
                    var promptSettings = new AppSettings();
                    var executor = new OfficeToolExecutor(
                        adapter,
                        new VbaJournalStore(paths),
                        new SkillStore(paths),
                        new ToolStore(paths),
                        () => promptSettings,
                        value => promptSettings = value,
                        paths);
                    var tools = OfficeToolCatalog.ForHost(host)
                        .Concat(executor.GetControllerTools())
                        .Where(tool => tool != null && tool.BuiltIn)
                        .ToArray();
                    foreach (var tool in tools)
                    {
                        var normalizedSchema = NormalizeSchema(tool.ArgumentSchemaJson);
                        var binding = tool.Binding == null ? string.Empty : tool.Binding.HandlerId;
                        var current = observed.FirstOrDefault(item =>
                            string.Equals(item.Id, tool.Id, StringComparison.Ordinal) &&
                            string.Equals(item.Schema, normalizedSchema, StringComparison.Ordinal) &&
                            string.Equals(item.Binding, binding, StringComparison.Ordinal) &&
                            string.Equals(item.Revision, CapabilityCatalogService.Revision(tool), StringComparison.Ordinal));
                        if (current == null)
                        {
                            current = new R61ObservedTool(tool);
                            observed.Add(current);
                        }
                        current.Hosts.Add(host);
                    }
                }

                var actual = string.Join("\n", observed
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .ThenBy(item => string.Join(",", item.Hosts), StringComparer.Ordinal)
                    .Select(item => item.InventoryLine()).ToArray());
                var path = Path.Combine(FindHarnessRepositoryRoot(), "docs", "stabilization",
                    "R61_TOOL_PROPERTY_INVENTORY.tsv");
                if (!File.Exists(path))
                    throw new InvalidOperationException("R61 inventory baseline is missing:\n" + actual);
                var baseline = File.ReadAllLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#", StringComparison.Ordinal))
                    .Select(line => line.TrimEnd()).ToArray();
                var expected = string.Join("\n", baseline.Select(line =>
                {
                    var columns = line.Split('\t');
                    AssertTrue(columns.Length == 7, "R61 inventory row has seven columns: " + line);
                    ReviewPlumbingProperties(columns[0], columns[5], columns[6]);
                    return string.Join("\t", columns.Take(6).ToArray());
                }).ToArray());
                AssertEqual(expected, actual,
                    "every built-in id, effective host/mode, binding and property path is reviewed");
            });
        }

        private static string NormalizeSchema(string json)
        {
            return JObject.Parse(json).ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void CollectPropertyPaths(JToken schema, string prefix, ISet<string> paths)
        {
            var value = schema as JObject;
            if (value == null) return;
            var properties = value["properties"] as JObject;
            if (properties != null)
            {
                foreach (var property in properties.Properties())
                {
                    var path = string.IsNullOrWhiteSpace(prefix)
                        ? property.Name : prefix + "." + property.Name;
                    paths.Add(path);
                    CollectPropertyPaths(property.Value, path, paths);
                }
            }
            CollectPropertyPaths(value["items"], prefix + "[]", paths);
            foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
            {
                var alternatives = value[keyword] as JArray;
                if (alternatives == null) continue;
                foreach (var alternative in alternatives)
                    CollectPropertyPaths(alternative, prefix, paths);
            }
        }

        private static void ReviewPlumbingProperties(
            string toolId, string propertiesValue, string reviewValue)
        {
            var properties = new HashSet<string>((propertiesValue ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var reviewed = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.Equals(reviewValue, "-", StringComparison.Ordinal))
            {
                foreach (var entry in (reviewValue ?? string.Empty)
                    .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = entry.IndexOf('=');
                    AssertTrue(separator > 0 && separator < entry.Length - 1,
                        toolId + " has malformed property review: " + entry);
                    var path = entry.Substring(0, separator);
                    AssertTrue(properties.Contains(path),
                        toolId + " reviews a missing property: " + path);
                    AssertTrue(!reviewed.ContainsKey(path),
                        toolId + " reviews a property twice: " + path);
                    reviewed.Add(path, entry.Substring(separator + 1));
                }
            }
            foreach (var path in properties.Where(IsPlumbingShapedProperty))
            {
                AssertTrue(reviewed.ContainsKey(path),
                    toolId + " plumbing-shaped property requires an explicit R61 decision: " + path);
            }
        }

        private static bool IsPlumbingShapedProperty(string path)
        {
            var name = (path ?? string.Empty).Split('.').LastOrDefault() ?? string.Empty;
            name = name.Replace("[]", string.Empty).ToLowerInvariant();
            return name == "id" || name.EndsWith("id", StringComparison.Ordinal) ||
                name == "uri" || name.EndsWith("uri", StringComparison.Ordinal) ||
                name == "revision" || name.EndsWith("revision", StringComparison.Ordinal) ||
                name == "cursor" || name.EndsWith("cursor", StringComparison.Ordinal) ||
                name == "offset" || name.EndsWith("offset", StringComparison.Ordinal) ||
                name == "etag" || name.EndsWith("etag", StringComparison.Ordinal) ||
                name == "token" || name.EndsWith("token", StringComparison.Ordinal) ||
                name == "guard" || name.EndsWith("guard", StringComparison.Ordinal) ||
                name == "hash" || name.EndsWith("hash", StringComparison.Ordinal) ||
                name.EndsWith("sha256", StringComparison.Ordinal);
        }

        private sealed class R61ObservedTool
        {
            internal R61ObservedTool(ToolCatalogEntry tool)
            {
                Id = tool.Id;
                Schema = NormalizeSchema(tool.ArgumentSchemaJson);
                Revision = CapabilityCatalogService.Revision(tool);
                Binding = tool.Binding == null ? string.Empty : tool.Binding.HandlerId;
                Modes = new SortedSet<string>(
                    tool.Policy == null ? new string[0] : tool.Policy.AllowedModes,
                    StringComparer.Ordinal);
                Hosts = new SortedSet<string>(StringComparer.Ordinal);
                Properties = new SortedSet<string>(StringComparer.Ordinal);
                CollectPropertyPaths(JObject.Parse(tool.ArgumentSchemaJson), string.Empty, Properties);
            }

            internal string Id { get; private set; }
            internal string Schema { get; private set; }
            internal string Binding { get; private set; }
            internal string Revision { get; private set; }
            internal SortedSet<string> Modes { get; private set; }
            internal SortedSet<string> Hosts { get; private set; }
            internal SortedSet<string> Properties { get; private set; }

            internal string InventoryLine()
            {
                return string.Join("\t", new[]
                {
                    Id,
                    string.Join(",", Hosts),
                    string.Join(",", Modes),
                    Binding,
                    Revision,
                    string.Join(",", Properties)
                });
            }
        }
    }
}
