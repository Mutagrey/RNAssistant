(function () {
  function objectValue(source, pascal, camel, fallback) {
    source = source || {};
    return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
  }

  function nonNegativeNumberValue(source, pascal, camel) {
    var value = Number(objectValue(source, pascal, camel, 0) || 0);
    return isFinite(value) && value > 0 ? value : 0;
  }

  window.messageId = function (message) {
    return objectValue(message, "Id", "id", "");
  };

  window.messageRole = function (message) {
    return objectValue(message, "Role", "role", "assistant") || "assistant";
  };

  window.messageContent = function (message) {
    return objectValue(message, "Content", "content", "") || "";
  };

  window.messageAttachments = function (message) {
    return objectValue(message, "Attachments", "attachments", []) || [];
  };

  window.messageActivity = function (message) {
    return objectValue(message, "Activity", "activity", null);
  };

  window.messageProtocolMessage = function (message) {
    return !!objectValue(message, "ProtocolMessage", "protocolMessage", false);
  };

  window.messageResponseProtocolVersion = function (message) {
    return Number(objectValue(message, "ResponseProtocolVersion", "responseProtocolVersion", 0) || 0);
  };

  window.messageResponseStatus = function (message) {
    if (window.messageResponseProtocolVersion(message) < 2) return "";
    return String(objectValue(message, "ResponseStatus", "responseStatus", "") || "").toLowerCase();
  };

  window.messageTotalTokens = function (message) {
    return objectValue(message, "TotalTokens", "totalTokens", null);
  };

  window.messagePromptTokens = function (message) {
    return objectValue(message, "PromptTokens", "promptTokens", null);
  };

  window.messageCompletionTokens = function (message) {
    return objectValue(message, "CompletionTokens", "completionTokens", null);
  };

  window.messageCreatedUtc = function (message) {
    return objectValue(message, "CreatedUtc", "createdUtc", "");
  };

  window.chatId = function (chat) {
    return objectValue(chat, "Id", "id", "");
  };

  window.chatTitle = function (chat) {
    return objectValue(chat, "Title", "title", "Новый чат") || "Новый чат";
  };

  window.chatMessageCount = function (chat) {
    return Number(objectValue(chat, "MessageCount", "messageCount", 0) || 0);
  };

  window.chatJsonlByteLength = function (chat) {
    return nonNegativeNumberValue(chat, "JsonlByteLength", "jsonlByteLength");
  };

  window.chatCasBlobCount = function (chat) {
    return nonNegativeNumberValue(chat, "CasBlobCount", "casBlobCount");
  };

  window.chatCasLogicalByteLength = function (chat) {
    return nonNegativeNumberValue(chat, "CasLogicalByteLength", "casLogicalByteLength");
  };

  window.chatCasStoredByteLength = function (chat) {
    return nonNegativeNumberValue(chat, "CasStoredByteLength", "casStoredByteLength");
  };

  window.chatCasMissingBlobCount = function (chat) {
    return nonNegativeNumberValue(chat, "CasMissingBlobCount", "casMissingBlobCount");
  };

  window.chatCasReferenceIssueCount = function (chat) {
    return nonNegativeNumberValue(chat, "CasReferenceIssueCount", "casReferenceIssueCount");
  };

  window.chatStorageWarningLevel = function (chat) {
    var value = String(objectValue(chat, "StorageWarningLevel", "storageWarningLevel", "none") || "none").toLowerCase();
    return value === "warning" || value === "critical" ? value : "none";
  };

  window.chatModel = function (chat) {
    return objectValue(chat, "Model", "model", "") || "";
  };

  window.chatMode = function (chat) {
    return objectValue(chat, "Mode", "mode", "agent") || "agent";
  };

  window.chatHost = function (chat) {
    return objectValue(chat, "Host", "host", "") || "";
  };

  window.chatDocumentKey = function (chat) {
    return objectValue(chat, "DocumentKey", "documentKey", "") || "";
  };

  window.chatDocumentTitle = function (chat) {
    return objectValue(chat, "DocumentTitle", "documentTitle", "Документ") || "Документ";
  };

  window.chatDocumentPath = function (chat) {
    return objectValue(chat, "DocumentPath", "documentPath", "") || "";
  };

  window.chatIsCurrentDocument = function (chat) {
    return !!objectValue(chat, "IsCurrentDocument", "isCurrentDocument", false);
  };

  window.detectCodeLanguage = function (code) {
    var classes = (code.className || "").split(/\s+/);
    for (var i = 0; i < classes.length; i++) {
      if (classes[i].indexOf("language-") === 0) {
        return classes[i].substring("language-".length);
      }
      if (classes[i].indexOf("lang-") === 0) {
        return classes[i].substring("lang-".length);
      }
    }
    return "";
  };

  window.normalizeCodeLanguage = function (language) {
    var value = (language || "").toLowerCase();
    var aliases = {
      "c#": "csharp",
      "cs": "csharp",
      "js": "javascript",
      "ts": "typescript",
      "py": "python",
      "ps": "powershell",
      "ps1": "powershell",
      "vb": "vbnet",
      "vba": "vbnet"
    };
    return aliases[value] || value;
  };

  window.headersToText = function (headers) {
    return Object.keys(headers || {}).map(function (key) {
      return key + ": " + headers[key];
    }).join("\n");
  };

  window.textToHeaders = function (text) {
    var headers = {};
    (text || "").split(/\r?\n/).forEach(function (line) {
      var index = line.indexOf(":");
      if (index > 0) {
        headers[line.slice(0, index).trim()] = line.slice(index + 1).trim();
      }
    });
    return headers;
  };

  window.formatNumber = function (value) {
    value = Number(value || 0);
    return value.toLocaleString ? value.toLocaleString() : String(value);
  };

  window.copyText = function (text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text);
      return;
    }

    var input = document.createElement("textarea");
    input.value = text;
    document.body.appendChild(input);
    input.select();
    document.execCommand("copy");
    document.body.removeChild(input);
  };

  window.iconSvg = function (name) {
    var icons = {
      copy: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"9\" y=\"9\" width=\"13\" height=\"13\" rx=\"2\"/><path d=\"M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1\"/></svg>",
      edit: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M12 20h9\"/><path d=\"M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z\"/></svg>",
      trash: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 6h18\"/><path d=\"M8 6V4h8v2\"/><path d=\"M19 6l-1 14H6L5 6\"/><path d=\"M10 11v5\"/><path d=\"M14 11v5\"/></svg>",
      branch: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M4 7h3c3 0 4 2 5 5s2 5 5 5h3\"/><path d=\"m17 14 3 3-3 3\"/><path d=\"M4 17h3c2.2 0 3.4-1.1 4.3-3.1\"/><path d=\"M12.7 10.1C13.6 8.1 14.8 7 17 7h3\"/><path d=\"m17 4 3 3-3 3\"/></svg>",
      retry: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M20 6v5h-5\"/><path d=\"M4 18v-5h5\"/><path d=\"M6.1 9A7 7 0 0 1 18.2 6.8L20 11\"/><path d=\"M17.9 15A7 7 0 0 1 5.8 17.2L4 13\"/></svg>",
      eye: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M2 12s3.5-6 10-6 10 6 10 6-3.5 6-10 6-10-6-10-6Z\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/></svg>",
      eyeOff: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 3l18 18\"/><path d=\"M10.6 10.6A3 3 0 0 0 13.4 13.4\"/><path d=\"M9.9 5.2A10.8 10.8 0 0 1 12 5c6.5 0 10 7 10 7a17.9 17.9 0 0 1-3.2 4.2\"/><path d=\"M6.1 6.6C3.4 8.4 2 12 2 12s3.5 7 10 7a10.6 10.6 0 0 0 4.1-.8\"/></svg>"
    };
    return icons[name] || "";
  };

  window.smallIconButton = function (title, icon, onClick) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "message-action";
    button.title = title;
    button.setAttribute("aria-label", title);
    button.innerHTML = iconSvg(icon);
    button.addEventListener("click", onClick);
    return button;
  };
}());
