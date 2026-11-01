// Composer entry to the single context-usage presentation in the chat inspector.
function contextDraftResourceIds() {
  return (state.draftAttachments || []).map(function (item) { return attachmentId(item); }).filter(Boolean);
}
function hideContextMeterTooltip() { if ($("contextMeterTooltip")) $("contextMeterTooltip").hidden = true; }
function renderContextMeterAvailability() {
  $("contextMeter").disabled = hasActiveMessageEdit() || state.bridgeUnavailable || !state.activeChatId;
  if ($("contextMeter").disabled) hideContextMeterTooltip();
}
function bindContextMeterActions() {
  bindComposerTooltip($("contextMeterWrap"), $("contextMeter"), $("contextMeterTooltip"), function () { return true; });
  $("contextMeter").addEventListener("click", function () { hideContextMeterTooltip(); RNAssistantChatInspector.openUsage(); });
}
