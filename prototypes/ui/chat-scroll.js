(function () {
  "use strict";
  var P = window.Prototype, positions = {}, observer, restoring = false;
  function scroller() { return document.querySelector(".conversation-scroll"); }
  function position(id) { return positions[id] || (positions[id] = { top: 0, follow: true, pending: false }); }
  function nearBottom(box) { return box.scrollHeight - box.clientHeight - box.scrollTop < 40; }
  function updateButton(box) {
    var button = document.getElementById("chat-scroll-bottom"); if (!button) return;
    var p = position(box.dataset.chat);
    button.hidden = nearBottom(box); button.classList.toggle("has-new", p.pending);
    button.querySelector("span").textContent = p.pending ? "Новые сообщения" : "";
    button.title = p.pending ? "Новые сообщения · перейти вниз" : "К последним сообщениям";
    button.setAttribute("aria-label", button.title);
  }
  P.chatScroll = {
    capture: function () {
      var box = scroller(); if (!box) return;
      var p = position(box.dataset.chat); p.top = box.scrollTop; p.follow = p.jumping || nearBottom(box); p.anchor = null;
      if (!p.follow) {
        var top = box.getBoundingClientRect().top;
        var anchor = Array.from(box.querySelectorAll("[data-scroll-key]")).find(function (el) { var r = el.getBoundingClientRect(); return r.height && r.bottom > top + 1; });
        if (anchor) p.anchor = { key: anchor.dataset.scrollKey, offset: anchor.getBoundingClientRect().top - top };
      }
    },
    restore: function (newContent) {
      var box = scroller(); if (!box) return;
      var p = position(box.dataset.chat);
      if (newContent && !p.follow) p.pending = true;
      restoring = true; box.scrollTop = p.follow ? box.scrollHeight : p.top;
      if (!p.follow && p.anchor) {
        var anchor = Array.from(box.querySelectorAll("[data-scroll-key]")).find(function (el) { return el.dataset.scrollKey === p.anchor.key; });
        if (anchor) anchor = anchor.closest('.run-history:not([open])') || anchor;
        if (anchor) box.scrollTop += anchor.getBoundingClientRect().top - box.getBoundingClientRect().top - p.anchor.offset;
      }
      p.width = box.clientWidth; p.height = box.clientHeight; restoring = false;
      updateButton(box);
      if (observer) observer.disconnect();
      observer = new ResizeObserver(function () { if (!box.isConnected) return; if (p.follow) box.scrollTop = box.scrollHeight; p.width = box.clientWidth; p.height = box.clientHeight; updateButton(box); });
      observer.observe(box); var transcript = box.querySelector(".transcript"); if (transcript) observer.observe(transcript);
    },
    bottom: function () { var box = scroller(); if (!box) return; var p = position(box.dataset.chat); p.follow = true; p.jumping = true; p.pending = false; box.scrollTo({ top: box.scrollHeight, behavior: P.state.motion === "reduced" || matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" }); updateButton(box); },
    hold: function () { var box = scroller(); if (box) { var p = position(box.dataset.chat); p.follow = false; p.jumping = false; } },
    follows: function (id) { return position(id).follow; },
    reset: function (id) { delete positions[id]; }
  };
  document.addEventListener("wheel", function (event) { var box = event.target.closest && event.target.closest(".conversation-scroll"); if (box) position(box.dataset.chat).jumping = false; }, { passive: true });
  document.addEventListener("keydown", function (event) { var box = event.target.closest && event.target.closest(".conversation-scroll"); if (box && ["ArrowUp", "PageUp", "Home"].includes(event.key)) position(box.dataset.chat).jumping = false; });
  document.addEventListener("scroll", function (event) {
    var box = event.target;
    if (!box.classList || !box.classList.contains("conversation-scroll") || restoring) return;
    var p = position(box.dataset.chat);
    if (p.width !== box.clientWidth || p.height !== box.clientHeight) return;
    p.top = box.scrollTop; p.follow = p.jumping || nearBottom(box);
    if (nearBottom(box)) p.jumping = false;
    if (p.follow) { p.pending = false; var c = P.state.chats[box.dataset.chat]; if (c && c.unread) { c.unread = false; if (P.refreshSidebar) P.refreshSidebar(); } }
    updateButton(box);
  }, true);
}());
