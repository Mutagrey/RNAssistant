using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Vba
{
    internal interface IVbaRenameJournal
    {
        VbaPackageMutationPreparation PrepareRename(VbaPackageMutationPreparation preparation);

        void CompleteRename(
            string host,
            string documentKey,
            string mutationId,
            string status,
            IEnumerable<VbaPackageMutationComponentAssessment> components,
            string errorCode,
            string message);

        IReadOnlyList<VbaPackageMutationRecord> ListOpenRenames(string host, string documentKey);
    }

    // Rename keeps the existing package.mutation.* two-identity wire. This adapter
    // exposes only rename records and never creates a second journal or projection.
    internal sealed class VbaRenameJournalStoreAdapter : IVbaRenameJournal
    {
        private readonly VbaJournalStore _store;

        public VbaRenameJournalStoreAdapter(VbaJournalStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public VbaPackageMutationPreparation PrepareRename(
            VbaPackageMutationPreparation preparation)
        {
            if (preparation == null ||
                !string.Equals(preparation.Operation, "rename", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A rename preparation is required.", nameof(preparation));
            }
            return _store.PreparePackageMutation(preparation);
        }

        public void CompleteRename(
            string host,
            string documentKey,
            string mutationId,
            string status,
            IEnumerable<VbaPackageMutationComponentAssessment> components,
            string errorCode,
            string message)
        {
            _store.CompletePackageMutation(
                host,
                documentKey,
                mutationId,
                status,
                components,
                errorCode,
                message);
        }

        public IReadOnlyList<VbaPackageMutationRecord> ListOpenRenames(
            string host,
            string documentKey)
        {
            return _store.ListOpenPackageMutations(host, documentKey)
                .Where(record => record != null && record.Prepared != null &&
                    string.Equals(
                        record.Prepared.Operation,
                        "rename",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
