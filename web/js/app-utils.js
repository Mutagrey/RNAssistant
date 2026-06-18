(function () {
  function objectValue(source, pascal, camel, fallback) {
    source = source || {};
    return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
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

  window.messageTotalTokens = function (message) {
    return objectValue(message, "TotalTokens", "totalTokens", null);
  };

  window.messagePromptTokens = function (message) {
    return objectValue(message, "PromptTokens", "promptTokens", null);
  };

  window.messageCompletionTokens = function (message) {
    return objectValue(message, "CompletionTokens", "completionTokens", null);
  };

  window.chatId = function (chat) {
    return objectValue(chat, "Id", "id", "");
  };

  window.chatTitle = function (chat) {
    return objectValue(chat, "Title", "title", "New chat") || "New chat";
  };

  window.chatMessageCount = function (chat) {
    return Number(objectValue(chat, "MessageCount", "messageCount", 0) || 0);
  };

  window.chatModel = function (chat) {
    return objectValue(chat, "Model", "model", "") || "";
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
}());
