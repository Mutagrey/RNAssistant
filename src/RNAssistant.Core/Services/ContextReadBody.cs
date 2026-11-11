using System;
using System.Text;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Core.Services
{
    // Derived state of one compiler atom, discarded with the request. Identity is
    // reusable only for the same immutable content and exact runtime call binding.
    internal sealed class ContextReadBody
    {
        private bool _parsed, _successful, _hasBody;
        private string _content, _callId, _toolName, _textHash;
        private ResourceEvidence _keyEvidence;
        private string _keyHash, _key;

        internal bool HasVisibleBody(ChatMessage message)
        { return Read(message, false) && _hasBody; }

        internal bool IsSuccessfulResourceRead(ChatMessage message)
        { return message?.ToolName == "core.read" && Read(message, false); }

        internal string Key(ResourceEvidence evidence, ChatMessage message)
        {
            string hash;
            if ((message.SyntheticResourceObservation || message.ResultBody?.Kind == ToolResultBodyKind.ResourceText) &&
                message.ResultPayload != null)
                hash = message.ResultPayload.Sha256;
            else
            {
                if (!Read(message, true) || !_hasBody) return null;
                hash = _textHash;
            }
            if (!ReferenceEquals(_keyEvidence, evidence) || _keyHash != hash)
            {
                _key = JsonConvert.SerializeObject(new { evidence.ScopeId, evidence.Resource.Uri,
                    evidence.Resource.Revision, evidence.View, evidence.Coverage, evidence.Complete, hash });
                _keyEvidence = evidence; _keyHash = hash;
            }
            return _key;
        }

        private bool Read(ChatMessage message, bool requireHash)
        {
            string error;
            // Recheck mutable transport metadata even on a content hit. A receipt,
            // invalid header or changed binding cannot inherit body authority.
            if (message == null || message.CompletedOperation != null ||
                message.ToolName != "core.read" && message.ToolName != "core.tools.read" ||
                !ToolResultHistoryReader.TryReadHeader(message, out error)) return false;
            if (_parsed && _content == message.Content && _callId == message.ToolCallId && _toolName == message.ToolName &&
                (!requireHash || !_hasBody || _textHash != null))
                return _successful;
            _parsed = true; _content = message.Content; _callId = message.ToolCallId; _toolName = message.ToolName;
            _successful = false; _hasBody = false; _textHash = null;
            ToolResultWireReadResult wire;
            if (!ToolResultHistoryReader.TryRead(message, out wire, out error) || wire.Result.Status != ToolResultStatus.Ok)
                return false;
            _successful = true;
            var data = wire.Data as JObject;
            if ((bool?)data?["rawContentIncluded"] == false || (bool?)data?["bodyIncluded"] == false) return true;
            string text = null;
            if (data?["text"]?.Type == JTokenType.String) text = (string)data["text"];
            else
            {
                var structured = ModelResultProjection.SelectFields(data, "table", "tree");
                if (structured.Properties().Any(item => item.Value.Type != JTokenType.Null))
                    text = structured.ToString(Formatting.None);
            }
            _hasBody = text != null;
            // Keep only derived metadata, never a second retained body/parsed tree.
            if (_hasBody && requireHash)
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    _textHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)))
                        .Replace("-", string.Empty).ToLowerInvariant();
            return true;
        }
    }
}
