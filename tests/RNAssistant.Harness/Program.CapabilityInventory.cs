using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        // Composition-only inventory: isolated stores, fake Office, no model/browser/COM dispatch.
        private sealed class CapabilityInventorySurface
        {
            public string Surface { get; set; }
            public string[] Tools { get; set; }
            public string[] Skills { get; set; }
        }

        private sealed class CapabilityInventoryReport
        {
            public List<CapabilityInventorySurface> Surfaces { get; set; }
            public string[] UniqueTools { get; set; }
            public string[] UniqueSkills { get; set; }
        }

        private static string[] InventoryIds(IEnumerable<string> ids)
        { return ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray(); }

        private static CapabilityInventoryReport CaptureCapabilityInventory()
        {
            var surfaces = new List<CapabilityInventorySurface>();
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            foreach (var javaScript in new[] { false, true })
                WithTempPaths(paths =>
                {
                    var adapter = FakeOfficeAdapter.ForHost(host);
                    var settings = new AppSettings { EnableAgentJavaScript = javaScript };
                    var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths), () => settings, paths: paths);
                    var publication = executor.CaptureCatalogs();
                    surfaces.Add(new CapabilityInventorySurface {
                        Surface = host + (javaScript ? "+js" : string.Empty),
                        Tools = InventoryIds(new ToolCatalogService(adapter, executor)
                            .GetPublishedVisibleTools(publication.Tools).Select(tool => tool.Id)),
                        Skills = InventoryIds(executor.CaptureSkills(publication).Skills.Select(skill => skill.Id))
                    });
                });
            WithTempPaths(paths =>
            {
                var root = Path.Combine(paths.Root, "workspace");
                Directory.CreateDirectory(root);
                var workspace = new WorkspaceStore(paths).Open(root);
                using (var service = new WorkspaceConversationService(paths))
                    surfaces.Add(new CapabilityInventorySurface {
                        Surface = "Workspace", Tools = InventoryIds(service.AvailableToolIds(workspace)),
                        Skills = InventoryIds(WorkspaceSkillProvider.GetSkills().Select(skill => skill.Id))
                    });
                // Exercise the real plugin source registrations, excluding dynamic installed commands.
                using (var plugins = new PluginApplicationService(paths))
                {
                    var plugin = plugins.Create(new PluginCreateRequest {
                        Name = "Inventory", RootPath = Path.Combine(paths.Root, "plugin-source") });
                    var source = new WorkspaceStore(paths).Open(plugin.SourceRoot);
                    surfaces.Add(new CapabilityInventorySurface {
                        Surface = "Plugin source additions",
                        Tools = InventoryIds(plugins.CaptureTools(source).Select(tool => tool.Entry.Id)),
                        Skills = new string[0]
                    });
                }
                // web.verify is conditional on an available browser. Its registration is source-owned
                // in WorkspacePorts; this inventory deliberately never starts a browser.
                surfaces.Add(new CapabilityInventorySurface {
                    Surface = "Browser addition (source-declared)",
                    Tools = new[] { "web.verify" }, Skills = new string[0]
                });
            });
            return new CapabilityInventoryReport {
                Surfaces = surfaces,
                UniqueTools = InventoryIds(surfaces.SelectMany(surface => surface.Tools)),
                UniqueSkills = InventoryIds(surfaces.SelectMany(surface => surface.Skills))
            };
        }

        private static int CapabilityInventory(string outputPath)
        {
            var report = CaptureCapabilityInventory();
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            foreach (var surface in report.Surfaces)
                Console.WriteLine(surface.Surface + ": " + surface.Tools.Length + " tools, " + surface.Skills.Length + " skills");
            Console.WriteLine("Union: " + report.UniqueTools.Length + " tools, " + report.UniqueSkills.Length + " skills");
            return 0;
        }
    }
}
