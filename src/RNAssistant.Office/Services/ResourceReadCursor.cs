using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ResourceReadPosition
    {
        public int Offset { get; set; }
        public string Revision { get; set; }
    }

    internal static class ResourceReadCursor
    {
        private const string RevisionCursorPrefix = "r1:";

        public static int ParseImmutable(ResourceReadRequest request)
        {
            var cursor = request == null ? null : request.Cursor;
            int offset;
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            if (!TryParseOffset(cursor, out offset))
            {
                throw InvalidCursor();
            }
            return offset;
        }

        public static string CreateImmutable(int offset)
        {
            if (offset < 0) throw new InvalidOperationException("Cannot create a resource cursor with a negative offset.");
            return offset.ToString(CultureInfo.InvariantCulture);
        }

        public static ResourceReadPosition ParseRevisionBound(ResourceReadRequest request)
        {
            return ParseRevisionBound(request == null ? null : request.Cursor);
        }

        public static ResourceReadPosition ParseRevisionBound(string cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return new ResourceReadPosition();
            var parts = cursor.Split(':');
            int offset;
            if (parts.Length != 3 || !string.Equals(parts[0], "r1", StringComparison.Ordinal) ||
                !TryParseOffset(parts[1], out offset) ||
                !IsRevisionToken(parts[2]))
            {
                throw InvalidCursor();
            }
            return new ResourceReadPosition { Offset = offset, Revision = parts[2] };
        }

        public static string CreateRevisionBound(int offset, string revision)
        {
            if (offset < 0 || !IsRevisionToken(revision))
            {
                throw new InvalidOperationException("Cannot create a resource cursor without a valid revision.");
            }
            return RevisionCursorPrefix + offset.ToString(CultureInfo.InvariantCulture) + ":" + revision;
        }

        public static void ValidatePinned(ResourceReadRequest request, string actualRevision)
        {
            var expected = request == null || request.Reference == null ? null : request.Reference.Revision;
            if (string.IsNullOrWhiteSpace(expected) ||
                string.Equals(expected, actualRevision, StringComparison.OrdinalIgnoreCase)) return;
            throw new ResourceRequestException(
                "The requested immutable resource revision does not match its canonical URI.",
                "resource_revision_mismatch",
                true);
        }

        public static void ValidateLive(
            ResourceReadRequest request,
            ResourceReadPosition position,
            string actualRevision)
        {
            var requested = request == null || request.Reference == null ? null : request.Reference.Revision;
            var cursorRevision = position == null ? null : position.Revision;
            if (!string.IsNullOrWhiteSpace(requested) && !string.IsNullOrWhiteSpace(cursorRevision) &&
                !string.Equals(requested, cursorRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidCursor();
            }
            var expected = string.IsNullOrWhiteSpace(cursorRevision) ? requested : cursorRevision;
            if (string.IsNullOrWhiteSpace(expected) ||
                string.Equals(expected, actualRevision, StringComparison.OrdinalIgnoreCase)) return;
            throw new ResourceRequestException(
                "The mutable resource changed after the referenced revision was observed. Resolve or read it again before continuing.",
                "resource_revision_changed",
                true);
        }

        public static void ValidateContinuation(ResourceReadPosition position, string actualRevision)
        {
            if (position == null || string.IsNullOrWhiteSpace(position.Revision) ||
                string.Equals(position.Revision, actualRevision, StringComparison.OrdinalIgnoreCase)) return;
            throw new ResourceRequestException(
                "The resource collection changed after the previous page was read. List it again from the first page.",
                "resource_revision_changed",
                true);
        }

        public static void ValidateCollectionOffset(ResourceReadPosition position, int count)
        {
            if (position == null || position.Offset > Math.Max(0, count)) throw InvalidCursor();
        }

        public static string CollectionRevision(IEnumerable<ResourceDescriptor> descriptors)
        {
            var builder = new StringBuilder();
            foreach (var descriptor in descriptors ?? new ResourceDescriptor[0])
            {
                if (descriptor == null) continue;
                AppendField(builder, descriptor.Reference == null ? null : descriptor.Reference.Uri);
                AppendField(builder, descriptor.Reference == null ? null : descriptor.Reference.Revision);
                AppendField(builder, descriptor.Kind);
                AppendField(builder, descriptor.Title);
                AppendField(builder, descriptor.MimeType);
                AppendField(builder, descriptor.ByteLength.HasValue
                    ? descriptor.ByteLength.Value.ToString(CultureInfo.InvariantCulture)
                    : null);
                foreach (var pair in (descriptor.Metadata ?? new Dictionary<string, string>())
                    .OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    AppendField(builder, pair.Key);
                    AppendField(builder, pair.Value);
                }
                builder.Append(';');
            }
            return TextPatternEngine.Sha256(builder.ToString());
        }

        private static void AppendField(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
        }

        public static void RejectCursor(ResourceReadRequest request)
        {
            if (request != null && !string.IsNullOrWhiteSpace(request.Cursor)) throw InvalidCursor();
        }

        private static bool IsRevisionToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!(character >= 'a' && character <= 'z') &&
                    !(character >= 'A' && character <= 'Z') &&
                    !(character >= '0' && character <= '9') &&
                    character != '-' && character != '_' && character != '.') return false;
            }
            return true;
        }

        private static bool TryParseOffset(string value, out int offset)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out offset) &&
                offset >= 0 &&
                string.Equals(
                    offset.ToString(CultureInfo.InvariantCulture),
                    value,
                    StringComparison.Ordinal);
        }

        private static ResourceRequestException InvalidCursor()
        {
            return new ResourceRequestException(
                "Resource continuation cursor is invalid or belongs to another revision.",
                "resource_cursor_invalid",
                true);
        }
    }
}
