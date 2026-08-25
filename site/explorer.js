// The captures and their text are in the HTML, so a reader without JavaScript gets all eight
// stacked with their explanations — the same content, just not one at a time. This only adds the
// selection behaviour: it reveals the sidebar and shows one pane.
(function () {
  var nav = document.querySelector("[data-tabs]");
  var body = document.getElementById("panels");
  if (!nav || !body) return;

  var tabs = Array.prototype.slice.call(nav.querySelectorAll('[role="tab"]'));
  var panes = Array.prototype.slice.call(body.querySelectorAll('[role="tabpanel"]'));
  if (tabs.length !== panes.length || !tabs.length) return;

  document.documentElement.classList.add("js");

  function show(n) {
    tabs.forEach(function (tab, i) {
      var on = i === n;
      tab.setAttribute("aria-selected", on ? "true" : "false");
      tab.tabIndex = on ? 0 : -1;
      panes[i].hidden = !on;
    });
  }

  tabs.forEach(function (tab, i) {
    tab.addEventListener("click", function () { show(i); });
    tab.addEventListener("keydown", function (e) {
      var n = e.key === "ArrowDown" || e.key === "ArrowRight" ? i + 1
            : e.key === "ArrowUp" || e.key === "ArrowLeft" ? i - 1
            : e.key === "Home" ? 0
            : e.key === "End" ? tabs.length - 1
            : -1;
      if (n < 0 || n >= tabs.length) return;
      e.preventDefault();
      show(n);
      tabs[n].focus();
    });
  });

  show(0);
}());
