using System;

namespace RNAssistant.Core.Models
{
    public sealed class ResourceRequestException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public ResourceRequestException(string message, string errorCode, bool retryable)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "resource_request_invalid" : errorCode;
            Retryable = retryable;
        }
    }

}
