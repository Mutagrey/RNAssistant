using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office
{
    public static partial class VbaProjectSupport
    {
        private static ToolResult ValidatePackageInstallGuards(dynamic vbProject, JArray payload)
        {
            var items = (payload ?? new JArray()).OfType<JObject>().ToList();
            var hasGuard = items.Any(item => item["expectedBeforeExists"] != null);
            if (!hasGuard || items.Any(item => item["expectedBeforeExists"] == null ||
                item["expectedBeforeOwnershipMarkerPresent"] == null))
            {
                return ToolResult.Fail(
                    "VBA package install guard is incomplete.",
                    null,
                    "vba_package_guard_invalid",
                    false);
            }

            foreach (var item in items)
            {
                var name = (string)item["name"];
                var expectedExists = item.Value<bool>("expectedBeforeExists");
                dynamic component = FindComponent(vbProject, name);
                var actualExists = component != null;
                if (actualExists != expectedExists)
                {
                    return StalePackageInstall(
                        name,
                        expectedExists,
                        actualExists,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null);
                }
                if (!expectedExists) continue;

                var expectedType = (string)item["expectedBeforeType"];
                var expectedHash = (string)item["expectedBeforeComparableCodeSha256"];
                if (string.IsNullOrWhiteSpace(expectedType) || string.IsNullOrWhiteSpace(expectedHash))
                {
                    return ToolResult.Fail(
                        "VBA package install guard is incomplete for component: " + name + ".",
                        null,
                        "vba_package_guard_invalid",
                        false);
                }
                var actualType = ComponentTypeName((int)component.Type);
                var actualCode = ReadComponentCode(component);
                var actualHash = VbaTextCanonicalizer.PackageComparableCodeSha256(actualCode);
                var expectedMarkerPresent = item.Value<bool>("expectedBeforeOwnershipMarkerPresent");
                var expectedMarker = (string)item["expectedBeforeOwnershipMarker"];
                var actualMarker = VbaPackageOwnershipMarker.Parse(actualCode);
                var markerMatches = actualMarker.Found == expectedMarkerPresent &&
                    (!expectedMarkerPresent || string.Equals(
                        actualMarker.Raw,
                        expectedMarker,
                        StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(actualType, expectedType, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                    !markerMatches)
                {
                    return StalePackageInstall(
                        name,
                        true,
                        true,
                        expectedType,
                        actualType,
                        expectedHash,
                        actualHash,
                        expectedMarkerPresent,
                        actualMarker.Found,
                        expectedMarker,
                        actualMarker.Raw);
                }
            }
            return null;
        }

        private static ToolResult StalePackageInstall(
            string componentName,
            bool expectedExists,
            bool actualExists,
            string expectedType,
            string actualType,
            string expectedComparableCodeSha256,
            string actualComparableCodeSha256,
            bool? expectedOwnershipMarkerPresent,
            bool? actualOwnershipMarkerPresent,
            string expectedOwnershipMarker,
            string actualOwnershipMarker)
        {
            return ToolResult.Fail(
                "VBA package component changed after preparation and was not overwritten: " + componentName + ".",
                JsonConvert.SerializeObject(new
                {
                    component = componentName,
                    expectedExists = expectedExists,
                    actualExists = actualExists,
                    expectedType = expectedType,
                    actualType = actualType,
                    expectedComparableCodeSha256 = expectedComparableCodeSha256,
                    actualComparableCodeSha256 = actualComparableCodeSha256,
                    expectedOwnershipMarkerPresent = expectedOwnershipMarkerPresent,
                    actualOwnershipMarkerPresent = actualOwnershipMarkerPresent,
                    expectedOwnershipMarker = expectedOwnershipMarker,
                    actualOwnershipMarker = actualOwnershipMarker
                }),
                "stale_vba_package",
                false);
        }
    }
}
