using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Vba
{
    internal sealed class VbaVerifier
    {
        private readonly VbaReader _reader;
        private readonly Action<ChatSession, string, string> _recordObservation;
        private readonly Action<ChatSession, string> _removeObservation;

        public VbaVerifier(
            VbaReader reader,
            Action<ChatSession, string, string> recordObservation,
            Action<ChatSession, string> removeObservation)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _recordObservation = recordObservation;
            _removeObservation = removeObservation;
        }

        public ToolResult VerifyModuleWrite(
            string moduleName,
            string expectedCode,
            string successMessage,
            string successDataJson,
            string errorPrefix,
            string expectedComponentType = null,
            ChatSession session = null)
        {
            var expectedHash = VbaTextCanonicalizer.LiveCodeSha256(expectedCode);
            var expectedComparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(expectedCode);
            var expectedLineCount = VbaTextCanonicalizer.LiveCodeLineCount(expectedCode);
            VbaModuleState actual;
            ToolResult readError;
            if (!_reader.TryReadModule(moduleName, 1000000, out actual, out readError))
            {
                return ToolResult.PartialFailure(
                    "VBA write completed but final state could not be read back: " +
                    (readError == null ? moduleName : readError.Message),
                    VerificationData(moduleName, expectedHash, null, successDataJson, expectedComponentType, null, expectedLineCount, null),
                    (errorPrefix ?? "vba_write") + "_verify_failed");
            }

            var actualHash = VbaTextCanonicalizer.LiveCodeSha256(actual.Code);
            var actualComparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(actual.Code);
            var codeMatches = string.Equals(expectedComparableHash, actualComparableHash, StringComparison.OrdinalIgnoreCase);
            var componentTypeMatches = string.IsNullOrWhiteSpace(expectedComponentType) ||
                string.Equals(expectedComponentType, actual.ComponentType, StringComparison.OrdinalIgnoreCase);
            if (!codeMatches || !componentTypeMatches)
            {
                return ToolResult.PartialFailure(
                    "VBA write verification failed for " + moduleName +
                    ": final code or component type differs from the requested state.",
                    VerificationData(moduleName, expectedHash, actualHash, successDataJson, expectedComponentType, actual.ComponentType, expectedLineCount, actual.LineCount),
                    (errorPrefix ?? "vba_write") + "_verify_mismatch");
            }

            if (_recordObservation != null) _recordObservation(session, moduleName, actualHash);
            return ToolResult.Ok(successMessage, SuccessfulVerificationData(
                moduleName,
                expectedHash,
                actualHash,
                successDataJson,
                actual.ComponentType,
                actual.LineCount));
        }

        public VbaMutationAssessment InspectMutation(VbaMutationPreparation prepared)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            VbaModuleState actual;
            ToolResult readError;
            bool readSucceeded;
            try
            {
                readSucceeded = _reader.TryReadModule(prepared.ModuleName, 1000000, out actual, out readError);
            }
            catch (Exception ex)
            {
                return new VbaMutationAssessment
                {
                    Status = VbaMutationStatuses.Unknown,
                    ActualExists = null,
                    ErrorCode = "vba_mutation_read_exception",
                    Message = "Live module inspection threw an exception. " + ex.Message
                };
            }

            if (readSucceeded)
            {
                var rawHash = VbaTextCanonicalizer.LiveCodeSha256(actual.Code);
                var comparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(actual.Code);
                if (prepared.IntendedAfterExists && MatchesRecordedState(
                    rawHash,
                    comparableHash,
                    prepared.IntendedAfterCodeSha256,
                    prepared.IntendedAfterComparableCodeSha256))
                {
                    return new VbaMutationAssessment
                    {
                        Status = VbaMutationStatuses.Committed,
                        ActualExists = true,
                        ActualCodeSha256 = rawHash,
                        ActualComparableCodeSha256 = comparableHash,
                        Message = "Live module matches the recorded intended state."
                    };
                }
                if (prepared.BeforeExists && MatchesRecordedState(
                    rawHash,
                    comparableHash,
                    prepared.BeforeCodeSha256,
                    prepared.BeforeComparableCodeSha256))
                {
                    return new VbaMutationAssessment
                    {
                        Status = VbaMutationStatuses.NotApplied,
                        ActualExists = true,
                        ActualCodeSha256 = rawHash,
                        ActualComparableCodeSha256 = comparableHash,
                        Message = "Live module matches the recorded before state."
                    };
                }
                return new VbaMutationAssessment
                {
                    Status = VbaMutationStatuses.Unknown,
                    ActualExists = true,
                    ActualCodeSha256 = rawHash,
                    ActualComparableCodeSha256 = comparableHash,
                    ErrorCode = "vba_mutation_diverged",
                    Message = "Live module matches neither the recorded before nor intended state."
                };
            }

            if (VbaReader.IsModuleNotFound(readError))
            {
                if (!prepared.IntendedAfterExists)
                {
                    return new VbaMutationAssessment
                    {
                        Status = VbaMutationStatuses.Committed,
                        ActualExists = false,
                        Message = "Live module absence matches the recorded intended state."
                    };
                }
                if (!prepared.BeforeExists)
                {
                    return new VbaMutationAssessment
                    {
                        Status = VbaMutationStatuses.NotApplied,
                        ActualExists = false,
                        Message = "Live module absence matches the recorded before state."
                    };
                }
                return new VbaMutationAssessment
                {
                    Status = VbaMutationStatuses.Unknown,
                    ActualExists = false,
                    ErrorCode = "vba_mutation_diverged",
                    Message = "Live module is absent but neither recorded state expected absence."
                };
            }

            return new VbaMutationAssessment
            {
                Status = VbaMutationStatuses.Unknown,
                ActualExists = null,
                ErrorCode = readError == null ? "vba_mutation_read_failed" : readError.ErrorCode,
                Message = "Live module could not be inspected. " + (readError == null ? string.Empty : readError.Message)
            };
        }

        public ToolResult VerifyModuleDeleted(
            string moduleName,
            string successDataJson,
            ChatSession session)
        {
            VbaModuleState remaining;
            ToolResult readError;
            if (_reader.TryReadModule(moduleName, 1000000, out remaining, out readError))
            {
                return ToolResult.PartialFailure(
                    "VBA delete returned success but the module is still present: " + moduleName + ".",
                    VerificationData(
                        moduleName,
                        null,
                        VbaTextCanonicalizer.LiveCodeSha256(remaining.Code),
                        successDataJson,
                        null,
                        null,
                        null,
                        null),
                    "vba_delete_verify_failed");
            }
            if (!VbaReader.IsModuleNotFound(readError))
            {
                return ToolResult.PartialFailure(
                    "VBA module deletion completed but could not be verified: " +
                    (readError == null ? moduleName : readError.Message),
                    VerificationData(moduleName, null, null, successDataJson, null, null, null, null),
                    "vba_delete_verify_failed");
            }
            if (_removeObservation != null) _removeObservation(session, moduleName);
            return ToolResult.Ok(
                "VBA module deleted: " + moduleName,
                successDataJson ?? JsonConvert.SerializeObject(new { moduleName = moduleName }));
        }

        public static VbaMutationAssessment CommittedAssessment(
            VbaMutationPreparation prepared,
            ToolResult result)
        {
            var actualHash = prepared.IntendedAfterCodeSha256;
            if (result != null && !string.IsNullOrWhiteSpace(result.DataJson))
            {
                try
                {
                    actualHash = (string)JObject.Parse(result.DataJson)["codeSha256"] ?? actualHash;
                }
                catch (JsonException)
                {
                }
            }
            return new VbaMutationAssessment
            {
                Status = VbaMutationStatuses.Committed,
                ActualExists = prepared.IntendedAfterExists,
                ActualCodeSha256 = prepared.IntendedAfterExists ? actualHash : null,
                ActualComparableCodeSha256 = prepared.IntendedAfterExists
                    ? prepared.IntendedAfterComparableCodeSha256
                    : null,
                Message = "The VBA operation completed and its read-back verification succeeded."
            };
        }

        internal static bool MatchesRecordedState(
            string actualRaw,
            string actualComparable,
            string expectedRaw,
            string expectedComparable)
        {
            if (!string.IsNullOrWhiteSpace(expectedComparable))
            {
                return string.Equals(actualComparable, expectedComparable, StringComparison.OrdinalIgnoreCase);
            }
            return !string.IsNullOrWhiteSpace(expectedRaw) &&
                string.Equals(actualRaw, expectedRaw, StringComparison.OrdinalIgnoreCase);
        }

        private static string SuccessfulVerificationData(
            string moduleName,
            string requestedHash,
            string actualHash,
            string operationDataJson,
            string actualComponentType,
            int actualLineCount)
        {
            JObject data;
            try { data = string.IsNullOrWhiteSpace(operationDataJson) ? new JObject() : JObject.Parse(operationDataJson); }
            catch (JsonException) { data = new JObject { ["operationData"] = operationDataJson ?? string.Empty }; }
            data["moduleName"] = moduleName ?? string.Empty;
            data["codeSha256"] = actualHash;
            data["lineCount"] = actualLineCount;
            data["componentType"] = actualComponentType ?? string.Empty;
            data["vbeNormalized"] = !string.Equals(requestedHash, actualHash, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(requestedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                data["requestedCodeSha256"] = requestedHash;
            }
            return data.ToString(Formatting.None);
        }

        private static string VerificationData(
            string moduleName,
            string expectedHash,
            string actualHash,
            string operationDataJson,
            string expectedComponentType,
            string actualComponentType,
            int? expectedLineCount,
            int? actualLineCount)
        {
            JToken operationData = null;
            if (!string.IsNullOrWhiteSpace(operationDataJson))
            {
                try { operationData = JToken.Parse(operationDataJson); }
                catch (JsonException) { operationData = new JValue(operationDataJson); }
            }
            return new JObject
            {
                ["moduleName"] = moduleName ?? string.Empty,
                ["expectedCodeSha256"] = string.IsNullOrWhiteSpace(expectedHash) ? null : expectedHash,
                ["actualCodeSha256"] = string.IsNullOrWhiteSpace(actualHash) ? null : actualHash,
                ["expectedComponentType"] = string.IsNullOrWhiteSpace(expectedComponentType) ? null : expectedComponentType,
                ["actualComponentType"] = string.IsNullOrWhiteSpace(actualComponentType) ? null : actualComponentType,
                ["expectedLineCount"] = expectedLineCount,
                ["actualLineCount"] = actualLineCount,
                ["operationData"] = operationData
            }.ToString(Formatting.None);
        }
    }

    internal sealed class VbaMutationAssessment
    {
        public string Status { get; set; }
        public bool? ActualExists { get; set; }
        public string ActualCodeSha256 { get; set; }
        public string ActualComparableCodeSha256 { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
    }
}
