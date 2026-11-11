using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    public sealed class AssistantWindowRectangle
    {
        // Logical units relative to the saved monitor's work area (including negative monitor origins).
        public string Monitor { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Valid { get { return Finite(X) && Finite(Y) && Finite(Width) && Finite(Height) &&
            Math.Abs(X) < 100000 && Math.Abs(Y) < 100000 && Width >= 40 && Height >= 40 && Width < 20000 && Height < 20000; } }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }

    public sealed class AssistantWindowPreferences
    {
        public AssistantWindowRectangle Full { get; set; }
        public AssistantWindowRectangle Compact { get; set; }
        public AssistantWindowRectangle Companion { get; set; }
        public HostWindowPresentation Last { get; set; }
        public HostWindowPresentation BeforeMaximize { get; set; }
        public bool Pinned { get; set; }
        public bool AutoCollapse { get; set; } = true;
    }

    public sealed class AssistantWindowPreferenceStore
    {
        private sealed class FileState
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, AssistantWindowPreferences> Profiles { get; set; } = new Dictionary<string, AssistantWindowPreferences>();
        }
        private readonly string _path, _profile;
        public AssistantWindowPreferenceStore(AppDataPaths paths, string profile)
        { _path = paths.NativeWindowStateFile; _profile = profile; }

        public AssistantWindowPreferences Load()
        {
            AssistantWindowPreferences value;
            if (!Read().Profiles.TryGetValue(_profile, out value)) return new AssistantWindowPreferences();
            if (value == null || value.Full != null && !value.Full.Valid || value.Compact != null && !value.Compact.Valid ||
                value.Companion != null && !value.Companion.Valid)
                throw new InvalidDataException("Invalid window geometry; using default placement.");
            return value;
        }
        private FileState Read()
        {
            if (!File.Exists(_path)) return new FileState();
            if (new FileInfo(_path).Length > 65536) throw new InvalidDataException("Window preferences exceed the size limit.");
            var value = JsonConvert.DeserializeObject<FileState>(File.ReadAllText(_path));
            if (value == null || value.Version != 1 || value.Profiles == null)
                throw new InvalidDataException("Unsupported window preferences schema; using default placement.");
            return value;
        }
        // Called off the Office STA. Merge only this profile under a short cross-process file lock.
        public void Save(AssistantWindowPreferences value)
        {
            using (var gate = Lock())
            {
                FileState file;
                try { file = Read(); }
                catch (JsonException) { file = new FileState(); }
                catch (InvalidDataException) { file = new FileState(); }
                AssistantWindowPreferences previous;
                if (file.Profiles.TryGetValue(_profile, out previous) &&
                    JsonConvert.SerializeObject(previous) == JsonConvert.SerializeObject(value)) return;
                file.Profiles[_profile] = value;
                StorageFileSystem.WriteAtomic(_path, temp => File.WriteAllText(temp, JsonConvert.SerializeObject(file)));
            }
        }
        private FileStream Lock()
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (attempt == 8) throw; Thread.Sleep(25); }
            }
        }
    }
}
