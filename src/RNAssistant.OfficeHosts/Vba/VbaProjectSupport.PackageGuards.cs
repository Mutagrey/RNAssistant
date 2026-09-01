using System;
using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Vba;
using RNAssistant.Office.Vba;

namespace RNAssistant.OfficeHosts.Vba
{
    public static partial class VbaProjectSupport
    {
        private static VbaBackendActionResult ValidatePackageInstallGuards(
            dynamic vbProject,
            System.Collections.Generic.IReadOnlyList<VbaInstallPackageComponent> payload)
        {
            var items = (payload ?? new VbaInstallPackageComponent[0])
                .Where(item => item != null)
                .ToList();
            var hasGuard = items.Any(item => item.ExpectedBeforeExists.HasValue);
            if (!hasGuard || items.Any(item =>
                !item.ExpectedBeforeExists.HasValue ||
                !item.ExpectedBeforeOwnershipMarkerPresent.HasValue))
            {
                return VbaBackendActionResult.Error(
                    "VBA package install guard is incomplete.",
                    null,
                    "vba_package_guard_invalid",
                    false);
            }

            foreach (var item in items)
            {
                var name = item.Name;
                var expectedExists = item.ExpectedBeforeExists.Value;
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

                var expectedType = item.ExpectedBeforeComponentType;
                var expectedHash = item.ExpectedBeforeComparableCodeSha256;
                if (string.IsNullOrWhiteSpace(expectedType) || string.IsNullOrWhiteSpace(expectedHash))
                {
                    return VbaBackendActionResult.Error(
                        "VBA package install guard is incomplete for component: " + name + ".",
                        null,
                        "vba_package_guard_invalid",
                        false);
                }
                var actualType = ComponentTypeName((int)component.Type);
                var actualCode = ReadComponentCode(component);
                var actualHash = VbaTextCanonicalizer.PackageComparableCodeSha256(actualCode);
                var expectedMarkerPresent =
                    item.ExpectedBeforeOwnershipMarkerPresent.Value;
                var expectedMarker = item.ExpectedBeforeOwnershipMarker;
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

        private static VbaBackendActionResult StalePackageInstall(
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
            return VbaBackendActionResult.Error(
                "VBA package component changed after preparation and was not overwritten: " + componentName + ".",
                new
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
                },
                "stale_vba_package",
                false);
        }
    }
}
