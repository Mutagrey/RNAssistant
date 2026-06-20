using System;
using Newtonsoft.Json;

namespace RNAssistant.OfficeHosts
{
    public sealed class OfficeTargetDescriptor
    {
        public string Host { get; set; }
        public string FullName { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public string DocumentKey { get; set; }
        public string EntryId { get; set; }
        public string FolderPath { get; set; }
        public string Selection { get; set; }
        public string Action { get; set; }

        public bool HasDocumentIdentity
        {
            get
            {
                return !string.IsNullOrWhiteSpace(FullName)
                    || !string.IsNullOrWhiteSpace(DocumentKey)
                    || !string.IsNullOrWhiteSpace(EntryId)
                    || !string.IsNullOrWhiteSpace(FolderPath);
            }
        }

        public static OfficeTargetDescriptor FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new OfficeTargetDescriptor();
            }

            var descriptor = JsonConvert.DeserializeObject<OfficeTargetDescriptor>(json.TrimStart('\uFEFF'));
            return descriptor ?? new OfficeTargetDescriptor();
        }

        public static OfficeTargetDescriptor FromBase64Json(string base64Json)
        {
            if (string.IsNullOrWhiteSpace(base64Json))
            {
                return new OfficeTargetDescriptor();
            }

            var bytes = Convert.FromBase64String(base64Json);
            return FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
