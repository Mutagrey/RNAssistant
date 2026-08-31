using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace RNAssistant.Office.Qualification
{
    public static class QualificationBuiltInCatalog
    {
        internal const string CoverageResource =
            "RNAssistant.Office.Qualification.Packs.coverage.v1.json";
        internal const string ShellPackResource =
            "RNAssistant.Office.Qualification.Packs.common.ui-shell.v1.json";
        internal const string ExcelWq0PackResource =
            "RNAssistant.Office.Qualification.Packs.excel.wq0.identity.v1.json";
        internal const string CommonQuickPackResource =
            "RNAssistant.Office.Qualification.Packs.common.quick.v1.json";
        internal const string ProviderLivePackResource =
            "RNAssistant.Office.Qualification.Packs.provider.live.v1.json";
        internal const string StorageRecoveryPackResource =
            "RNAssistant.Office.Qualification.Packs.storage.recovery.v1.json";
        internal const string UiWebViewPackResource =
            "RNAssistant.Office.Qualification.Packs.ui.webview.v1.json";
        internal const string ExcelReadWritePackResource =
            "RNAssistant.Office.Qualification.Packs.excel.read-write.v1.json";
        internal const string ExcelComplexPackResource =
            "RNAssistant.Office.Qualification.Packs.excel.complex-task.v1.json";
        internal const string VbaLifecyclePackResource =
            "RNAssistant.Office.Qualification.Packs.vba.lifecycle.v1.json";
        internal const string CrossFullRunPackResource =
            "RNAssistant.Office.Qualification.Packs.cross.full-run.v1.json";

        private static readonly string[] PackResources =
        {
            ShellPackResource,
            CommonQuickPackResource,
            ProviderLivePackResource,
            StorageRecoveryPackResource,
            UiWebViewPackResource,
            ExcelWq0PackResource,
            ExcelReadWritePackResource,
            ExcelComplexPackResource,
            VbaLifecyclePackResource,
            CrossFullRunPackResource
        };

        public static QualificationPackCatalog Load()
        {
            return Load(typeof(QualificationBuiltInCatalog).Assembly);
        }

        internal static QualificationPackCatalog Load(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            var coverage = QualificationCoverageRegistry.Parse(Read(assembly, CoverageResource));
            var parser = new QualificationManifestParser();
            var packs = new QualificationPack[PackResources.Length];
            for (var index = 0; index < PackResources.Length; index++)
            {
                packs[index] = parser.Parse(Read(assembly, PackResources[index]));
            }
            return new QualificationPackCatalog(coverage, packs);
        }

        private static string Read(Assembly assembly, string name)
        {
            using (var stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded qualification resource is missing: " + name + ".");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
