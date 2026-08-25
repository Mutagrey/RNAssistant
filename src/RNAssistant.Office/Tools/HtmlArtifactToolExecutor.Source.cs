using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlArtifactToolExecutor
    {
        private static string ReadWorkspaceSchema()
        {
            var properties = new JObject
            {
                ["resourceType"] = new JObject { ["type"] = "string", ["enum"] = new JArray("file", "data"), ["description"] = "Resource selector; omit resourceType and name together to read the compact workspace manifest." },
                ["name"] = new JObject { ["type"] = "string", ["description"] = "Exact file path or data-source name for resourceType.", ["minLength"] = 1, ["maxLength"] = 260 },
                ["startLine"] = new JObject { ["type"] = "integer", ["description"] = "One-based first line for a bounded file read; omit for the whole file.", ["minimum"] = 1 },
                ["lineCount"] = new JObject { ["type"] = "integer", ["description"] = "Maximum consecutive file lines. Supplied alone it starts at line 1; when only startLine is supplied runtime returns up to 200 lines.", ["minimum"] = 1, ["maximum"] = 500 }
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray
                {
                    HtmlResourceVariant(properties, new[] { "resourceType", "name", "startLine", "lineCount" }, new[] { "resourceType", "name" }, "file"),
                    HtmlResourceVariant(properties, new[] { "resourceType", "name" }, new[] { "resourceType", "name" }, "data"),
                    HtmlResourceVariant(properties, new string[0], new string[0])
                }
            }.ToString(Formatting.None);
        }

        private static string SearchWorkspaceSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression query.\",\"minLength\":1,\"maxLength\":2048}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Optional exact workspace-relative file path.\",\"maxLength\":260}," +
                "\"kind\":{\"type\":\"string\",\"description\":\"Optional file-kind filter.\",\"enum\":[\"html\",\"css\",\"script\"]}," +
                "\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]}," +
                "\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false}," +
                "\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false}," +
                "\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":100,\"minimum\":1,\"maximum\":500}," +
                "\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80,\"minimum\":0,\"maximum\":1000}" +
                "},\"required\":[\"query\"],\"additionalProperties\":false}";
        }

        private static string UpsertWorkspaceSchema()
        {
            var properties = new JObject
            {
                ["resourceType"] = new JObject { ["type"] = "string", ["enum"] = new JArray("file", "data"), ["description"] = "Resource to write: file or data." },
                ["name"] = new JObject { ["type"] = "string", ["description"] = "Workspace-relative file path or stable data-source name.", ["minLength"] = 1, ["maxLength"] = 260 },
                ["content"] = new JObject { ["type"] = "string", ["description"] = "Complete file text, or complete valid JSON text when resourceType=data.", ["maxLength"] = 300000 },
                ["setActive"] = new JObject { ["type"] = "boolean", ["description"] = "For a written HTML file, select it as the active preview.", ["default"] = true },
                ["mode"] = new JObject { ["type"] = "string", ["description"] = "upsert creates or updates; createOnly/updateOnly enforce exact existence.", ["default"] = "upsert", ["enum"] = new JArray("upsert", "createOnly", "updateOnly") }
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray("resourceType", "name", "content"),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray
                {
                    HtmlResourceVariant(properties, new[] { "resourceType", "name", "content", "setActive", "mode" }, new[] { "resourceType", "name", "content" }, "file"),
                    HtmlResourceVariant(properties, new[] { "resourceType", "name", "content", "mode" }, new[] { "resourceType", "name", "content" }, "data")
                }
            }.ToString(Formatting.None);
        }

        private static JObject HtmlResourceVariant(
            JObject sourceProperties,
            IEnumerable<string> allowed,
            IEnumerable<string> required,
            string resourceType = null)
        {
            var properties = new JObject();
            foreach (var name in allowed ?? new string[0])
            {
                if (sourceProperties[name] != null) properties[name] = sourceProperties[name].DeepClone();
            }
            if (!string.IsNullOrWhiteSpace(resourceType))
            {
                ((JObject)properties["resourceType"])["enum"] = new JArray(resourceType);
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(required ?? new string[0]),
                ["additionalProperties"] = false
            };
        }

        private static string ApplyPatchSchema()
        {
            var find = new JObject
            {
                ["type"] = "string",
                ["description"] = "Non-empty exact source text or unique insertion anchor.",
                ["minLength"] = 1,
                ["maxLength"] = MaxHtmlChars
            };
            var text = new JObject
            {
                ["type"] = "string",
                ["description"] = "Replacement or inserted source text; empty text is valid for replacement/deletion.",
                ["maxLength"] = MaxHtmlChars
            };
            var operations = new JArray
            {
                PatchOperationSchema("replace", "Replace exactly one occurrence; ambiguity is rejected.",
                    new JObject { ["find"] = find.DeepClone(), ["text"] = text.DeepClone() }, "find", "text"),
                PatchOperationSchema("replaceAll", "Replace every exact occurrence explicitly.",
                    new JObject { ["find"] = find.DeepClone(), ["text"] = text.DeepClone() }, "find", "text"),
                PatchOperationSchema("insertBefore", "Insert non-empty text before one unique exact anchor.",
                    new JObject
                    {
                        ["find"] = find.DeepClone(),
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Non-empty source text to insert.", ["minLength"] = 1, ["maxLength"] = MaxHtmlChars }
                    }, "find", "text"),
                PatchOperationSchema("insertAfter", "Insert non-empty text after one unique exact anchor.",
                    new JObject
                    {
                        ["find"] = find.DeepClone(),
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Non-empty source text to insert.", ["minLength"] = 1, ["maxLength"] = MaxHtmlChars }
                    }, "find", "text"),
                PatchOperationSchema("replaceLines", "Replace or delete a current one-based line range after preceding operations.",
                    new JObject
                    {
                        ["startLine"] = new JObject { ["type"] = "integer", ["description"] = "One-based first line.", ["minimum"] = 1 },
                        ["deleteCount"] = new JObject { ["type"] = "integer", ["description"] = "Number of existing lines to delete.", ["minimum"] = 0 },
                        ["text"] = text.DeepClone()
                    }, "startLine", "deleteCount", "text"),
                PatchOperationSchema("regexReplace", "Replace a bounded capture-group regex match set.",
                    new JObject
                    {
                        ["pattern"] = new JObject { ["type"] = "string", ["description"] = "Non-empty regular expression.", ["minLength"] = 1, ["maxLength"] = TextPatternEngine.MaxPatternChars },
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "Replacement text; capture groups such as $1 are supported.", ["maxLength"] = MaxHtmlChars },
                        ["matchCase"] = new JObject { ["type"] = "boolean", ["description"] = "Whether matching is case-sensitive.", ["default"] = true },
                        ["wholeWord"] = new JObject { ["type"] = "boolean", ["description"] = "Whether only whole-word matches are accepted.", ["default"] = false },
                        ["replaceAll"] = new JObject { ["type"] = "boolean", ["description"] = "Whether every match is replaced.", ["default"] = true },
                        ["maxReplacements"] = new JObject { ["type"] = "integer", ["description"] = "Maximum replacements allowed.", ["default"] = 500, ["minimum"] = 1, ["maximum"] = 500 }
                    }, "pattern", "text")
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["name"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Existing workspace-relative HTML, CSS, or JavaScript file path.",
                        ["minLength"] = 1,
                        ["maxLength"] = 260
                    },
                    ["patch"] = new JObject
                    {
                        ["type"] = "array",
                        ["description"] = "Native JSON array of ordered operations applied atomically to current file source.",
                        ["minItems"] = 1,
                        ["maxItems"] = 100,
                        ["items"] = new JObject { ["anyOf"] = operations }
                    }
                },
                ["required"] = new JArray("name", "patch"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static JObject PatchOperationSchema(string operation, string description, JObject properties, params string[] required)
        {
            properties = properties ?? new JObject();
            properties.AddFirst(new JProperty("op", new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JArray(operation)
            }));
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(new[] { "op" }.Concat(required ?? new string[0])),
                ["additionalProperties"] = false
            };
        }

        private static ToolResult ValidateWorkspaceUpsertMode(ChatSession session, string resourceType, string name, string mode)
        {
            mode = (mode ?? "upsert").Trim();
            if (!string.Equals(mode, "upsert", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("mode must be upsert, createOnly, or updateOnly.", null, "invalid_arguments", false);
            }

            var workspace = NormalizedWorkspaceCopy(session == null ? null : session.HtmlWorkspace);
            bool exists;
            if (string.Equals(resourceType, "file", StringComparison.OrdinalIgnoreCase))
            {
                var id = FileId(NormalizePath(name));
                exists = workspace.Files.Any(item => item != null && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            }
            else if (string.Equals(resourceType, "data", StringComparison.OrdinalIgnoreCase))
            {
                var id = DataSourceId(NormalizeDataName(name));
                exists = workspace.DataSources.Any(item => item != null && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                return null;
            }

            if (exists && string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("HTML workspace " + resourceType + " already exists: " + name + ".", null, "html_workspace_item_exists", false);
            }
            if (!exists && string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("HTML workspace " + resourceType + " was not found: " + name + ".", null, "html_workspace_item_not_found", false);
            }
            return null;
        }

        private static ToolResult SearchWorkspace(ChatSession session, ToolCommand command, CancellationToken cancellationToken)
        {
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty);
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("query is required.", null, "invalid_arguments", true);
            var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty).Trim();
            var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty).Trim().ToLowerInvariant();
            var maxResults = Math.Max(1, Math.Min(TextPatternEngine.MaxResults, ToolArgumentReader.Int32(command.Arguments, "maxResults", 100)));
            var contextChars = Math.Max(0, Math.Min(1000, ToolArgumentReader.Int32(command.Arguments, "contextChars", 80)));
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var files = string.IsNullOrWhiteSpace(name)
                ? workspace.Files.Where(item => item != null).ToList()
                : new List<HtmlWorkspaceFile> { FindFile(workspace, name, false) };
            if (!string.IsNullOrWhiteSpace(kind))
            {
                files = files.Where(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            try
            {
                var rows = new JArray();
                var matchCount = 0;
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = file.Content ?? string.Empty;
                    var found = TextPatternEngine.Find(
                        source,
                        query,
                        new TextPatternOptions
                        {
                            Mode = ToolArgumentReader.String(command.Arguments, "mode", "literal"),
                            MatchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false),
                            WholeWord = ToolArgumentReader.Boolean(command.Arguments, "wholeWord", false)
                        },
                        Math.Max(1, maxResults - rows.Count),
                        contextChars);
                    matchCount += found.MatchCount;
                    var lineStarts = SourceLineStarts(source);
                    foreach (var match in found.Matches)
                    {
                        if (rows.Count >= maxResults) break;
                        int line;
                        int column;
                        SourcePosition(lineStarts, match.Index, out line, out column);
                        rows.Add(new JObject
                        {
                            ["name"] = file.Path,
                            ["kind"] = file.Kind,
                            ["line"] = line,
                            ["column"] = column,
                            ["start"] = match.Index,
                            ["end"] = match.Index + match.Length,
                            ["preview"] = match.Preview,
                            ["contentSha256"] = TextPatternEngine.Sha256(source)
                        });
                    }
                }

                var truncated = matchCount > rows.Count;
                return ToolResult.Ok(
                    "HTML workspace matches returned: " + rows.Count + (truncated ? " (results truncated)." : "."),
                    new JObject
                    {
                        ["type"] = "rnassistant.htmlWorkspaceSearch",
                        ["version"] = 1,
                        ["revisionArtifactId"] = session.ActiveHtmlArtifactId,
                        ["matchCount"] = matchCount,
                        ["matchCountIsExact"] = true,
                        ["returnedCount"] = rows.Count,
                        ["truncated"] = truncated,
                        ["matches"] = rows
                    }.ToString(Formatting.None));
            }
            catch (TextPatternException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false);
            }
        }

        private static ToolResult ApplyWorkspacePatch(
            ChatSession session,
            ToolCommand command,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
                var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
                var file = FindFile(workspace, name, false);
                var before = file.Content ?? string.Empty;
                var patch = StructuredTextPatchEngine.Apply(before, ReadPatchOperations(command), MaxHtmlChars);
                ValidateFile(file.Path, file.Kind, patch.Text);
                ValidateWorkspaceCapacity(workspace, file.Id, patch.Text, null, null);
                var changed = !string.Equals(before, patch.Text, StringComparison.Ordinal);
                var preview = new JObject
                {
                    ["type"] = "rnassistant.htmlWorkspacePatch",
                    ["version"] = 1,
                    ["name"] = file.Path,
                    ["kind"] = file.Kind,
                    ["saved"] = !dryRun && changed,
                    ["changed"] = changed,
                    ["oldCharacters"] = before.Length,
                    ["newCharacters"] = patch.Text.Length,
                    ["oldLineCount"] = SourceLineCount(before),
                    ["newLineCount"] = SourceLineCount(patch.Text),
                    ["previousContentSha256"] = TextPatternEngine.Sha256(before),
                    ["contentSha256"] = TextPatternEngine.Sha256(patch.Text),
                    ["operations"] = new JArray(patch.Steps.Select(item => new JObject
                    {
                        ["op"] = item.Op,
                        ["matchCount"] = item.MatchCount,
                        ["message"] = item.Message
                    }))
                };
                if (dryRun)
                {
                    preview["revisionArtifactId"] = session.ActiveHtmlArtifactId;
                    return ToolResult.Ok("Dry run: would apply HTML workspace patch to " + file.Path + ".", preview.ToString(Formatting.None));
                }
                if (!changed)
                {
                    preview["revisionArtifactId"] = session.ActiveHtmlArtifactId;
                    return ToolResult.Ok("HTML workspace patch made no content changes: " + file.Path + ".", preview.ToString(Formatting.None));
                }

                var saved = UpsertFile(session, file.Path, file.Kind, patch.Text, false);
                preview["revisionArtifactId"] = session.ActiveHtmlArtifactId;
                return ToolResult.Ok("HTML workspace patch applied: " + saved.Path + ".", preview.ToString(Formatting.None));
            }
            catch (StructuredTextPatchException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, true);
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Invalid HTML workspace patch: " + ex.Message, null, "text_patch_invalid", true);
            }
        }

        private static List<StructuredTextPatchOperation> ReadPatchOperations(ToolCommand command)
        {
            object raw;
            if (command == null || command.Arguments == null || !command.Arguments.TryGetValue("patch", out raw) || raw == null)
            {
                return new List<StructuredTextPatchOperation>();
            }
            var token = raw as JToken;
            var array = token as JArray;
            if (array == null)
            {
                var text = raw as string;
                array = text == null ? JArray.FromObject(raw) : JArray.Parse(text);
            }

            return array.Select(item => item as JObject).Select(item => item == null
                ? null
                : new StructuredTextPatchOperation
                {
                    Op = (string)item["op"],
                    Find = (string)item["find"],
                    Text = (string)item["text"],
                    Pattern = (string)item["pattern"],
                    StartLine = (int?)item["startLine"],
                    DeleteCount = (int?)item["deleteCount"],
                    MatchCase = (bool?)item["matchCase"] ?? true,
                    WholeWord = (bool?)item["wholeWord"] ?? false,
                    ReplaceAll = (bool?)item["replaceAll"] ?? true,
                    MaxReplacements = (int?)item["maxReplacements"] ?? 500
                }).ToList();
        }

        private static int SourceLineCount(string source)
        {
            return SourceLineStarts(source).Count;
        }

        private static string ReadSourceLines(string source, int startLine, int lineCount)
        {
            if (startLine < 1 || lineCount < 1)
            {
                throw new InvalidOperationException("startLine and lineCount must be positive.");
            }
            source = source ?? string.Empty;
            var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (startLine > lines.Length)
            {
                throw new InvalidOperationException("startLine is outside the HTML workspace file.");
            }
            return string.Join(CurrentSourceNewLine(source), lines.Skip(startLine - 1).Take(lineCount).ToArray());
        }

        private static List<int> SourceLineStarts(string source)
        {
            source = source ?? string.Empty;
            var starts = new List<int> { 0 };
            for (var index = 0; index < source.Length; index++)
            {
                if (source[index] == '\r')
                {
                    if (index + 1 < source.Length && source[index + 1] == '\n') index++;
                    starts.Add(index + 1);
                }
                else if (source[index] == '\n')
                {
                    starts.Add(index + 1);
                }
            }
            return starts;
        }

        private static void SourcePosition(List<int> lineStarts, int sourceIndex, out int line, out int column)
        {
            lineStarts = lineStarts ?? new List<int> { 0 };
            var found = lineStarts.BinarySearch(Math.Max(0, sourceIndex));
            var lineIndex = found >= 0 ? found : Math.Max(0, ~found - 1);
            line = lineIndex + 1;
            column = Math.Max(1, sourceIndex - lineStarts[lineIndex] + 1);
        }

        private static string CurrentSourceNewLine(string source)
        {
            source = source ?? string.Empty;
            return source.IndexOf("\r\n", StringComparison.Ordinal) >= 0
                ? "\r\n"
                : source.IndexOf('\r') >= 0 ? "\r" : "\n";
        }
    }
}
