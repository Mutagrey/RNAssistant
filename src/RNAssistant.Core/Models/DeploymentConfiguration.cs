using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    // Public build input. It contains neither a password nor a signing secret.
    public sealed class DeploymentTrust
    {
        public DeploymentPublicKey PublicKey { get; set; }
        public string PasswordSalt { get; set; }
        public string PasswordHash { get; set; }
        public int PasswordIterations { get; set; }
    }
    public sealed class DeploymentPublicKey
    {
        public byte[] Modulus { get; set; }
        public byte[] Exponent { get; set; }
    }
    public sealed class DeploymentConfiguration
    {
        public int FormatVersion { get; set; }
        public int PolicyVersion { get; set; }
        public DateTime IssuedUtc { get; set; }
        public DeploymentBrowserSettings Browser { get; set; }
    }
    public sealed class DeploymentBrowserSettings
    {
        public List<string> AllowedSites { get; set; } = new List<string>();
        public bool AllowAnyManualSite { get; set; }
    }
    public enum BrowserAccessKind { Agent, Manual }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum DeploymentConfigState { Absent, Applied, Rejected }
    public sealed class DeploymentConfigStatus
    {
        [JsonProperty("state")] public DeploymentConfigState State { get; set; }
    }
    public sealed class DeploymentConfigEditor
    {
        [JsonProperty("sessionToken")] public string SessionToken { get; set; }
        [JsonProperty("expiresUtc")] public DateTime ExpiresUtc { get; set; }
        [JsonProperty("sites")] public List<string> Sites { get; set; }
        [JsonProperty("allowAnyManualSite")] public bool AllowAnyManualSite { get; set; }
        [JsonProperty("policyVersion")] public int PolicyVersion { get; set; }
        [JsonProperty("filePath")] public string FilePath { get; set; }
        [JsonProperty("status")] public DeploymentConfigStatus Status { get; set; }
    }
}
