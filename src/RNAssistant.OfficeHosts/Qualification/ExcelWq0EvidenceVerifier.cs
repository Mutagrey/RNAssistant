using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Qualification;

namespace RNAssistant.OfficeHosts.Qualification
{
    public static class ExcelWq0EvidenceVerifier
    {
        public static QualificationVerificationResult Verify(
            QualificationEvidenceSnapshot evidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var baseline = EvidenceObject(evidence, "baseline");
                var switched = EvidenceObject(evidence, "after-switch");
                var savedAs = EvidenceObject(evidence, "after-save-as");
                var secondWindow = EvidenceObject(evidence, "second-window");
                var rotated = EvidenceObject(evidence, "rotate-client");
                var rebound = EvidenceObject(evidence, "rebind-reopened");
                var other = EvidenceObject(evidence, "other-process");
                var baselineKey = ObservationKey((JObject)baseline["inProcess"]);
                var actual = new JObject
                {
                    ["baselineClientsAgree"] = ObservationSetAgrees(baseline),
                    ["activeSwitchKeepsIdentity"] = ObservationSetAgrees(switched) &&
                        ObservationKey((JObject)switched["inProcess"]) == baselineKey,
                    ["saveAsKeepsIdentity"] = ObservationSetAgrees(savedAs) &&
                        ObservationKey((JObject)savedAs["inProcess"]) == baselineKey &&
                        !string.Equals((string)baseline["inProcess"]["fullName"],
                            (string)savedAs["inProcess"]["fullName"], StringComparison.OrdinalIgnoreCase),
                    ["secondWindowKeepsIdentity"] = ObservationSetAgrees(secondWindow) &&
                        ObservationKey((JObject)secondWindow["inProcess"]) == baselineKey &&
                        (int?)secondWindow["inProcess"]["windowCount"] >= 2,
                    ["detachAttachKeepsIdentity"] = ObservationSetAgrees(rotated) &&
                        ObservationKey((JObject)rotated["inProcess"]) == baselineKey &&
                        Labels(rotated).Contains("client-B") && Labels(rotated).Contains("client-C"),
                    ["closeReopenChangesLifetime"] = RebindIsNewLifetime(rebound, baselineKey),
                    ["sameNameOtherProcessIsDistinct"] = OtherProcessIsDistinct(other,
                        (string)savedAs["inProcess"]["name"], baselineKey),
                    ["readsDoNotDirtyFixture"] = EvidenceReadsPreserveSaved(
                        new[] { baseline, switched, savedAs, secondWindow, rotated })
                };
                var expected = new JObject();
                foreach (var property in actual.Properties()) expected[property.Name] = true;
                var expectedJson = expected.ToString(Formatting.None);
                var actualJson = actual.ToString(Formatting.None);
                return JToken.DeepEquals(expected, actual)
                    ? QualificationVerificationResult.Passed(expectedJson, actualJson,
                        "Excel WQ0 identity evidence matched across independent clients.", "verified_no_change")
                    : QualificationVerificationResult.Failed("excel_identity_mismatch",
                        "Excel WQ0 identity matrix did not match.", expectedJson, actualJson);
            }
            catch (Exception ex)
            {
                return QualificationVerificationResult.Blocked(
                    "excel_identity_evidence_invalid", Bound(ex.Message, 1000));
            }
        }

        private static JObject EvidenceObject(QualificationEvidenceSnapshot evidence, string stepId)
        {
            var step = evidence == null ? null : evidence.Find(stepId);
            if (step == null || step.Outcome != QualificationStepOutcome.Passed ||
                string.IsNullOrWhiteSpace(step.ActualJson))
                throw new InvalidDataException("Required WQ0 evidence is missing: " + stepId + ".");
            return JObject.Parse(step.ActualJson);
        }

        private static bool ObservationSetAgrees(JObject evidence)
        {
            var expected = ObservationKey(evidence["inProcess"] as JObject);
            var clients = evidence["clients"] as JArray;
            return expected != null && clients != null && clients.Count >= 2 && clients.OfType<JObject>().All(item =>
                string.Equals((string)item["status"], "observed", StringComparison.Ordinal) &&
                ObservationKey(item) == expected);
        }

        private static HashSet<string> Labels(JObject evidence)
        {
            return new HashSet<string>((evidence["clients"] as JArray ?? new JArray())
                .OfType<JObject>().Select(item => (string)item["label"]), StringComparer.Ordinal);
        }

        private static bool RebindIsNewLifetime(JObject evidence, string baselineKey)
        {
            var oldClients = evidence["oldClients"] as JArray;
            var reopened = evidence["newObservation"] as JObject;
            return oldClients != null && oldClients.Count >= 2 && oldClients.OfType<JObject>().All(item =>
                    string.Equals((string)item["status"], "closed", StringComparison.Ordinal)) &&
                reopened != null && ObservationSetAgrees(reopened) &&
                ObservationKey(reopened["inProcess"] as JObject) != baselineKey;
        }

        private static bool OtherProcessIsDistinct(JObject evidence, string expectedName, string baselineKey)
        {
            var foreign = evidence["foreign"] as JObject;
            return foreign != null && string.Equals((string)foreign["status"], "observed", StringComparison.Ordinal) &&
                string.Equals((string)foreign["name"], expectedName, StringComparison.OrdinalIgnoreCase) &&
                ObservationKey(foreign) != baselineKey;
        }

        private static bool EvidenceReadsPreserveSaved(IEnumerable<JObject> items)
        {
            foreach (var item in items)
            {
                var observations = new List<JObject> { item["inProcess"] as JObject };
                observations.AddRange((item["clients"] as JArray ?? new JArray()).OfType<JObject>());
                if (observations.Any(value => value == null ||
                    (bool?)value["savedBeforeRead"] != (bool?)value["savedAfterRead"])) return false;
            }
            return true;
        }

        private static string ObservationKey(JObject observation)
        {
            if (observation == null || (int?)observation["excelProcessId"] <= 0 ||
                string.IsNullOrWhiteSpace((string)observation["excelProcessStartUtc"]) ||
                string.IsNullOrWhiteSpace((string)observation["oxid"]) ||
                string.IsNullOrWhiteSpace((string)observation["oid"])) return null;
            return (int)observation["excelProcessId"] + "|" +
                (string)observation["excelProcessStartUtc"] + "|" +
                (string)observation["oxid"] + "|" + (string)observation["oid"];
        }

        private static string Bound(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximum) return value;
            return value.Substring(0, maximum);
        }
    }
}
