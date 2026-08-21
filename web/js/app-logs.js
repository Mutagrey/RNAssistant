function bindLogActions() {
  var clear = $("clearLogButton");
  if (!clear) return;
  clear.addEventListener("click", function () {
    $("logBox").textContent = "";
  });
}
