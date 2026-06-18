using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
    public interface IOfficeApplicationAdapter
    {
        string HostName { get; }
        string DocumentKey { get; }
        string LegacyDocumentKey { get; }
        string RuntimeDocumentKey { get; }
        string DocumentTitle { get; }
        string GetDocumentSnapshot(int maxChars);
        string GetVbaSnapshot(int maxChars);
        ContextNote CaptureSelectionContext(string mode, int maxChars);
        IEnumerable<SkillDefinition> GetBuiltInSkills();
        SkillResult ExecuteSkill(SkillCommand command);
    }
}
