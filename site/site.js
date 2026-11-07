// Progressive polish shared by every page: a copy button on each code block, the nav link for the
// current page marked, a shadow under the nav once the page scrolls beneath it, and the theme toggle.
//
// NOTHING HERE IS LOAD-BEARING. Every page is complete without this file: the code is selectable,
// the nav still navigates, and the shadow is decoration. It is loaded with `defer` for the same
// reason — nothing below depends on it having run.
(function () {
  "use strict";

  // ---- Copy buttons -------------------------------------------------------------------------
  //
  // ON THE <pre>, NOT INSIDE IT. A button inside the <pre> becomes part of what a manual selection
  // drags over, so the reader who ignores the button ends up copying the word "Copy" along with
  // the command.
  var blocks = document.querySelectorAll("pre > code");
  Array.prototype.forEach.call(blocks, function (code) {
    var pre = code.parentNode;
    pre.classList.add("has-copy");

    var button = document.createElement("button");
    button.type = "button";
    button.className = "copy-button";
    button.textContent = "Copy";
    // The command is already visible; the button is redundant for a screen reader unless it says
    // what it copies.
    button.setAttribute("aria-label", "Copy this command to the clipboard");

    button.addEventListener("click", function () {
      var text = code.textContent;
      var done = function (ok) {
        button.textContent = ok ? "Copied" : "Press ⌘C";
        button.classList.toggle("copied", ok);
        setTimeout(function () {
          button.textContent = "Copy";
          button.classList.remove("copied");
        }, 1600);
      };

      // navigator.clipboard IS NOT ALWAYS THERE: it needs a secure context, so a page opened from
      // disk during development has no clipboard API at all. Failing silently would leave a button
      // that looks broken; saying which keys to press is a real answer.
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(function () { done(true); },
                                                 function () { done(false); });
      } else {
        done(false);
      }
    });

    pre.appendChild(button);
  });

  // ---- Which page am I on -------------------------------------------------------------------
  //
  // COMPARED BY RESOLVED PATH, not by the href attribute: the links are relative, the site is
  // served from a project subpath, and "docs/" and "docs/index.html" are the same page.
  var here = location.pathname.replace(/index\.html$/, "");
  var links = document.querySelectorAll(".site-nav a");
  Array.prototype.forEach.call(links, function (a) {
    var target = new URL(a.getAttribute("href"), location.href);
    if (target.origin !== location.origin) return;
    if (target.pathname.replace(/index\.html$/, "") === here) {
      a.setAttribute("aria-current", "page");
    }
  });

  // ---- Nav shadow once content slides under it ----------------------------------------------
  var nav = document.querySelector(".site-nav");
  if (nav) {
    var sync = function () { nav.classList.toggle("scrolled", window.scrollY > 4); };
    // passive: this listener never calls preventDefault, and saying so keeps scrolling smooth.
    window.addEventListener("scroll", sync, { passive: true });
    sync();
  }

  // ---- Theme: System, Light, Dark -------------------------------------------------------------
  //
  // SYSTEM IS THE ABSENCE OF A CHOICE, not a third theme: no data-theme on <html>, so the
  // stylesheet's media query decides, exactly as it does with script off. The inline script in each
  // page's <head> applies a stored choice before first paint; this adds the button and changes it.
  //
  // localStorage CAN THROW (a private window, storage disabled), so every touch is guarded and a
  // failure leaves the toggle working for this page view and simply not remembered.
  if (nav) {
    var KEY = "cxagent-theme";
    var ORDER = ["system", "light", "dark"];
    var ICONS = {
      system: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="8" fill="none" stroke="currentColor" stroke-width="2"/><path d="M12 4a8 8 0 0 1 0 16z" fill="currentColor"/></svg>',
      light: '<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>',
      dark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z" fill="currentColor"/></svg>'
    };
    var LABELS = { system: "Theme: system", light: "Theme: light", dark: "Theme: dark" };
    // THE BROWSER BAR FOLLOWS THE CHOICE TOO. The theme-color metas carry media queries for the
    // system setting; a chosen theme overrides both so the bar matches the page, not the OS.
    var metas = document.querySelectorAll('meta[name="theme-color"]');
    var original = Array.prototype.map.call(metas, function (m) { return m.content; });
    var BAR = { light: "#ffffff", dark: "#080a0c" };

    var current = function () {
      var t = document.documentElement.dataset.theme;
      return t === "light" || t === "dark" ? t : "system";
    };
    var toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "theme-toggle";
    var apply = function (mode) {
      if (mode === "system") delete document.documentElement.dataset.theme;
      else document.documentElement.dataset.theme = mode;
      Array.prototype.forEach.call(metas, function (m, i) {
        m.content = mode === "system" ? original[i] : BAR[mode];
      });
      toggle.innerHTML = ICONS[mode];
      toggle.title = LABELS[mode];
      toggle.setAttribute("aria-label", LABELS[mode] + ". Change theme");
    };
    toggle.addEventListener("click", function () {
      var next = ORDER[(ORDER.indexOf(current()) + 1) % ORDER.length];
      apply(next);
      try {
        if (next === "system") localStorage.removeItem(KEY);
        else localStorage.setItem(KEY, next);
      } catch (e) { /* not remembered; still applied */ }
    });
    apply(current());
    nav.appendChild(toggle);
  }

  // ---- The version, from the catalog ---------------------------------------------------------
  //
  // THE CATALOG IS REGENERATED BY EVERY RELEASE, so the version it carries is the one that shipped.
  // A number written into the page would be stale the moment a release went out, and nothing would
  // fail to say so. Absent or unreachable, the line simply does not appear.
  //
  // cxagentVersion, NEVER A PLUGIN'S VERSION. A plugin that did not change keeps its own number
  // across a release, so plugins[0].version drifts behind the app the first time one is carried
  // forward — which is exactly what it is meant to do, and exactly why it cannot answer this.
  var versionSlot = document.getElementById("version");
  if (versionSlot) {
    fetch("catalog.json", { cache: "no-cache" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (catalog) {
        var version = catalog && catalog.cxagentVersion;
        if (!version) return;
        // textContent, not innerHTML: the anchor and its href are already in the markup, so this
        // only fills the label -- and a value from a fetched file never becomes markup.
        versionSlot.textContent = "v" + version + " — what changed →";
        versionSlot.hidden = false;
        // AND EVERY OTHER PLACE THE PAGE QUOTES IT — the first minute's install line — from the same fetch.
        document.querySelectorAll("[data-cxagent-version]").forEach(function (el) { el.textContent = "v" + version; });
      })
      .catch(function () { /* a footer line is not worth an error message */ });
  }
}());
