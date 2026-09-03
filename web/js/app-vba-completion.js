(function (root) {
  "use strict";

  var MAX_MODULES = 200;
  var MAX_SOURCE_CHARS = 2000000;
  var MAX_PROCEDURES = 500;
  var MAX_HINTS = 80;
  var VBA_WORDS = [
    "#Const", "#Else", "#ElseIf", "#End If", "#If", "AddressOf", "Alias",
    "And", "As", "Attribute", "Binary", "Boolean", "ByRef", "Byte", "ByVal",
    "Call", "Case", "Const", "Currency", "Date", "Declare", "Dim", "Do",
    "Double", "Each", "Else", "ElseIf", "Empty", "End", "Enum", "Eqv",
    "Erase", "Error", "Event", "Exit", "Explicit", "False", "For", "Friend",
    "Function", "Get", "Global", "GoSub", "GoTo", "If", "Implements", "Imp",
    "In", "Input", "Integer", "Is", "Len", "Let", "Lib", "Like", "Lock",
    "Long", "LongLong", "LongPtr", "Loop", "Me", "Mid", "Mod", "New",
    "Next", "Not", "Nothing", "Null", "Object", "On", "Open", "Option",
    "Option Explicit", "Option Private Module", "Optional", "Or", "Output",
    "ParamArray", "Preserve", "Print", "Private", "Property", "Property Get",
    "Property Let", "Property Set", "PtrSafe", "Public", "Put", "RaiseEvent",
    "ReDim", "Rem", "Resume", "Return", "Seek", "Select", "Set", "Single",
    "Static", "Step", "Stop", "String", "Sub", "Then", "Time", "To", "True",
    "Type", "Unlock", "Until", "Variant", "Wend", "While", "Width", "With",
    "WithEvents", "Write", "Xor"
  ];

  function value(value) {
    return value === null || value === undefined ? "" : String(value);
  }

  function editorId(editor) {
    if (editor && editor._rnEditorId) return editor._rnEditorId;
    if (editor && typeof editor.getTextArea === "function") {
      var textarea = editor.getTextArea();
      return textarea && textarea.id ? textarea.id : "";
    }
    return "";
  }

  function elementValue(id) {
    if (!root.document || typeof root.document.getElementById !== "function") return "";
    var node = root.document.getElementById(id);
    return node ? value(node.value) : "";
  }

  function addSource(target, byName, name, code, current) {
    name = value(name).trim();
    code = value(code);
    var key = name.toLowerCase();
    var source = { name: name, code: code, current: !!current };
    if (key && byName[key] !== undefined) {
      if (current) target[byName[key]] = source;
      return;
    }
    if (key) byName[key] = target.length;
    target.push(source);
  }

  function collectProjectSources(editor) {
    var result = [];
    var byName = {};
    var project = root.state && root.state.vba;
    var modules = project && Array.isArray(project.modules) ? project.modules : [];
    var selectedName = value(project && project.selectedModule) || elementValue("vbaModuleSelect");
    var currentCode = editor && typeof editor.getValue === "function" ? editor.getValue() : "";

    modules.slice(0, MAX_MODULES).forEach(function (module) {
      var name = value(module && (module.name !== undefined ? module.name : module.Name));
      var current = !!selectedName && name.toLowerCase() === selectedName.toLowerCase();
      var code = current ? currentCode : value(module && (module.code !== undefined ? module.code : module.Code));
      addSource(result, byName, name, code, current);
    });
    if (selectedName && byName[selectedName.toLowerCase()] === undefined) {
      addSource(result, byName, selectedName, currentCode, true);
    }
    return result;
  }

  function collectToolSources(editor) {
    var result = [];
    var byName = {};
    var state = root.state || {};
    var tools = Array.isArray(state.tools) ? state.tools : [];
    var tool = tools[state.selectedToolIndex];
    var components = tool && (tool.Components || tool.components);
    components = Array.isArray(components) ? components : [];
    var selectedIndex = Number(state.selectedToolComponentIndex || 0);
    var currentCode = editor && typeof editor.getValue === "function" ? editor.getValue() : "";

    components.slice(0, MAX_MODULES).forEach(function (component, index) {
      var current = index === selectedIndex;
      var name = current ? (elementValue("toolComponentNameInput") || value(component && (component.Name || component.name))) : value(component && (component.Name || component.name));
      var code = current ? currentCode : value(component && (component.Code !== undefined ? component.Code : component.code));
      addSource(result, byName, name, code, current);
    });
    if (!result.length) {
      addSource(result, byName, elementValue("toolComponentNameInput"), currentCode, true);
    }
    return result;
  }

  function collectSources(editor) {
    var id = editorId(editor);
    var sources = id === "vbaCodeInput" ? collectProjectSources(editor) :
      id === "toolCodeInput" ? collectToolSources(editor) : [];
    if (!sources.length && editor && typeof editor.getValue === "function") {
      addSource(sources, {}, "", editor.getValue(), true);
    }
    sources.sort(function (left, right) { return Number(right.current) - Number(left.current); });
    return sources;
  }

  function parseProcedures(sources) {
    var result = [];
    var remainingChars = MAX_SOURCE_CHARS;
    var pattern = /^\s*(?:(Public|Private|Friend)\s+)?(?:(Static)\s+)?(Sub|Function|Property\s+(?:Get|Let|Set))\s+([A-Za-z_][A-Za-z0-9_]*)\s*(\([^\r\n]*\))?/gim;
    sources.forEach(function (source) {
      if (remainingChars <= 0 || result.length >= MAX_PROCEDURES) return;
      var code = value(source.code).slice(0, remainingChars);
      remainingChars -= code.length;
      pattern.lastIndex = 0;
      var match;
      while ((match = pattern.exec(code)) && result.length < MAX_PROCEDURES) {
        result.push({
          access: value(match[1] || "Public").toLowerCase(),
          kind: value(match[3]).replace(/\s+/g, " "),
          name: value(match[4]),
          signature: value(match[5]).slice(0, 120),
          moduleName: source.name,
          current: source.current
        });
      }
    });
    return result;
  }

  function completionContext(editor, cursor) {
    var line = editor && typeof editor.getLine === "function" ? value(editor.getLine(cursor.line)) : "";
    var before = line.slice(0, cursor.ch);
    var qualified = /\b([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)?$/.exec(before);
    if (qualified) {
      return {
        qualifier: qualified[1],
        prefix: qualified[2] || "",
        from: root.CodeMirror.Pos(cursor.line, cursor.ch - value(qualified[2]).length),
        to: cursor
      };
    }
    var word = /([A-Za-z_][A-Za-z0-9_]*)$/.exec(before);
    return {
      qualifier: "",
      prefix: word ? word[1] : "",
      from: root.CodeMirror.Pos(cursor.line, word ? cursor.ch - word[1].length : cursor.ch),
      to: cursor
    };
  }

  function procedureCandidate(procedure) {
    var detail = procedure.kind + (procedure.moduleName ? " · " + procedure.moduleName : "");
    return {
      text: procedure.name,
      displayText: procedure.name + procedure.signature + "  · " + detail,
      className: "rn-vba-hint-procedure",
      matchText: procedure.name,
      rank: procedure.current ? 0 : 2
    };
  }

  function createHintResult(editor) {
    if (!editor || typeof editor.getCursor !== "function") return null;
    var cursor = editor.getCursor();
    var token = typeof editor.getTokenAt === "function" ? editor.getTokenAt(cursor) : null;
    if (token && /(?:^|\s)(?:comment|string)(?:\s|$)/.test(value(token.type))) return null;

    var context = completionContext(editor, cursor);
    var prefix = context.prefix.toLowerCase();
    var sources = collectSources(editor);
    var procedures = parseProcedures(sources);
    var candidates = [];

    if (context.qualifier) {
      var qualifier = context.qualifier.toLowerCase();
      var source = sources.filter(function (item) { return item.name.toLowerCase() === qualifier; })[0];
      if (!source) return null;
      procedures.forEach(function (procedure) {
        if (procedure.moduleName.toLowerCase() !== qualifier) return;
        if (!source.current && (procedure.access === "private" || procedure.access === "friend")) return;
        candidates.push(procedureCandidate(procedure));
      });
    } else {
      VBA_WORDS.forEach(function (word) {
        candidates.push({ text: value(word), displayText: value(word), className: "rn-vba-hint-keyword", matchText: value(word), rank: 3 });
      });
      sources.forEach(function (source) {
        if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(source.name)) return;
        candidates.push({ text: source.name, displayText: source.name + "  · module", className: "rn-vba-hint-module", matchText: source.name, rank: 1 });
      });
      procedures.forEach(function (procedure) {
        if (!procedure.current && (procedure.access === "private" || procedure.access === "friend")) return;
        candidates.push(procedureCandidate(procedure));
      });
    }

    var seen = {};
    var list = candidates.filter(function (candidate) {
      if (candidate.matchText.toLowerCase().indexOf(prefix) !== 0) return false;
      var key = candidate.text.toLowerCase() + "|" + candidate.className;
      if (seen[key]) return false;
      seen[key] = true;
      return true;
    }).sort(function (left, right) {
      if (left.rank !== right.rank) return left.rank - right.rank;
      return left.displayText.localeCompare(right.displayText);
    }).slice(0, MAX_HINTS).map(function (candidate) {
      return { text: candidate.text, displayText: candidate.displayText, className: candidate.className };
    });

    return list.length ? { list: list, from: context.from, to: context.to } : null;
  }

  function register(CodeMirror) {
    if (!CodeMirror || typeof CodeMirror.registerHelper !== "function") return false;
    if (CodeMirror.__rnVbaCompletionRegistered) return true;
    CodeMirror.registerHelper("hint", "vb", createHintResult);
    CodeMirror.__rnVbaCompletionRegistered = true;
    return true;
  }

  root.RNAssistantVbaCompletion = {
    collectSources: collectSources,
    parseProcedures: parseProcedures,
    createHintResult: createHintResult,
    register: register
  };
  register(root.CodeMirror);
}(window));
