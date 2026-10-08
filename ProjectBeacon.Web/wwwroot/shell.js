window.beaconShell = {
  listen: function (ref) {
    this.unlisten();
    this._handler = function (e) {
      var key = (e.key || "").toLowerCase();
      if ((e.ctrlKey || e.metaKey) && key === "k") {
        e.preventDefault();
        ref.invokeMethodAsync("ToggleCommand");
      } else if (key === "escape") {
        ref.invokeMethodAsync("CloseCommand");
      }
    };
    document.addEventListener("keydown", this._handler);
  },
  unlisten: function () {
    if (this._handler) {
      document.removeEventListener("keydown", this._handler);
      this._handler = null;
    }
  }
};

window.beaconTheme = {
  persist: function (value) {
    document.cookie = "beacon-theme=" + encodeURIComponent(value) + ";path=/;max-age=31536000;samesite=lax";
  },
  read: function () {
    var match = document.cookie.match(/(?:^|; )beacon-theme=([^;]*)/);
    return match ? decodeURIComponent(match[1]) : "";
  },
  apply: function (isDark) {
    var root = document.documentElement;
    root.classList.toggle("beacon-theme-dark", !!isDark);
    root.classList.toggle("beacon-theme-light", !isDark);
  },
  systemDark: function () {
    return window.matchMedia("(prefers-color-scheme: dark)").matches;
  },
  watchSystem: function (ref) {
    this.unwatchSystem();
    var mq = window.matchMedia("(prefers-color-scheme: dark)");
    this._mq = mq;
    this._themeHandler = function (e) {
      ref.invokeMethodAsync("SystemThemeChanged", e.matches);
    };
    if (mq.addEventListener)
      mq.addEventListener("change", this._themeHandler);
    else if (mq.addListener)
      mq.addListener(this._themeHandler);
  },
  unwatchSystem: function () {
    if (this._mq && this._themeHandler) {
      if (this._mq.removeEventListener)
        this._mq.removeEventListener("change", this._themeHandler);
      else if (this._mq.removeListener)
        this._mq.removeListener(this._themeHandler);
    }
    this._mq = null;
    this._themeHandler = null;
  }
};
