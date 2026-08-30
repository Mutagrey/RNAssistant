using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Vba
{
    internal interface IVbaPackageJournal
    {
        VbaPackageMutationPreparation PreparePackageMutation(VbaPackageMutationPreparation preparation);

        void CompletePackageMutation(
            string host,
            string documentKey,
            string mutationId,
            string status,
            IEnumerable<VbaPackageMutationComponentAssessment> components,
            string errorCode,
            string message);

        IReadOnlyList<VbaPackageMutationRecord> ListOpenPackageMutations(string host, string documentKey);
        IReadOnlyList<VbaPackageMutationRecord> ListPackageMutations(string host, string documentKey);
    }

    internal sealed class VbaPackageJournalStoreAdapter : IVbaPackageJournal
    {
        private readonly VbaJournalStore _store;

        public VbaPackageJournalStoreAdapter(VbaJournalStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public VbaPackageMutationPreparation PreparePackageMutation(VbaPackageMutationPreparation preparation)
        {
            return _store.PreparePackageMutation(preparation);
        }

        public void CompletePackageMutation(
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

        public IReadOnlyList<VbaPackageMutationRecord> ListOpenPackageMutations(string host, string documentKey)
        {
            return _store.ListOpenPackageMutations(host, documentKey);
        }

        public IReadOnlyList<VbaPackageMutationRecord> ListPackageMutations(string host, string documentKey)
        {
            return _store.ListPackageMutations(host, documentKey);
        }
    }
}
