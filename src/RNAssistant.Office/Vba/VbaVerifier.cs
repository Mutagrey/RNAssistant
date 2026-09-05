using System;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Vba
{
    internal sealed class VbaVerifier
    {
        private readonly IVbaMutationReader _reader;

        public VbaVerifier(IVbaMutationReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public VbaMutationActionResult VerifyModuleWrite(
            string moduleName,
            string expectedCode,
            string successMessage,
            JObject successData,
            string errorPrefix,
            string expectedComponentType = null,
            string sessionId = null)
        {
            var expectedHash = VbaTextCanonicalizer.LiveCodeSha256(expectedCode);
            var expectedComparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(expectedCode);
            var expectedLineCount = VbaTextCanonicalizer.LiveCodeLineCount(expectedCode);
            var read = _reader.ReadModule(moduleName, 1000000);
            if (read == null)
            {
                return VbaMutationActionResult.Unknown(
                    "VBA write completed but final state could not be read back: " + moduleName,
                    VerificationData(
                        moduleName,
                        expectedHash,
                        null,
                        successData,
                        expectedComponentType,
                        null,
                        expectedLineCount,
                        null),
                    (errorPrefix ?? "vba_write") + "_verify_failed");
            }
            if (!read.Success)
            {
                return VbaMutationActionResult.Unknown(
                    "VBA write completed but final state could not be read back: " +
                    (string.IsNullOrWhiteSpace(read.Message) ? moduleName : read.Message),
                    VerificationData(
                        moduleName,
                        expectedHash,
                        null,
                        successData,
                        expectedComponentType,
                        null,
                        expectedLineCount,
                        null),
                    (errorPrefix ?? "vba_write") + "_verify_failed");
            }
            var actual = read.Module;

            var actualHash = VbaTextCanonicalizer.LiveCodeSha256(actual.Code);
            var actualComparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(actual.Code);
            var codeMatches = string.Equals(
                expectedComparableHash,
                actualComparableHash,
                StringComparison.OrdinalIgnoreCase);
            var componentTypeMatches = string.IsNullOrWhiteSpace(expectedComponentType) ||
                string.Equals(
                    expectedComponentType,
                    actual.ComponentType,
                    StringComparison.OrdinalIgnoreCase);
            if (!codeMatches || !componentTypeMatches)
            {
                return VbaMutationActionResult.Unknown(
                    "VBA write verification failed for " + moduleName +
                    ": final code or component type differs from the requested state.",
                    VerificationData(
                        moduleName,
                        expectedHash,
                        actualHash,
                        successData,
                        expectedComponentType,
                        actual.ComponentType,
                        expectedLineCount,
                        actual.LineCount),
                    (errorPrefix ?? "vba_write") + "_verify_mismatch");
            }

            return VbaMutationActionResult.Verified(
                successMessage,
                SuccessfulVerificationData(
                    moduleName,
                    expectedHash,
                    actualHash,
                    successData,
                    actual.ComponentType,
                    actual.LineCount));
        }

        public VbaMutationAssessment InspectMutation(VbaMutationPreparation prepared)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            VbaMutationReadResult read;
            try
            {
                read = _reader.ReadModule(prepared.ModuleName, 1000000);
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
            if (read == null)
            {
                return new VbaMutationAssessment
                {
                    Status = VbaMutationStatuses.Unknown,
                    ActualExists = null,
                    ErrorCode = "vba_mutation_read_failed",
                    Message = "Live module inspection returned no typed result."
                };
            }

            if (read.Success)
            {
                var actual = read.Module;
                var rawHash = VbaTextCanonicalizer.LiveCodeSha256(actual.Code);
                var comparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(actual.Code);
                var componentTypeMatches = string.IsNullOrWhiteSpace(prepared.ComponentType) ||
                    string.Equals(
                        prepared.ComponentType,
                        actual.ComponentType,
                        StringComparison.OrdinalIgnoreCase);
                if (prepared.IntendedAfterExists && MatchesRecordedState(
                    rawHash,
                    comparableHash,
                    prepared.IntendedAfterCodeSha256,
                    prepared.IntendedAfterComparableCodeSha256) &&
                    componentTypeMatches)
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
                    prepared.BeforeComparableCodeSha256) &&
                    componentTypeMatches)
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
                    ErrorCode = componentTypeMatches
                        ? "vba_mutation_diverged"
                        : "vba_mutation_component_type_diverged",
                    Message = componentTypeMatches
                        ? "Live module matches neither the recorded before nor intended state."
                        : "Live module component type differs from the recorded mutation state."
                };
            }

            if (read.IsNotFound)
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
                ErrorCode = string.IsNullOrWhiteSpace(read.ErrorCode)
                    ? "vba_mutation_read_failed"
                    : read.ErrorCode,
                Message = "Live module could not be inspected. " +
                    read.Message
            };
        }

        public VbaMutationActionResult VerifyModuleDeleted(
            string moduleName,
            JObject successData,
            string sessionId)
        {
            var read = _reader.ReadModule(moduleName, 1000000);
            if (read == null)
            {
                return VbaMutationActionResult.Unknown(
                    "VBA module deletion completed but could not be verified: " + moduleName,
                    VerificationData(moduleName, null, null, successData, null, null, null, null),
                    "vba_delete_verify_failed");
            }
            if (read.Success)
            {
                var remaining = read.Module;
                return VbaMutationActionResult.Unknown(
                    "VBA delete returned success but the module is still present: " + moduleName + ".",
                    VerificationData(
                        moduleName,
                        null,
                        VbaTextCanonicalizer.LiveCodeSha256(remaining.Code),
                        successData,
                        null,
                        null,
                        null,
                        null),
                    "vba_delete_verify_failed");
            }
            if (!read.IsNotFound)
            {
                return VbaMutationActionResult.Unknown(
                    "VBA module deletion completed but could not be verified: " +
                    (string.IsNullOrWhiteSpace(read.Message) ? moduleName : read.Message),
                    VerificationData(moduleName, null, null, successData, null, null, null, null),
                    "vba_delete_verify_failed");
            }
            var data = VbaMutationData.Clone(successData);
            data["moduleName"] = moduleName ?? string.Empty;
            return VbaMutationActionResult.Verified(
                "VBA module deleted: " + moduleName,
                data);
        }

        public static VbaMutationAssessment CommittedAssessment(
            VbaMutationPreparation prepared,
            VbaMutationActionResult result)
        {
            var actualHash = prepared.IntendedAfterCodeSha256;
            var resultData = result == null ? null : result.Data;
            if (resultData != null)
            {
                actualHash = (string)resultData["codeSha256"] ?? actualHash;
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
                return string.Equals(
                    actualComparable,
                    expectedComparable,
                    StringComparison.OrdinalIgnoreCase);
            }
            return !string.IsNullOrWhiteSpace(expectedRaw) &&
                string.Equals(actualRaw, expectedRaw, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject SuccessfulVerificationData(
            string moduleName,
            string requestedHash,
            string actualHash,
            JObject operationData,
            string actualComponentType,
            int actualLineCount)
        {
            var data = VbaMutationData.Clone(operationData);
            data["moduleName"] = moduleName ?? string.Empty;
            data["codeSha256"] = actualHash;
            data["lineCount"] = actualLineCount;
            data["componentType"] = actualComponentType ?? string.Empty;
            data["vbeNormalized"] = !string.Equals(
                requestedHash,
                actualHash,
                StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(requestedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                data["requestedCodeSha256"] = requestedHash;
            }
            return data;
        }

        private static JObject VerificationData(
            string moduleName,
            string expectedHash,
            string actualHash,
            JObject operationData,
            string expectedComponentType,
            string actualComponentType,
            int? expectedLineCount,
            int? actualLineCount)
        {
            return new JObject
            {
                ["moduleName"] = moduleName ?? string.Empty,
                ["expectedCodeSha256"] = string.IsNullOrWhiteSpace(expectedHash) ? null : expectedHash,
                ["actualCodeSha256"] = string.IsNullOrWhiteSpace(actualHash) ? null : actualHash,
                ["expectedComponentType"] = string.IsNullOrWhiteSpace(expectedComponentType)
                    ? null
                    : expectedComponentType,
                ["actualComponentType"] = string.IsNullOrWhiteSpace(actualComponentType)
                    ? null
                    : actualComponentType,
                ["expectedLineCount"] = expectedLineCount,
                ["actualLineCount"] = actualLineCount,
                ["operationData"] = operationData == null ? null : operationData.DeepClone()
            };
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
