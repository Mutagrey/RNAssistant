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
            var identityObject = new object();
            AssertEqual(
                DocumentIdentity.RuntimeKey("Excel", identityObject),
                DocumentIdentity.RuntimeKey("Excel", identityObject),
                "runtime identity is stable for the same object");

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

            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            {
                var guardedAdapter = FakeOfficeAdapter.ForHost(host);
                var toolId = guardedAdapter.GetBuiltInTools().First().Id;
                using (var dispatched = new DispatchedOfficeApplicationAdapter(delegate { return guardedAdapter; }))
                {
                    var originalDocumentKey = dispatched.DocumentKey;
                    var originalRuntimeKey = dispatched.RuntimeDocumentKey;
                    using (((IOfficeDocumentExecutionGuard)dispatched).BeginExpectedDocument(
                        host, originalDocumentKey, originalRuntimeKey))
                    {
                        guardedAdapter.RuntimeDocumentKeyValue = originalRuntimeKey + "-new-proxy";
                        var sameDocument = dispatched.ExecuteTool(new ToolCommand { ToolId = toolId });
                        AssertTrue(sameDocument.Success,
                            host + " guard accepts a stable document key when COM runtime identity changes");
                    }

                    guardedAdapter.RuntimeDocumentKeyValue = originalRuntimeKey;
                    using (((IOfficeDocumentExecutionGuard)dispatched).BeginExpectedDocument(
                        host, originalDocumentKey, originalRuntimeKey))
                    {
                        guardedAdapter.DocumentKeyValue = originalDocumentKey + "-saved";
                        var migratedDocument = dispatched.ExecuteTool(new ToolCommand { ToolId = toolId });
                        AssertTrue(migratedDocument.Success,
                            host + " guard accepts the same runtime document after identity migration");
                    }

                    using (((IOfficeDocumentExecutionGuard)dispatched).BeginExpectedDocument(
                        host, guardedAdapter.DocumentKey, guardedAdapter.RuntimeDocumentKey))
                    {
                        guardedAdapter.DocumentKeyValue += "-other";
                        guardedAdapter.RuntimeDocumentKeyValue += "-other";
                        var blocked = dispatched.ExecuteTool(new ToolCommand { ToolId = toolId });
                        AssertEqual("active_document_changed", blocked.ErrorCode,
                            host + " guard blocks a different Office document");
                        var readBlocked = false;
                        try
                        {
                            dispatched.GetDocumentSnapshot(128);
                        }
                        catch (OfficeDocumentGuardException ex)
                        {
                            readBlocked = string.Equals(
                                ex.ErrorCode,
                                "active_document_changed",
                                StringComparison.Ordinal);
                        }
                        AssertTrue(readBlocked,
                            host + " guard also blocks live document reads after dispatch");
                        AssertEqual(2, guardedAdapter.Executed.Count,
                            host + " blocked tool never reaches Office adapter");
                    }
                }
            }
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

        private static void DocumentOpenServiceRecognizesWebPaths()
        {
            AssertTrue(DocumentOpenService.IsAvailable("https://example.sharepoint.com/Documents/Book.xlsx"), "https document path");
            AssertTrue(DocumentOpenService.IsAvailable("http://example.test/Book.xlsx"), "http document path");
            AssertTrue(!DocumentOpenService.IsAvailable(string.Empty), "empty document path");
            AssertTrue(DocumentOpenService.SamePath("C:\\Docs\\Book.xlsx", "c:/docs/book.xlsx"),
                "Windows paths compare case-insensitively");
            AssertTrue(DocumentOpenService.SamePath(
                "https://example.sharepoint.com/Documents/Book%20One.xlsx",
                "https://EXAMPLE.sharepoint.com/Documents/Book One.xlsx"),
                "SharePoint URLs compare canonically");
            AssertTrue(!DocumentOpenService.SamePath("C:\\Docs\\One.xlsx", "C:\\Docs\\Two.xlsx"),
                "different full paths stay distinct");
        }

        private static void UnsavedDocumentIdentityUsesRuntimeKey()
        {
            var properties = new FakeDocumentProperties();
            var key = DocumentIdentity.ForOfficeDocument("Excel", string.Empty, "Excel:Runtime:first", delegate { return properties; });

            AssertEqual("Excel:Runtime:first", key, "unsaved document runtime identity");
            AssertEqual(0, properties.Count, "identity lookup does not dirty unsaved document");
        }

        private static void SavedDocumentIdentityUsesFullPathOrLegacyId()
        {
            var properties = new FakeDocumentProperties();
            var first = DocumentIdentity.ForOfficeDocument(
                "Excel",
                "C:\\Docs\\One.xlsx",
                "Excel:Runtime:first",
                delegate { return properties; });
            var second = DocumentIdentity.ForOfficeDocument(
                "Excel",
                "C:\\Docs\\Two.xlsx",
                "Excel:Runtime:second",
                delegate { return properties; });

            AssertEqual("Excel:Path:C:\\Docs\\One.xlsx", first, "saved document full path identity");
            AssertEqual("Excel:Path:C:\\Docs\\Two.xlsx", second, "same-folder documents stay distinct");
            AssertEqual(0, properties.Count, "identity lookup does not add a hidden property");

            properties.Add(DocumentIdentity.PropertyName, false, 4, "legacy-id");
            var legacy = DocumentIdentity.ForOfficeDocument(
                "Excel",
                "C:\\Docs\\One.xlsx",
                "Excel:Runtime:first",
                delegate { return properties; });
            AssertEqual("Excel:DocumentId:legacy-id", legacy, "existing persisted identity remains supported");
        }

        public sealed class FakeDocumentProperties
        {
            private readonly Dictionary<string, FakeDocumentProperty> _values =
                new Dictionary<string, FakeDocumentProperty>(StringComparer.OrdinalIgnoreCase);

            public int Count { get { return _values.Count; } }

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
