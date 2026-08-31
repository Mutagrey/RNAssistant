using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelSheetService
    {
        private readonly IExcelSheetBackend _backend;

        public ExcelSheetService(IExcelSheetBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public ExcelSheetOutcome Add(
            ExcelAddSheetRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelAddSheetRequest();
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            var name = request.Name ?? string.Empty;
            var dispatched = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = ReadSnapshot();
                var nameError = ValidateName(before.SheetNames, name, null);
                if (nameError != null) return nameError;
                _backend.Add(new ExcelAddSheetApplyRequest
                {
                    Name = name,
                    ExpectedSheetNames = before.SheetNames.ToArray()
                }, delegate
                {
                    dispatched = true;
                    markDispatchPossible();
                });
                cancellationToken.ThrowIfCancellationRequested();
                var after = ReadSnapshot();
                var expected = before.SheetNames.Concat(new[] { name }).ToArray();
                if (!SameNamesIgnoringOrder(expected, after.SheetNames) ||
                    !after.SheetNames.Any(item => string.Equals(item, name, StringComparison.Ordinal)))
                    return Unknown(
                        "Excel sheet may have been added, but exact read-back diverged.",
                        "excel_sheet_verification_failed");
                return ExcelSheetOutcome.Ok(
                    "Added sheet: " + name, null, ExcelSheetEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExcelSheetBackendException ex)
            {
                return dispatched
                    ? Unknown(ex.Message, "excel_sheet_effect_unknown", ex.DetailsJson)
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? Unknown(ex.Message, "excel_sheet_effect_unknown")
                    : Failure(ex.Message, "office_tool_error", true);
            }
        }

        public ExcelSheetOutcome Rename(
            ExcelRenameSheetRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelRenameSheetRequest();
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            if (string.IsNullOrWhiteSpace(request.NewName))
                return Failure("newName is required.", "invalid_arguments", false);
            var dispatched = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = ReadSnapshot();
                var source = ResolveSource(before, request.Sheet);
                if (source == null)
                    return Failure(
                        string.IsNullOrWhiteSpace(request.Sheet)
                            ? "Workbook has no worksheets."
                            : "Worksheet not found: " + request.Sheet,
                        "excel_sheet_not_found", false);
                var nameError = ValidateName(before.SheetNames, request.NewName, source);
                if (nameError != null) return nameError;
                if (string.Equals(source, request.NewName, StringComparison.Ordinal))
                    return ExcelSheetOutcome.Ok(
                        "Renamed sheet " + source + " to " + request.NewName,
                        null, ExcelSheetEffect.VerifiedNoChange);

                _backend.Rename(new ExcelRenameSheetApplyRequest
                {
                    Sheet = source,
                    NewName = request.NewName,
                    ExpectedSheetNames = before.SheetNames.ToArray()
                }, delegate
                {
                    dispatched = true;
                    markDispatchPossible();
                });
                cancellationToken.ThrowIfCancellationRequested();
                var after = ReadSnapshot();
                var expected = before.SheetNames.Select(item =>
                    string.Equals(item, source, StringComparison.OrdinalIgnoreCase)
                        ? request.NewName : item).ToArray();
                if (!SameSequence(expected, after.SheetNames))
                    return Unknown(
                        "Excel sheet may have been renamed, but exact read-back diverged.",
                        "excel_sheet_verification_failed");
                return ExcelSheetOutcome.Ok(
                    "Renamed sheet " + source + " to " + request.NewName,
                    null, ExcelSheetEffect.VerifiedChange);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExcelSheetBackendException ex)
            {
                return dispatched
                    ? Unknown(ex.Message, "excel_sheet_effect_unknown", ex.DetailsJson)
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? Unknown(ex.Message, "excel_sheet_effect_unknown")
                    : Failure(ex.Message, "office_tool_error", true);
            }
        }

        private ExcelSheetCollectionSnapshot ReadSnapshot()
        {
            var snapshot = _backend.Read();
            if (snapshot == null || snapshot.SheetNames == null)
                throw new ExcelSheetBackendException(
                    "Excel sheet snapshot is unavailable.",
                    "excel_sheet_snapshot_invalid", false);
            var names = snapshot.SheetNames.ToArray();
            if (names.Any(string.IsNullOrWhiteSpace) ||
                names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw new ExcelSheetBackendException(
                    "Excel sheet snapshot is invalid.",
                    "excel_sheet_snapshot_invalid", false);
            return new ExcelSheetCollectionSnapshot
            {
                ActiveSheet = snapshot.ActiveSheet ?? string.Empty,
                SheetNames = names
            };
        }

        private static ExcelSheetOutcome ValidateName(
            IReadOnlyList<string> names, string name, string currentName)
        {
            if (!ExcelWorksheetNameRules.IsValid(name))
                return Failure(
                    "Invalid Excel worksheet name: " + (name ?? string.Empty),
                    "excel_sheet_name_invalid", false);
            var existing = names.FirstOrDefault(item =>
                string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !string.Equals(
                name, currentName, StringComparison.OrdinalIgnoreCase))
                return Failure(
                    "Worksheet already exists: " + name,
                    "excel_sheet_already_exists", false);
            return null;
        }

        private static string ResolveSource(
            ExcelSheetCollectionSnapshot snapshot, string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested))
                return snapshot.SheetNames.FirstOrDefault(item =>
                    string.Equals(item, requested, StringComparison.OrdinalIgnoreCase));
            var active = snapshot.SheetNames.FirstOrDefault(item =>
                string.Equals(item, snapshot.ActiveSheet, StringComparison.OrdinalIgnoreCase));
            return active ?? snapshot.SheetNames.FirstOrDefault();
        }

        private static bool SameSequence(
            IReadOnlyList<string> expected, IReadOnlyList<string> actual)
        {
            if (expected == null || actual == null || expected.Count != actual.Count)
                return false;
            for (var index = 0; index < expected.Count; index++)
                if (!string.Equals(expected[index], actual[index], StringComparison.Ordinal))
                    return false;
            return true;
        }

        private static bool SameNamesIgnoringOrder(
            IReadOnlyList<string> expected, IReadOnlyList<string> actual)
        {
            if (expected == null || actual == null || expected.Count != actual.Count)
                return false;
            var remaining = new List<string>(actual);
            foreach (var name in expected)
            {
                var index = remaining.FindIndex(item =>
                    string.Equals(item, name, StringComparison.Ordinal));
                if (index < 0) return false;
                remaining.RemoveAt(index);
            }
            return remaining.Count == 0;
        }

        private static ExcelSheetOutcome Failure(
            string message, string code, bool retryable, string detailsJson = null)
        {
            return ExcelSheetOutcome.Error(message,
                ErrorData(code, retryable, detailsJson).ToString(Formatting.None),
                code, retryable);
        }

        private static ExcelSheetOutcome Unknown(
            string message, string code, string detailsJson = null)
        {
            return ExcelSheetOutcome.Unknown(message,
                ErrorData(code, false, detailsJson).ToString(Formatting.None), code);
        }

        private static JObject ErrorData(
            string code, bool retryable, string detailsJson)
        {
            var data = new JObject
            {
                ["code"] = code,
                ["retryable"] = retryable
            };
            if (!string.IsNullOrWhiteSpace(detailsJson))
            {
                try { data["details"] = JToken.Parse(detailsJson); }
                catch (JsonException) { data["details"] = detailsJson; }
            }
            return data;
        }
    }
}
