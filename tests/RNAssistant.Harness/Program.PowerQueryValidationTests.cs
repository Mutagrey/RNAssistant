using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PowerQueryValidationRequiresCompleteObservations()
        {
            var backend = new StubPowerQueryBackend(); backend.Seed("Sales", "let x = 1 in x");
            var adapter = FakeOfficeAdapter.ForHost("Excel"); adapter.ExcelPowerQueryBackend = backend;
            var load = new ExcelPowerQueryLoadBinding { Sheet = "Data", Table = "SalesOutput", Rows = 3, Columns = 2,
                HeaderRange = "A1:B1", DataRange = "A2:B4", Refreshing = false, BindingIdentity = "result-table" };
            var bindingVersion = "load-v1";
            backend.BindingsOverride = name => new ExcelPowerQueryBindingsSnapshot { QueryName = name,
                QueryIdentity = "fixture-query:" + name, BindingVersion = bindingVersion, IsComplete = true,
                Loads = new[] { load }, Connections = new ExcelPowerQueryConnectionBinding[0] };
            var values = new List<List<object>> { new List<object> { 1, "Alpha" },
                new List<object> { 1, " " }, new List<object> { 2, null } };
            var bodyReads = 0;
            var reader = new StubExcelReadBackend { ReadRangeOverride = rangeRequest => {
                var header = rangeRequest.Address == "A1:B1";
                if (!header) bodyReads++;
                return new ExcelRangeSnapshot { Sheet = "Data", Address = rangeRequest.Address,
                    Rows = header ? 1 : values.Count, Columns = 2, CellCount = (header ? 1 : values.Count) * 2,
                    Values = header ? new List<List<object>> { new List<object> { "Id", "Value" } } : values };
            } };
            var provider = new ExcelResourceProvider(adapter, reader, null);
            var session = NewSession(adapter); session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
            var request = new PowerQueryDataCheckRequest { ExpectedRows = 3, Table = "SalesOutput",
                RequiredColumns = new[] { "Id", "Value" }, NonEmptyColumns = new[] { "Value" }, KeyColumns = new[] { "Id" } };
            var failed = provider.ValidateQueryData(session, "Sales", request, CancellationToken.None);
            AssertEqual("failed", failed.Status, "actual blank cells and duplicate keys fail the loaded-table checks");
            AssertEqual(2, failed.Checks.Single(check => check.Name == "nonEmpty").FailureCount, "all blank cells are counted");
            AssertEqual(1, failed.Checks.Single(check => check.Name == "uniqueKey").FailureCount, "all rows participate in uniqueness");
            AssertEqual("whole_loaded_table", failed.Coverage, "complete coverage is explicit");

            values = new List<List<object>> { new List<object> { 1, "#N/A" },
                new List<object> { 2, -2146826246 }, new List<object> { 3, "Gamma" } };
            var plainValues = provider.ValidateQueryData(session, "Sales", request, CancellationToken.None);
            AssertEqual("passed", plainValues.Status, "literal error-like text and numeric values do not invent Excel errors");
            backend.CellErrors = new ExcelPowerQueryCellErrorSnapshot { Complete = false, Message = "Mask unavailable." };
            var unobserved = provider.ValidateQueryData(session, "Sales", request, CancellationToken.None);
            AssertEqual("incomplete", unobserved.Status, "unavailable error evidence cannot claim a clean table");
            AssertEqual("not_checked", unobserved.Checks.Single(check => check.Name == "cellErrors").Status,
                "error observation remains independently incomplete");
            backend.CellErrors = new ExcelPowerQueryCellErrorSnapshot { Complete = true, ErrorCount = 1 };
            AssertEqual("failed", provider.ValidateQueryData(session, "Sales", request, CancellationToken.None).Status,
                "an actual native error mask fails validation even when scalar values look ordinary");

            backend.CellErrors = new ExcelPowerQueryCellErrorSnapshot { Complete = true, ErrorCount = 0 };
            var captured = reader.ReadRangeOverride;
            reader.ReadRangeOverride = range => { var result = captured(range); bindingVersion = "changed-load"; return result; };
            var changed = provider.ValidateQueryData(session, "Sales", request, CancellationToken.None);
            AssertTrue(!changed.BindingStable && changed.Status == "incomplete" &&
                changed.Checks.All(check => check.Status != "passed"), "binding drift invalidates sequential positive checks");
            reader.ReadRangeOverride = captured;
            load.Rows = ExcelReadService.MaxReadCells; load.DataRange = "A2:B100001";
            request.ExpectedRows = load.Rows;
            backend.CellErrors = new ExcelPowerQueryCellErrorSnapshot { Complete = false, Message = "Mask bound exceeded." };
            var before = bodyReads;
            var oversized = provider.ValidateQueryData(session, "Sales", request, CancellationToken.None);
            AssertTrue(oversized.Status == "incomplete" && oversized.Coverage == "metadata" && oversized.CapturedRows == 0,
                "large tables are explicitly incomplete instead of silently sampled");
            AssertEqual(before, bodyReads, "the full-data bound is checked before any body capture");
            AssertEqual("not_checked", oversized.Checks.Single(check => check.Name == "uniqueKey").Status,
                "uniqueness cannot pass from a bounded partial sample");
            AssertEqual(0, backend.Dispatches, "loaded data validation never refreshes or evaluates M");
        }

        private static void PowerQueryErrorMaskUsesOnlyNativeBooleanEvidence()
        {
            int count;
            AssertTrue(ExcelPowerQueryErrorMask.TryCount(false, 1, 1, out count) && count == 0,
                "one clean cell is a complete scalar boolean observation");
            AssertTrue(ExcelPowerQueryErrorMask.TryCount(true, 1, 1, out count) && count == 1,
                "one native error cell is counted");
            var mask = Array.CreateInstance(typeof(object), new[] { 2, 2 }, new[] { 1, 4 });
            mask.SetValue(false, 1, 4); mask.SetValue(true, 1, 5);
            mask.SetValue(true, 2, 4); mask.SetValue(false, 2, 5);
            AssertTrue(ExcelPowerQueryErrorMask.TryCount(mask, 2, 2, out count) && count == 2,
                "COM-style nonzero array lower bounds preserve the complete native mask");
            mask.SetValue(-2146826246, 2, 5);
            AssertTrue(!ExcelPowerQueryErrorMask.TryCount(mask, 2, 2, out count) && count == 0,
                "an HRESULT-like number is unavailable evidence and discards a partial count");
            AssertTrue(!ExcelPowerQueryErrorMask.TryCount("#N/A", 1, 1, out count) &&
                !ExcelPowerQueryErrorMask.TryCount(0, 1, 1, out count) &&
                !ExcelPowerQueryErrorMask.TryCount(new bool[1, 2], 2, 1, out count),
                "text, numbers, and transposed dimensions cannot stand in for native booleans");
            AssertTrue(!ExcelPowerQueryErrorMask.TryCount(new bool[65, 65], 65, 65, out count),
                "the native mask bound is enforced before walking an oversized result");
        }
    }
}
