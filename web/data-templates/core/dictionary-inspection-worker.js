"use strict";

importScripts("selectors.js", "snapshot.js", "json.js", "template.js");
self.onmessage = event => {
  try {
    const value = RNTabularJson.parse(event.data);
    RNTabularTemplate.openDictionaries(value);
    self.postMessage({ type: "result", inspection: { revision: value.revision } });
  } catch (error) {
    self.postMessage({ type: "error", error: { code: error.code || "DICTIONARY_INVALID",
      path: error.path || "", message: error.message || "Invalid dictionary." } });
  }
};
