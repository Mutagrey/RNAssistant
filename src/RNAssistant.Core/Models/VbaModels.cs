using System;

namespace RNAssistant.Core.Models
{
    public sealed class VbaModuleBackup
    {
        public string BackupId { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string ModuleName { get; set; }
        public string ComponentType { get; set; }
        public string Code { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
