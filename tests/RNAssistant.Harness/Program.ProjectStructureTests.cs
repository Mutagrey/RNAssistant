using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ProductionProjectsIncludeAllSourceFiles()
        {
            var root = FindHarnessRepositoryRoot();
            var sourceRoot = Path.Combine(root, "src");
            var missing = new List<string>();

            foreach (var projectPath in Directory.GetFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
            {
                var projectDirectory = Path.GetDirectoryName(projectPath);
                var document = XDocument.Load(projectPath);
                var included = new HashSet<string>(
                    document.Descendants()
                        .Where(element => element.Name.LocalName == "Compile")
                        .Select(element => (string)element.Attribute("Include"))
                        .Where(value => !string.IsNullOrWhiteSpace(value) &&
                            value.IndexOf('*') < 0 && value.IndexOf('?') < 0)
                        .Select(value => Path.GetFullPath(Path.Combine(
                            projectDirectory,
                            value.Replace('\\', Path.DirectorySeparatorChar)))),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var sourcePath in Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
                {
                    if (IsGeneratedProjectPath(sourcePath) || included.Contains(Path.GetFullPath(sourcePath))) continue;
                    missing.Add(Path.GetRelativePath(root, sourcePath).Replace('\\', '/'));
                }
            }

            AssertEqual(0, missing.Count,
                "old-style production projects must explicitly include every source file: " +
                string.Join(", ", missing.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        private static string FindHarnessRepositoryRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var current = new DirectoryInfo(start);
                while (current != null)
                {
                    if (File.Exists(Path.Combine(current.FullName, "RNAssistant.sln")) &&
                        Directory.Exists(Path.Combine(current.FullName, "src")))
                    {
                        return current.FullName;
                    }
                    current = current.Parent;
                }
            }
            throw new InvalidOperationException("RNAssistant repository root was not found.");
        }

        private static bool IsGeneratedProjectPath(string path)
        {
            var normalized = Path.GetFullPath(path)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalized.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
