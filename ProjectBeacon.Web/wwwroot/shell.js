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
