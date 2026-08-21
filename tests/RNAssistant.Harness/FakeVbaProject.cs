using System;
using System.Collections;
using System.Collections.Generic;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal sealed class FakeVbaDocumentObject
    {
        public FakeVbaDocumentObject()
        {
            VBProject = new FakeVbaProjectObject();
        }

        public FakeVbaProjectObject VBProject { get; private set; }
    }

    internal sealed class FakeVbaProjectObject
    {
        public FakeVbaProjectObject()
        {
            VBComponents = new FakeVbaComponents();
        }

        public FakeVbaComponents VBComponents { get; private set; }
    }

    internal sealed class FakeVbaComponents : IEnumerable
    {
        private readonly List<FakeVbaComponent> _items = new List<FakeVbaComponent>();

        public int Count { get { return _items.Count; } }
        public bool FailNextAddedModuleWrite { get; set; }

        public FakeVbaComponent Add(int type)
        {
            var component = new FakeVbaComponent("Module" + (_items.Count + 1), type, string.Empty);
            component.CodeModule.FailNextAdd = FailNextAddedModuleWrite;
            FailNextAddedModuleWrite = false;
            _items.Add(component);
            return component;
        }

        public FakeVbaComponent Seed(string name, string code)
        {
            var component = new FakeVbaComponent(name, 1, code);
            _items.Add(component);
            return component;
        }

        public void Remove(FakeVbaComponent component)
        {
            _items.Remove(component);
        }

        public IEnumerator GetEnumerator()
        {
            return _items.GetEnumerator();
        }
    }

    internal sealed class FakeVbaComponent
    {
        public FakeVbaComponent(string name, int type, string code)
        {
            Name = name;
            Type = type;
            CodeModule = new FakeVbaCodeModule(code);
        }

        public string Name { get; set; }
        public int Type { get; private set; }
        public FakeVbaCodeModule CodeModule { get; private set; }
    }

    internal sealed class FakeVbaCodeModule
    {
        private string _code;

        public FakeVbaCodeModule(string code)
        {
            _code = code ?? string.Empty;
            Lines = new FakeVbaLines(this);
        }

        public bool FailNextAdd { get; set; }
        public string Code { get { return _code; } }
        public int CountOfLines { get { return VbaToolManifestParser.LiveCodeLineCount(_code); } }
        public FakeVbaLines Lines { get; private set; }

        public void DeleteLines(int startLine, int count)
        {
            if (startLine != 1 || count != CountOfLines)
            {
                throw new InvalidOperationException("Fake code module only supports full deletion.");
            }
            _code = string.Empty;
        }

        public void AddFromString(string code)
        {
            if (FailNextAdd)
            {
                FailNextAdd = false;
                throw new InvalidOperationException("scripted VBA write failure");
            }
            _code = code ?? string.Empty;
        }

        public void InsertLines(int startLine, string code)
        {
            if (startLine != 1 || CountOfLines != 0)
            {
                throw new InvalidOperationException("Fake code module only supports insertion into an empty module at line 1.");
            }
            AddFromString(code);
        }
    }

    internal sealed class FakeVbaLines
    {
        private readonly FakeVbaCodeModule _module;

        public FakeVbaLines(FakeVbaCodeModule module)
        {
            _module = module;
        }

        public string this[int startLine, int count]
        {
            get { return _module.Code; }
        }
    }
}
