using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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

        private static void OfficeEventConsumersUseTypedPort()
        {
            var root = FindHarnessRepositoryRoot();
            var officeRoot = Path.Combine(root, "src", "RNAssistant.Office");
            var forbiddenMembers = new[]
            {
                ".AppendTrace(",
                ".AppendTraceBytes(",
                ".ReadEvents(",
                ".ReadCompleteEvents(",
                ".ReadEventPayload("
            };
            var offenders = new List<string>();
            foreach (var path in Directory.GetFiles(officeRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGeneratedProjectPath(path)) continue;
                var source = File.ReadAllText(path);
                foreach (var member in forbiddenMembers)
                {
                    if (source.IndexOf(member, StringComparison.Ordinal) >= 0)
                    {
                        offenders.Add(Path.GetRelativePath(root, path).Replace('\\', '/') + ": " + member);
                    }
                }
            }
            AssertEqual(0, offenders.Count,
                "Office event consumers must use IEventStore instead of broad ChatStore event members: " +
                string.Join(", ", offenders.ToArray()));

            var causal = File.ReadAllText(Path.Combine(officeRoot, "Services", "RunCausalTrace.cs"));
            AssertContains(causal, "public CausalTraceRecord(SessionEventKind kind)",
                "causal records require a closed event kind at construction");
            AssertContains(causal, "descriptor.Lane != SessionEventLane.DomainDiagnostic",
                "causal records cannot claim Agent authority");
            AssertTrue(causal.IndexOf("public string Stage { get; set; }", StringComparison.Ordinal) < 0,
                "causal records cannot inject an arbitrary persisted event type");

            var publicChatStoreMethods = typeof(RNAssistant.Core.Storage.ChatStore)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Select(method => method.Name)
                .ToArray();
            foreach (var broadMember in new[]
            {
                "AppendTrace",
                "AppendTraceBytes",
                "ReadEvents",
                "ReadCompleteEvents",
                "ReadEventPayload"
            })
            {
                AssertTrue(!publicChatStoreMethods.Contains(broadMember, StringComparer.Ordinal),
                    "replaced broad ChatStore event API is not externally callable: " + broadMember);
            }
        }

        private static void OfficeConversationConsumersUseTypedPort()
        {
            var root = FindHarnessRepositoryRoot();
            var officeRoot = Path.Combine(root, "src", "RNAssistant.Office");
            var allowedConcreteCalls = new[]
            {
                ".LoadArtifactBody",
                ".LoadArtifactBodies",
                ".TryActivateHtmlWorkspaceRevision"
            };
            var offenders = new List<string>();
            foreach (var path in Directory.GetFiles(officeRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGeneratedProjectPath(path)) continue;
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.IndexOf("_chatStore.", StringComparison.Ordinal) < 0 ||
                        allowedConcreteCalls.Any(call => line.IndexOf(call, StringComparison.Ordinal) >= 0))
                    {
                        continue;
                    }
                    offenders.Add(relative + ": " + line.Trim());
                }
            }
            AssertEqual(0, offenders.Count,
                "Office conversation consumers must use IConversationStore; concrete ChatStore is reserved for artifact/CAS operations: " +
                string.Join(", ", offenders.ToArray()));

            AssertEqual(typeof(RNAssistant.Core.Persistence.IConversationStore),
                typeof(RNAssistant.Office.Services.ChatSessionService)
                    .GetField("_conversations", BindingFlags.Instance | BindingFlags.NonPublic).FieldType,
                "chat session service depends on the conversation port");

            var portMethods = typeof(RNAssistant.Core.Persistence.IConversationStore)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public);
            AssertEqual(15, portMethods.Length, "conversation port contains only current aggregate operations");
            foreach (var forbidden in new[]
            {
                "LoadArtifactBody",
                "ReadEvents",
                "AppendTrace",
                "CollectCasReferences",
                "ClearMessages"
            })
            {
                AssertTrue(!portMethods.Any(method => string.Equals(method.Name, forbidden, StringComparison.Ordinal)),
                    "conversation port excludes storage/artifact internals: " + forbidden);
            }

            var publicChatStoreMethods = typeof(RNAssistant.Core.Storage.ChatStore)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Select(method => method.Name)
                .ToArray();
            foreach (var replaced in new[]
            {
                "LoadOrCreateActive",
                "Create",
                "CreateTransient",
                "Load",
                "Save",
                "IsPersisted",
                "List",
                "ListHeaders",
                "Move",
                "MoveDocument",
                "Delete",
                "DeleteDocument",
                "ClearMessages",
                "LoadActiveSessionId",
                "SaveActiveSessionId",
                "CloseOpenSteps",
                "HasOpenToolExecution"
            })
            {
                AssertTrue(!publicChatStoreMethods.Contains(replaced, StringComparer.Ordinal),
                    "replaced broad ChatStore conversation API is not externally callable: " + replaced);
            }
        }

        private static void RunViewConsumersUseTypedProjection()
        {
            AssertEqual(typeof(RNAssistant.Core.Models.RunViewState),
                typeof(RNAssistant.Office.Services.ChatTurnResult).GetProperty("RunViewState").PropertyType,
                "application result exposes one typed run projection");
            AssertEqual(typeof(RNAssistant.Core.Models.RunViewState),
                typeof(RNAssistant.Office.Contracts.ChatStateResponse).GetProperty("RunViewState").PropertyType,
                "bridge exposes the same typed run projection");
            AssertEqual(typeof(long),
                typeof(RNAssistant.Office.Contracts.ChatStateResponse).GetProperty("SessionRevision").PropertyType,
                "bridge projection carries its durable ordering revision");
            AssertEqual(typeof(RNAssistant.Core.Models.RunViewState),
                typeof(RNAssistant.Core.Models.ChatSessionSummary).GetProperty("RunViewState").PropertyType,
                "chat catalog uses the same projection");

            foreach (var type in new[]
            {
                typeof(RNAssistant.Office.Services.ChatTurnResult),
                typeof(RNAssistant.Office.Contracts.ChatStateResponse),
                typeof(RNAssistant.Office.Contracts.SendChatResponse),
                typeof(RNAssistant.Core.Models.ChatSessionSummary)
            })
            {
                AssertTrue(type.GetProperty("ExecutionSummary") == null && type.GetProperty("RunStatus") == null,
                    "active application/bridge/UI DTO has no flat run projection: " + type.FullName);
            }
            AssertTrue(typeof(RNAssistant.Office.Contracts.ChatStateResponse).GetProperty("ResponseStatus") == null &&
                typeof(RNAssistant.Office.Contracts.SendChatResponse).GetProperty("ResponseStatus") == null,
                "model response status is not a UI bridge lifecycle");
            AssertTrue(typeof(RNAssistant.Core.Models.ChatMessage).Assembly.GetType(
                "RNAssistant.Core.Models.RunExecutionSummary", false) == null,
                "the replaced flat projection type is physically removed");
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

        private static void VersioningOrdinaryBuildsNeedNoBump()
        {
            using (var fixture = new VersioningFixture())
            {
                var props = File.ReadAllText(Path.Combine(fixture.Repository, "Directory.Build.props"));
                var head = fixture.Git("rev-parse", "HEAD").Trim();
                var expected = fixture.ProductVersion + "+g" + head.Substring(0, 12);
                for (var i = 0; i < 2; i++)
                {
                    var output = fixture.Build("PrintBuildIdentity", true, "-p:RNAssistantBuildNumber=42");
                    AssertContains(output, "Identity=" + expected, "same product version supports repeated builds");
                    AssertContains(output, "File=" + fixture.VersionPrefix + ".42", "build number is not product version");
                    AssertContains(output, "Application=" + fixture.VersionPrefix + ".42", "ClickOnce uses numeric version");
                }
                AssertEqual("", fixture.Git("status", "--porcelain").Trim(), "validation does not modify tracked source");
                File.AppendAllText(Path.Combine(fixture.Repository, "CHANGELOG.md"), "\nLocal work\n");
                AssertContains(fixture.Build("PrintBuildIdentity", true), expected + ".dirty", "dirty builds remain allowed and identifiable");
                fixture.Commit("ordinary change without bump");
                var nextHead = fixture.Git("rev-parse", "HEAD").Trim();
                AssertTrue(head != nextHead, "fixture creates a distinct ordinary commit");
                AssertContains(fixture.Build("PrintBuildIdentity", true),
                    "Identity=" + fixture.ProductVersion + "+g" + nextHead.Substring(0, 12), "new commit needs no product bump");
                AssertEqual(props, File.ReadAllText(Path.Combine(fixture.Repository, "Directory.Build.props")), "product version is unchanged");
                AssertEqual("", fixture.Git("tag", "--list").Trim(), "ordinary builds and commits create no tags");
            }
        }

        private static void VersioningSourceArchivesBuildWithoutGit()
        {
            using (var fixture = new VersioningFixture())
            {
                var head = fixture.Git("rev-parse", "HEAD").Trim();
                Directory.Move(Path.Combine(fixture.Repository, ".git"), Path.Combine(fixture.TemporaryRoot, "saved-git"));
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var output = fixture.Build("PrintBuildIdentity", true, "-p:Configuration=" + configuration);
                    AssertContains(output, "Identity=" + fixture.ProductVersion + "+source-archive.unknown", "ordinary archive build is visibly unidentified");
                    AssertContains(output, "Git provenance is unknown", "archive provenance warning is explicit");
                }
                fixture.Build("GenerateRNAssistantVersionInfo", true);
                var generated = File.ReadAllText(Path.Combine(fixture.Repository, "obj", "RNAssistant.Version.g.cs"));
                foreach (var field in new[] { "CommitSha", "Branch", "WorkingTreeState" })
                    AssertContains(generated, "AssemblyMetadataAttribute(\"" + field + "\", \"unknown\")", "archive metadata does not invent provenance");

                AssertContains(fixture.Build("PrintBuildIdentity", true, "-p:RNAssistantCommitSha=" + head),
                    fixture.ProductVersion + "+g" + head.Substring(0, 12) + ".unknown", "partial archive metadata does not imply a clean tree");
                File.WriteAllText(Path.Combine(fixture.Repository, "Directory.Build.local.props"),
                    "<Project><PropertyGroup><RNAssistantCommitSha>" + head + "</RNAssistantCommitSha>" +
                    "<RNAssistantBranch>stabilization/16.1</RNAssistantBranch>" +
                    "<RNAssistantWorkingTreeState>dirty</RNAssistantWorkingTreeState></PropertyGroup></Project>");
                var identified = fixture.Build("PrintBuildIdentity", true);
                AssertContains(identified, fixture.ProductVersion + "+g" + head.Substring(0, 12) + ".dirty", "explicit archive provenance is preserved");
                AssertTrue(!identified.Contains("Git provenance is unknown"), "identified archive needs no warning");
                AssertContains(fixture.Build("ValidateVersionFormat", false, "-p:RNAssistantCommitSha=invalid"),
                    "full Git commit SHA", "malformed archive SHA still fails");
                AssertContains(fixture.ReleaseBuild("ValidateRNAssistantRelease", false),
                    "Release requires a Git checkout", "explicit metadata does not bypass live release checks");
                File.Delete(Path.Combine(fixture.Repository, "Directory.Build.local.props"));
                AssertContains(fixture.ReleaseBuild("PrintBuildIdentity", false),
                    "Release requires explicit Git provenance", "release tag cannot qualify an unknown archive");
                AssertContains(fixture.Build("PrintBuildIdentity", false, "-p:RNAssistantReleaseBuild=true"),
                    "Release requires explicit Git provenance", "explicit release flag cannot qualify an unknown archive");
                AssertContains(fixture.Build("ValidateRNAssistantRelease", false),
                    "Release requires explicit Git provenance", "direct release validation cannot qualify an unknown archive");
            }
        }

        private static void VersioningRejectsMalformedMetadata()
        {
            using (var fixture = new VersioningFixture())
            {
                var cases = new[]
                {
                    new[] { "-p:RNAssistantVersionPrefix=01.1.0", "SemVer core" },
                    new[] { "-p:RNAssistantVersionSuffix=beta.01", "SemVer prerelease" },
                    new[] { "-p:RNAssistantBuildNumber=-1", "RNAssistantBuildNumber" },
                    new[] { "-p:RNAssistantBuildNumber=65535", "RNAssistantBuildNumber" },
                    new[] { "-p:Version=99.0.0", "Version must be derived" },
                    new[] { "-p:AssemblyVersion=99.0.0.0", "AssemblyVersion must match" },
                    new[] { "-p:InformationalVersion=unidentified", "InformationalVersion must include" },
                    new[] { "-p:RNAssistantCommitSha=unknown", "full Git commit SHA" },
                    new[] { "-p:RNAssistantWorkingTreeState=unknown", "RNAssistantWorkingTreeState must be clean or dirty" }
                };
                foreach (var item in cases)
                    AssertContains(fixture.Build("ValidateVersionFormat", false, item[0]), item[1], "invalid build metadata fails");
                fixture.Build("ValidateVersionFormat", true, "-p:RNAssistantVersionSuffix=beta.1");
            }
        }

        private static void VersioningReleaseGatesAreExplicit()
        {
            using (var fixture = new VersioningFixture())
            {
                fixture.ReleaseBuild("ValidateRNAssistantRelease", true);
                fixture.ReleaseBuild("PrintBuildIdentity", true);
                AssertContains(fixture.Build("PrintBuildIdentity", false, "-p:RNAssistantReleaseBuild=true"),
                    "RNAssistantReleaseTag must exactly match", "release builds cannot omit their tag");
                AssertContains(fixture.Build("PrintBuildIdentity", false, "-p:RNAssistantReleaseTag=v99.0.0"),
                    "RNAssistantReleaseTag must exactly match", "tag builds automatically enable release checks");
                AssertContains(fixture.Build("ValidateReleaseTagMatchesProductVersion", false,
                    "-p:RNAssistantVersionPrefix=16.1.0", "-p:RNAssistantVersionSuffix=dev", "-p:RNAssistantReleaseTag=v16.1.0-dev"),
                    "development builds cannot be tagged", "dev is not a release milestone");

                File.WriteAllText(Path.Combine(fixture.Repository, "untracked.txt"), "not releasable");
                fixture.Build("PrintBuildIdentity", true);
                AssertContains(fixture.ReleaseBuild("PrintBuildIdentity", false), "clean working tree", "untracked files block release builds");
                fixture.Git("add", "untracked.txt");
                AssertContains(fixture.ReleaseBuild("ValidateRNAssistantRelease", false), "clean working tree", "staged files also block release");
                fixture.Commit("fixture cleanup");

                var changelog = Path.Combine(fixture.Repository, "CHANGELOG.md");
                File.WriteAllText(changelog, "# Changelog\n\n## [16.1.0-rc.1] - 2026-08-28\n\n### Fixed\n\n## [16.0.4] - 2026-08-27\n- Old note\n");
                AssertContains(fixture.ReleaseBuild("ValidateReleaseChangelog", false), "at least one release note", "older notes cannot qualify an empty release");
                File.Delete(changelog);
                AssertContains(fixture.ReleaseBuild("ValidateReleaseChangelog", false), "requires CHANGELOG.md", "missing changelog blocks release");
            }
        }

        private static void VersioningTagsCannotBeReused()
        {
            using (var fixture = new VersioningFixture())
            {
                AssertContains(fixture.ReleaseBuild("ValidateTagDoesNotExist", false),
                    "Unable to verify remote release tag absence", "missing remote fails closed");
                var remote = Path.Combine(fixture.TemporaryRoot, "remote.git");
                fixture.Git("init", "--quiet", "--bare", remote);
                fixture.Git("remote", "add", "origin", remote);
                fixture.ReleaseBuild("ValidateTagDoesNotExist", true);
                // These refs exist only in disposable fixture repositories, never in the working repository.
                fixture.Git("push", "--quiet", "origin", "HEAD:refs/tags/v16.1.0-rc.1");
                AssertContains(fixture.ReleaseBuild("ValidateTagDoesNotExist", false), "already exists remotely", "remote tags cannot be reused");
                fixture.Git("update-ref", "refs/tags/v16.1.0-rc.1", "HEAD");
                AssertContains(fixture.ReleaseBuild("ValidateTagDoesNotExist", false), "already exists locally", "local tags cannot be reused");
            }
        }

        private static void VersioningGeneratesAssemblyMetadata()
        {
            var assembly = typeof(Program).Assembly;
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            var identity = metadata["CommitSha"] == "unknown"
                ? "+source-archive" : "+g" + metadata["CommitSha"].Substring(0, 12);
            AssertContains(informational, metadata["ProductVersion"] + identity,
                "SDK assembly carries product and commit identity or explicit archive provenance");
            AssertTrue(metadata["BuildUtc"].EndsWith("Z", StringComparison.Ordinal), "SDK assembly records UTC");
            AssertTrue(!string.IsNullOrWhiteSpace(metadata["Branch"]) && !string.IsNullOrWhiteSpace(metadata["Channel"]), "SDK assembly records branch/channel");
            using (var fixture = new VersioningFixture())
            {
                fixture.Build("GenerateRNAssistantVersionInfo", true);
                var generated = File.ReadAllText(Path.Combine(fixture.Repository, "obj", "RNAssistant.Version.g.cs"));
                AssertContains(generated, "AssemblyInformationalVersionAttribute", "old-style project emits informational version");
                AssertContains(generated, fixture.ProductVersion + "+g", "old-style project includes build identity");
                AssertContains(generated, "AssemblyMetadataAttribute(\"CommitSha\", \"" + fixture.Git("rev-parse", "HEAD").Trim() + "\")",
                    "old-style project emits the full commit SHA");
                AssertContains(generated, "AssemblyMetadataAttribute(\"BuildUtc\"", "old-style project emits UTC metadata");
            }
        }

        private sealed class VersioningFixture : IDisposable
        {
            public string TemporaryRoot { get; private set; }
            public string Repository { get; private set; }
            public string VersionPrefix { get; private set; }
            public string ProductVersion { get; private set; }

            public VersioningFixture()
            {
                TemporaryRoot = Path.Combine(Path.GetTempPath(), "RNAssistant versioning " + Guid.NewGuid().ToString("N"));
                Repository = Path.Combine(TemporaryRoot, "repo");
                Directory.CreateDirectory(Path.Combine(Repository, "build"));
                Directory.CreateDirectory(Path.Combine(Repository, "obj"));
                var root = FindHarnessRepositoryRoot();
                foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "build/RNAssistant.Release.targets" })
                    File.Copy(Path.Combine(root, file), Path.Combine(Repository, file));
                var props = XDocument.Load(Path.Combine(Repository, "Directory.Build.props"));
                VersionPrefix = props.Descendants().Single(element => element.Name.LocalName == "RNAssistantVersionPrefix").Value;
                var suffix = props.Descendants().Single(element => element.Name.LocalName == "RNAssistantVersionSuffix").Value;
                ProductVersion = VersionPrefix + (suffix.Length == 0 ? "" : "-" + suffix);
                File.WriteAllText(Path.Combine(Repository, ".gitignore"), "obj/\n");
                File.WriteAllText(Path.Combine(Repository, "CHANGELOG.md"),
                    "# Changelog\n\n## [Unreleased]\n\n## [16.1.0-rc.1] - 2026-08-28\n\n### Fixed\n\n- Don't lose build identity.\n");
                File.WriteAllText(Path.Combine(Repository, "Versioning.csproj"), @"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""Directory.Build.props"" />
  <PropertyGroup><IntermediateOutputPath>obj/</IntermediateOutputPath></PropertyGroup>
  <Import Project=""Directory.Build.targets"" />
  <Target Name=""PrepareForBuild"" />
  <Target Name=""PrintBuildIdentity"" DependsOnTargets=""PrepareForBuild"">
    <Message Importance=""high"" Text=""Product=$(Version) Identity=$(InformationalVersion) File=$(FileVersion) Application=$(ApplicationVersion)"" />
  </Target>
</Project>");
                Git("init", "--quiet", "--initial-branch=stabilization/16.1");
                Commit("versioning fixture");
            }

            public string Git(params string[] arguments)
            {
                return RunVersioningCommand(Repository, "git", true, new[]
                {
                    "-c", "user.name=RNAssistant Harness", "-c", "user.email=harness@example.invalid",
                    "-c", "commit.gpgsign=false", "-c", "core.hooksPath="
                }.Concat(arguments).ToArray());
            }

            public void Commit(string message)
            {
                Git("add", ".");
                Git("commit", "--quiet", "-m", message);
            }

            public string Build(string target, bool success, params string[] properties)
            {
                return RunVersioningCommand(Repository, "dotnet", success, new[]
                {
                    "msbuild", "Versioning.csproj", "-nologo", "-v:minimal", "-t:" + target
                }.Concat(properties).ToArray());
            }

            public string ReleaseBuild(string target, bool success)
            {
                return Build(target, success, "-p:RNAssistantVersionPrefix=16.1.0",
                    "-p:RNAssistantVersionSuffix=rc.1", "-p:RNAssistantReleaseTag=v16.1.0-rc.1");
            }

            public void Dispose()
            {
                foreach (var path in Directory.GetFiles(TemporaryRoot, "*", SearchOption.AllDirectories))
                    File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(TemporaryRoot, true);
            }
        }

        private static string RunVersioningCommand(string directory, string executable, bool success, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill(true);
                    throw new InvalidOperationException("Versioning fixture command timed out: " + executable);
                }
                var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                AssertEqual(success, process.ExitCode == 0, executable + " " + string.Join(" ", arguments) + "\n" + output);
                return output;
            }
        }
    }
}
