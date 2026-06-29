using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    public static class AttachmentImageService
    {
        public static byte[] ReadForModel(AttachmentStore store, ChatAttachment attachment)
        {
            return store == null ? null : store.ReadBytes(attachment);
        }
    }
}
