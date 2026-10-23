// Chat helpers: delegated handlers for code-block buttons, auto-scroll, and jump-to-latest.
window.BeaconChat = {
  copyCode: function (btn) {
    var box = btn.closest(".beacon-code");
    var pre = box && box.querySelector("pre");
    if (!pre)
      return;
    var mark = function (ok) {
      btn.classList.remove("beacon-code-copied");
      btn.classList.remove("beacon-code-failed");
      var cls = ok ? "beacon-code-copied" : "beacon-code-failed";
      btn.classList.add(cls);
      setTimeout(function () { btn.classList.remove(cls); }, 1500);
    };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(pre.innerText).then(
        function () { mark(true); },
        function () { mark(false); });
      return;
    }
    // Fallback for non-secure origins and browsers without the async clipboard API.
    try {
      var area = document.createElement("textarea");
      area.value = pre.innerText;
      area.style.position = "fixed";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.select();
      var ok = document.execCommand("copy");
      document.body.removeChild(area);
      mark(ok);
    } catch (err) {
      mark(false);
    }
  },
  toggleCode: function (btn) {
    var box = btn.closest(".beacon-code");
    if (!box)
      return;
    var collapsed = box.classList.toggle("beacon-code-collapsed");
    var expand = btn.getAttribute("data-beacon-code-expand");
    var collapse = btn.getAttribute("data-beacon-code-collapse-label");
    if (expand && collapse)
      btn.setAttribute("aria-label", collapsed ? expand : collapse);
  },
  // One atomic check-and-snap: the proximity test and the scroll happen in a single JS
  // call so a user scroll cannot land between them. Returns whether the log was already
  // near the latest part.
  followLatest: function (el) {
    if (!el)
      return true;
    var near = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    if (near)
      el.scrollTop = el.scrollHeight;
    return near;
  },
  jumpToLatest: function (el) {
    if (el)
      el.scrollTop = el.scrollHeight;
  }
};

document.addEventListener("click", function (e) {
  var copy = e.target.closest("[data-beacon-code-copy]");
  if (copy) {
    window.BeaconChat.copyCode(copy);
    return;
  }
  var collapse = e.target.closest("[data-beacon-code-collapse]");
  if (collapse)
    window.BeaconChat.toggleCode(collapse);
});