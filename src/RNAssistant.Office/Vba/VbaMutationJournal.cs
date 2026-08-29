using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Vba
{
    internal interface IVbaMutationJournal
    {
        VbaMutationPreparation PrepareMutation(
            VbaMutationPreparation preparation,
            string beforeCode,
            string intendedAfterCode);

        void CompleteMutation(
            string host,
            string documentKey,
            string mutationId,
            string status,
            bool? actualExists,
            string actualCodeSha256,
            string actualComparableCodeSha256,
            string errorCode,
            string message);
    }

    internal sealed class VbaMutationJournalStoreAdapter : IVbaMutationJournal
    {
        private readonly VbaJournalStore _store;

        public VbaMutationJournalStoreAdapter(VbaJournalStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public VbaMutationPreparation PrepareMutation(
            VbaMutationPreparation preparation,
            string beforeCode,
            string intendedAfterCode)
        {
            return _store.PrepareMutation(preparation, beforeCode, intendedAfterCode);
        }

        public void CompleteMutation(
            string host,
            string documentKey,
            string mutationId,
            string status,
            bool? actualExists,
            string actualCodeSha256,
            string actualComparableCodeSha256,
            string errorCode,
            string message)
        {
            _store.CompleteMutation(
                host,
                documentKey,
                mutationId,
                status,
                actualExists,
                actualCodeSha256,
                actualComparableCodeSha256,
                errorCode,
                message);
        }
    }
}
