// Chat helpers: delegated handlers for code-block buttons, auto-scroll, and jump-to-latest.
window.BeaconChat = {
  copyCode: function (btn) {
    var box = btn.closest(".beacon-code");
    var pre = box && box.querySelector("pre");
    if (!pre || !navigator.clipboard)
      return;
    navigator.clipboard.writeText(pre.innerText).then(function () {
      btn.classList.add("beacon-code-copied");
      setTimeout(function () { btn.classList.remove("beacon-code-copied"); }, 1500);
    });
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
  isNearBottom: function (el) {
    if (!el)
      return true;
    return el.scrollHeight - el.scrollTop - el.clientHeight < 80;
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
