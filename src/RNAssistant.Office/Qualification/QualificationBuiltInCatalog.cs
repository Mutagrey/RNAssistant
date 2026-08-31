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

        public static QualificationPackCatalog Load()
        {
            return Load(typeof(QualificationBuiltInCatalog).Assembly);
        }

        internal static QualificationPackCatalog Load(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            var coverage = QualificationCoverageRegistry.Parse(Read(assembly, CoverageResource));
            var parser = new QualificationManifestParser();
            return new QualificationPackCatalog(coverage, new[]
            {
                parser.Parse(Read(assembly, ShellPackResource)),
                parser.Parse(Read(assembly, ExcelWq0PackResource))
            });
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
