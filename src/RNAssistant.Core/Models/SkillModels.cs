using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class SkillDefinition
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ArgumentSchemaJson { get; set; }
        public string Executor { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool MutatesDocument { get; set; }
        public bool AgentCanRun { get; set; }
        public string PipelineJson { get; set; }
        public string Code { get; set; }
        public string Readme { get; set; }
        public string StoragePath { get; set; }
        public bool Enabled { get; set; }
        public bool BuiltIn { get; set; }

        public SkillDefinition()
        {
            Enabled = true;
            Executor = "builtin";
            ArgumentSchemaJson = "{}";
            AgentCanRun = true;
        }
    }

    public sealed class SkillCommand
    {
        public string SkillId { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Arguments { get; set; }

        public SkillCommand()
        {
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class SkillResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string DataJson { get; set; }

        public static SkillResult Ok(string message, string dataJson = null)
        {
            return new SkillResult { Success = true, Message = message, DataJson = dataJson };
        }

        public static SkillResult Fail(string message)
        {
            return new SkillResult { Success = false, Message = message };
        }
    }
}
