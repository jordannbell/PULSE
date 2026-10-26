// Light, dark or automatic (follows the system). Runs before the page is painted, so there is no flash.
(function () {
  var KEY = "pulse-theme";
  var ORDER = ["auto", "light", "dark"];

  function read() {
    try { return localStorage.getItem(KEY) || "auto"; } catch (e) { return "auto"; }
  }

  function apply(mode) {
    var root = document.documentElement;
    if (mode === "auto") { root.removeAttribute("data-theme"); } else { root.setAttribute("data-theme", mode); }
    root.setAttribute("data-theme-mode", mode);
  }

  apply(read());

  // One listener on the document: it keeps working when the page content is swapped by navigation.
  document.addEventListener("click", function (event) {
    var button = event.target.closest("[data-theme-toggle]");
    if (!button) { return; }
    var next = ORDER[(ORDER.indexOf(read()) + 1) % ORDER.length];
    try { localStorage.setItem(KEY, next); } catch (e) { /* private mode: the choice lasts until reload */ }
    apply(next);
  });
})();
