using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Runtime
{
    public sealed class ToolHandlerRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, RegisteredTool> _tools = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        private readonly Dictionary<string, IToolHandler> _handlers = new Dictionary<string, IToolHandler>(StringComparer.Ordinal);

        public void Register(ToolRegistration registration, IToolHandler handler)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            JObject schema;
            string error;
            // Reuse the canonical schema validator without coupling runtime
            // registration to catalog persistence or package behavior.
            if (!ToolSchemaSupport.TryParse(new ToolCatalogEntry
                { Id = registration.Descriptor.Id, ArgumentSchemaJson = registration.Descriptor.ParametersJson }, out schema, out error))
                throw new ArgumentException("Invalid registered tool schema: " + error, nameof(registration));

            lock (_gate)
            {
                if (_tools.ContainsKey(registration.Descriptor.Id))
                    throw new InvalidOperationException("Duplicate exact tool id: " + registration.Descriptor.Id);
                IToolHandler existing;
                if (_handlers.TryGetValue(registration.Binding.HandlerId, out existing) && !ReferenceEquals(existing, handler))
                    throw new InvalidOperationException("Handler identity is already bound: " + registration.Binding.HandlerId);
                _handlers[registration.Binding.HandlerId] = handler;
                _tools.Add(registration.Descriptor.Id, new RegisteredTool(registration, handler, schema));
            }
        }

        public ToolRegistration Find(string exactToolId)
        {
            var found = Lookup(exactToolId);
            return found == null ? null : found.Registration;
        }

        internal RegisteredTool Lookup(string exactToolId)
        {
            if (exactToolId == null) return null;
            lock (_gate)
            {
                RegisteredTool value;
                return _tools.TryGetValue(exactToolId, out value) ? value : null;
            }
        }

        internal sealed class RegisteredTool
        {
            private readonly JObject _schema;
            internal ToolRegistration Registration { get; private set; }
            internal IToolHandler Handler { get; private set; }

            internal RegisteredTool(ToolRegistration registration, IToolHandler handler, JObject schema)
            {
                Registration = registration;
                Handler = handler;
                _schema = (JObject)schema.DeepClone();
            }

            internal JObject Schema() { return (JObject)_schema.DeepClone(); }
            internal ToolPolicySnapshot Policy()
            {
                return new ToolPolicySnapshot(Registration.Descriptor.Id, Registration.Revision, Registration.Policy);
            }
        }
    }
}
