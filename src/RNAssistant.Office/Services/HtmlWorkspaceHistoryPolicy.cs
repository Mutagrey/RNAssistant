using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class HtmlWorkspaceHistoryPolicy
    {
        internal const int MaxItems = 20;
        internal const long MaxContentCharacters = 2000000;

        public static List<HtmlWorkspaceSnapshot> Trim(IEnumerable<HtmlWorkspaceSnapshot> snapshots)
        {
            var result = new List<HtmlWorkspaceSnapshot>();
            long storedCharacters = 0;
            foreach (var snapshot in snapshots ?? new HtmlWorkspaceSnapshot[0])
            {
                if (snapshot == null || result.Count >= MaxItems)
                {
                    continue;
                }

                var snapshotCharacters = EstimateContentCharacters(snapshot);
                if (result.Count > 0 && storedCharacters + snapshotCharacters > MaxContentCharacters)
                {
                    break;
                }

                result.Add(snapshot);
                storedCharacters += snapshotCharacters;
            }
            return result;
        }

        internal static long EstimateContentCharacters(HtmlWorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }

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
