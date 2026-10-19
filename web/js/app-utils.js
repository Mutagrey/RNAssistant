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

  window.copyTextResult = function (text) {
    text = String(text === null || text === undefined ? "" : text);
    if (navigator.clipboard && navigator.clipboard.writeText) {
      try {
        return Promise.resolve(navigator.clipboard.writeText(text));
      } catch (error) {
        return Promise.reject(error);
      }
    }
    return new Promise(function (resolve, reject) {
      var input = document.createElement("textarea");
      input.value = text;
      input.setAttribute("aria-hidden", "true");
      document.body.appendChild(input);
      input.select();
      try {
        if (!document.execCommand("copy")) throw new Error("Clipboard command was rejected.");
        resolve();
      } catch (error) {
        reject(error);
      } finally {
        document.body.removeChild(input);
      }
    });
  };

  window.copyText = function (text) {
    window.copyTextResult(text).catch(function () {});
  };

  window.iconSvg = function (name) {
    return window.RNAssistantIcons && window.RNAssistantIcons.has(name) ? window.RNAssistantIcons.render(name) : "";
  };

  window.smallIconButton = function (title, icon, onClick) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "message-action icon-btn";
    button.title = title;
    button.setAttribute("aria-label", title);
    button.innerHTML = iconSvg(icon);
    button.addEventListener("click", onClick);
    return button;
  };
}());
