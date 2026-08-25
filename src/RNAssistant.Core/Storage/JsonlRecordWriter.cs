using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace RNAssistant.Core.Storage
{
    internal static class JsonlRecordWriter
    {
        public static void RewriteAll<TRecord>(string path, IEnumerable<TRecord> records, Encoding encoding)
        {
            var content = string.Join("\n", (records ?? Enumerable.Empty<TRecord>())
                .Select(record => JsonConvert.SerializeObject(record, Formatting.None)));
            if (content.Length > 0) content += "\n";
            StorageFileSystem.WriteAllTextAtomic(path, content, encoding);
        }
    }
}
