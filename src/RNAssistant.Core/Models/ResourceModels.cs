using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class ResourceRepresentations
    {
        public const string Metadata = "metadata";
        public const string Text = "text";
        public const string Structure = "structure";
        public const string Media = "media";
        public const string Source = "source";
    }

    public sealed class ResourceRef
    {
        public string Uri { get; set; }
        public string Revision { get; set; }

        public ResourceRef()
        {
        }

        public ResourceRef(string uri, string revision = null)
        {
            Uri = uri;
            Revision = revision;
        }
    }

    public sealed class ResourceDescriptor
    {
        public ResourceRef Reference { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string MimeType { get; set; }
        public bool Mutable { get; set; }
        public List<string> Representations { get; set; }
        public Dictionary<string, string> Metadata { get; set; }

        public ResourceDescriptor()
        {
            Representations = new List<string>();
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class ArtifactRevision
    {
        public ResourceRef Reference { get; set; }
        public ResourceRef Parent { get; set; }
        public string ContentSha256 { get; set; }
        public long ContentByteLength { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ArtifactRevision()
        {
            CreatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class ResourceHead
    {
        public string Uri { get; set; }
        public ResourceRef Current { get; set; }
    }

    public sealed class ResourceReadRequest
    {
        public ResourceRef Reference { get; set; }
        public string Representation { get; set; }
        public string Cursor { get; set; }
        public int MaxChars { get; set; }
    }

    public sealed class ResourceReadResult
    {
        public ResourceDescriptor Resource { get; set; }
        public string Representation { get; set; }
        public string Text { get; set; }
        public string NextCursor { get; set; }
        public bool Complete { get; set; }
        public bool HydratedForNextModelStep { get; set; }
        public List<ResourceRef> Related { get; set; }

        public ResourceReadResult()
        {
            Related = new List<ResourceRef>();
        }
    }
}
