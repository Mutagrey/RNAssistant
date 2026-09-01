using System;
using System.Reflection;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.OfficeHosts.Vba
{
    internal sealed class VbaInteropBackend : IVbaHostBackend
    {
        private readonly IOfficeDocumentSession _session;
        private readonly object _application;

        internal VbaInteropBackend(
            IOfficeDocumentSession session,
            object application)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _application = application ??
                throw new ArgumentNullException(nameof(application));
        }

        public string HostName { get { return _session.Host; } }
        public string DocumentKey { get { return _session.StableDocumentId; } }
        public string RuntimeDocumentKey { get { return _session.RuntimeDocumentId; } }
        public string DocumentTitle
        {
            get
            {
                var document = RequireDocument();
                return Convert.ToString(document.GetType().InvokeMember(
                    "Name",
                    BindingFlags.GetProperty,
                    null,
                    document,
                    null));
            }
        }

        public VbaProjectSnapshot ListProjectComponents()
        {
            try
            {
                return VbaProjectSupport.ListProjectComponents(
                    RequireDocument(), DocumentTitle);
            }
            catch (VbaBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw AccessFailure(ex);
            }
        }

        public VbaModuleSnapshot ReadModule(VbaReadModuleRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                return VbaProjectSupport.ReadModule(
                    RequireDocument(),
                    request.ModuleName,
                    request.MaxChars);
            }
            catch (VbaBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw AccessFailure(ex);
            }
        }

        public VbaBackendActionResult ReplaceModule(
            VbaReplaceModuleRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return RunAction(delegate
            {
                return VbaProjectSupport.ReplaceModule(
                    RequireDocument(),
                    request.ModuleName,
                    request.Code,
                    request.CreateIfMissing,
                    request.ExpectedCodeSha256);
            });
        }

        public VbaBackendActionResult CreateModule(VbaCreateModuleRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return RunAction(delegate
            {
                return VbaProjectSupport.CreateModule(
                    RequireDocument(),
                    request.ModuleName,
                    request.ComponentType,
                    request.Code);
            });
        }

        public VbaBackendActionResult RenameModule(VbaRenameModuleRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return RunAction(delegate
            {
                return VbaProjectSupport.RenameModule(
                    RequireDocument(),
                    request.ModuleName,
                    request.NewModuleName,
                    request.ExpectedCodeSha256,
                    request.ExpectedComponentType);
            });
        }

        public VbaBackendActionResult DeleteModule(VbaDeleteModuleRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return RunAction(delegate
            {
                return VbaProjectSupport.DeleteModule(
                    RequireDocument(),
                    request.ModuleName,
                    request.ExpectedCodeSha256);
            });
        }

        public VbaBackendActionResult InstallPackage(
            VbaInstallPackageRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return RunAction(delegate
            {
                return VbaProjectSupport.InstallPackage(
                    RequireDocument(), request.Components, request.Marker);
            });
        }

        public VbaBackendActionResult RemovePackage(
            VbaRemovePackageRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return RunAction(delegate
            {
                return VbaProjectSupport.RemovePackage(
                    RequireDocument(),
                    request.ExpectedComparableHashes,
                    request.ExpectedMarker);
            });
        }

        public VbaBackendActionResult RunMacro(VbaRunMacroRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return RunAction(delegate
            {
                if (string.IsNullOrWhiteSpace(request.MacroName))
                    return VbaBackendActionResult.Error(
                        "No macroName provided.",
                        null,
                        "vba_macro_name_required",
                        true);
                var output = VbaProjectSupport.RunStringFunction(
                    _application,
                    request.MacroName,
                    request.Arguments);
                return VbaBackendActionResult.Ok(
                    "Macro ran: " + request.MacroName,
                    new { output = output });
            }, "office_tool_error", true);
        }

        private object RequireDocument()
        {
            if (!_session.IsAlive)
                throw new InvalidOperationException(
                    "The bound Office document is not open.");
            return _session.BoundDocumentObject;
        }

        private static VbaBackendActionResult RunAction(
            Func<VbaBackendActionResult> action,
            string errorCode = "vba_access_error",
            bool retryable = false)
        {
            try
            {
                return action() ?? VbaBackendActionResult.Unknown(
                    "VBA backend returned no result.",
                    null,
                    "vba_backend_missing_result");
            }
            catch (VbaBackendException ex)
            {
                return VbaBackendActionResult.Error(
                    ex.Message, ex.Details, ex.ErrorCode, ex.Retryable);
            }
            catch (Exception ex)
            {
                return VbaBackendActionResult.Error(
                    ex.Message, null, errorCode, retryable);
            }
        }

        private static VbaBackendException AccessFailure(Exception exception)
        {
            return new VbaBackendException(
                exception == null ? "VBA access failed." : exception.Message,
                "vba_access_error",
                false,
                null,
                exception);
        }
    }
}
