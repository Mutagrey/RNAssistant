using System;
using Newtonsoft.Json;

namespace RNAssistant.Core.ModelProtocol
{
    public enum ModelProtocolDisposition { Unvalidated, Accepted, Rejected }
    public enum ModelRuntimeDisposition { Rejected, Cancelled, Superseded }

    // Mandatory semantic body. No proposed calls or partially parsed JSON can
    // enter execution/model history through this type.
    public sealed class RetainedModelAttempt
    {
        public ConversationAttemptSnapshot Body { get; private set; }
        public ModelProtocolDisposition Protocol { get; private set; }
        public ModelRuntimeDisposition Runtime { get; private set; }
        public string Explanation { get; private set; }

        [JsonConstructor]
        public RetainedModelAttempt(ConversationAttemptSnapshot body, ModelProtocolDisposition protocol,
            ModelRuntimeDisposition runtime, string explanation)
        {
            if (body == null || !body.TransportEnded || string.IsNullOrWhiteSpace(body.AttemptId))
                throw new ArgumentException("Retention requires a frozen, addressed attempt.");
            if (!Enum.IsDefined(typeof(ModelProtocolDisposition), protocol) || !Enum.IsDefined(typeof(ModelRuntimeDisposition), runtime))
                throw new ArgumentException("An explicit protocol and runtime disposition is required.");
            foreach (var field in new[] { body.Message, body.Reason, body.Decision, body.Reasoning, body.Refusal })
                if (field == null || field.Length > ConversationStreamProgressProjector.MaximumFieldCharacters)
                    throw new ArgumentException("Retained visible fields require their bounded snapshot.");
            Body = body; Protocol = protocol; Runtime = runtime;
            Explanation = explanation ?? string.Empty;
        }
    }

    internal sealed class ModelAttemptPersistenceException : Exception
    {
        internal ModelAttemptPersistenceException(Exception cause)
            : base("Не удалось сохранить ответ модели. Новая попытка не начата. " + cause.Message, cause) { }
    }
}
