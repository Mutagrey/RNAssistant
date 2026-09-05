using System;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class ResourceDefinitionReader
    {
        internal static T Read<T>(ResourceGatewayService gateway, ChatSession session, ResourceRef requested, out ResourceRef reference)
        {
            reference = requested;
            var text = new StringBuilder();
            string cursor = null;
            do
            {
                var read = gateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "text", Cursor = cursor, MaxChars = 32000 }).Result;
                if (text.Length + read.ReturnedCharacters > 128000) throw new InvalidOperationException("Definition exceeds its bounded contract.");
                reference = read.Resource.Reference.Copy(); text.Append(read.Text); cursor = read.NextCursor;
                if (read.Complete) break;
                if (string.IsNullOrEmpty(cursor)) throw new InvalidOperationException("Definition snapshot is incomplete.");
            } while (true);
            return JsonConvert.DeserializeObject<T>(text.ToString());
        }
    }
}
