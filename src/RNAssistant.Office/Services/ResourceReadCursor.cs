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
        private const string ImmutableCursorPrefix = "i2:";
        private const string RevisionCursorPrefix = "r2:";

        public static string ListBinding(string provider, string kind)
        {
            return CreateBinding(
                "list",
                (provider ?? string.Empty).Trim().ToLowerInvariant(),
                (kind ?? string.Empty).Trim().ToLowerInvariant());
        }

        public static string ReadBinding(string resourceUri, string representation)
        {
            return CreateBinding(
                "read",
                resourceUri ?? string.Empty,
                (representation ?? string.Empty).Trim().ToLowerInvariant());
        }

        public static int ParseImmutable(ResourceReadRequest request, string binding)
        {
            var cursor = request == null ? null : request.Cursor;
            int offset;
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            var parts = cursor.Split(':');
            if (parts.Length != 3 || !string.Equals(parts[0], "i2", StringComparison.Ordinal) ||
                !TryParseOffset(parts[1], out offset) ||
                !IsBindingToken(parts[2]) ||
                !string.Equals(parts[2], binding, StringComparison.Ordinal))
            {
                throw InvalidCursor();
            }
            return offset;
        }

        public static string CreateImmutable(int offset, string binding)
        {
            if (offset < 0 || !IsBindingToken(binding))
            {
                throw new InvalidOperationException("Cannot create an immutable resource cursor without an exact binding.");
            }
            return ImmutableCursorPrefix + offset.ToString(CultureInfo.InvariantCulture) + ":" + binding;
        }

        public static ResourceReadPosition ParseRevisionBound(ResourceReadRequest request, string binding)
        {
            return ParseRevisionBound(request == null ? null : request.Cursor, binding);
        }

        public static ResourceReadPosition ParseRevisionBound(string cursor, string binding)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return new ResourceReadPosition();
            var parts = cursor.Split(':');
            int offset;
            if (parts.Length != 4 || !string.Equals(parts[0], "r2", StringComparison.Ordinal) ||
                !TryParseOffset(parts[1], out offset) ||
                !IsRevisionToken(parts[2]) ||
                !IsBindingToken(parts[3]) ||
                !string.Equals(parts[3], binding, StringComparison.Ordinal))
            {
                throw InvalidCursor();
            }
            return new ResourceReadPosition { Offset = offset, Revision = parts[2] };
        }

        public static string CreateRevisionBound(int offset, string revision, string binding)
        {
            if (offset < 0 || !IsRevisionToken(revision) || !IsBindingToken(binding))
            {
                throw new InvalidOperationException("Cannot create a resource cursor without a valid revision and exact binding.");
            }
            return RevisionCursorPrefix + offset.ToString(CultureInfo.InvariantCulture) + ":" + revision + ":" + binding;
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
                "The mutable resource changed after the previous chunk. Restart common.resources_read for the same semantic target with action=read.",
                "resource_revision_changed",
                true);
        }

        public static void ValidateContinuation(ResourceReadPosition position, string actualRevision)
        {
            if (position == null || string.IsNullOrWhiteSpace(position.Revision) ||
                string.Equals(position.Revision, actualRevision, StringComparison.OrdinalIgnoreCase)) return;
            throw new ResourceRequestException(
                "The resource collection changed during internal discovery. Run common.resources_find again with the same semantic scope and query.",
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
            RejectCursor(request == null ? null : request.Cursor);
        }

        public static void RejectCursor(string cursor)
        {
            if (!string.IsNullOrWhiteSpace(cursor)) throw InvalidCursor();
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

        private static bool IsBindingToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!(character >= 'a' && character <= 'f') &&
                    !(character >= '0' && character <= '9')) return false;
            }
            return true;
        }

        private static string CreateBinding(string operation, params string[] values)
        {
            var builder = new StringBuilder();
            AppendField(builder, operation);
            foreach (var value in values ?? new string[0]) AppendField(builder, value);
            return TextPatternEngine.Sha256(builder.ToString());
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
                "Resource continuation cursor is invalid for this exact operation, query, URI, or representation. Omit cursor and restart from the first page or chunk; never reuse a cursor from another result.",
                "resource_cursor_invalid",
                false);
        }
    }
}
