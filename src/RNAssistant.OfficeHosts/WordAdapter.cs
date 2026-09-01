using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Word = Microsoft.Office.Interop.Word;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Vba;
using RNAssistant.Office.Domains.Word;
using RNAssistant.Office.Tools;
using RNAssistant.OfficeHosts.Identity;

namespace RNAssistant.OfficeHosts
{
    public sealed class WordAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog, IOfficeDocumentSessionProvider, IOfficeDispatcherProvider, IWordBackendProvider, IVbaHostBackendProvider
    {
        private readonly Word.Application _application;
        private readonly Word.Document _targetDocument;
        private readonly WordDocumentSession _documentSession;
        private readonly WordInteropBackend _wordBackend;
        private readonly VbaInteropBackend _vbaHostBackend;

        public WordAdapter(
            Word.Application application,
            Word.Document targetDocument,
            IOfficeStaDispatcher dispatcher)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _targetDocument = targetDocument ?? throw new ArgumentNullException(nameof(targetDocument));
            var runtimeDocumentId = DocumentIdentity.RuntimeKey(HostName, _targetDocument);
            _documentSession = new WordDocumentSession(
                _targetDocument, runtimeDocumentId, dispatcher);
            _wordBackend = new WordInteropBackend(_documentSession);
            _vbaHostBackend = new VbaInteropBackend(
                _documentSession, _application);
        }

        public string HostName { get { return "Word"; } }
        public IOfficeDocumentSession DocumentSession { get { return _documentSession; } }
        public IOfficeStaDispatcher StaDispatcher { get { return _documentSession.StaDispatcher; } }
        public IWordBackend WordBackend { get { return _wordBackend; } }
        public IVbaHostBackend VbaHostBackend { get { return _vbaHostBackend; } }
        public string DocumentKey { get { return _documentSession.StableDocumentId; } }
        public string RuntimeDocumentKey { get { return _documentSession.RuntimeDocumentId; } }
        public string DocumentTitle { get { return RequireDocument().Name; } }

        public OfficeContext GetOfficeContext()
        {
            var context = new OfficeContext { Host = HostName };
            try
            {
                var hwnd = NativeWindowInfo.ReadLongMemberPath(
                    RequireDocument(), "ActiveWindow", "Hwnd");
                context.AppHwnd = new IntPtr(hwnd);
                context.ProcessId = NativeWindowInfo.GetProcessId(hwnd);
            }
            catch
            {
            }

            var doc = RequireDocument();
            context.DocumentPath = PersistentPath(doc);
            context.DocumentTitle = SafeString(delegate { return doc.Name; });

            try
            {
                var range = ResolveSelectionRange(doc);
                context.SelectionAddress = range == null ? null : range.Start + ":" + range.End;
                context.SelectionText = range == null ? null : Trim(range.Text, 2000);
            }
            catch
            {
            }

            return context;
        }

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            var result = new List<OpenOfficeDocumentDto>();
            foreach (Word.Document document in _application.Documents)
            {
                result.Add(new OpenOfficeDocumentDto
                {
                    Host = HostName,
                    DocumentKey = KeyForDocument(document),
                    Title = SafeString(delegate { return document.Name; }),
                    Path = PersistentPath(document),
                    IsActive = SameDocument(_targetDocument, document)
                });
            }
            return result;
        }

        public bool ActivateDocument(string documentKey)
        {
            if (string.IsNullOrWhiteSpace(documentKey))
            {
                return false;
            }

            foreach (Word.Document document in _application.Documents)
            {
                if (!string.Equals(KeyForDocument(document), documentKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                document.Activate();
                NativeWindowInfo.BringToForeground(NativeWindowInfo.ReadLongMemberPath(_application, "ActiveWindow", "Hwnd"));
                return true;
            }
            return false;
        }

        public bool OpenDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var document = _application.Documents.Open(path);
                if (document == null)
                {
                    return false;
                }
                document.Activate();
                NativeWindowInfo.BringToForeground(NativeWindowInfo.ReadLongMemberPath(_application, "ActiveWindow", "Hwnd"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string KeyForDocument(Word.Document document)
        {
            if (document == null)
            {
                return "Word:NoDocument";
            }

            var runtimeKey = DocumentIdentity.RuntimeKey(HostName, document);
            return DocumentIdentity.ForOfficeDocument(
                HostName,
                PersistentPath(document),
                runtimeKey,
                () => document.CustomDocumentProperties);
        }

        private static string PersistentPath(Word.Document document)
        {
            if (document == null || string.IsNullOrWhiteSpace(SafeString(delegate { return document.Path; })))
            {
                return string.Empty;
            }

            return SafeString(delegate { return document.FullName; });
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                new SkillDefinition
                {
                    Id = "word.document_editing",
                    Host = "Word",
                    Name = "Word document editing",
                    Description = "Rewrite, insert, format, and review Word document content.",
                    BodyMarkdown = "# Word Document Editing\n\nUse this skill for Word drafting and editing tasks.\n\n- Read selection or document context before targeted edits.\n- Preserve user tone unless the user asks to change it.\n- Use insert/replace tools for document mutations.\n- Keep formatting changes explicit.\n- For review tasks, separate findings from suggested edits.",
                    Enabled = true,
                    BuiltIn = true
                }
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            return Trim(RequireDocument().Range().Text, maxChars);
        }

        public void PrepareForContextCapture()
        {
            try
            {
                RequireDocument().Activate();
            }
            catch
            {
            }
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            var doc = RequireDocument();
            var range = ResolveSelectionRange(doc);
            var referenceOnly = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase);
            var reference = doc.Name + " chars " + range.Start + "-" + range.End;
            var selectedText = Trim(range.Text, maxChars);
            if (string.IsNullOrWhiteSpace(selectedText) && !referenceOnly)
            {
                throw new InvalidOperationException("Select Word text first.");
            }

            var text = referenceOnly
                ? "Reference only. Use Word tools with the current selection/document if exact text is needed."
                : selectedText;

            return new ContextNote
            {
                Host = HostName,
                Kind = referenceOnly ? "text-reference" : "text-selection",
                Title = "Word selection",
                Reference = reference,
                Source = reference,
                Text = text,
                Preview = Trim(text, 360),
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    document = doc.Name,
                    start = range.Start,
                    end = range.End,
                    mode = referenceOnly ? "reference" : "text"
                })
            };
        }

        private Word.Document RequireDocument()
        {
            if (!_documentSession.IsAlive)
                throw new InvalidOperationException(
                    "Target Word document is not open.");
            return _targetDocument;
        }

        private Word.Range ResolveSelectionRange(Word.Document doc)
        {
            try
            {
                if (_application.Selection != null && _application.Selection.Range != null)
                {
                    var range = _application.Selection.Range;
                    if (RangeBelongsToDocument(range, doc))
                    {
                        return range;
                    }
                }
            }
            catch
            {
            }

            try
            {
                var window = doc.ActiveWindow;
                var selection = window == null ? null : window.Selection;
                var range = selection == null ? null : selection.Range;
                if (RangeBelongsToDocument(range, doc)) return range;
            }
            catch
            {
            }
            try
            {
                if (doc.Windows.Count > 0)
                {
                    var selection = doc.Windows[1].Selection;
                    var range = selection == null ? null : selection.Range;
                    if (RangeBelongsToDocument(range, doc)) return range;
                }
            }
            catch
            {
            }
            throw new InvalidOperationException(
                "Select Word text first in the bound document.");
        }

        private static bool RangeBelongsToDocument(Word.Range range, Word.Document doc)
        {
            if (range == null || doc == null)
            {
                return false;
            }

            try
            {
                return string.Equals(
                    DocumentIdentity.RuntimeKey("Word", range.Document),
                    DocumentIdentity.RuntimeKey("Word", doc),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool SameDocument(
            Word.Document left, Word.Document right)
        {
            if (left == null || right == null) return false;
            try
            {
                return string.Equals(
                    DocumentIdentity.RuntimeKey("Word", left),
                    DocumentIdentity.RuntimeKey("Word", right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private delegate string StringGetter();

        private static string SafeString(StringGetter getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private static string Trim(string text, int maxChars)
        {
            maxChars = Math.Max(0, maxChars);
            if (maxChars == 0) return string.Empty;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
