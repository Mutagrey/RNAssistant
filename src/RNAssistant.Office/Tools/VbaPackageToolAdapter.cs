using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal static class VbaPackageToolAdapter
    {
        public static VbaPackageSourceDefinition ToSource(ToolDefinition tool)
        {
            if (tool == null) return null;
            return new VbaPackageSourceDefinition
            {
                Id = tool.Id,
                Host = tool.Host,
                Code = tool.Code,
                StoragePath = tool.StoragePath,
                Readme = tool.Readme,
                Components = (tool.Components ?? new List<VbaToolComponent>())
                    .Where(component => component != null)
                    .Select(component => new VbaPackageSourceComponent
                    {
                        Name = component.Name,
                        Type = component.Type,
                        FileName = component.FileName,
                        Code = component.Code
                    })
                    .ToList()
            };
        }
    }

    internal sealed class VbaPackageBackendAdapter : IVbaPackageBackend
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly Func<string, string> _backendToolId;

        public VbaPackageBackendAdapter(
            IOfficeApplicationAdapter adapter,
            Func<string, string> backendToolId)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _backendToolId = backendToolId ?? throw new ArgumentNullException(nameof(backendToolId));
        }

        public VbaMutationActionResult InstallPackage(VbaPackageInstallActionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var components = (request.Components ?? new VbaPackageComponent[0]).ToList();
            var expected = (request.ExpectedBefore ?? new VbaPackageExpectedComponentState[0])
                .Where(component => component != null && !string.IsNullOrWhiteSpace(component.Name))
                .ToDictionary(component => component.Name, StringComparer.OrdinalIgnoreCase);
            if (components.Count == 0 || expected.Count != components.Count ||
                components.Any(component => component == null || !expected.ContainsKey(component.Name)))
            {
                return VbaMutationActionResult.Error(
                    "VBA package install requires one prepared before-state guard per component.",
                    null,
                    "vba_package_guard_invalid",
                    false);
            }
            var command = new ToolCommand { ToolId = _backendToolId("vba_install_package_internal") };
            command.Arguments["componentsJson"] = JsonConvert.SerializeObject(
                components.Select(component =>
                {
                    VbaPackageExpectedComponentState before;
                    expected.TryGetValue(component.Name, out before);
                    return new
                    {
                        name = component.Name,
                        type = component.Type,
                        code = component.Code,
                        expectedBeforeExists = before == null ? (bool?)null : before.Exists,
                        expectedBeforeType = before == null ? null : before.ComponentType,
                        expectedBeforeComparableCodeSha256 = before == null ? null : before.ComparableCodeSha256,
                        expectedBeforeOwnershipMarkerPresent = before == null ? (bool?)null : before.OwnershipMarkerPresent,
                        expectedBeforeOwnershipMarker = before == null ? null : before.OwnershipMarker
                    };
                }).ToArray());
            command.Arguments["marker"] = request.Marker;
            return VbaMutationToolResultMapper.FromBackend(
                _adapter.ExecuteTool(command),
                "VBA package installation returned no result.",
                "vba_package_install_failed");
        }

        public VbaMutationActionResult RemovePackage(VbaPackageRemoveActionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var expected = new JObject();
            foreach (var component in request.ExpectedComparableHashes ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            {
                expected[component.Key] = component.Value;
            }
            var command = new ToolCommand { ToolId = _backendToolId("vba_remove_package_internal") };
            command.Arguments["expectedComponentsJson"] = expected.ToString(Formatting.None);
            command.Arguments["expectedMarker"] = request.ExpectedMarker;
            return VbaMutationToolResultMapper.FromBackend(
                _adapter.ExecuteTool(command),
                "VBA package removal returned no result.",
                "vba_package_remove_failed");
        }

        public VbaMutationActionResult RunMacro(VbaPackageRunActionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var command = new ToolCommand { ToolId = _backendToolId("run_macro") };
            command.Arguments["macroName"] = request.MacroName;
            command.Arguments["argumentsJson"] = (request.Arguments ?? new JArray()).ToString(Formatting.None);
            return VbaMutationToolResultMapper.FromBackend(
                _adapter.ExecuteTool(command),
                "VBA function returned no result.",
                "vba_function_failed");
        }
    }
}
