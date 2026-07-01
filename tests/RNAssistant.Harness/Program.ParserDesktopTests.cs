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

        private static void UnsavedDocumentIdentityUsesStoredId()
        {
            var properties = new FakeDocumentProperties();
            var first = DocumentIdentity.ForOfficeDocument("Excel", string.Empty, "Excel:Runtime:first", delegate { return properties; });
            var second = DocumentIdentity.ForOfficeDocument("Excel", string.Empty, "Excel:Runtime:second", delegate { return properties; });

            AssertTrue(first.StartsWith("Excel:DocumentId:", StringComparison.Ordinal), "unsaved document id prefix");
            AssertEqual(first, second, "unsaved document id stable");
        }

        public sealed class FakeDocumentProperties
        {
            private readonly Dictionary<string, FakeDocumentProperty> _values =
                new Dictionary<string, FakeDocumentProperty>(StringComparer.OrdinalIgnoreCase);

            public FakeDocumentProperty this[string name]
            {
                get
                {
                    FakeDocumentProperty property;
                    if (!_values.TryGetValue(name, out property))
                    {
                        throw new KeyNotFoundException();
                    }
                    return property;
                }
            }

            public void Add(string name, bool linkToContent, int propertyType, string value)
            {
                _values[name] = new FakeDocumentProperty { Value = value };
            }
        }

        public sealed class FakeDocumentProperty
        {
            public string Value { get; set; }
        }
    }
}
