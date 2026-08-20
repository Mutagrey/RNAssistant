function contextNotes() {
  var context = state.context || {};
  return (context.Notes || context.notes || []).filter(function (note) { return !!note; });
}

function noteValue(note, pascal, camel, fallback) {
  note = note || {};
  return note[pascal] || note[camel] || fallback || "";
}

function noteTitle(note) {
  return noteValue(note, "Title", "title", noteValue(note, "Source", "source", "Context"));
}

function noteReference(note) {
  return noteValue(note, "Reference", "reference", noteValue(note, "Source", "source", ""));
}

function notePreview(note) {
  return noteValue(note, "Preview", "preview", noteValue(note, "Text", "text", ""));
}

function noteText(note) {
  return noteValue(note, "Text", "text", notePreview(note));
}

function noteKind(note) {
  return noteValue(note, "Kind", "kind", "context");
}

function noteDetails(note) {
  return noteValue(note, "DetailsJson", "detailsJson", "");
}

function noteHost(note) {
  return noteValue(note, "Host", "host", state.host || "");
}

function noteId(note) {
  return noteValue(note, "Id", "id", "");
}

function hostBadge(note) {
  var host = noteHost(note).toLowerCase();
  if (host.indexOf("excel") >= 0) {
    return "XL";
  }
  if (host.indexOf("word") >= 0) {
    return "W";
  }
  if (host.indexOf("powerpoint") >= 0) {
    return "PPT";
  }
  if (host.indexOf("outlook") >= 0) {
    return "Mail";
  }
  return "Ctx";
}
