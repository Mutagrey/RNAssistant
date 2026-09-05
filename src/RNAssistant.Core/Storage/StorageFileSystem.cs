using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace RNAssistant.Core.Storage
{
    internal static class StorageFileSystem
    {
        internal static IDisposable AcquireWriteLock(string path)
        {
            EnsureRegularDirectory(Path.GetDirectoryName(path));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline) throw;
                    System.Threading.Thread.Sleep(20);
                }
            }
        }

        public static void WriteAllTextAtomic(string path, string content)
        {
            WriteAllTextAtomic(path, content, null);
        }

        public static void WriteAllTextAtomic(string path, string content, Encoding encoding)
        {
            WriteAtomic(path, tempPath =>
            {
                if (encoding == null)
                {
                    File.WriteAllText(tempPath, content ?? string.Empty);
                }
                else
                {
                    File.WriteAllText(tempPath, content ?? string.Empty, encoding);
                }
            });
        }

        public static void WriteAtomic(string path, Action<string> writeTempFile)
        {
            if (writeTempFile == null)
            {
                throw new ArgumentNullException("writeTempFile");
            }
            if (File.Exists(path) && IsReparsePoint(path))
            {
                throw new IOException("Storage file cannot be a reparse point: " + path);
            }

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                writeTempFile(tempPath);

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        public static IEnumerable<string> GetFilesRecursive(string directory, string pattern)
        {
            return GetFilesRecursive(directory, pattern, null);
        }

        public static IEnumerable<string> GetFilesRecursive(
            string directory,
            string pattern,
            Action<string, string> onSkipped)
        {
            var files = new List<string>();
            AddFiles(directory, pattern, files, onSkipped, true);
            return files;
        }

        public static IEnumerable<string> GetFiles(string directory, string pattern)
        {
            var files = new List<string>();
            if (string.IsNullOrWhiteSpace(directory) || IsReparsePoint(directory)) return files;
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(directory, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern);
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                return files;
            }
            foreach (var candidate in candidates)
            {
                if (!IsReparsePoint(candidate)) files.Add(candidate);
            }
            return files;
        }

        public static IEnumerable<string> GetDirectories(string directory)
        {
            var directories = new List<string>();
            if (string.IsNullOrWhiteSpace(directory) || IsReparsePoint(directory)) return directories;
            string[] candidates;
            try
            {
                candidates = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                return directories;
            }
            foreach (var candidate in candidates)
            {
                if (!IsReparsePoint(candidate)) directories.Add(candidate);
            }
            return directories;
        }

        public static string SafeSegment(string value, string fallback)
        {
            var chars = (value ?? fallback).Select(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            var result = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(result) ? fallback : result;
        }

        public static void EnsureRegularDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Directory path is required.", "path");
            Directory.CreateDirectory(path);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                throw new IOException("Managed storage directory could not be verified: " + path, ex);
            }
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Managed storage directory must be a regular directory: " + path);
            }
        }

        public static bool IsRegularDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                return false;
            }
        }

        public static bool TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0) return false;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(path, false);
                    return !Directory.Exists(path);
                }
                foreach (var file in Directory.GetFiles(path))
                {
                    File.Delete(file);
                }
                foreach (var directory in Directory.GetDirectories(path))
                {
                    if (!TryDeleteDirectory(directory)) return false;
                }
                Directory.Delete(path, false);
                return !Directory.Exists(path);
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
        }

        internal static bool IsReparsePoint(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                return true;
            }
        }

        private static void AddFiles(
            string directory,
            string pattern,
            ICollection<string> files,
            Action<string, string> onSkipped,
            bool root)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory);
            }
            catch (DirectoryNotFoundException)
            {
                if (!root) ReportSkipped(onSkipped, directory, "A discovered directory disappeared before it could be read.");
                return;
            }
            catch (FileNotFoundException)
            {
                if (!root) ReportSkipped(onSkipped, directory, "A discovered directory disappeared before it could be read.");
                return;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                ReportSkipped(onSkipped, directory, "Directory attributes could not be read: " + ex.Message);
                return;
            }
            if ((attributes & FileAttributes.Directory) == 0)
            {
                ReportSkipped(onSkipped, directory, "Storage traversal root is not a directory.");
                return;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                ReportSkipped(onSkipped, directory, "Directory reparse points are not traversed.");
                return;
            }

            string[] localFiles;
            string[] childDirectories;
            try
            {
                localFiles = Directory.GetFiles(directory, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern);
                childDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                ReportSkipped(onSkipped, directory, "Directory could not be enumerated: " + ex.Message);
                return;
            }

            foreach (var file in localFiles)
            {
                if (IsReparsePoint(file))
                {
                    ReportSkipped(onSkipped, file, "File reparse points are not read as storage records.");
                }
                else
                {
                    files.Add(file);
                }
            }
            foreach (var childDirectory in childDirectories)
            {
                AddFiles(childDirectory, pattern, files, onSkipped, false);
            }
        }

        private static bool IsFileSystemException(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
                ex is ArgumentException || ex is NotSupportedException;
        }

        private static void ReportSkipped(Action<string, string> onSkipped, string path, string message)
        {
            if (onSkipped != null) onSkipped(path, message);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
