using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Vba
{
    internal interface IVbaMutationJournal
    {
        VbaBackupReadResult FindBackup(
            string host,
            string documentKey,
            string backupId,
            string moduleName);

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

        public VbaBackupReadResult FindBackup(
            string host,
            string documentKey,
            string backupId,
            string moduleName)
        {
            try
            {
                var backup = _store.Find(host, documentKey, backupId, moduleName);
                return backup == null
                    ? VbaBackupReadResult.NotFound()
                    : VbaBackupReadResult.Found(new VbaBackupSnapshot(
                        backup.BackupId,
                        backup.ModuleName,
                        backup.ComponentType,
                        backup.CodeSha256,
                        backup.CodeByteLength,
                        backup.Code,
                        backup.CreatedUtc));
            }
            catch (VbaJournalException ex)
            {
                return VbaBackupReadResult.Failure(
                    ex.Message,
                    "vba_backup_unavailable",
                    false);
            }
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
