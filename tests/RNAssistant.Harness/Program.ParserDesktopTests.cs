using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ParsesFencedAgentSteps()
        {
            var commands = new ToolCommandParser().Parse(
                "```rnassistant-agent\n" +
                "{\"steps\":[" +
                "{\"description\":\"Add sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}," +
                "{\"toolId\":\"excel.add_chart\",\"args\":{\"title\":\"Sales\"}}" +
                "]}" +
                "\n```");

            AssertEqual(2, commands.Count, "command count");
            AssertEqual("excel.add_sheet", commands[0].ToolId, "first tool id");
            AssertEqual("Report", commands[0].Arguments["name"], "first arg");
            AssertEqual("excel.add_chart", commands[1].ToolId, "second tool id");
        }

        private static void ParsesNativeToolCalls()
        {
            var commands = new ToolCommandParser().Parse(
                "```rnassistant-agent\n" +
                "{\"tool_calls\":[{\"id\":\"call_abc\",\"type\":\"function\",\"function\":{\"name\":\"excel.write_table\",\"arguments\":\"{\\\"sheet\\\":\\\"Data\\\",\\\"startAddress\\\":\\\"A1\\\"}\"}}]}" +
                "\n```");

            AssertEqual(1, commands.Count, "command count");
            AssertEqual("excel.write_table", commands[0].ToolId, "tool id");
            AssertEqual("Data", commands[0].Arguments["sheet"], "sheet arg");
            AssertEqual("A1", commands[0].Arguments["startAddress"], "address arg");
        }

        private static void ParserNormalizesPrimitiveAndComplexArgs()
        {
            var commands = new ToolCommandParser().Parse(
                "```rnassistant-agent\n" +
                "{\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"Data\",\"count\":2,\"enabled\":true,\"values\":[[\"Month\",\"Sales\"]],\"meta\":{\"source\":\"test\"}}}" +
                "\n```");

            AssertEqual(1, commands.Count, "command count");
            AssertEqual("Data", commands[0].Arguments["sheet"], "string arg");
            AssertEqual(2L, commands[0].Arguments["count"], "integer arg");
            AssertEqual(true, commands[0].Arguments["enabled"], "bool arg");
            AssertEqual("[[\"Month\",\"Sales\"]]", commands[0].Arguments["values"], "array arg");
            AssertEqual("{\"source\":\"test\"}", commands[0].Arguments["meta"], "object arg");
        }

        private static void ParsesBareJsonArray()
        {
            var commands = new ToolCommandParser().Parse(
                "[" +
                "{\"tool\":\"word.insert_text\",\"parameters\":{\"text\":\"Hello\"}}," +
                "{\"action\":\"excel.autofit\",\"input\":{\"sheet\":\"Data\"}}" +
                "]");

            AssertEqual(2, commands.Count, "command count");
            AssertEqual("word.insert_text", commands[0].ToolId, "first tool id");
            AssertEqual("Hello", commands[0].Arguments["text"], "text arg");
            AssertEqual("excel.autofit", commands[1].ToolId, "second tool id");
        }

        private static void ParsesNoisyEmbeddedJson()
        {
            var commands = new ToolCommandParser().Parse(
                "I will handle it. First, here is the plan: " +
                "{\"steps\":[{\"toolId\":\"powerpoint.add_slide\",\"arguments\":{\"title\":\"Q1\",\"body\":\"Revenue grew\"}}]} " +
                "Then I will summarize.");

            AssertEqual(1, commands.Count, "command count");
            AssertEqual("powerpoint.add_slide", commands[0].ToolId, "tool id");
            AssertEqual("Q1", commands[0].Arguments["title"], "title arg");
        }

        private static void SkipsBadJson()
        {
            var commands = new ToolCommandParser().Parse("```rnassistant-agent\n{\"steps\":[\n```");
            AssertEqual(0, commands.Count, "command count");
        }

        private static void RecoversMalformedAgentJson()
        {
            var result = new ToolCommandParser().ParseWithDiagnostics(
                "```rnassistant-agent\n" +
                "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"},}\n```");

            AssertEqual(1, result.Commands.Count, "command count");
            AssertEqual("excel.add_sheet", result.Commands[0].ToolId, "tool id");
            AssertEqual("Report", result.Commands[0].Arguments["name"], "sheet name");
            AssertTrue(result.HasRecoveredCommands, "recovery diagnostic");
        }

        private static void ParsesOfficeTargetJsonDescriptor()
        {
            var target = OfficeTargetDescriptor.FromJson("{\"Host\":\"Excel\",\"Hwnd\":123456,\"ProcessId\":4321,\"FullName\":\"C:\\\\Docs\\\\Book.xlsx\",\"Name\":\"Book.xlsx\",\"Selection\":\"Sheet1!A1:B2\"}");
            AssertEqual("Excel", target.Host, "host");
            AssertEqual(123456L, target.Hwnd, "hwnd");
            AssertEqual(4321, target.ProcessId, "process id");
            AssertEqual("C:\\Docs\\Book.xlsx", target.FullName, "full name");
            AssertEqual("Book.xlsx", target.Name, "name");
            AssertEqual("Sheet1!A1:B2", target.Selection, "selection");
            AssertTrue(target.HasDocumentIdentity, "has identity");
        }

        private static void ParsesOfficeTargetBase64Descriptor()
        {
            var json = "{\"Host\":\"Outlook\",\"EntryId\":\"abc123\",\"Name\":\"Mail\"}";
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var target = OfficeTargetDescriptor.FromBase64Json(base64);
            AssertEqual("Outlook", target.Host, "host");
            AssertEqual("abc123", target.EntryId, "entry id");
            AssertEqual("Mail", target.Name, "name");
            AssertTrue(target.HasDocumentIdentity, "has identity");
        }

        private static void OfficeTargetIgnoresUtf8Bom()
        {
            var json = "\uFEFF{\"Host\":\"Word\",\"FullName\":\"C:\\\\Docs\\\\Doc.docx\"}";
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var target = OfficeTargetDescriptor.FromBase64Json(base64);
            AssertEqual("Word", target.Host, "host");
            AssertEqual("C:\\Docs\\Doc.docx", target.FullName, "full name");
        }

        private static void TargetRegistryManualModeKeepsSelection()
        {
            var registry = new OfficeTargetRegistry();
            registry.Mode = TargetSelectionMode.Manual;
            var first = registry.Select(new OfficeTargetDescriptor { Host = "Excel", Hwnd = 1, FullName = "C:\\Docs\\A.xlsx", Name = "A.xlsx" });
            var second = registry.Upsert(new OfficeTargetDescriptor { Host = "Word", Hwnd = 2, FullName = "C:\\Docs\\B.docx", Name = "B.docx" });

            AssertEqual(TargetSelectionMode.Manual, registry.Mode, "manual mode");
            AssertEqual(first.Id, registry.SelectedTargetId, "manual selected id");
            AssertEqual("A.xlsx", registry.SelectedTarget.Target.Name, "manual selected target");
            AssertTrue(second != null, "second target added");
            AssertEqual(2, registry.Targets.Count, "registry count");
        }

        private static void TargetRegistryAutoModeCanSwitchSelection()
        {
            var registry = new OfficeTargetRegistry();
            registry.Mode = TargetSelectionMode.AutoFollow;
            registry.Select(new OfficeTargetDescriptor { Host = "Excel", Hwnd = 1, FullName = "C:\\Docs\\A.xlsx", Name = "A.xlsx" });
            var second = registry.Select(new OfficeTargetDescriptor { Host = "Word", Hwnd = 2, FullName = "C:\\Docs\\B.docx", Name = "B.docx" });

            AssertEqual(TargetSelectionMode.AutoFollow, registry.Mode, "mode");
            AssertEqual(second.Id, registry.SelectedTargetId, "auto selected id");
            AssertEqual("B.docx", registry.SelectedTarget.Target.Name, "auto selected target");
            AssertEqual(1, registry.ForHost("Word").Count, "word count");
        }

        private static void OfficeStaDispatcherRunsSta()
        {
            using (var dispatcher = new OfficeStaDispatcher())
            {
                var firstThreadId = dispatcher.Invoke(delegate { return Thread.CurrentThread.ManagedThreadId; });
                var secondThreadId = dispatcher.Invoke(delegate { return Thread.CurrentThread.ManagedThreadId; });

                AssertEqual(firstThreadId, secondThreadId, "dispatcher thread id");
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var apartment = dispatcher.Invoke(delegate { return Thread.CurrentThread.GetApartmentState(); });
                    AssertEqual(ApartmentState.STA, apartment, "dispatcher apartment");
                }
            }
        }

        private static void DispatchedAdapterDelegatesCalls()
        {
            var createdOnThread = 0;
            var executeOnThread = 0;
            var adapter = new FakeOfficeAdapter();

            using (var dispatched = new DispatchedOfficeApplicationAdapter(delegate
            {
                createdOnThread = Thread.CurrentThread.ManagedThreadId;
                return new ThreadRecordingOfficeAdapter(adapter, delegate
                {
                    executeOnThread = Thread.CurrentThread.ManagedThreadId;
                });
            }))
            {
                AssertEqual("Excel", dispatched.HostName, "host name");
                var result = dispatched.ExecuteTool(new ToolCommand { ToolId = "excel.read_range" });
                AssertTrue(result.Success, "tool success");
                AssertEqual(1, adapter.Executed.Count, "executed count");
            }

            AssertTrue(createdOnThread != 0, "created thread");
            AssertEqual(createdOnThread, executeOnThread, "execute thread");
        }

        private static void DocumentCatalogActivatesSelectedDocument()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var catalog = (IOfficeDocumentCatalog)adapter;
            var before = catalog.ListOpenDocuments();

            AssertEqual(2, before.Count, "open document count");
            AssertTrue(before.Any(item => item.DocumentKey == "forecast-doc" && !item.IsActive), "forecast initially inactive");
            AssertTrue(catalog.ActivateDocument("forecast-doc"), "forecast activation succeeds");
            AssertEqual("forecast-doc", adapter.DocumentKey, "active document key");
            AssertEqual("Forecast.xlsx", adapter.DocumentTitle, "active document title");
            AssertTrue(catalog.ListOpenDocuments().Any(item => item.DocumentKey == "forecast-doc" && item.IsActive), "forecast marked active");
        }
    }
}
