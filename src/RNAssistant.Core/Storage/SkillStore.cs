using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class SkillStore
    {
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;

        public SkillStore(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
        }

        public List<SkillDefinition> Load()
        {
            return _json.Load(_paths.SkillsFile, new List<SkillDefinition>());
        }

        public void Save(IEnumerable<SkillDefinition> skills)
        {
            _json.Save(_paths.SkillsFile, new List<SkillDefinition>(skills ?? new SkillDefinition[0]));
        }
    }
}
