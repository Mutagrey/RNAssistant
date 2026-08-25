using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RNAssistant.Core.Storage
{
    internal static class StorageFileSystem
    {
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
            var files = new List<string>();
            AddFiles(directory, pattern, files);
            return files;
        }

        public static string SafeSegment(string value, string fallback)
        {
            var chars = (value ?? fallback).Select(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            var result = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(result) ? fallback : result;
        }

        public static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void AddFiles(string directory, string pattern, ICollection<string> files)
        {
            string[] localFiles;
            string[] childDirectories;
            try
            {
                localFiles = Directory.GetFiles(directory, pattern);
                childDirectories = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var file in localFiles)
            {
                files.Add(file);
            }
            foreach (var childDirectory in childDirectories)
            {
                AddFiles(childDirectory, pattern, files);
            }
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
