using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class HtmlWorkspaceHistoryPolicy
    {
        public const int MaxItems = 20;
        public const long MaxContentCharacters = 2000000;

        public static List<HtmlWorkspaceSnapshot> Trim(IEnumerable<HtmlWorkspaceSnapshot> snapshots)
        {
            var result = new List<HtmlWorkspaceSnapshot>();
            long storedCharacters = 0;
            foreach (var snapshot in snapshots ?? new HtmlWorkspaceSnapshot[0])
            {
                if (snapshot == null) continue;
                if (result.Count >= MaxItems) break;
                var snapshotCharacters = EstimateContentCharacters(snapshot);
                if (snapshotCharacters > MaxContentCharacters ||
                    storedCharacters + snapshotCharacters > MaxContentCharacters) break;
                result.Add(snapshot);
                storedCharacters += snapshotCharacters;
            }
            return result;
        }

        public static long EstimateContentCharacters(HtmlWorkspaceSnapshot snapshot)
        {
            if (snapshot == null) return 0;
            long total = TextLength(snapshot.Id) + TextLength(snapshot.Label) + TextLength(snapshot.ActiveFileId);
            foreach (var file in snapshot.Files ?? new List<HtmlWorkspaceFile>())
            {
                if (file == null) continue;
                total += TextLength(file.Id) + TextLength(file.Path) + TextLength(file.Kind) + TextLength(file.Content);
            }
            foreach (var dataSource in snapshot.DataSources ?? new List<HtmlWorkspaceDataSource>())
            {
                if (dataSource == null) continue;
                total += TextLength(dataSource.Id) + TextLength(dataSource.Name) + TextLength(dataSource.Json);
            }
            return total;
        }

        private static int TextLength(string value)
        {
            return string.IsNullOrEmpty(value) ? 0 : value.Length;
        }
    }
}
