// Compatibility commands for selected-surface callers. Edit state belongs to each composer.
function officeMessageEdit() { return chatComposer().edit(officeComposerTarget()); }
function resetMessageEditState(options) { return chatComposer().cancelEdit(officeComposerTarget(), options); }
function hasActiveMessageEdit() { return !!officeMessageEdit(); }
function normalizeEditedMessageText(text) { return String(text || "").replace(/\r\n?/g, "\n").trim(); }
function startMessageEdit(message, index) { return chatComposer().dispatch("messageAction", { name: "edit", message: message, index: index }); }
function cancelMessageEdit() { return chatComposer().dispatch("cancelEdit"); }
function saveMessageEdit() { return chatComposer().dispatch("submit"); }
