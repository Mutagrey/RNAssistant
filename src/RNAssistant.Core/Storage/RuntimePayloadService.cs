using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    // Storage encoding of opaque runtime bodies. No alternate run state or replay authority.
    public static class RuntimePayloadService
    {
        private const int InlineLimit = 8192;
        private static readonly string[] ExecutionNodes = {
            "LastRun.KernelState.InFlightTool", "LastRun.KernelState.Summary.PendingConfirmation" };

        internal static void ExternalizeProjection(JObject root, ChatBlobStore payloads)
        {
            foreach (var path in ExecutionNodes)
            {
                var node = root.SelectToken(path) as JObject;
                if (node == null) continue;
                Externalize(node["Call"] as JObject, "ArgumentsJson", "ArgumentsPayload", payloads);
                Externalize(node, "PreparedStateJson", "PreparedStatePayload", payloads);
            }
        }

        internal static JObject HydrateActiveExecution(JObject root, ChatBlobStore payloads)
        {
            // Replay reduces metadata first; only the selected run's pending/in-flight
            // execution needs its exact opaque arguments for recovery/confirmation.
            if (!ExecutionNodes.Any(path => root.SelectToken(path + ".ArgumentsPayload") != null ||
                root.SelectToken(path + ".Call.ArgumentsPayload") != null || root.SelectToken(path + ".PreparedStatePayload") != null)) return root;
            var copy = (JObject)root.DeepClone();
            foreach (var path in ExecutionNodes)
            {
                var node = copy.SelectToken(path) as JObject;
                if (node == null) continue;
                Hydrate(node["Call"] as JObject, "ArgumentsJson", "ArgumentsPayload", payloads);
                Hydrate(node, "PreparedStateJson", "PreparedStatePayload", payloads);
            }
            return copy;
        }

        public static void ExternalizeActivity(ChatActivity activity, ChatBlobStore payloads)
        {
            if (activity == null) return;
            if ((activity.ArgumentsJson?.Length ?? 0) > InlineLimit)
            {
                activity.ArgumentsPayload = PayloadRef.FromBlob(payloads.StoreText(activity.ArgumentsJson, "application/json"));
                activity.ArgumentsJson = null;
            }
            else if (activity.ArgumentsJson != null) activity.ArgumentsPayload = null;
            if ((activity.DataJson?.Length ?? 0) > InlineLimit)
            {
                activity.ResultPayload = PayloadRef.FromBlob(payloads.StoreText(activity.DataJson, "application/json"));
                activity.DataJson = null;
            }
            else if (activity.DataJson != null) activity.ResultPayload = null;
            foreach (var child in activity.Children ?? new System.Collections.Generic.List<ChatActivity>()) ExternalizeActivity(child, payloads);
        }

        public static string ReadArguments(ChatActivity activity, ChatBlobStore payloads)
        { return activity.ArgumentsPayload == null ? activity.ArgumentsJson : Read(activity.ArgumentsPayload, payloads); }

        private static void Externalize(JObject node, string bodyKey, string referenceKey, ChatBlobStore payloads)
        {
            var text = (string)node?[bodyKey];
            if ((text?.Length ?? 0) <= InlineLimit) return;
            node[referenceKey] = JObject.FromObject(PayloadRef.FromBlob(payloads.StoreText(text, "application/json")));
            node.Remove(bodyKey);
        }

        private static void Hydrate(JObject node, string bodyKey, string referenceKey, ChatBlobStore payloads)
        {
            if (node?[referenceKey] == null) return;
            if (node[bodyKey]?.Type == JTokenType.String)
                throw new InvalidOperationException("RUNTIME_PAYLOAD_INVALID: two competing runtime bodies.");
            node[bodyKey] = Read(node[referenceKey].ToObject<PayloadRef>(), payloads);
            node.Remove(referenceKey);
        }

        private static string Read(PayloadRef payload, ChatBlobStore payloads)
        {
            if (payload == null || payloads == null || payload.ByteLength > 16L * 1024 * 1024)
                throw new InvalidOperationException("RUNTIME_PAYLOAD_UNAVAILABLE: exact bounded payload required; cancel the pending action or open a new chat.");
            var value = payloads.ReadText(payload.ToBlobReference());
            if (value == null) throw new InvalidOperationException("RUNTIME_PAYLOAD_UNAVAILABLE: exact body is missing; no replay or newer body substitution is allowed.");
            return value;
        }
    }
}
