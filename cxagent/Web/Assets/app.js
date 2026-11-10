// cxagent web — the page. One WebSocket carries everything; every event is numbered, so a dropped connection comes back
// with "since" and loses nothing. Model and tool text reaches the page only as textContent, or through render(), which
// escapes before it formats.

import { render } from "./markdown.js";

const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined && text !== null) e.textContent = text;
  return e;
};

const state = {
  socket: null,
  backoff: 1000,
  commands: [],
  rows: [],
  views: new Map(),     // id → view: a session by its id, a worker as "sessionId~agentId"
  active: null,
  prompts: new Map(),   // promptId → { message, answeredHere }
  panels: new Map(),    // sessionId → the last panel the server sent
  pane: "session",
};

// ---- theme ------------------------------------------------------------------------------------------------------

function stored(key) { try { return localStorage.getItem(key); } catch { return null; } }
function store(key, value) { try { localStorage.setItem(key, value); } catch { /* a private window keeps it for this visit */ } }

// THE THEME IS AN ATTRIBUTE, NOT A MEDIA QUERY ALONE: the code themes and the page's tokens both key off data-mode, so
// one toggle switches both, and with no choice stored it follows the system.
const system = window.matchMedia("(prefers-color-scheme: dark)");
function applyTheme() {
  document.documentElement.dataset.mode = stored("cxagent-theme") || (system.matches ? "dark" : "light");
}
applyTheme();
system.addEventListener("change", applyTheme);
$("theme").addEventListener("click", () => {
  store("cxagent-theme", document.documentElement.dataset.mode === "dark" ? "light" : "dark");
  applyTheme();
});

// ---- the side panel's place -------------------------------------------------------------------------------------

// ON A DESKTOP THE PANEL IS A COLUMN, open unless closed (and remembered); narrower, it slides over and starts shut.
const wide = () => window.matchMedia("(min-width: 1100px)").matches;
const narrow = () => window.matchMedia("(max-width: 699px)").matches;
// THE APP'S OWN PAGES, in every session's strip: config, the colleagues library and the marketplace are not one
// session's, and tied to the one they were opened from, a switch left them open with no tab to reach them by.
const APP_PAGES = ["config", "plugins", "colleagues"];

// A WINDOW WIDENED OR NARROWED PAST THE LINE swaps the strip between one session's tabs and every open view.
window.matchMedia("(min-width: 1100px)").addEventListener("change", () => drawTabs());
function setPanel(open) {
  if (wide()) {
    document.body.classList.toggle("no-panel", !open);
    store("cxagent-panel", open ? "1" : "0");
    drawPanel();
  } else {
    document.body.classList.toggle("show-panel", open);
  }
}
if (stored("cxagent-panel") === "0") document.body.classList.add("no-panel");
$("panel-toggle").addEventListener("click", () =>
  setPanel(wide() ? document.body.classList.contains("no-panel") : !document.body.classList.contains("show-panel")));
$("phone-panel").addEventListener("click", () => setPanel(true));
$("panel-close").addEventListener("click", () => setPanel(false));
$("scrim").addEventListener("click", () => document.body.classList.remove("drawer", "show-panel"));
for (const tab of document.querySelectorAll(".ptab"))
  tab.addEventListener("click", () => { state.pane = tab.dataset.pane; drawPanel(); });

// ---- connection -------------------------------------------------------------------------------------------------

function connect() {
  const scheme = location.protocol === "https:" ? "wss:" : "ws:";
  const socket = new WebSocket(scheme + "//" + location.host + "/ws");
  state.socket = socket;
  socket.addEventListener("open", () => {
    state.backoff = 1000;
    $("conn").classList.remove("down");
    $("conn").title = "Connected";
    // CARRY ON WHERE EACH VIEW LEFT OFF. Prompts go first: whatever was answered while away is gone, and the server
    // sends again what still waits.
    for (const [pid, p] of state.prompts) if (state.views.has(p.message.sessionId)) state.prompts.delete(pid);
    drawPrompts();
    for (const [id, view] of state.views) {
      if (view.kind === "worker") send({ type: "openWorker", sessionId: view.parent, agentId: view.agentId, since: view.seq });
      else if (view.kind === "command") send({ type: "openCommand", sessionId: view.parent, pid: view.pid });
      else if (view.kind === "file") send({ type: "openFile", sessionId: view.parent, path: view.path });
      // A SHELL DOES NOT SURVIVE ITS CONNECTION: the server ended it when the socket went, as closing the tab would.
      else if (view.kind === "shell") shellEnded(view, null, "the connection dropped, so the shell ended");
      // NOT SESSIONS, SO NEVER "open": the config page asks for its tab again — an edited config.json keeps its edits,
      // which the next save checks against the file — and the marketplace for its rows.
      else if (view.kind === "config") {
        if (view.tab !== "json") send({ type: "configForm", sessionId: configSession(), tab: view.tab, item: view.item });
        else if (!view.dirty) send({ type: "getConfig" });
      }
      else if (view.kind === "plugins") { if (view.parent) send({ type: "plugins", sessionId: view.parent }); }
      else send({ type: "open", id, since: view.seq });
    }
  });
  socket.addEventListener("message", (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    handle(m);
  });
  socket.addEventListener("close", () => {
    $("conn").classList.add("down");
    $("conn").title = "Reconnecting…";
    // BACK OFF: 1, 2, 4 … 30 seconds. A login that expired shows the login page on the next load.
    setTimeout(connect, state.backoff);
    state.backoff = Math.min(state.backoff * 2, 30000);
  });
}

function send(message) {
  if (state.socket && state.socket.readyState === WebSocket.OPEN) state.socket.send(JSON.stringify(message));
}

// ---- messages ---------------------------------------------------------------------------------------------------

function handle(m) {
  switch (m.type) {
    case "hello":
      state.access = m.access || null;
      document.querySelector(".logout").hidden = !m.login;
      showBanners(m.banners || []);
      break;
    case "banner": showBanners([m.text]); break;
    case "sessions": state.rows = m.rows || []; drawSessions(); break;
    case "opened": opened(m); break;
    case "openedWorker": openedWorker(m); break;
    case "reset": { const v = state.views.get(m.sessionId); if (v) resetView(v); break; }
    case "event": event(m); break;
    case "panel":
      state.panels.set(m.sessionId, m);
      liveCards(m.sessionId);
      {
        // THE LIST IS SENT WHEN SESSIONS COME AND GO, not when a turn ends; the panel says whether one is working now.
        const row = state.rows.find((r) => r.id === m.sessionId);
        const now = m.session.working ? "working" : "idle";
        if (row && (row.state === "working" || row.state === "idle") && row.state !== now) { row.state = now; drawSessions(); }
      }
      if (activeSessionId() === m.sessionId) { drawPanel(); drawPicks(); drawWorkerHead(); drawStop(); }
      drawTabs();
      break;
    case "prompt": prompt(m); break;
    case "promptResolved": resolved(m.promptId, m.by); break;
    case "notice": {
      const v = state.views.get(m.sessionId);
      if (v) { const n = el("div", "notice"); n.innerHTML = render(m.text); append(v, n); }
      break;
    }
    case "confirmClose": confirmClose(m); break;
    case "confirmDelete": confirmDelete(m); break;
    case "confirmWorkerDelete": confirmWorkerDelete(m); break;
    case "workerDeleted": { const key = m.sessionId + "~" + m.agentId; if (state.views.has(key)) dropView(key); break; }
    case "confirmForget": confirmForget(m); break;
    case "confirmColleagueForget": confirmColleagueForget(m); break;
    case "confirmColleagueDistill": confirmColleagueDistill(m); break;
    case "completions": if (m.n === completeN && m.sessionId === activeSessionId()) completion(m.rows); break;
    case "closed": closed(m.sessionId); break;
    case "gone":
      // NO LONGER THERE — unloaded, or from a server since restarted. Back to the list, where it can be resumed.
      if (state.views.has(m.id)) { closed(m.id); document.body.dataset.view = "list"; }
      else flash("That session is no longer there.");
      break;
    case "folders": drawFolders(m); break;
    case "restore": restore(m); break;
    case "exportForm": exportForm(m); break;
    case "exportPreview": exportPreview(m); break;
    case "export": exported(m); break;
    case "openedCommand": openedCommand(m); break;
    case "openedFile": openedFile(m); break;
    case "openedShell": openedShell(m); break;
    case "config": openedConfig(m); break;
    case "configForm": openedConfigForm(m); break;
    case "configConfirm": configConfirm(m); break;
    case "plugins": openedPlugins(m); break;
    case "colleagues": openedColleagues(m); break;
    case "pluginResult": pluginResult(m); break;
    case "configAsk": configAsk(m); break;
    case "configRefused": configRefused(m); break;
    case "configSaved": configSaved(m); break;
    case "shellOutput": { const v = state.views.get(m.sessionId); if (v && v.term) v.term.write(bytes(m.data)); else if (v) (v.pending = v.pending || []).push(m.data); break; }
    case "shellExited": { const v = state.views.get(m.sessionId); if (v) shellEnded(v, m.exitCode); break; }
    case "fileSaved": fileSaved(m); break;
    case "fileConflict": fileConflict(m); break;
    case "fileChanged": fileChanged(m); break;
    case "commandOutput": commandOutput(m); break;
    case "commandState": commandState(m); break;
    case "error": flash(m.message); break;
  }
}

function showBanners(lines) { $("banners").replaceChildren(...lines.map((l) => el("div", "banner", l))); }

function flash(text) {
  const view = state.views.get(state.active);
  if (view) append(view, el("div", "said Warning", text));
  else showBanners([text]);
}

// ---- figures -----------------------------------------------------------------------------------------------------

function clock(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return s + "s";
  const m = Math.floor(s / 60);
  if (m < 60) return m + "m " + (s % 60) + "s";
  return Math.floor(m / 60) + "h " + (m % 60) + "m";
}
function ago(iso) {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (!(s >= 0)) return "";
  if (s < 60) return "just now";
  if (s < 3600) return Math.floor(s / 60) + "m ago";
  if (s < 86400) return Math.floor(s / 3600) + "h ago";
  return Math.floor(s / 86400) + "d ago";
}
// A TICKING ELAPSED TIME: every element carrying data-since shows the time since then, redrawn each second by one
// timer, so a spinner's "12s" keeps moving between events.
function ticking(cls, since, prefix) {
  const e = el("span", cls);
  e.dataset.since = String(since);
  e.dataset.prefix = prefix || "";
  e.textContent = (prefix || "") + clock(Date.now() - since);
  return e;
}
setInterval(() => {
  for (const e of document.querySelectorAll("[data-since]")) e.textContent = e.dataset.prefix + clock(Date.now() - Number(e.dataset.since));
}, 1000);

// THE LAST TWO PARTS OF A PATH — enough to tell projects apart without a line of parent folders.
function shortFolder(path) {
  const parts = String(path || "").split("/").filter(Boolean);
  return (parts.length > 2 ? "…/" : "/") + parts.slice(-2).join("/");
}
function tokens(n) { return n >= 1e6 ? (n / 1e6).toFixed(1) + "M" : n >= 1e3 ? (n / 1e3).toFixed(1) + "k" : String(n || 0); }
function money(n) { return n == null ? "" : n > 0 && n < 0.01 ? "<$0.01" : "$" + n.toFixed(2); }

// ---- sessions list ----------------------------------------------------------------------------------------------

function title(row) { return row.title || row.folder.split("/").filter(Boolean).pop() || row.folder; }

function drawSessions() {
  const box = $("sessions");
  box.replaceChildren();
  const groups = new Map();
  if (narrow()) {
    // PHONE: WHAT NEEDS YOU FIRST.
    for (const [key, label] of [["waiting", "Waiting on you"], ["working", "Working"], ["idle", "Open"], ["recent", "Recent"], ["held", "Elsewhere"]])
      groups.set(label, state.rows.filter((r) => r.state === key));
  } else {
    for (const r of state.rows) {
      if (!groups.has(r.folder)) groups.set(r.folder, []);
      groups.get(r.folder).push(r);
    }
  }
  for (const [label, rows] of groups) {
    if (rows.length === 0) continue;
    const g = el("div", "group", narrow() ? label : shortFolder(label));
    g.title = label;
    box.append(g);
    for (const r of rows) box.append(sessionRow(r));
  }
  drawTabs();
}

function sessionRow(r) {
  const row = el("div", "row" + (r.id === activeSessionId() ? " on" : ""));
  row.dataset.id = r.id;
  row.append(el("span", "dot " + r.state));
  const t = el("div", "t");
  t.append(el("b", null, title(r)));
  const sub = r.state === "held" ? r.heldBy : r.state === "waiting" ? "waiting on you" : r.state === "working" ? "working"
    : narrow() ? shortFolder(r.folder) : [ago(r.updatedAt), r.short, r.tokens && r.tokens + " tokens"].filter(Boolean).join(" · ");
  t.append(el("small", null, sub));
  row.append(t);
  if (r.state === "idle" || r.state === "working" || r.state === "waiting") {
    const x = el("button", "x icon", "×");
    x.type = "button";
    x.title = "Close this session";
    x.addEventListener("click", (e) => { e.stopPropagation(); send({ type: "close", sessionId: r.id }); });
    row.append(x);
  } else if (r.state === "recent") {
    // A SAVED ONE IS DELETED FROM HERE, without opening it — the server asks first, in the row itself.
    const x = el("button", "x icon", "×");
    x.type = "button";
    x.title = "Delete this session";
    x.addEventListener("click", (e) => { e.stopPropagation(); send({ type: "deleteSession", id: r.id }); });
    row.append(x);
  }
  row.addEventListener("click", () => {
    document.body.classList.remove("drawer");
    if (r.state === "held") { flash("That session is " + r.heldBy + "."); return; }
    if (state.views.has(r.id)) activate(r.id);
    else send({ type: "open", id: r.id, since: 0 });
  });
  return row;
}

function workerRunning(view) {
  const panel = state.panels.get(view.parent);
  const w = panel && (panel.workers || []).find((x) => x.agentId === view.agentId);
  return w || null;
}

// WHICH VIEWS THE STRIP HOLDS. Wide, the sidebar switches sessions, so the strip is the session in front and what was
// opened from it — its chat first, then its workers, colleagues, commands, files, shell and pages — as the terminal's
// strip is. Narrow, the sidebar is a drawer, and the strip keeps every open view so one tap reaches any of them.
function tabsShown() {
  const all = [...state.views];
  if (!wide()) return all;
  const sid = activeSessionId();
  return all.filter(([id, v]) => (sid && id === sid) || APP_PAGES.includes(v.kind) || (sid && v.kind !== "session" && v.parent === sid))
    .sort(([, a], [, b]) => (a.kind === "session" ? 0 : 1) - (b.kind === "session" ? 0 : 1));
}

function drawTabs() {
  const tabs = $("tabs");
  tabs.replaceChildren();
  const scoped = wide();
  for (const [id, view] of tabsShown()) {
    const worker = view.kind === "worker";
    const command = view.kind === "command";
    const file = view.kind === "file";
    const shell = view.kind === "shell";
    const config = view.kind === "config";
    const plugins = view.kind === "plugins";
    const colleagues = view.kind === "colleagues";
    const sub = worker || command || file || shell || config || plugins || colleagues;
    const row = sub ? null : state.rows.find((r) => r.id === id);
    const w = worker ? workerRunning(view) : null;
    const tab = el("button", "tab" + (id === state.active ? " on" : "") + (sub ? " worker" : ""));
    tab.type = "button";
    const dot = config ? (view.dirty ? "waiting" : "") : shell ? (view.ended ? "" : "working") : file ? (view.dirty ? "waiting" : "") : command ? (view.running ? "working" : "") : worker ? (w && w.running ? "working" : "") : (row ? row.state : "idle");
    tab.append(el("span", "dot " + dot),
      document.createTextNode(scoped && !sub ? "Chat" : colleagues ? "◆ Colleagues" : plugins ? "⧉ Plugins" : config ? "⚙ Config" + (view.dirty ? " •" : "") : shell ? "❯ " + view.title : file ? view.title + (view.dirty ? " •" : "") : command ? "▭ " + view.title : worker ? workerLabel(view) : view.title || "session"));
    if (file) tab.title = view.path;
    tab.addEventListener("click", () => activate(id));
    if (sub) {
      // A WORKER'S TAB CLOSES ON THE PAGE ONLY — the worker carries on, and its card still opens it again.
      const x = el("span", "x", "×");
      x.title = "Close this tab";
      x.addEventListener("click", (e) => { e.stopPropagation(); dropView(id); });
      tab.append(x);
    }
    tabs.append(tab);
  }
  // NO "+" WHERE THE SIDEBAR HAS "+ New session": the strip there is one session's, and a new one is not opened from it.
  if (!scoped) {
    const plus = el("button", "tab", "+");
    plus.type = "button";
    plus.title = "New session";
    plus.addEventListener("click", openPicker);
    tabs.append(plus);
  }
}

// ---- views ------------------------------------------------------------------------------------------------------

function newView(id, kind) {
  const view = { id, kind, log: el("div", "view"), seq: 0, turns: new Map(), tools: new Map(), cards: new Map(),
    turn: null, round: null, segment: null, working: false };
  state.views.set(id, view);
  return view;
}

function resetView(view) {
  view.log.replaceChildren();
  view.turns.clear(); view.tools.clear(); view.cards.clear();
  view.turn = view.last = view.round = view.segment = null;
  view.seq = 0;
}

function opened(m) {
  let view = state.views.get(m.sessionId);
  const fresh = !view;
  if (!view) view = newView(m.sessionId, "session");
  Object.assign(view, { agentId: m.agentId, folder: m.folder,
    title: (state.rows.find((r) => r.id === m.sessionId) || {}).title || m.folder.split("/").filter(Boolean).pop() });
  // A RECONNECT RE-OPENS EVERY VIEW; only one the user just asked for comes to the front.
  if (fresh || state.active === m.sessionId) activate(m.sessionId);
  else drawTabs();
}

function openedWorker(m) {
  let view = state.views.get(m.sessionId);
  const fresh = !view;
  if (!view) view = newView(m.sessionId, "worker");
  Object.assign(view, { parent: m.parent, agentId: m.agentId, name: m.name, workerType: m.workerType, task: m.task,
    model: m.model, folder: m.folder, title: m.name, badge: m.badge || null });
  if (fresh || state.active === m.sessionId) activate(m.sessionId);
  else drawTabs();
}

// A WORKER TAB'S LABEL: the terminal's — a colleague by its glyph and name, any other worker by its handle.
function workerLabel(view) { return view.badge ? view.badge.glyph + " " + view.badge.name : "⤷ " + view.name; }

function openWorker(sessionId, agentId) {
  const key = sessionId + "~" + agentId;
  if (state.views.has(key)) activate(key);
  else send({ type: "openWorker", sessionId, agentId, since: 0 });
}

// THE SESSION BEHIND THE ACTIVE VIEW: a worker's tab shows its parent's panel, model and prompts.
function activeSessionId() {
  const v = state.views.get(state.active);
  return v ? (v.kind === "session" ? v.id : v.parent) : null;
}

function activate(id) {
  const view = id ? state.views.get(id) : null;
  // AN APP PAGE BELONGS TO THE SESSION IN FRONT when it is brought up: kept on the one it was opened from, clicking its
  // tab after a switch swung the strip back to that session's tabs.
  const was = activeSessionId();
  if (view && APP_PAGES.includes(view.kind) && was) view.parent = was;
  const arriving = view && state.active !== id;
  state.active = view ? id : null;
  if (arriving && view.kind === "plugins") setTimeout(relistPlugins, 0);
  $("chat").replaceChildren(view ? view.log : $("empty"));
  document.body.dataset.view = view ? "chat" : "list";
  // A COMMAND'S TAB TAKES NO MESSAGES: it is the command's output, and the terminal's tab has no composer either.
  // A TAB THAT IS NOT A CONVERSATION TAKES NO MESSAGES: a command's output, a file, a shell — the keys go to it.
  const conversation = !!view && (view.kind === "session" || view.kind === "worker");
  $("input").disabled = !conversation;
  $("send").disabled = !conversation;
  document.body.classList.toggle("editing", !!view && (view.kind === "file" || view.kind === "shell" || (view.kind === "config" && view.tab === "json")));
  // AND NO MESSAGE BOX AT ALL there: one that cannot send is room taken from the shell, the file or the output.
  document.body.classList.toggle("no-composer", !!view && !conversation);
  $("input").placeholder = view && view.kind === "worker" ? "Message " + view.name + "…" : "Message, or / for commands";
  // THE FOLDER STAYS IN THE HEADER on a page that is not a conversation — the marketplace, the config page: theirs is
  // the session they were opened from, as the terminal's title keeps its folder whatever dialog is open.
  const where = view && !view.folder && view.parent ? state.views.get(view.parent) : view;
  $("crumb").textContent = where ? where.folder || "" : "";
  $("phone-title").textContent = view ? (view.kind === "worker" ? workerLabel(view) : view.kind === "command" ? "▭ " + view.title : view.title) : "";
  if (view && (view.kind === "file" || view.kind === "config")) setTimeout(() => view.ta.focus(), 0);
  if (view && view.kind === "shell") setTimeout(() => { if (view.fit) view.fit.fit(); if (view.term) view.term.focus(); }, 0);
  drawWorkerHead();
  drawPrompts();
  drawSessions();
  drawPanel();
  drawQueued();
  if (view && view.restored != null) { putBack(view.restored); view.restored = null; }
  drawPicks();
  drawStop();
  scrollDown(true);
  if (view && !narrow()) $("input").focus();
}

function dropView(id) {
  const view = state.views.get(id);
  // A COMMAND'S TAB STOPS ITS FOLLOWING ON THE SERVER; the command itself carries on, as closing the terminal's tab leaves it.
  // EVERY TAB THAT IS NOT A SESSION STOPS ITS STREAM ON THE SERVER — a worker's too; the thing itself carries on.
  if (view && view.kind !== "session" && view.kind !== "config" && view.kind !== "plugins") send({ type: "leave", sessionId: id });
  if (view && view.term) view.term.dispose();
  state.views.delete(id);
  // BACK TO THE SESSION IT WAS OPENED FROM, the tab beside it — not whichever view happened to be opened first.
  const back = view && view.parent && state.views.has(view.parent) ? view.parent : state.views.keys().next().value || null;
  if (state.active === id) activate(back);
  else drawTabs();
}

function drawWorkerHead() {
  const view = state.views.get(state.active);
  const head = $("worker-head");
  if (view && view.kind === "command") { drawCommandHead(view, head); return; }
  if (view && view.kind === "file") { drawFileHead(view, head); return; }
  if (view && view.kind === "shell") { drawShellHead(view, head); return; }
  if (view && view.kind === "config") { drawConfigHead(view, head); return; }
  if (view && view.kind === "plugins") { drawPluginsHead(view, head); return; }
  if (view && view.kind === "colleagues") { drawColleaguesHead(view, head); return; }
  if (!view || view.kind !== "worker") { head.hidden = true; return; }
  head.hidden = false;
  head.replaceChildren();
  const w = workerRunning(view);
  const running = !!(w && w.running);
  // A COLLEAGUE BETWEEN TASKS IS IDLE, NOT FINISHED: no ✓ or ✗ beside it, as on its terminal tab.
  if (running) head.append(el("span", "spin violet"));
  else if (!view.badge) head.append(el("span", w && w.state === "Failed" ? "mark-bad" : "mark-ok", w && w.state === "Failed" ? "✗" : "✓"));
  // A COLLEAGUE SAYS SO, with its name — the badge the terminal's tab and card show too.
  head.append(el("span", "tag" + (view.badge ? " colleague" : ""), view.badge ? view.badge.kind : "Worker"),
    el("b", null, view.badge ? view.badge.glyph + " " + view.badge.name : view.name));
  // A COLLEAGUE'S FACTS NAME NO TYPE, and its state in the terminal's colleague words (idle, learning, …).
  const said = w ? (w.colleague ? w.colleague.state : w.state.toLowerCase()) + (w.background && w.running ? " · background" : "") : view.badge ? "idle" : "";
  const facts = [view.badge ? view.name : view.workerType, view.model, said].filter(Boolean);
  head.append(el("span", "meta", facts.join(" · ")));
  // A COLLEAGUE'S WORKER IS DESCRIBED BY ITS NAME, already the header's title — as on its terminal tab.
  if (view.task && !(view.badge && view.task === view.badge.name)) head.append(el("span", "meta", view.task));
  head.append(el("span", "spacer"));
  head.append(button("Back to session", "ghost", () => activate(view.parent)));
  if (running) {
    if (!w.background)
      head.append(button("Background", null, () => send({ type: "workerAction", sessionId: view.parent, agentId: view.agentId, action: "background" })));
    head.append(button("Stop", "danger", () => send({ type: "workerAction", sessionId: view.parent, agentId: view.agentId, action: "stop" })));
  }
}

// ---- a background command's tab ------------------------------------------------------------------------------------

// THE TERMINAL'S F7 → Enter: what the command has written, followed as it writes, with Kill and Copy to transcript.
// The server reads its output file; a reconnect opens it again, and the server starts again from the file.
const COMMAND_KEEP = 400000;

function openedCommand(m) {
  let view = state.views.get(m.sessionId);
  const fresh = !view;
  if (!view) view = newView(m.sessionId, "command");
  view.log.replaceChildren();
  view.out = el("pre", "cmd-out");
  view.log.append(view.out);
  Object.assign(view, { parent: m.parent, pid: m.pid, command: m.command, path: m.path, title: String(m.pid), running: true,
    folder: (state.views.get(m.parent) || {}).folder || "" });
  if (fresh || state.active === m.sessionId) activate(m.sessionId);
  else drawTabs();
}

function commandOutput(m) {
  const view = state.views.get(m.sessionId);
  if (!view || !view.out) return;
  const stick = isAtBottom();
  let text = view.out.textContent + m.text;
  // THE TAIL, NOT THE WHOLE: a command that writes for an hour would grow the page without bound; the file has it all.
  if (text.length > COMMAND_KEEP) text = text.slice(text.length - COMMAND_KEEP);
  view.out.textContent = text;
  if (view.id === state.active) scrollDown(stick);
}

function commandState(m) {
  const view = state.views.get(m.sessionId);
  if (!view) return;
  view.running = m.state === "Running";
  view.state = m.state;
  view.exitCode = m.exitCode;
  if (view.id === state.active) drawWorkerHead();
  drawTabs();
}

function drawCommandHead(view, head) {
  head.hidden = false;
  head.replaceChildren();
  head.append(view.running ? el("span", "spin small") : el("span", view.exitCode === 0 ? "mark-ok" : "mark-bad", view.exitCode === 0 ? "✓" : "✗"));
  head.append(el("span", "tag", "Command"));
  const cmd = el("code", "cmd", view.command);
  cmd.title = view.command;
  head.append(cmd);
  head.append(el("span", "meta", "pid " + view.pid + " · " + (view.running ? "running" : view.exitCode != null ? "exit " + view.exitCode : String(view.state || "").toLowerCase())));
  head.append(el("span", "spacer"));
  head.append(button("Back to session", "ghost", () => activate(view.parent)));
  head.append(button("Copy to transcript", null, () => send({ type: "copyCommand", sessionId: view.parent, pid: view.pid })));
  if (view.running) head.append(button("Kill", "danger", () => send({ type: "stopCommand", sessionId: view.parent, pid: view.pid })));
}

function openCommand(sessionId, pid) {
  const key = sessionId + "#" + pid;
  if (state.views.has(key)) activate(key);
  else send({ type: "openCommand", sessionId, pid });
}

// ---- the editor -----------------------------------------------------------------------------------------------------

// THE TERMINAL'S /open: a file in a tab, highlighted, saved with the bytes it was read with. A textarea over a highlighted
// copy of its text — the page already carries highlight.js, and a textarea keeps the browser's own editing, undo and
// input methods, which a hand-built editor would get wrong.
const HIGHLIGHT_LIMIT = 300000;

function openedFile(m) {
  let view = state.views.get(m.sessionId);
  const fresh = !view;
  if (!view) {
    view = newView(m.sessionId, "file");
    Object.assign(view, { parent: m.parent, path: m.path, title: m.name, language: m.language, dirty: false,
      folder: (state.views.get(m.parent) || {}).folder || "" });
    buildEditor(view);
  }
  // A RECONNECT OPENS IT AGAIN. Unedited, the buffer takes what is there now; edited, the edits stay, and a disk that
  // moved on meanwhile is said, as the terminal says it.
  if (!view.dirty) { setBuffer(view, m.text); view.baseline = m.text; view.bar = null; }
  else if (m.text !== view.baseline) { view.baseline = m.text; view.bar = { kind: "changed", text: m.text }; }
  if (fresh || state.active === m.sessionId) activate(m.sessionId);
  else { drawTabs(); if (state.active === view.id) drawWorkerHead(); }
}

function buildEditor(view) {
  const ed = el("div", "editor");
  const gutter = el("pre", "ed-gutter");
  const wrap = el("div", "ed-wrap");
  const hl = el("pre", "ed-hl");
  const code = el("code", "hljs");
  hl.append(code);
  const ta = el("textarea", "ed-text");
  ta.spellcheck = false;
  ta.setAttribute("autocapitalize", "off");
  ta.setAttribute("autocomplete", "off");
  ta.wrap = "off";
  wrap.append(hl, ta);
  ed.append(gutter, wrap);
  view.log.append(ed);
  Object.assign(view, { ed, gutter, hl, code, ta });
  ta.addEventListener("input", () => {
    const was = view.dirty;
    view.dirty = ta.value !== view.baseline;
    paintEditor(view);
    if (was !== view.dirty) { drawTabs(); drawWorkerHead(); }
  });
  ta.addEventListener("scroll", () => { hl.scrollTop = ta.scrollTop; hl.scrollLeft = ta.scrollLeft; gutter.scrollTop = ta.scrollTop; });
  ta.addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") { e.preventDefault(); if (view.save) view.save(); else saveFile(view, false); return; }
    if (e.key === "Tab" && !e.shiftKey && !e.ctrlKey && !e.altKey) {
      // INDENT AS THE FILE DOES: a tab where it indents with tabs, four spaces otherwise.
      e.preventDefault();
      const unit = /^\t/m.test(ta.value) ? "\t" : "    ";
      document.execCommand("insertText", false, unit);
    }
  });
}

function setBuffer(view, text) {
  view.ta.value = text;
  view.dirty = false;
  paintEditor(view);
}

function paintEditor(view) {
  if (view.painting) return;
  view.painting = true;
  requestAnimationFrame(() => {
    view.painting = false;
    const text = view.ta.value;
    const lines = text.split("\n").length;
    view.gutter.textContent = Array.from({ length: lines }, (_, i) => String(i + 1)).join("\n");
    // A TRAILING NEWLINE, so the copy is as tall as the textarea and the last line lines up.
    const shown = text + "\n";
    const lang = view.language && window.hljs && hljs.getLanguage(view.language) ? view.language : null;
    if (lang && text.length < HIGHLIGHT_LIMIT) {
      // ESCAPED BY highlight.js, which builds its markup from the text it is given and escapes every character of it.
      const safe = hljs.highlight(shown, { language: lang, ignoreIllegals: true }).value;
      view.code.innerHTML = safe;
    } else view.code.textContent = shown;
    view.hl.scrollTop = view.ta.scrollTop;
    view.hl.scrollLeft = view.ta.scrollLeft;
  });
}

function saveFile(view, force) {
  view.saving = true;
  send({ type: "saveFile", sessionId: view.id, text: view.ta.value, force });
  drawWorkerHead();
}

function fileSaved(m) {
  const view = state.views.get(m.sessionId);
  if (!view) return;
  view.saving = false;
  view.baseline = view.ta.value;
  view.dirty = false;
  view.bar = null;
  drawTabs();
  if (state.active === view.id) drawWorkerHead();
}

function fileConflict(m) {
  const view = state.views.get(m.sessionId);
  if (!view) return;
  view.saving = false;
  view.bar = { kind: "conflict", text: m.text, deleted: m.deleted };
  if (state.active !== view.id) activate(view.id); else drawWorkerHead();
}

function fileChanged(m) {
  const view = state.views.get(m.sessionId);
  if (!view) return;
  // UNEDITED, IT FOLLOWS THE FILE, as the terminal's tab does; edited, the edits are kept and the bar says so.
  if (!view.dirty && !m.deleted) { setBuffer(view, m.text); view.baseline = m.text; send({ type: "reloadFile", sessionId: view.id }); return; }
  view.bar = { kind: "changed", text: m.text, deleted: m.deleted };
  if (state.active === view.id) drawWorkerHead();
}

function drawFileHead(view, head) {
  head.hidden = false;
  head.replaceChildren();
  head.append(el("span", "tag", "File"));
  const name = el("b", null, view.title + (view.dirty ? " •" : ""));
  name.title = view.path;
  head.append(name, el("span", "meta", view.language || "text"));
  head.append(el("span", "spacer"));
  head.append(button("Back to session", "ghost", () => activate(view.parent)));
  const save = button(view.saving ? "Saving…" : "Save", "primary", () => saveFile(view, false));
  save.title = "Ctrl+S";
  save.disabled = !view.dirty || view.saving;
  head.append(save);
  if (view.bar) {
    const bar = el("div", "file-bar");
    const said = view.bar.kind === "conflict"
      ? (view.bar.deleted ? "Deleted on disk since you opened it." : "Changed on disk since you opened it — saving would overwrite that.")
      : (view.bar.deleted ? "Deleted on disk — Save recreates it." : "Changed on disk. Your edits are kept here.");
    bar.append(el("span", null, said), el("span", "spacer"));
    if (!view.bar.deleted) bar.append(button("See theirs", null, () => { $("theirs-text").textContent = view.bar.text || ""; $("theirs").hidden = false; }));
    if (!view.bar.deleted) bar.append(button("Reload", null, () => { view.dirty = false; view.bar = null; send({ type: "reloadFile", sessionId: view.id }); }));
    if (view.bar.kind === "conflict") bar.append(button("Save anyway", "danger", () => { view.bar = null; saveFile(view, true); }));
    head.append(bar);
  }
}

$("theirs-close").addEventListener("click", () => { $("theirs").hidden = true; });
$("theirs").addEventListener("click", (e) => { if (e.target === $("theirs")) $("theirs").hidden = true; });

// ---- the shell ------------------------------------------------------------------------------------------------------

// THE TERMINAL'S /shell: a real terminal, the user's own shell on a pseudo-terminal in the session's folder. xterm.js is
// loaded the first time one opens — most pages never need it.
let xtermReady = null;
function loadXterm() {
  if (xtermReady) return xtermReady;
  const css = el("link");
  css.rel = "stylesheet";
  css.href = "/vendor/xterm.css";
  document.head.append(css);
  const script = (src) => new Promise((ok, bad) => { const s = el("script"); s.src = src; s.onload = ok; s.onerror = bad; document.head.append(s); });
  xtermReady = script("/vendor/xterm.js").then(() => Promise.all([script("/vendor/xterm-addon-fit.js"), script("/vendor/xterm-addon-canvas.js")]));
  return xtermReady;
}

function bytes(b64) {
  const raw = atob(b64);
  const out = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

function openedShell(m) {
  const view = newView(m.sessionId, "shell");
  Object.assign(view, { parent: m.parent, title: m.shell, shellName: m.shell, folder: m.folder, ended: false });
  const box = el("div", "shell");
  view.log.append(box);
  view.box = box;
  activate(m.sessionId);
  loadXterm().then(() => {
    const mono = getComputedStyle(document.documentElement).getPropertyValue("--mono").trim() || "monospace";
    const term = new Terminal({ cursorBlink: true, fontFamily: mono, fontSize: 13,
      theme: { background: "#0b0e14", foreground: "#d9dee6" }, scrollback: 5000, convertEol: false });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    // THE CANVAS RENDERER, NOT THE DOM ONE, and loaded before open so the DOM one is never made: it colours text through
    // <style> elements it adds to the page, which the page's policy refuses — text arrived uncoloured and barely visible.
    term.loadAddon(new CanvasAddon.CanvasAddon());
    term.open(box);
    view.term = term;
    view.fit = fit;
    term.onData((data) => { if (!view.ended) send({ type: "shellInput", sessionId: view.id, data }); });
    term.onResize(({ rows, cols }) => send({ type: "shellResize", sessionId: view.id, rows, cols }));
    new ResizeObserver(() => { if (state.active === view.id) fit.fit(); }).observe(box);
    for (const d of view.pending || []) term.write(bytes(d));
    view.pending = null;
    fit.fit();
    send({ type: "shellResize", sessionId: view.id, rows: term.rows, cols: term.cols });
    if (state.active === view.id) term.focus();
  }, () => flash("The terminal could not load."));
}

// WHAT THE SCREEN SHOWS, as the terminal's tab sends it: the buffer's lines, trailing blanks dropped.
function screenLines(term) {
  const buf = term.buffer.active;
  const lines = [];
  for (let i = 0; i < buf.length; i++) lines.push(buf.getLine(i)?.translateToString(true) ?? "");
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop();
  return lines.slice(-400);
}

function reportShell(view, exitCode) {
  if (!view.term) return;
  send({ type: "shellReport", sessionId: view.id, lines: screenLines(view.term), exitCode });
}

function shellEnded(view, exitCode, why) {
  if (view.ended) return;
  view.ended = true;
  view.exitCode = exitCode;
  if (view.term) view.term.write("\r\n\x1b[2m[" + (why || ("exited" + (exitCode != null ? " " + exitCode : ""))) + "]\x1b[0m\r\n");
  // TOLD ON EXIT ONLY WHEN ASKED, as the terminal's tab does: its "copy back on exit" is off by default — a shell you
  // opened is yours, and one worth sharing says so. Queued for the next message, no turn of its own.
  if (!why && view.copyBack) reportShell(view, exitCode);
  drawTabs();
  if (state.active === view.id) drawWorkerHead();
}

function drawShellHead(view, head) {
  head.hidden = false;
  head.replaceChildren();
  head.append(view.ended ? el("span", view.exitCode === 0 ? "mark-ok" : "mark-bad", view.exitCode === 0 ? "✓" : "✗") : el("span", "spin small"));
  head.append(el("span", "tag", "Shell"), el("b", null, view.shellName));
  const where = el("span", "meta", shortFolder(view.folder) + (view.ended ? " · ended" : ""));
  where.title = view.folder;
  head.append(where, el("span", "spacer"));
  head.append(button("Back to session", "ghost", () => activate(view.parent)));
  // A CHECKBOX FOR EXIT, A BUTTON FOR NOW — the terminal's pair: the decision about exit is made before the shell ends.
  if (!view.ended) {
    const box = el("label", "check");
    const tick = el("input");
    tick.type = "checkbox";
    tick.checked = !!view.copyBack;
    tick.addEventListener("change", () => { view.copyBack = tick.checked; });
    box.append(tick, document.createTextNode(" copy back on exit"));
    head.append(box);
  }
  head.append(button("Copy now", null, () => reportShell(view, view.ended ? view.exitCode : null)));
  head.append(button(view.ended ? "Close" : "End shell", "danger", () => dropView(view.id)));
}

// ---- the config page --------------------------------------------------------------------------------------------------

// F8 FOR A BROWSER, behind web.allow.config: config.json as text, judged by the loader startup runs before anything is
// written. The server keeps the secrets — shown as their last four characters — and the browser's own limits, which are
// not in the text at all.
const CONFIG_ID = "config:";

// THE SESSION THE CONFIG PAGE CAME FROM: its folder holds the project's and local hooks files.
function configSession() {
  const v = state.views.get(CONFIG_ID);
  return (v && v.parent) || activeSessionId();
}

function configView() {
  let view = state.views.get(CONFIG_ID);
  if (view) return { view, fresh: false };
  view = newView(CONFIG_ID, "config");
  Object.assign(view, { parent: activeSessionId(), language: "json", title: "config.json", dirty: false, folder: "", tab: "general", tabs: [] });
  view.formBox = el("div", "config-form");
  view.log.append(view.formBox);
  buildEditor(view);
  view.save = () => saveConfig(view, null);
  return { view, fresh: true };
}

function showConfigTab(view, tab) {
  view.tab = tab;
  view.formBox.hidden = tab === "json";
  view.ed.hidden = tab !== "json";
  document.body.classList.toggle("editing", state.active === CONFIG_ID && tab === "json");
}

function openedConfig(m) {
  const { view, fresh } = configView();
  showConfigTab(view, "json");
  setBuffer(view, m.text);
  view.baseline = m.text;
  // THE RELOAD THAT FOLLOWS A SAVE keeps the save's bar; any other reload starts clean.
  if (view.keepBar) view.keepBar = false; else view.bar = null;
  if (fresh || state.active !== CONFIG_ID) activate(CONFIG_ID); else drawWorkerHead();
  showConfigTab(view, "json");
}

// ---- F8's tabs as forms: drawn from what the server describes, saved through the tab's own draft --------------------

function openedConfigForm(m) {
  const { view, fresh } = configView();
  view.tabs = m.tabs || view.tabs;
  if (view.keepBar) view.keepBar = false; else view.bar = null;
  view.formDirty = false;
  drawConfigForm(view, m);
  showConfigTab(view, m.tab);
  if (fresh || state.active !== CONFIG_ID) activate(CONFIG_ID); else drawWorkerHead();
  showConfigTab(view, m.tab);
}

function drawConfigForm(view, m) {
  const outer = view.formBox;
  outer.replaceChildren();
  view.fields = [];
  view.item = m.item || null;
  view.formActions = m.actions || [];
  view.readOnly = !!m.readOnly;
  // A LIST TAB: its items on the left, the chosen one's fields beside them — F8's list and detail.
  let box = outer;
  if (m.items) {
    outer.classList.add("has-items");
    const list = el("nav", "config-items");
    for (const it of m.items) {
      const b = el("button", "config-item" + (it.id === m.item ? " on" : ""));
      b.type = "button";
      b.append(el("b", null, it.label));
      if (it.sub) b.append(el("small", null, it.sub));
      b.addEventListener("click", () => send({ type: "configForm", sessionId: configSession(), tab: m.tab, item: it.id }));
      list.append(b);
    }
    if (m.addLabel) {
      const add = el("button", "config-item add" + (m.item === "+new" ? " on" : ""), "+ " + m.addLabel);
      add.type = "button";
      add.addEventListener("click", () => send({ type: "configForm", sessionId: configSession(), tab: m.tab, item: "+new" }));
      list.append(add);
    }
    box = el("div", "config-detail");
    outer.append(list, box);
  } else outer.classList.remove("has-items");
  const dirty = () => { if (!view.formDirty) { view.formDirty = true; drawWorkerHead(); } };
  for (const sec of m.sections) {
    const s = el("section", "form-section");
    s.append(el("h3", null, sec.title));
    if (sec.help) s.append(el("p", "none", sec.help));
    for (const f of sec.fields) {
      const row = el("div", "form-field " + f.kind);
      if (f.kind !== "radio" && f.kind !== "checks" && f.kind !== "order") row.append(el("label", "form-label", f.label));
      let read;
      if (f.kind === "radio") {
        const fs = el("fieldset");
        fs.append(el("legend", "form-label", f.label));
        const name = "f-" + f.id;
        for (const o of f.options) {
          const l = el("label", "form-choice");
          const i = el("input");
          i.type = "radio"; i.name = name; i.value = o.value; i.checked = String(f.value) === o.value;
          i.addEventListener("change", dirty);
          l.append(i, el("span", null, o.label));
          if (o.help) l.append(el("small", null, o.help));
          fs.append(l);
        }
        row.append(fs);
        read = () => (fs.querySelector("input:checked") || {}).value;
      } else if (f.kind === "order") {
        // F8'S ORDERED LIST: ticked providers in their order, the first marked default, ↑/↓ to reorder.
        row.append(el("div", "form-label", f.label));
        const list = el("div", "form-order");
        const on = new Set(f.value || []);
        const mark = () => {
          let first = true;
          for (const item of list.children) {
            const ticked = item.querySelector("input").checked;
            item.querySelector(".default").hidden = !(ticked && first);
            if (ticked) first = false;
          }
        };
        const move = (item, by) => {
          const to = by < 0 ? item.previousElementSibling : item.nextElementSibling;
          if (!to) return;
          if (by < 0) list.insertBefore(item, to); else list.insertBefore(to, item);
          mark(); dirty();
        };
        for (const o of f.options) {
          const item = el("div", "form-order-item");
          const i = el("input");
          i.type = "checkbox"; i.value = o.value; i.checked = on.has(o.value);
          i.addEventListener("change", () => { mark(); dirty(); });
          const up = button("↑", "ghost", () => move(item, -1));
          const down = button("↓", "ghost", () => move(item, 1));
          item.append(i, el("span", null, o.label), el("span", "badge default", "default"), el("span", "spacer"), up, down);
          list.append(item);
        }
        mark();
        row.append(list);
        read = () => [...list.querySelectorAll("input:checked")].map((i) => i.value);
      } else if (f.kind === "checks") {
        row.append(el("div", "form-label", f.label));
        const grid = el("div", "form-checks");
        const on = new Set(f.value || []);
        for (const o of f.options) {
          const l = el("label", "form-check");
          const i = el("input");
          i.type = "checkbox"; i.value = o.value; i.checked = on.has(o.value);
          i.addEventListener("change", dirty);
          l.append(i, el("span", null, o.label));
          grid.append(l);
        }
        row.append(grid);
        read = () => [...grid.querySelectorAll("input:checked")].map((i) => i.value);
      } else if (f.kind === "lines") {
        const t = el("textarea", "form-input");
        t.rows = 3; t.value = f.value || ""; t.spellcheck = false;
        t.addEventListener("input", dirty);
        row.append(t);
        read = () => t.value;
      } else if (f.kind === "secret") {
        // NEVER THE KEY ITSELF: whether one is set, and its last four characters. Empty keeps it; Clear removes it.
        const i = el("input", "form-input");
        i.type = "password"; i.autocomplete = "new-password";
        i.placeholder = f.saved && f.saved !== "none" ? f.saved + " — type to replace" : "none";
        i.addEventListener("input", dirty);
        const clear = el("label", "form-check");
        const c = el("input"); c.type = "checkbox";
        c.addEventListener("change", dirty);
        clear.append(c, el("span", null, "Clear"));
        row.append(i, clear);
        read = () => i.value;
        view.fields.push({ id: f.id + "Clear", read: () => c.checked });
      } else if (f.kind === "select") {
        const sel = el("select", "form-input");
        for (const o of f.options) sel.append(new Option(o.label, o.value));
        sel.value = f.value ?? "";
        sel.addEventListener("change", dirty);
        row.append(sel);
        read = () => sel.value;
      } else if (f.kind === "bool") {
        row.firstChild.remove();
        const l = el("label", "form-check");
        const c = el("input"); c.type = "checkbox"; c.checked = !!f.value;
        c.addEventListener("change", dirty);
        l.append(c, el("span", "form-label", f.label));
        row.append(l);
        read = () => c.checked;
      } else if (f.kind === "info") {
        row.append(el("div", "form-info", String(f.value ?? "")));
        read = null;
      } else {
        const i = el("input", "form-input");
        i.type = f.kind === "number" ? "text" : "text";
        i.inputMode = f.kind === "number" ? "numeric" : "text";
        i.value = f.value ?? "";
        i.addEventListener("input", dirty);
        row.append(i);
        read = () => i.value;
      }
      if (f.help && f.kind !== "radio") row.append(el("small", "form-help", f.help));
      if (read) view.fields.push({ id: f.id, read });
      s.append(row);
    }
    box.append(s);
  }
  if (view.formActions.length) {
    const acts = el("div", "btns form-actions");
    for (const a of view.formActions)
      acts.append(button(a.label, a.danger ? "danger" : null, () => send({ type: "configFormAction", sessionId: configSession(), tab: view.tab, item: view.item, action: a.id })));
    box.append(acts);
  }
  if (view.readOnly) box.querySelectorAll("input, textarea, select").forEach((i) => { i.disabled = true; });
}

// F8 ASKS BEFORE IT DELETES: the server's sentence says what else changes, and nothing is written until it is confirmed.
function configConfirm(m) {
  const view = state.views.get(CONFIG_ID);
  if (!view) return;
  view.bar = { kind: "confirm", text: m.text, tab: m.tab, item: m.item, action: m.action };
  drawWorkerHead();
}

function saveConfigForm(view, choice) {
  const values = {};
  for (const f of view.fields) values[f.id] = f.read();
  view.saving = true;
  send({ type: "configFormSave", sessionId: configSession(), tab: view.tab, item: view.item, values, choice });
  drawWorkerHead();
}

function saveConfig(view, choice) {
  view.saving = true;
  send({ type: "saveConfig", text: view.ta.value, choice });
  drawWorkerHead();
}

// THE QUESTION F8 ASKS when a save moves running tabs to another model: the move re-sends the conversation.
function configAsk(m) {
  const view = state.views.get(CONFIG_ID);
  if (!view) return;
  view.saving = false;
  view.bar = { kind: "ask", instances: m.instances };
  drawWorkerHead();
}

function configRefused(m) {
  const view = state.views.get(CONFIG_ID);
  if (!view) return;
  view.saving = false;
  view.bar = { kind: "refused", errors: m.errors || [] };
  drawWorkerHead();
}

function configSaved(m) {
  const view = state.views.get(CONFIG_ID);
  if (!view) return;
  view.saving = false;
  view.dirty = false;
  view.bar = (m.warnings || []).length ? { kind: "warned", errors: m.warnings } : { kind: "saved" };
  view.keepBar = true;
  view.formDirty = false;
  // THE TEXT AGAIN FROM THE FILE, so a hidden secret just typed is hidden here too. A form's save sends its form back.
  if (view.tab === "json") send({ type: "getConfig" });
  drawTabs();
}

function drawConfigHead(view, head) {
  head.hidden = false;
  head.replaceChildren();
  head.append(el("span", "tag", "Configuration"));
  const strip = el("div", "config-tabs");
  for (const t of view.tabs.length ? view.tabs : [{ id: "general", title: "General" }, { id: "json", title: "config.json" }]) {
    const b = button(t.title, "config-tab" + (t.id === view.tab ? " on" : ""), () => send(t.id === "json" ? { type: "getConfig" } : { type: "configForm", sessionId: configSession(), tab: t.id }));
    strip.append(b);
  }
  head.append(strip, el("span", "spacer"));
  if (view.parent) head.append(button("Back to session", "ghost", () => activate(view.parent)));
  const json = view.tab === "json";
  head.append(button("Reload", null, () => send(json ? { type: "getConfig" } : { type: "configForm", sessionId: configSession(), tab: view.tab })));
  const save = button(view.saving ? "Saving…" : "Save", "primary", () => (json ? saveConfig(view, null) : saveConfigForm(view, null)));
  save.title = json ? "Ctrl+S" : "";
  save.disabled = (json ? !view.dirty : !view.formDirty) || view.saving || !!view.readOnly;
  head.append(save);
  if (!view.bar) return;
  const bar = el("div", "file-bar" + (view.bar.kind === "saved" ? " good" : ""));
  if (view.bar.kind === "saved") bar.append(el("span", null, "Saved, and live: new sessions use it, and open ones on a changed model follow it."));
  else if (view.bar.kind === "confirm") {
    const b = view.bar;
    bar.append(el("span", null, b.text), el("span", "spacer"),
      button("Yes", "danger", () => { view.bar = null; send({ type: "configFormAction", sessionId: configSession(), tab: b.tab, item: b.item, action: b.action, confirmed: true }); }),
      button("No", null, () => { view.bar = null; drawWorkerHead(); }));
  } else if (view.bar.kind === "ask") {
    bar.append(el("span", null, "Running sessions on " + view.bar.instances.join(", ") + " change model. Their conversation is re-sent:"), el("span", "spacer"));
    for (const [label, choice] of [["Now", "ApplyNow"], ["Compress first", "CompressThenApply"], ["At the next clear", "AtNextClearOrCompression"], ["New sessions only", "NewTabsOnly"]])
      bar.append(button(label, null, () => { view.bar = null; if (view.tab === "json") saveConfig(view, choice); else saveConfigForm(view, choice); }));
  } else {
    const list = el("div", "errors");
    list.append(el("b", null, view.bar.kind === "warned" ? "Saved, but not live:" : "Not saved:"));
    for (const e of view.bar.errors) list.append(el("div", null, e));
    bar.append(list);
  }
  head.append(bar);
}

// ---- the plugin marketplace ----------------------------------------------------------------------------------------

// F2 FOR A BROWSER, behind web.allow.plugins: the same rows — installed, updates, needs attention, available — and the
// same actions. Loading still shows a plugin's hash and asks, and a download still asks first, as prompts here.
// THE COLLEAGUES' LIBRARY: the terminal's tab as a page — a list, and for the one picked its facts and its experience,
// read-only — with Open (one-to-one) and Bring in. One page; it follows the session it was last opened from.
const COLLEAGUES_ID = "colleagues:";

function openColleagues() {
  const sid = activeSessionId();
  if (!sid) { flash("Open a session first: colleagues are brought into a session."); return; }
  state.colleaguesWanted = true;
  send({ type: "colleagues", sessionId: sid });
}

function openedColleagues(m) {
  let view = state.views.get(COLLEAGUES_ID);
  const fresh = !view;
  if (!view) { view = newView(COLLEAGUES_ID, "colleagues"); view.picked = null; }
  view.parent = m.sessionId;
  view.data = m;
  if (!view.picked || !m.rows.some((r) => r.name === view.picked)) view.picked = m.rows.length ? m.rows[0].name : null;
  drawColleaguesPage(view);
  const wanted = fresh || state.colleaguesWanted;
  state.colleaguesWanted = false;
  if (wanted) activate(COLLEAGUES_ID); else if (state.active === view.id) drawWorkerHead();
}

function drawColleaguesPage(view) {
  const box = el("div", "library");
  const rail = el("div", "rail");
  const detail = el("div", "detail");
  box.append(rail, detail);
  if (!view.data.rows.length)
    detail.append(el("p", "none", "No colleagues yet. Keep a worker of a session as one: /colleagues keep <worker> [name], or ask the agent to."));
  for (const r of view.data.rows) {
    const item = el("div", "item" + (r.name === view.picked ? " on" : ""), r.name);
    item.addEventListener("click", () => { view.picked = r.name; drawColleaguesPage(view); });
    rail.append(item);
  }
  const e = view.data.rows.find((r) => r.name === view.picked);
  if (e) {
    detail.append(el("h2", null, "◆ " + e.name));
    if (e.description) detail.append(el("p", null, e.description));
    detail.append(el("p", "meta", e.factsLine));
    if (e.knows.length) detail.append(el("p", "meta", "knows: " + e.knows.join(", ")));
    const acts = el("div", "btns");
    acts.append(button("Open", null, () => send({ type: "colleagueOpen", sessionId: view.parent, name: e.name })),
      button("Bring in", null, () => send({ type: "colleagueBring", sessionId: view.parent, name: e.name })),
      // ASKED FIRST, in the session's conversation — the terminal's buttons ask there too.
      button("Distil again…", null, () => send({ type: "colleagueDistill", sessionId: view.parent, name: e.name })),
      button("Forget…", "danger", () => send({ type: "colleagueForget", sessionId: view.parent, name: e.name })));
    detail.append(acts);
    // READ-ONLY, AS DISTILLED: a colleague is corrected by talking to it (Open), not by editing it behind its back.
    for (const part of e.experience) {
      detail.append(el("h3", null, part.title));
      const md = el("div", "md");
      md.innerHTML = render(part.markdown);
      detail.append(md);
    }
    if (!e.experience.length) detail.append(el("p", "none", "Nothing distilled yet."));
  }
  view.log.replaceChildren(box);
}

function drawColleaguesHead(view, head) {
  head.hidden = false;
  head.replaceChildren();
  head.append(el("span", "tag colleague", "Colleagues"));
  head.append(el("span", "spacer"));
  head.append(button("Back to session", "ghost", () => activate(view.parent)));
  head.append(button("Refresh", null, () => send({ type: "colleagues", sessionId: view.parent })));
}

const PLUGINS_ID = "plugins:";
const SECTION_TITLES = { Installed: "Installed", Updates: "Updates", NeedsAttention: "Needs attention", Available: "Available" };
const ACTION_LABELS = { install: "Install", update: "Update", activate: "Activate", deactivate: "Deactivate",
  enable: "Enable auto-load", disable: "Disable auto-load", addToConfig: "Add to config", uninstall: "Uninstall" };

function openPlugins(refresh) {
  const sid = activeSessionId();
  if (!sid) { flash("Open a session first: plugins load into a session."); return; }
  // ASKED FOR, SO SHOWN when it comes back — see openedPlugins: a reconnect re-requests it too, and that one must not
  // pull the page in front of whatever the user is reading.
  state.pluginsWanted = true;
  send({ type: "plugins", sessionId: sid, refresh: !!refresh });
}

function openedPlugins(m) {
  let view = state.views.get(PLUGINS_ID);
  const fresh = !view;
  if (!view) {
    view = newView(PLUGINS_ID, "plugins");
    Object.assign(view, { parent: m.sessionId, folder: "", filter: "" });
  }
  view.parent = m.sessionId;
  // A RE-LIST THAT FOUND NOTHING NEW draws nothing, so the filter being typed into keeps its focus.
  const same = !fresh && view.data && JSON.stringify(view.data.rows) === JSON.stringify(m.rows)
    && JSON.stringify(view.data.stores) === JSON.stringify(m.stores) && JSON.stringify(view.data.folders) === JSON.stringify(m.folders);
  view.data = m;
  if (!same) drawPlugins(view);
  // IN FRONT WHEN THE USER ASKED FOR IT, not only the first time: after "Back to session" the view still exists, and
  // a refresh alone would happen out of sight, leaving Marketplace looking dead.
  const wanted = fresh || state.pluginsWanted;
  state.pluginsWanted = false;
  if (wanted) activate(PLUGINS_ID); else if (state.active === view.id) drawWorkerHead();
}

// WHAT IS INSTALLED CHANGES FROM ELSEWHERE — the terminal's F2, another tab — and the page re-reads it while it is in
// front: from the catalog already read (no network), only what is on disk is read again. Every few seconds, and as it
// comes into view, so a plugin the terminal just installed is not still offered for Install here.
function relistPlugins() {
  const view = state.views.get(PLUGINS_ID);
  if (!view || state.active !== PLUGINS_ID || view.busy || !view.parent || document.hidden) return;
  send({ type: "plugins", sessionId: view.parent, refresh: false });
}
setInterval(relistPlugins, 4000);

function drawPlugins(view) {
  const m = view.data;
  const box = el("div", "plugins");
  if (m.error) box.append(el("div", "said Warning", "The catalog could not be read: " + m.error + " — what is cached is shown."));
  box.append(pluginSources(view, m));
  for (const n of m.notes || []) box.append(el("div", "said", n));
  const f = view.filter.toLowerCase();
  for (const section of ["Updates", "NeedsAttention", "Installed", "Available"]) {
    const rows = m.rows.filter((r) => r.section === section && (!f || (r.name + " " + (r.catalog?.description || "")).toLowerCase().includes(f)));
    // INSTALLED IS ALWAYS SAID, as F2 says "Nothing installed yet": an empty list of installed plugins is an answer.
    if (!rows.length && !(section === "Installed" && !f)) continue;
    box.append(el("h3", null, SECTION_TITLES[section] + " · " + rows.length));
    if (!rows.length) box.append(el("p", "none", "Nothing installed yet."));
    for (const r of rows) box.append(pluginCard(view, r));
  }
  if (!m.rows.length) box.append(el("p", "none", "No plugins installed, and none offered."));
  view.log.replaceChildren(box);
}

// F2'S "Stores and folders": catalogs beside the official one, and the folders searched for plugins, in order.
function pluginSources(view, m) {
  const d = el("details", "plugin-sources");
  d.open = !!view.sourcesOpen;
  d.addEventListener("toggle", () => { view.sourcesOpen = d.open; });
  d.append(el("summary", null, "Stores and folders · " + (m.stores || []).length + " stores · " + (m.folders || []).length + " folders"));
  const act = (name, action, values) => {
    view.busy = action.replace(/([A-Z])/g, " $1").toLowerCase() + "…";
    drawWorkerHead();
    send({ type: "pluginAction", sessionId: view.parent, name, action, values });
  };
  d.append(el("h4", null, "Stores"));
  for (const st of m.stores || []) {
    const row = el("div", "source-row");
    row.append(el("b", null, st.name), el("code", null, st.url), el("span", "spacer"), button("Remove", "danger", () => act(st.name, "removeStore")));
    d.append(row);
  }
  const addStore = el("form", "source-add");
  const sn = el("input", "form-input"); sn.placeholder = "name";
  const su = el("input", "form-input"); su.placeholder = "catalog URL";
  addStore.append(sn, su, button("Add store", null, () => addStore.requestSubmit()));
  addStore.addEventListener("submit", (e) => { e.preventDefault(); act(sn.value.trim(), "addStore", { url: su.value.trim() }); });
  d.append(addStore);
  d.append(el("h4", null, "Folders searched, in order"));
  (m.folders || []).forEach((f, i) => {
    const row = el("div", "source-row");
    row.append(el("code", null, f), el("span", "spacer"));
    if (i > 0) row.append(button("↑", null, () => act(f, "moveFolderUp")));
    row.append(button("Remove", "danger", () => act(f, "removeFolder")));
    d.append(row);
  });
  const addFolder = el("form", "source-add");
  const fi = el("input", "form-input"); fi.placeholder = "/path/or/~/folder";
  addFolder.append(fi, button("Add folder", null, () => addFolder.requestSubmit()));
  addFolder.addEventListener("submit", (e) => { e.preventDefault(); act(fi.value.trim(), "addFolder"); });
  d.append(addFolder);
  return d;
}

function pluginCard(view, r) {
  const c = r.catalog;
  const card = el("div", "plugin-card");
  const head = el("div", "plugin-head");
  head.append(el("b", null, c?.title || r.name));
  if (c?.version) head.append(el("span", "meta", c.version));
  if (r.loaded) head.append(el("span", "badge", "active"));
  if (r.autoLoad === true) head.append(el("span", "badge", "auto-load"));
  if (c?.store) head.append(el("span", "badge", c.store));
  card.append(head);
  if (c?.description) card.append(el("p", null, c.description));
  const meta = [r.detail, c?.publisher && "by " + c.publisher, c?.license].filter(Boolean);
  if (meta.length) card.append(el("div", "meta", meta.join(" · ")));
  // F2'S FACTS — STATE, NEEDS, CONTRACT, TOOLS ("1, never asks") — as its details pane lists them, from the same source;
  // a fact with no key is a second line under the one before.
  if (r.facts && r.facts.length) {
    const dl = el("dl", "plugin-facts");
    for (const f of r.facts) dl.append(el("dt", null, f.key), el("dd", null, f.value));
    card.append(dl);
  }
  if (c?.tools?.length) {
    const tools = el("div", "plugin-tools");
    for (const t of c.tools) {
      const tag = el("span", "tool" + (t.gated === "never" ? "" : " gated"), t.name);
      tag.title = t.gated === "never" ? "never asks" : "asks: " + t.gated;
      tools.append(tag);
    }
    card.append(tools);
  }
  if (r.cannotUpdate) card.append(el("div", "said Warning", r.cannotUpdate));
  // F2'S DETAILS: the plugin's own README, from beside its binary.
  if (r.readme) {
    const d = el("details", "plugin-more");
    d.append(el("summary", null, "README"));
    const md = el("div", "md");
    md.innerHTML = render(r.readme);
    d.append(md);
    card.append(d);
  }
  // F2'S SETTINGS TAB: each key the catalog names, its sentence, and what config holds; Save writes them.
  if (r.settings) {
    const d = el("details", "plugin-more");
    d.append(el("summary", null, "Settings"));
    const inputs = [];
    for (const st of r.settings) {
      const row = el("div", "form-field");
      row.append(el("label", "form-label", st.key), el("small", "form-help", st.prose));
      let t;
      if (st.secret) {
        // NEVER THE VALUE: whether one is set, and its last four characters. Empty keeps it.
        t = el("input", "form-input");
        t.type = "password"; t.autocomplete = "new-password";
        t.placeholder = st.saved && st.saved !== "none" ? st.saved + " — type to replace" : "none";
      } else {
        t = el("textarea", "form-input");
        t.rows = 2; t.value = st.value; t.spellcheck = false;
      }
      row.append(t);
      inputs.push([st.key, t]);
      d.append(row);
    }
    d.append(button("Save settings", "primary", () => {
      const values = {};
      for (const [k, t] of inputs) values[k] = t.value;
      view.busy = r.name + " · saving settings…";
      drawWorkerHead();
      send({ type: "pluginAction", sessionId: view.parent, name: r.name, action: "saveSettings", values });
    }));
    card.append(d);
  }
  const acts = el("div", "btns");
  for (const a of r.actions) {
    if (a === "install") {
      const where = el("select");
      for (const d of view.data.destinations) where.append(new Option(d.label, d.folder));
      acts.append(where, button("Install", "primary", () => act(view, r.name, "install", where.value)));
    } else acts.append(button(ACTION_LABELS[a] || a, a === "uninstall" ? "danger" : a === "update" ? "primary" : null, () => act(view, r.name, a, null)));
  }
  if (r.actions.length) card.append(acts);
  return card;
}

function act(view, name, action, folder) {
  view.busy = name + " · " + (ACTION_LABELS[action] || action).toLowerCase() + "…";
  drawWorkerHead();
  send({ type: "pluginAction", sessionId: view.parent, name, action, folder });
}

// A RESULT IS A SENTENCE; TWO OF THEM ASK AGAIN. "confirm:" carries the question F2 asks before deleting; "installed:"
// offers what F2 offers after an install — load it now, add it to config, or neither — never all three at once.
function pluginResult(m) {
  const view = state.views.get(PLUGINS_ID);
  if (!view) return;
  view.busy = null;
  if (m.text.startsWith("confirm:")) view.bar = { kind: "confirm", text: m.text.slice(8), name: m.name };
  else if (m.text.startsWith("installed:")) view.bar = { kind: "installed", text: m.text.slice(10), name: m.name };
  else view.bar = { kind: "said", text: m.text };
  if (state.active === view.id) drawWorkerHead();
}

function drawPluginsHead(view, head) {
  head.hidden = false;
  head.replaceChildren();
  head.append(el("span", "tag", "Plugins"));
  const filter = el("input", "plugin-filter");
  filter.placeholder = "filter";
  filter.value = view.filter;
  filter.addEventListener("input", () => { view.filter = filter.value; drawPlugins(view); });
  head.append(filter);
  head.append(el("span", "meta", view.busy || ("catalog fetched " + ago(view.data.readAt))));
  head.append(el("span", "spacer"));
  head.append(button("Back to session", "ghost", () => activate(view.parent)));
  head.append(button("Refresh", null, () => { view.bar = null; openPlugins(true); }));
  if (!view.bar) return;
  const bar = el("div", "file-bar" + (view.bar.kind === "said" ? " good" : ""));
  bar.append(el("span", null, view.bar.text), el("span", "spacer"));
  if (view.bar.kind === "confirm") {
    const name = view.bar.name;
    bar.append(button("Remove", "danger", () => { view.bar = null; act(view, name, "uninstall", "confirmed"); }), button("Keep it", null, () => { view.bar = null; drawWorkerHead(); }));
  } else if (view.bar.kind === "installed") {
    const name = view.bar.name;
    bar.append(button("Load now", "primary", () => { view.bar = null; act(view, name, "activate", null); }),
      button("Add to config", null, () => { view.bar = null; act(view, name, "addToConfig", null); }),
      button("Done", null, () => { view.bar = null; drawWorkerHead(); }));
  } else bar.append(button("OK", null, () => { view.bar = null; drawWorkerHead(); }));
  head.append(bar);
}

// ---- export ---------------------------------------------------------------------------------------------------------

function download(name, text) {
  const url = URL.createObjectURL(new Blob([text], { type: "text/markdown;charset=utf-8" }));
  const a = el("a");
  a.href = url;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

// F12 AS THE TERMINAL HAS IT: scope, detail, the parts under Advanced, where it goes, and what it will be before it is
// made. The server sends the parts in the terminal's words and answers each change with the terminal's size line, so
// nothing here decides what an export holds. A browser cannot write a file where the terminal would, so a download
// stands in for one; the clipboard is offered where the page may write to it.
const exporting = { form: null, timer: 0 };

function exportForm(m) {
  closeExportForm();
  const f = { sessionId: m.sessionId, parts: m.chosen, scope: m.scope || "Whole", turns: m.turns || 3,
    clipboard: !!m.clipboard && !!navigator.clipboard, preview: null };
  exporting.form = f;

  const back = el("div", "viewer export-dialog");
  back.id = "export-dialog";
  const box = el("div", "theirs-box export-box");
  box.setAttribute("role", "dialog");
  box.setAttribute("aria-label", "Export session");
  const head = el("div", "theirs-head");
  head.append(el("b", null, "Export session"), el("span", "spacer"));
  const x = button("×", "icon", closeExportForm);
  x.setAttribute("aria-label", "Close");
  head.append(x);

  const body = el("div", "export-body");
  const row = (label, ...items) => {
    const r = el("div", "export-row");
    r.append(el("span", "export-label", label), ...items);
    return r;
  };
  const radio = (name, value, label, on, pick) => {
    const l = el("label", "form-check");
    const i = el("input");
    i.type = "radio";
    i.name = name;
    i.value = value;
    i.checked = on;
    i.addEventListener("change", () => { if (i.checked) pick(value); });
    l.append(i, document.createTextNode(label));
    return l;
  };

  const turns = el("input", "form-input export-turns");
  turns.type = "number";
  turns.min = "1";
  turns.value = String(f.turns);
  turns.setAttribute("aria-label", "Turns");
  turns.addEventListener("input", () => { f.turns = Math.max(1, parseInt(turns.value, 10) || 1); changed(); });
  const scope = (v) => { f.scope = v; changed(); };
  body.append(row("Scope", radio("export-scope", "Whole", "Whole session", f.scope === "Whole", scope),
    radio("export-scope", "LastTurn", "Last turn", f.scope === "LastTurn", scope),
    radio("export-scope", "LastTurns", "Last", f.scope === "LastTurns", scope), turns, el("span", "muted", "turns")));

  // THE PRESETS SET THE BOXES, and the boxes name the preset: a mix that is neither leaves both unticked, as the
  // terminal's does, and the line under them says Custom.
  const readable = radio("export-preset", "readable", m.readable, false, () => setParts(m.readableParts));
  const full = radio("export-preset", "full", m.full, false, () => setParts(m.fullParts));
  const describe = el("div", "form-help export-describe");
  body.append(row("Detail", readable, full), row("", describe));

  const adv = el("details", "export-advanced");
  adv.append(el("summary", null, "Advanced"));
  const boxes = el("div", "form-checks");
  const checks = [];
  for (const p of m.parts) {
    const l = el("label", "form-check");
    const c = el("input");
    c.type = "checkbox";
    c.checked = (f.parts & p.bit) !== 0;
    c.addEventListener("change", () => { f.parts = c.checked ? f.parts | p.bit : f.parts & ~p.bit; changed(); });
    checks.push([p.bit, c]);
    l.append(c, document.createTextNode(p.label));
    boxes.append(l);
  }
  adv.append(boxes);
  body.append(row("", adv));

  const to = (v) => { f.clipboard = v === "clipboard"; changed(); };
  const clip = radio("export-to", "clipboard", "Clipboard", f.clipboard, to);
  if (!navigator.clipboard) {
    clip.querySelector("input").disabled = true;
    clip.title = "This page may not write to the clipboard — it is not served over https or from this machine";
  }
  body.append(row("Save to", radio("export-to", "download", "Download", !f.clipboard, to), clip));
  const notes = el("div", "form-help export-notes");
  body.append(row("", notes));

  const foot = el("div", "export-foot");
  const size = el("span", "export-size", "…");
  const go = button("Export", "primary", () => {
    if (!f.preview || f.preview.messages === 0) return;
    f.sent = true;
    send({ type: "export", sessionId: f.sessionId, scope: f.scope, turns: f.turns, parts: f.parts });
  });
  go.disabled = true;
  foot.append(size, el("span", "spacer"), go, button("Cancel", null, closeExportForm));

  box.append(head, body, foot);
  back.append(box);
  back.addEventListener("click", (e) => { if (e.target === back) closeExportForm(); });
  document.body.append(back);
  f.ui = { readable, full, describe, checks, notes, size, go };

  function setParts(bits) {
    f.parts = bits;
    for (const [bit, c] of checks) c.checked = (bits & bit) !== 0;
    changed();
  }
  function changed() {
    f.preview = null;
    size.textContent = "…";
    go.disabled = true;
    clearTimeout(exporting.timer);
    exporting.timer = setTimeout(() => send({ type: "exportPreview", sessionId: f.sessionId, scope: f.scope, turns: f.turns,
      parts: f.parts, clipboard: f.clipboard }), 150);
  }
  changed();
  go.focus();
}

function exportPreview(m) {
  const f = exporting.form;
  if (!f || f.sessionId !== m.sessionId) return;
  f.preview = m;
  const ui = f.ui;
  ui.readable.querySelector("input").checked = m.preset === "Readable";
  ui.full.querySelector("input").checked = m.preset === "Full";
  ui.describe.textContent = m.describe;
  ui.notes.textContent = (m.notes || []).join("  ·  ");
  ui.notes.hidden = !m.notes || m.notes.length === 0;
  ui.size.replaceChildren(document.createTextNode(m.size));
  if (m.warn) ui.size.append(" · ", el("span", "warn", m.warn));
  ui.go.disabled = m.messages === 0;
}

async function exported(m) {
  const f = exporting.form;
  const toClipboard = !!(f && f.sessionId === m.sessionId && f.clipboard);
  closeExportForm();
  if (toClipboard) {
    try {
      await navigator.clipboard.writeText(m.markdown);
      flashInfo("Copied " + m.size + " as markdown");
      return;
    } catch {
      flash("The clipboard refused it — downloading instead.");
    }
  }
  download(m.name, m.markdown);
}

function closeExportForm() {
  clearTimeout(exporting.timer);
  // THE SERVER LETS GO of the conversation it read for the form — unless the form closed because it exported, which
  // let go already.
  if (exporting.form && !exporting.form.sent) send({ type: "exportClosed", sessionId: exporting.form.sessionId });
  exporting.form = null;
  const d = $("export-dialog");
  if (d) d.remove();
}

document.addEventListener("keydown", (e) => { if (e.key === "Escape" && exporting.form) { e.preventDefault(); closeExportForm(); } });

function flashInfo(text) {
  const view = state.views.get(state.active);
  if (view) append(view, el("div", "said Info", text));
}

// ---- images -------------------------------------------------------------------------------------------------------

// THE TERMINAL'S IMAGE WINDOW: a click on a sent image shows it whole, over the page, until a click or Esc.
function viewImage(src, label) {
  $("viewer-img").src = src;
  $("viewer-cap").textContent = label || "";
  $("viewer").hidden = false;
}
function closeViewer() { $("viewer").hidden = true; $("viewer-img").src = ""; }
$("viewer").addEventListener("click", closeViewer);
document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !$("viewer").hidden) closeViewer(); });

// ---- appending ----------------------------------------------------------------------------------------------------

function isAtBottom() { const c = $("chat"); return c.scrollHeight - c.scrollTop - c.clientHeight < 90; }
function scrollDown(force) { if (force) { const c = $("chat"); c.scrollTop = c.scrollHeight; } }
function into(view, parent, node) {
  const stick = isAtBottom();
  parent.append(node);
  if (view.id === state.active) scrollDown(stick);
}
function append(view, node) {
  view.turn = view.last = view.round = view.segment = null;
  into(view, view.log, node);
}

// ---- a turn ------------------------------------------------------------------------------------------------------

// A TURN IS ONE ASSISTANT BLOCK: a header with who is speaking and a spinner while it works, then its pieces in the
// order they came — thinking, text, a round of tool calls, more text, a worker. Text after tools starts a new segment
// so the tools sit between the words that led to them and the words that came after.
function turnOf(view, id) {
  let t = view.turns.get(id);
  if (!t) {
    view.last = null;
    const node = el("div", "turn");
    const who = el("div", "who");
    const spinner = el("span", "spin small");
    const label = el("span", "label");
    who.append(el("span", "name", view.kind === "worker" ? view.name : "cxagent"), spinner, label);
    node.append(who);
    t = { id, node, who, spinner, label, empty: true, thinking: null };
    view.turns.set(id, t);
    into(view, view.log, node);
  }
  view.turn = t;
  return t;
}

// TOOLS RUN AFTER THE TURN THAT ASKED FOR THEM HAS ENDED: the model finishes its message, then its calls run. So a
// tool with no open turn joins the last one, provided nothing else has been shown since. Only with no turn to join (a
// resumed session's replay) does it get one of its own, with no spinner, since no turnEnded will come to stop it.
function currentTurn(view) {
  if (view.turn) return view.turn;
  if (view.last) { view.turn = view.last; return view.last; }
  const t = turnOf(view, "loose-" + view.seq);
  t.spinner.remove();
  return t;
}

function segment(view, t) {
  if (!view.segment || view.segment.turn !== t) {
    const node = el("div", "md");
    view.segment = { turn: t, node, text: "", pending: false };
    view.round = null;
    into(view, t.node, node);
  }
  return view.segment;
}

function paint(view, seg) {
  // ONE RENDER PER FRAME, however many tokens arrived in it.
  if (seg.pending) return;
  seg.pending = true;
  requestAnimationFrame(() => {
    seg.pending = false;
    const stick = isAtBottom();
    seg.node.innerHTML = render(seg.text);
    if (view.id === state.active) scrollDown(stick);
  });
}

function settleTurn(view, t) {
  if (!t) return;
  t.spinner.remove();
  if (t.empty) { t.node.remove(); view.turns.delete(t.id); }
  else view.last = t;
  // THE ROUND STAYS OPEN: the calls that follow are this turn's, and belong in the round its first call started.
  if (view.turn === t) view.turn = view.segment = null;
}

// ---- tool rounds ------------------------------------------------------------------------------------------------

// A ROUND IS THE TOOL CALLS BETWEEN TWO PIECES OF TEXT, folded to one line as the terminal folds them: what ran, how
// long, what failed. Open, it is a table; a row opens its output. A failure opens the round so it is not missed.
function round(view, t) {
  if (view.round && view.round.turn === t) return view.round;
  const node = el("details", "round");
  const summary = el("summary");
  const mark = el("span", "spin small");
  const meta = el("span", "m");
  const badges = el("span", "badges");
  summary.append(mark, el("span", "what", "Tools"), meta, badges, el("span", "caret", "›"));
  const table = el("table", "calls");
  node.append(summary, table);
  view.round = { turn: t, node, mark, meta, badges, table, calls: [] };
  view.segment = null;
  t.empty = false;
  into(view, t.node, node);
  return view.round;
}

function drawRound(r) {
  const done = r.calls.filter((c) => c.done);
  const failed = done.filter((c) => c.state === "Failed").length;
  const uncontained = done.filter((c) => c.sandbox === "uncontained").length;
  const tools = new Set(r.calls.map((c) => c.tool)).size;
  const seconds = done.reduce((s, c) => s + c.ms, 0) / 1000;
  const running = r.calls.find((c) => !c.done);
  r.mark.className = running ? "spin small" : failed ? "mark-bad" : "mark-ok";
  r.mark.textContent = running ? "" : failed ? "✗" : "✓";
  const parts = [r.calls.length === 1 ? (r.calls[0].tool + (r.calls[0].target ? " " + r.calls[0].target : ""))
    : r.calls.length + " calls · " + tools + " tool" + (tools === 1 ? "" : "s")];
  if (done.length && !running && done.every((c) => c.ms != null)) parts.push(seconds.toFixed(1) + "s");
  if (failed) parts.push(failed + " failed");
  if (running) parts.push("running " + running.tool + (running.progress ? " · " + running.progress : ""));
  r.meta.textContent = parts.join(" · ");
  r.badges.replaceChildren();
  if (uncontained) r.badges.append(el("span", "badge out", uncontained + " uncontained"));
  else if (done.some((c) => c.sandbox === "contained")) r.badges.append(el("span", "badge", "contained"));
  if (done.some((c) => c.sandboxWarning)) r.badges.append(el("span", "badge out", "git files changed"));
  if (failed) r.node.open = true;
}

// A TOOL'S DISPLAY NAME IS "tool · target"; the two go in separate columns.
function splitName(name) {
  const at = name.indexOf(" · ");
  return at > 0 ? { tool: name.slice(0, at), target: name.slice(at + 3) } : { tool: name, target: "" };
}

function callRow(view, m, table, owner) {
  const { tool, target } = splitName(m.name);
  const call = { job: m.job, tool, target, started: Date.now(), done: false, ms: 0, owner };
  const row = el("tr", "call");
  call.markCell = el("td", "mk");
  call.markCell.append(el("span", "spin small"));
  call.timeCell = el("td", "n");
  call.timeCell.append(ticking(null, call.started));
  const targetCell = el("td", "target", target);
  targetCell.title = target;
  row.append(call.markCell, el("td", "name", tool), targetCell, call.timeCell);
  const outRow = el("tr", "out");
  outRow.hidden = true;
  const outCell = el("td");
  outCell.colSpan = 4;
  call.pre = el("pre");
  outCell.append(call.pre);
  outRow.append(outCell);
  row.addEventListener("click", () => { outRow.hidden = !outRow.hidden; });
  table.append(row, outRow);
  call.outRow = outRow;
  view.tools.set(m.job, call);
  return call;
}

function startCall(view, m) {
  const r = round(view, currentTurn(view));
  r.calls.push(callRow(view, m, r.table, r));
  drawRound(r);
}

// A WORKER'S OWN TOOL CALLS, should they reach the parent's feed, go inside its card, not into the parent's rounds.
function startWorkerCall(view, m, card) {
  if (!card.table) { card.table = el("table", "calls"); card.body.insertBefore(card.table, card.open); }
  card.live = true;
  callRow(view, m, card.table, null);
}

// A WORKER'S FINISHED CALL, as a row of its card's table — what the terminal's expanded worker row shows. A foreground
// worker's calls already arrive live (startWorkerCall), so its card takes these only when nothing came that way. THE
// CELLS COME FORMATTED from the server's CallTable, the terminal's own: this draws them and decides nothing.
function workerCall(view, m) {
  if (m.agent === view.agentId) return;
  const card = cardFor(view, m.agent);
  if (!card || card.live) return;
  if (!card.table) {
    card.summary = el("div", "calls-summary");
    card.table = el("table", "calls");
    card.body.insertBefore(card.summary, card.open);
    card.body.insertBefore(card.table, card.open);
  }
  card.summary.textContent = m.summary || "";
  const row = el("tr");
  row.append(el("td", "mark-" + m.tone, m.mark), el("td", null, m.tool), el("td", null, m.target || ""),
    el("td", null, m.ms), el("td", null, m.out));
  card.table.append(row);
}

function endCall(view, m) {
  const call = view.tools.get(m.job);
  if (!call) return;
  call.done = true;
  call.state = m.state;
  call.sandbox = m.sandbox;
  call.sandboxWarning = m.sandboxWarning;
  // NO TIME FOR A REPLAYED CALL: its history does not keep one, and the page timing a replay's burst reads 0.0s.
  call.ms = m.ms != null ? m.ms : null;
  const ok = m.state === "Succeeded", bad = m.state === "Failed";
  call.markCell.replaceChildren(el("span", ok ? "mark-ok" : bad ? "mark-bad" : "mark-skip", ok ? "✓" : bad ? "✗" : "–"));
  call.timeCell.replaceChildren(document.createTextNode(call.ms == null ? "" : call.ms < 1000 ? call.ms + "ms" : (call.ms / 1000).toFixed(1) + "s"));
  // THE RESULT COMES WITH THE END when nothing streamed; a failure's carries its error already.
  if (m.output && !call.pre.textContent) call.pre.textContent = m.output;
  else if (m.error) call.pre.textContent += (call.pre.textContent ? "\n" : "") + m.error;
  if (!call.pre.textContent) call.pre.textContent = "(no output)";
  if (bad) call.outRow.hidden = false;
  if (call.owner) drawRound(call.owner);
}

// ---- workers ------------------------------------------------------------------------------------------------------

function shortModel(runsOn) {
  const model = runsOn.includes(":") ? runsOn.slice(runsOn.indexOf(":") + 1) : runsOn;
  return model.split("/").pop();
}

// A WORKER IS A CARD, NOT A TOOL ROW: violet, with its type, the model it runs on, its task and a live progress line.
// Open, it shows what the worker reports; "Open" gives it a tab of its own with its whole transcript.
function startWorker(view, m) {
  const t = currentTurn(view);
  t.empty = false;
  const { tool, target } = splitName(m.name);
  const node = el("details", "worker");
  const summary = el("summary");
  const mark = el("span", "spin violet");
  const who = el("span", "who-is", tool);
  const model = el("span", "model", m.runsOn ? shortModel(m.runsOn) : "");
  const task = el("span", "for", target);
  const progress = el("span", "progress", m.progress || "starting…");
  // THE TERMINAL'S WORD FOR THE ROW — "Colleague" for one brought in — sent with the event.
  const author = m.author || "Worker";
  summary.append(mark, el("span", "tag" + (author === "Colleague" ? " colleague" : ""), author), who, model, task, progress);
  const body = el("div", "body");
  const facts = el("pre");
  facts.hidden = true;
  const open = button("Open in a tab ▸", "open", (e) => {
    e.preventDefault();
    if (card.agentId) openWorker(view.id, card.agentId);
  });
  open.disabled = true;
  body.append(facts, open);
  node.append(summary, body);
  view.segment = null;
  view.round = null;
  into(view, t.node, node);
  const card = { node, mark, who, model, task, progress, facts, open, body, table: null, agentId: null };
  view.cards.set(m.job, card);
}

function markCard(card, how) {
  card.mark.className = how === "running" ? "spin violet" : how === "done" ? "mark-ok" : "mark-bad";
  card.mark.textContent = how === "running" ? "" : how === "done" ? "✓" : "✗";
  card.node.classList.toggle("done", how !== "running");
}

// THE CARDS FOLLOW THE PANEL'S WORKERS. A card's row stops reporting when it ends, and a worker sent to the background
// ends its row at launch while it carries on working; the panel, sent every second, says what each one is doing now.
function liveCards(sessionId) {
  const panel = state.panels.get(sessionId);
  const view = state.views.get(sessionId);
  if (!panel || !view) return;
  for (const card of view.cards.values()) {
    if (!card.agentId || !card.ended) continue;
    const w = panel.workers.find((x) => x.agentId === card.agentId);
    if (!w) continue;
    if (w.running) {
      markCard(card, "running");
      card.progress.textContent = "working in the background · " + clock(w.seconds * 1000);
    } else if (card.background) {
      markCard(card, w.state === "Failed" ? "failed" : "done");
      card.progress.textContent = w.state.toLowerCase() + " · " + clock(w.seconds * 1000);
    }
    card.background = w.running;
  }
}

function cardFor(view, agentId) {
  for (const c of view.cards.values()) if (c.agentId === agentId) return c;
  return null;
}

// ---- the queue ----------------------------------------------------------------------------------------------------

// THE TERMINAL'S QUEUED BLOCK: a line typed while a turn runs waits for the turn's next step, shown here until it is
// taken — and Cancel puts it back in this composer instead, without stopping the turn.
function drawQueued() {
  const bar = $("queued");
  const view = state.views.get(state.active);
  const text = view && view.kind === "session" ? view.queued || "" : "";
  bar.hidden = !text;
  if (!text) { bar.replaceChildren(); return; }
  const cancel = button("Cancel · back to composer", "ghost", () => send({ type: "unqueue", sessionId: state.active }));
  bar.replaceChildren(el("span", "tag", "queued"), el("span", "queued-text", text), el("span", "spacer"), cancel);
}

// TEXT HANDED BACK — by this tab's Esc or its Cancel — goes above anything typed since: it was typed first.
function restore(m) {
  const view = state.views.get(m.sessionId);
  if (!view) return;
  if (state.active === m.sessionId) putBack(m.text);
  else view.restored = view.restored ? view.restored + "\n" + m.text : m.text;
}

function putBack(text) {
  const input = $("input");
  input.value = input.value ? text + "\n" + input.value : text;
  resize();
  input.focus();
  input.setSelectionRange(input.value.length, input.value.length);
}

// ---- events ------------------------------------------------------------------------------------------------------

function event(m) {
  const view = state.views.get(m.sessionId);
  if (!view || m.seq <= view.seq) return;
  view.seq = m.seq;
  switch (m.kind) {
    // /clear: THE TRANSCRIPT EMPTIES, as the terminal's tab does. resetView, but the count is kept: this event is part
    // of the stream, and what follows it continues from here.
    // WHAT WAITS FOR THE RUNNING TURN, whole; empty once the turn took it or it went back to a composer.
    case "queued":
      view.queued = m.text;
      if (state.active === m.sessionId) drawQueued();
      break;
    case "cleared": {
      const seq = view.seq;
      resetView(view);
      view.seq = seq;
      break;
    }
    case "user": {
      const u = el("div", "user", m.text);
      u.dataset.turn = String(m.turn);
      append(view, u);
      break;
    }
    case "images": {
      // ON THE MESSAGE THAT CARRIED THEM, as the terminal shows a thumbnail and a button on it.
      const u = view.log.querySelector('.user[data-turn="' + m.turn + '"]');
      if (!u) break;
      const row = el("div", "images");
      for (const i of m.images) {
        if (!i.src) { row.append(el("span", "image-missing", i.label)); continue; }
        const img = el("img");
        img.src = i.src;
        img.alt = i.label;
        img.title = i.label + " — click to see it whole";
        img.addEventListener("click", () => viewImage(i.src, i.label));
        row.append(img);
      }
      u.append(row);
      break;
    }
    case "asideAsked": {
      // AN ASIDE IS ITS OWN BLOCK, marked so it is never read as the main conversation, which it does not change.
      const box = el("div", "aside");
      const head = el("div", "aside-head");
      const spin = el("span", "spin small");
      head.append(el("span", "tag", "btw"), el("span", "q", m.text), spin);
      const body = el("div", "md");
      box.append(head, body);
      view.asides = view.asides || new Map();
      view.asides.set(m.aside, { box, spin, body, text: "", thinking: null });
      append(view, box);
      break;
    }
    case "asideThinking": {
      const a = view.asides && view.asides.get(m.aside);
      if (!a) break;
      if (!a.thinking) {
        const d = el("details", "thinking");
        const sm = el("summary");
        sm.append(el("span", "mark", "◆"), document.createTextNode(" Thinking"));
        const pre = el("pre");
        d.append(sm, pre);
        a.thinking = { d, sm, pre };
        a.box.insertBefore(d, a.body);
      }
      a.thinking.pre.textContent += m.text;
      break;
    }
    case "asideThinkingEnded": {
      const a = view.asides && view.asides.get(m.aside);
      if (a && a.thinking) a.thinking.sm.replaceChildren(el("span", "mark", "◆"), document.createTextNode(" Thought · " + m.words + " words"));
      break;
    }
    case "asideLookedUp": {
      const a = view.asides && view.asides.get(m.aside);
      if (a) a.box.insertBefore(el("div", "aside-look", "↳ " + m.text), a.body);
      break;
    }
    case "asideText": {
      const a = view.asides && view.asides.get(m.aside);
      if (!a) break;
      a.text += m.text;
      const stick = isAtBottom();
      a.body.innerHTML = render(a.text);
      if (view.id === state.active) scrollDown(stick);
      break;
    }
    case "asideEnded": {
      const a = view.asides && view.asides.get(m.aside);
      if (!a) break;
      a.spin.remove();
      if (m.end !== "Answered") a.box.append(el("div", "said Warning", m.end === "Stopped" ? "stopped" : m.end === "Capped" ? "answered from what it had" : "failed" + (m.detail ? ": " + m.detail : "")));
      break;
    }
    case "turnBegan":
      turnOf(view, m.turn);
      setWorking(view, true);
      break;
    case "text": {
      const t = turnOf(view, m.turn);
      t.empty = false;
      const seg = segment(view, t);
      seg.text += m.text;
      paint(view, seg);
      break;
    }
    case "thinking": {
      const t = turnOf(view, m.turn);
      if (!t.thinking) {
        const d = el("details", "thinking");
        const s = el("summary");
        s.append(el("span", "mark", "◆"), ticking(null, Date.now(), "Thinking · "));
        const pre = el("pre");
        d.append(s, pre);
        t.thinking = { d, s, pre };
        t.empty = false;
        view.segment = null;
        view.round = null;
        into(view, t.node, d);
      }
      t.thinking.pre.textContent += m.text;
      break;
    }
    case "thinkingEnded": {
      const t = turnOf(view, m.turn);
      if (!t.thinking) break;
      if (m.hidden && !t.thinking.pre.textContent) t.thinking.d.remove();
      else t.thinking.s.replaceChildren(el("span", "mark", "◆"), document.createTextNode((m.seconds == null ? "Thought" : "Thought for " + m.seconds + "s") + " · " + m.words + " words"));
      t.thinking = null;
      break;
    }
    case "labelled": turnOf(view, m.turn).label.textContent = m.text; break;
    case "turnEnded":
      settleTurn(view, view.turns.get(m.turn));
      setWorking(view, false);
      break;
    case "said": {
      const n = el("div", "said " + (m.severity || "Info"));
      n.innerHTML = render(m.text || "");
      append(view, n);
      break;
    }
    // A WORKER WORTH KEEPING, with the terminal's two buttons. Answered in any tab, every tab takes it away — and a
    // tab replaying the log meets the answer right after the offer.
    case "keepSuggestion": {
      const n = el("div", "said Info keep-offer");
      n.dataset.handle = m.handle;
      n.append(el("div", null, m.text));
      const btns = el("div", "btns");
      const answer = (choice) => send({ type: "keepSuggestion", sessionId: view.id, handle: m.handle, choice });
      btns.append(button("Keep", null, () => answer("keep")), button("Not this one", "ghost", () => answer("dismiss")));
      n.append(btns);
      append(view, n);
      break;
    }
    case "keepAnswered":
      for (const n of view.log.querySelectorAll(".keep-offer")) if (n.dataset.handle === m.handle) n.remove();
      break;
    case "toolStarted": {
      if (m.jobType === "llm_agent") { startWorker(view, m); break; }
      const card = m.agent && view.agentId && m.agent !== view.agentId ? cardFor(view, m.agent) : null;
      if (card) startWorkerCall(view, m, card);
      else startCall(view, m);
      break;
    }
    case "toolProgress": {
      const card = view.cards.get(m.job);
      if (card) {
        if (m.progress) card.progress.textContent = m.progress;
        if (m.body) { card.facts.textContent = m.body; card.facts.hidden = false; }
        if (m.runsOn) card.model.textContent = shortModel(m.runsOn);
        break;
      }
      const call = view.tools.get(m.job);
      if (call && m.progress) { call.progress = m.progress; if (call.owner) drawRound(call.owner); }
      break;
    }
    case "toolOutput": {
      const call = view.tools.get(m.job);
      if (call) call.pre.textContent += m.text;
      break;
    }
    case "toolEnded": {
      const card = view.cards.get(m.job);
      if (!card) { endCall(view, m); break; }
      card.ended = true;
      if (m.progress) card.progress.textContent = m.progress;
      if (m.body) { card.facts.textContent = m.body; card.facts.hidden = false; }
      if (m.error) { card.facts.textContent += (card.facts.textContent ? "\n" : "") + m.error; card.facts.hidden = false; }
      markCard(card, m.state === "Succeeded" ? "done" : "failed");
      // THE PANEL MAY ALREADY KNOW BETTER: a worker sent to the background is still running when its row ends.
      liveCards(view.kind === "worker" ? view.parent : view.id);
      break;
    }
    case "workerCall": workerCall(view, m); break;
    case "worker": {
      const card = view.cards.get(m.job);
      if (!card) break;
      card.agentId = m.agentId;
      card.open.disabled = false;
      if (m.workerType) card.who.textContent = m.workerType;
      if (m.badge) card.who.textContent = m.badge.glyph + " " + m.badge.name;
      // THE TITLE STAYS THE TITLE, as the terminal's row keeps its short description: the full brief is the card's hover,
      // and the worker's own tab shows it in full.
      if (m.task) card.task.title = m.task;
      break;
    }
  }
}

function setWorking(view, working) {
  view.working = working;
  if (view.id === state.active) drawStop();
}

function drawStop() {
  const view = state.views.get(state.active);
  const panel = state.panels.get(activeSessionId());
  const working = !!view && view.kind !== "worker" && (view.working || !!(panel && panel.session.working));
  $("stop").hidden = !working;
}

// ---- the dropdowns ----------------------------------------------------------------------------------------------

// THE PANEL CARRIES "AcceptEdits"; the /mode command's names are "accept-edits". Compared without case or punctuation.
const norm = (s) => String(s || "").toLowerCase().replace(/[^a-z]/g, "");

function options(select, choices, current, label) {
  select.replaceChildren(...choices.map((c) => {
    const o = el("option", null, label ? label(c.name) : c.name);
    o.value = c.name;
    o.title = c.summary || "";
    return o;
  }));
  const match = [...select.options].find((o) => norm(o.value) === norm(current));
  if (match) select.value = match.value;
  else if (current) { select.prepend(new Option(current, current)); select.value = current; }
}

function drawPicks() {
  const panel = state.panels.get(activeSessionId());
  for (const id of ["pick-model", "pick-mode", "pick-sandbox"]) $(id).hidden = !panel;
  drawAside(panel);
  if (!panel) { $("pick-think").hidden = true; return; }
  // A SELECT THE USER HAS OPEN IS LEFT ALONE: the panel arrives every second, and rebuilding it would shut the list.
  const busy = document.activeElement;
  if (busy !== $("model")) options($("model"), panel.choices.models || [], panel.model);
  if (busy !== $("mode")) options($("mode"), panel.choices.modes || [], panel.mode, (n) => n.replace(/-/g, " "));

  // HOW HARD IT THINKS: the levels the model itself offers, then its own default — /think and F10's choices. Hidden
  // until the model has said, and for one that offers none.
  const th = panel.thinking || { levels: [] };
  $("pick-think").hidden = !th.levels.length;
  if (th.levels.length && busy !== $("think")) {
    const current = th.pending || th.level || "default";
    options($("think"), [...th.levels.map((l) => ({ name: l })), { name: "default", summary: "the model's own" }], current,
      (n) => (n === "default" ? "default thinking" : "think " + n) + (th.pending && n === th.pending ? " (next message)" : ""));
  }

  const sb = $("sandbox");
  const on = panel.sandbox.enabled;
  const status = panel.sandbox.status;
  if (busy !== sb) {
    sb.replaceChildren(new Option(status === "Failed" ? "sandbox failed" : status === "Unavailable" ? "no sandbox" : "sandboxed", "on"),
      new Option("unsandboxed", "off"));
    sb.value = on ? "on" : "off";
  }
  $("pick-sandbox").className = "pick " + (on && status !== "Failed" && status !== "Unavailable" ? "good" : "warn");
  $("pick-sandbox").title = on ? "Commands run in the sandbox" : "Commands run outside the sandbox, for this session only";
}

$("model").addEventListener("change", (e) => {
  const sid = activeSessionId();
  if (sid) send({ type: "say", sessionId: sid, text: "/model " + e.target.value });
  e.target.blur();
});
$("mode").addEventListener("change", (e) => {
  const sid = activeSessionId();
  if (sid) send({ type: "say", sessionId: sid, text: "/mode edits " + e.target.value });
  e.target.blur();
});
$("think").addEventListener("change", (e) => {
  const sid = activeSessionId();
  if (sid) send({ type: "say", sessionId: sid, text: "/think " + e.target.value });
  e.target.blur();
});

// BTW MODE: what is typed goes to the aside until it is left — Esc, as in the terminal, or the bar's button.
function drawAside(panel) {
  const on = !!(panel && panel.aside) && state.views.get(state.active)?.kind === "session";
  $("aside-bar").hidden = !on;
  document.body.classList.toggle("in-aside", on);
}
function leaveAside() {
  const sid = activeSessionId();
  if (sid) send({ type: "leaveAside", sessionId: sid });
}
$("aside-leave").addEventListener("click", leaveAside);

$("sandbox").addEventListener("change", (e) => {
  const sid = activeSessionId();
  const off = e.target.value === "off";
  e.target.blur();
  if (!sid) return;
  if (!off) { send({ type: "setSandbox", sessionId: sid, enabled: true }); return; }
  // TURNING IT OFF WIDENS WHAT COMMANDS MAY TOUCH, so the page asks first; until then the select shows it on.
  e.target.value = "on";
  const box = el("div", "prompt");
  box.append(el("div", "q", "Run this session's commands outside the sandbox?"),
    el("div", "why", "They run as you and can reach anything you can. Only this session; config.json is not changed."));
  const btns = el("div", "btns");
  btns.append(button("Turn it off", "danger", () => { box.remove(); send({ type: "setSandbox", sessionId: sid, enabled: false }); }),
    button("Keep it on", null, () => box.remove()));
  box.append(btns);
  $("prompts").append(box);
});

// ---- the side panel ---------------------------------------------------------------------------------------------

function kv(label, value, cls) {
  const r = el("div", "kv" + (cls ? " " + cls : ""));
  r.append(el("span", null, label), el("b", null, value));
  return r;
}

function drawPanel() {
  const sid = activeSessionId();
  const panel = state.panels.get(sid);
  for (const tab of document.querySelectorAll(".ptab")) tab.classList.toggle("on", tab.dataset.pane === state.pane);
  $("pane-session").hidden = state.pane !== "session";
  $("pane-tasks").hidden = state.pane !== "tasks";
  const running = panel ? panel.workers.filter((w) => w.running).length + panel.commands.filter((c) => c.state === "Running").length : 0;
  $("tasks-count").hidden = running === 0;
  $("tasks-count").textContent = String(running);
  // THE COUNT BESIDE ◧ WHILE THE PANEL IS HIDDEN, as the terminal's F5 item carries it: the Tasks tab that shows it is
  // hidden with the panel.
  $("panel-count").hidden = running === 0 || !document.body.classList.contains("no-panel");
  $("panel-count").textContent = String(running);
  $("panel-toggle").title = running > 0 && !$("panel-count").hidden ? "Side panel · " + running + " running" : "Side panel";
  drawStatusline(panel);
  if (!panel) {
    $("pane-session").replaceChildren(el("p", "none", "Open a session to see its figures."));
    $("pane-tasks").replaceChildren(el("p", "none", "Open a session to see its workers and commands."));
    return;
  }
  // NOT WHILE SOMETHING IN IT HAS THE KEYBOARD: the panel arrives every second, and rebuilding the pane would throw
  // away the path being typed into "Open a file".
  if (!$("pane-session").contains(document.activeElement)) drawSessionPane(panel);
  drawTasksPane(panel, sid);
}

function drawStatusline(panel) {
  const line = $("statusline");
  if (!panel) { line.textContent = ""; return; }
  const c = panel.context;
  const pct = c.used && c.window ? Math.round(100 * c.used / c.window) + "% context" : "";
  // WHO IS BEING DISTILLED, as the terminal's status bar says it.
  const distilling = panel.colleagues && panel.colleagues.distilling.length ? "distilling " + panel.colleagues.distilling.join(", ") + "…" : "";
  line.textContent = [shortFolder(panel.here.folder), panel.here.git, panel.model, pct, tokens(panel.tokens.total) + " tokens",
    money(panel.tokens.cost), distilling].filter(Boolean).join("  ·  ");
}

function drawSessionPane(p) {
  const pane = $("pane-session");
  pane.replaceChildren(el("h3", null, "Context"));
  const used = p.context.used || 0;
  if (p.context.window) {
    const pct = Math.min(100, Math.round(100 * used / p.context.window));
    pane.append(kv(tokens(used) + " of " + tokens(p.context.window), pct + "%"));
    const meter = el("div", "meter" + (pct >= 80 ? " warn" : ""));
    const fill = el("i");
    fill.style.width = pct + "%";
    meter.append(fill);
    pane.append(meter);
  } else pane.append(kv("Used", tokens(used)));

  pane.append(el("h3", null, "Session"));
  const time = kv("Time", "");
  time.querySelector("b").append(ticking(null, new Date(p.session.since).getTime()));
  pane.append(time, kv("Turns", String(p.session.turns)), kv("Tool calls", String(p.session.toolCalls)));

  pane.append(el("h3", null, "Tokens"));
  for (const m of p.tokens.models) pane.append(kv(m.name, tokens(m.tokens) + (m.cost != null ? " · " + money(m.cost) : "")));
  pane.append(kv("In / out", "↑ " + tokens(p.tokens.input) + "  ↓ " + tokens(p.tokens.output)));
  if (p.tokens.cacheHitRate != null) pane.append(kv("Cache hits", Math.round(p.tokens.cacheHitRate * 100) + "%"));
  if (p.tokens.workers) pane.append(kv("By workers", tokens(p.tokens.workers)));
  if (p.tokens.cost != null) pane.append(kv("Cost", money(p.tokens.cost), "total"));

  // F12: the conversation as markdown, chosen in the terminal's export form.
  pane.append(el("h3", null, "Export"));
  const exp = el("div", "btns export");
  exp.append(button("Export…", null, () => { send({ type: "exportForm", sessionId: activeSessionId() }); if (!wide()) setPanel(false); }));
  exp.title = "Export this conversation as markdown";
  pane.append(exp);

  if (state.access && state.access.shell) {
    pane.append(el("h3", null, "Shell"));
    const sh = el("div", "btns export");
    sh.append(button("Open a shell", null, () => { send({ type: "openShell", sessionId: activeSessionId() }); if (!wide()) setPanel(false); }));
    pane.append(sh);
  }

  if (state.access && state.access.plugins) {
    pane.append(el("h3", null, "Plugins"));
    const pl = el("div", "btns export");
    pl.append(button("Marketplace", null, () => { openPlugins(false); if (!wide()) setPanel(false); }));
    pane.append(pl);
  }

  if (state.access && state.access.config) {
    pane.append(el("h3", null, "Configuration"));
    const cf = el("div", "btns export");
    cf.append(button("Settings", null, () => { send({ type: "configForm", sessionId: configSession(), tab: "general" }); if (!wide()) setPanel(false); }));
    pane.append(cf);
  }

  pane.append(el("h3", null, "Open a file"));
  const of = el("form", "open-file");
  const ofIn = el("input");
  ofIn.placeholder = "path, relative to the folder";
  ofIn.setAttribute("aria-label", "File to open");
  of.append(ofIn, button("Open", null, () => of.requestSubmit()));
  of.addEventListener("submit", (e) => {
    e.preventDefault();
    if (ofIn.value.trim()) send({ type: "openFile", sessionId: activeSessionId(), path: ofIn.value.trim() });
    if (!wide()) setPanel(false);
  });
  pane.append(of);

  // THE SWITCHES, READ-ONLY: what a browser may do beyond the conversation. Only the terminal changes them.
  if (state.access) {
    pane.append(el("h3", null, "Browser access"));
    const a = state.access;
    for (const [label, on] of [["Plugins", a.plugins], ["Open files anywhere", a.openAnywhere], ["Shell", a.shell], ["Configuration", a.config]])
      pane.append(kv(label, on ? "on" : "off", on ? null : "off"));
    const roots = kv("Folders", (a.roots || []).map(shortFolder).join(", ") || "—");
    roots.title = (a.roots || []).join("\n");
    pane.append(roots);
    pane.append(el("p", "none", "Changed in the terminal: F8 › Web › Browser access."));
  }

  pane.append(el("h3", null, "Here"));
  const folder = kv("Folder", shortFolder(p.here.folder));
  folder.title = p.here.folder;
  pane.append(folder);
  if (p.here.git) {
    pane.append(kv("Branch", p.here.git));
    pane.append(kv("Working tree", p.here.gitTree || "clean"));
  }
  pane.append(kv("Skills loaded", String(p.here.skills)));
  pane.append(kv("Always-allow rules", String(p.here.permissions)));
  pane.append(kv("Turn limit", String(p.here.turnCap)));
  for (const s of p.here.mcp) pane.append(kv("MCP · " + s.name, s.failed ? "failed" : s.tools + " tools", s.failed ? "bad" : null));

  // THE NOTES THE AGENT KEEPS, as the files have them now — absent when memory is off. Open is the editor, by name;
  // Forget asks first, on the server's sentence.
  if (p.memory) {
    pane.append(el("h3", null, "Memory"));
    if (!p.memory.length) pane.append(el("p", "none", "No notes yet. The agent keeps them as it learns about you and this project."));
    for (const n of p.memory) {
      const acts = [
        button("Open", null, () => { send({ type: "memoryOpen", sessionId: activeSessionId(), name: n.name }); if (!wide()) setPanel(false); }),
        button("Forget", "danger", () => send({ type: "memoryForget", sessionId: activeSessionId(), name: n.name })),
      ];
      pane.append(taskRow("", el("b", null, n.name), [n.scope, n.description].filter(Boolean).join(" · "), acts));
    }
  }
  // NO COLLEAGUES HERE: the Tasks pane has them — the ones here as cards, the rest with Bring — as the terminal's F7.
}

function taskRow(dot, head, sub, actions) {
  const r = el("div", "task");
  r.append(el("span", "dot " + dot));
  const t = el("div", "t");
  t.append(head, el("small", null, sub));
  const acts = el("div", "acts");
  acts.append(...actions);
  r.append(t, acts);
  return r;
}

// THE TERMINAL'S F7 COLLEAGUES SECTION: the ones here as cards — how each stands, what it was asked, what it knows —
// and the library's others under Available with Bring. Both read from this session, so a switch switches them. No type
// and never "done": a colleague is what it has learned, and idle between tasks.
function drawColleagueCards(pane, p, sid) {
  const here = p.workers.filter((w) => w.colleague);
  const away = p.colleagues ? p.colleagues.rows.filter((c) => !c.handle) : [];
  if (!here.length && !away.length) return;
  pane.append(el("h3", null, "Colleagues"));
  for (const w of here) {
    const c = w.colleague;
    const open = () => { openWorker(sid, w.agentId); if (!wide()) setPanel(false); };
    // ITS OWN CARD, NOT A TASK ROW: the name gets the whole width and the buttons a row of their own — beside them a
    // long name was cut to "Explore SharpConso…", and the state and task wrapped down the pane.
    const card = el("div", "colleague-card" + (w.running ? " working" : ""));
    const head = el("div", "cc-head");
    head.append(el("span", "dot " + (w.running ? "working" : c.learning && !c.frozen ? "held" : "")), el("b", null, w.badge.glyph + " " + w.badge.name));
    head.title = w.badge.name;
    const state = el("div", "cc-state" + (w.running ? " working" : /failed/.test(c.state) ? " failed" : ""),
      w.running ? c.state + " · " + clock(w.seconds * 1000) : c.state);
    card.append(head, state);
    if (c.task) {
      const task = el("div", "cc-task", c.task);
      task.title = c.task;
      card.append(task);
    }
    card.append(el("div", "cc-knows", "v" + c.version + (c.knows.length ? " · knows " + c.knows.join(", ") : "")));
    const acts = el("div", "cc-acts");
    acts.append(button("Open", null, open), button("Ask…", null, () => { open(); setTimeout(() => $("input").focus(), 0); }));
    if (w.running) acts.append(button("Stop", "danger", () => send({ type: "workerAction", sessionId: sid, agentId: w.agentId, action: "stop" })));
    card.append(acts);
    pane.append(card);
  }
  if (away.length) {
    pane.append(el("p", "none", "Available"));
    for (const c of away)
      pane.append(taskRow("", el("b", null, c.name), c.description || "",
        [button("Bring", null, () => send({ type: "colleagueBring", sessionId: sid, name: c.name }))]));
  }
}

function drawTasksPane(p, sid) {
  const pane = $("pane-tasks");
  pane.replaceChildren();
  drawColleagueCards(pane, p, sid);
  pane.append(el("h3", null, "Workers"));
  const workers = p.workers.filter((w) => !w.colleague);
  if (!workers.length) pane.append(el("p", "none", "No workers in this session."));
  for (const w of workers) {
    const acts = [button("Open", null, () => { openWorker(sid, w.agentId); if (!wide()) setPanel(false); })];
    if (w.running) acts.push(button("Stop", "danger", () => send({ type: "workerAction", sessionId: sid, agentId: w.agentId, action: "stop" })));
    // F7's x ON A FINISHED CARD: the worker and its conversation go; the server asks first if its commands still run.
    else acts.push(button("Delete", "danger", () => send({ type: "workerAction", sessionId: sid, agentId: w.agentId, action: "delete" })));
    const sub = [w.badge ? w.badge.kind.toLowerCase() : "", w.type, w.state.toLowerCase(), clock(w.seconds * 1000), w.background ? "background" : ""].filter(Boolean).join(" · ");
    const row = taskRow(w.running ? "working" : w.state === "Failed" ? "held" : "", el("b", null, w.badge ? w.badge.glyph + " " + w.badge.name : w.name), sub, acts);
    if (w.task) row.querySelector(".t").append(el("small", "task-text", w.task));
    pane.append(row);
  }
  pane.append(el("h3", null, "Commands"));
  if (!p.commands.length) pane.append(el("p", "none", "No background commands."));
  for (const c of p.commands) {
    const running = c.state === "Running";
    const acts = [button("Open", null, () => { openCommand(sid, c.pid); if (!wide()) setPanel(false); })];
    if (running) acts.push(button("Stop", "danger", () => send({ type: "stopCommand", sessionId: sid, pid: c.pid })));
    const head = el("code", null, c.command);
    head.title = c.command;
    pane.append(taskRow(running ? "working" : "", head,
      "pid " + c.pid + " · " + (running ? "running" : c.exitCode != null ? "exit " + c.exitCode : c.state.toLowerCase()) + " · " + clock(c.seconds * 1000), acts));
  }
}

// ---- prompts ----------------------------------------------------------------------------------------------------

function prompt(m) {
  state.prompts.set(m.id, { message: m, answeredHere: false });
  drawPrompts();
  const row = state.rows.find((r) => r.id === m.sessionId);
  if (row) { row.state = "waiting"; drawSessions(); }
}

function resolved(id, by) {
  const p = state.prompts.get(id);
  state.prompts.delete(id);
  drawPrompts();
  // SAID ONLY WHEN SOMEONE ELSE ANSWERED — the terminal, or another tab. This tab knows what it clicked.
  if (p && by && !p.answeredHere) {
    const view = state.views.get(p.message.sessionId);
    if (view) append(view, el("div", "said", "answered by " + by));
  }
  send({ type: "list" });
}

function drawPrompts() {
  // ONLY THE SERVER'S PROMPTS ARE REDRAWN; a page-made one (close anyway, sandbox off) stays until it is answered.
  // A PROMPT STILL WAITING KEEPS ITS NODE — what is half typed, where the caret is — and one that comes back after
  // a switch of tab is rebuilt from the state kept on its entry, so a third question is not reset to the first.
  const box = $("prompts");
  const sid = activeSessionId();
  const kept = new Map();
  for (const n of [...box.children]) if (n.dataset.prompt) { kept.set(n.dataset.prompt, n); n.remove(); }
  for (const p of state.prompts.values()) {
    if (p.message.sessionId !== sid) continue;
    const node = kept.get(p.message.id) || promptNode(p.message);
    node.dataset.prompt = p.message.id;
    box.append(node);
  }
}

function button(text, cls, onClick) {
  const b = el("button", cls, text);
  b.type = "button";
  b.addEventListener("click", onClick);
  return b;
}

function promptNode(m) {
  const box = el("div", "prompt");
  const answer = (fields) => {
    const p = state.prompts.get(m.id);
    if (p) p.answeredHere = true;
    send(Object.assign({ type: "answer", promptId: m.id }, fields));
  };
  const btns = el("div", "btns");
  if (m.kind === "permission") {
    box.append(el("div", "q", m.heading || "Allow this?"), el("div", "what", m.display));
    if (m.requester) box.append(el("div", "why", "asked by " + m.requester));
    btns.append(button("Allow once", "good", () => answer({ choice: "once" })));
    if (m.offerAlways) btns.append(button("Always allow" + (m.alwaysCovers ? " (" + m.alwaysCovers + ")" : ""), null, () => answer({ choice: "always" })));
    if (m.offerTrust) btns.append(button("Trust folder", null, () => answer({ choice: "trust" })));
    btns.append(button("Deny", "danger", () => answer({ choice: "deny" })));
  } else if (m.kind === "trust") {
    // THE TERMINAL'S WORDS AND ORDER, sent by the server: the cautious answer first, as the one Enter would take.
    box.append(el("div", "q", "Trust this folder?"), el("div", "what", m.folder));
    for (const line of m.lines || []) box.append(el("div", "why", line));
    for (const a of m.answers || []) btns.append(button(a.label, a.trusted ? "danger" : "good", () => answer({ trusted: a.trusted })));
  } else if (m.kind === "question") {
    // ONE QUESTION AT A TIME, AS THE TERMINAL ASKS: "Question 2 of 3", Back and Next (Alt+← / Alt+→), and — for more
    // than one — "Your answers" before anything is sent, the last chance to change a decision the model will act on.
    // What is answered is kept while stepping back and forth; a question left blank is skipped, "you decide".
    // ON THE PROMPT'S ENTRY, not in this node: the node is rebuilt when the tab comes back, and the answers and the
    // step must survive that. THE PICKS STAY A LIST until they are sent, so a label holding ", " is still one pick.
    const entry = state.prompts.get(m.id) || {};
    const qs = m.questions;
    const st = entry.ui || (entry.ui = { picked: qs.map(() => []), other: qs.map(() => ""), at: 0 });
    const step = st;
    const said = (i) => st.picked[i].concat(st.other[i].trim() ? [st.other[i].trim()] : []);
    const answers = () => qs.map((_, i) => said(i).join(", "));
    const stage = el("div", "question-steps");
    box.append(stage);
    const read = [];
    const show = () => {
      stage.replaceChildren();
      btns.replaceChildren();
      if (step.at >= qs.length) {
        stage.append(el("div", "q", "Your answers"));
        qs.forEach((q, i) => {
          const row = el("div", "answer-row");
          const a = said(i).join(", ");
          row.append(el("span", "meta", q.header || q.question), el("span", a ? null : "meta", a || "(skipped — you decide)"));
          stage.append(row);
        });
        btns.append(button("Send", "good", () => answer({ answers: answers() })), button("Change the last one", null, () => { step.at = qs.length - 1; show(); }));
        return;
      }
      const q = qs[step.at];
      if (qs.length > 1) stage.append(el("div", "meta", "Question " + (step.at + 1) + " of " + qs.length + (q.header ? " · " + q.header : "")));
      else if (q.header) stage.append(el("div", "meta", q.header));
      stage.append(el("div", "q", q.question));
      const name = "q" + Math.random().toString(36).slice(2);
      const picks = [];
      const had = st.picked[step.at];
      for (const o of q.options) {
        const label = el("label");
        const input = el("input");
        input.type = q.multiple ? "checkbox" : "radio";
        input.name = name;
        input.value = o.label;
        input.checked = had.includes(o.label);
        input.addEventListener("change", () => read[0]());
        label.append(input, el("span", null, o.label), o.description ? el("small", null, o.description) : "");
        stage.append(label);
        picks.push(input);
      }
      const other = el("input");
      other.type = "text";
      other.placeholder = q.options.length ? "or type your own answer" : "your answer";
      other.value = st.other[step.at];
      other.addEventListener("input", () => read[0]());
      stage.append(other);
      read[0] = () => {
        st.picked[step.at] = picks.filter((i) => i.checked).map((i) => i.value);
        st.other[step.at] = other.value;
      };
      const next = () => { read[0](); step.at = qs.length === 1 ? qs.length : step.at + 1; if (qs.length === 1) answer({ answers: answers() }); else show(); };
      if (step.at > 0) btns.append(button("Back", null, () => { read[0](); step.at--; show(); }));
      btns.append(button(qs.length === 1 ? "Answer" : step.at === qs.length - 1 ? "Review" : "Next", "good", next));
      btns.append(button("Skip all", null, () => answer({ cancelled: true })));
      box.onkeydown = (e) => {
        if (!e.altKey || step.at >= qs.length) return;
        if (e.key === "ArrowLeft" && step.at > 0) { e.preventDefault(); read[0](); step.at--; show(); }
        if (e.key === "ArrowRight") { e.preventDefault(); next(); }
      };
      (picks[0] || other).focus?.();
    };
    show();
  }
  box.append(btns);
  return box;
}

// A DELETED NOTE CANNOT BE BROUGHT BACK, so forgetting one asks — the terminal's Forget it / Keep buttons, here.
function confirmForget(m) {
  if (activeSessionId() !== m.sessionId) activate(m.sessionId);
  const box = el("div", "prompt");
  box.append(el("div", "q", m.text));
  const btns = el("div", "btns");
  btns.append(button("Forget it", "danger", () => { box.remove(); send({ type: "memoryForget", sessionId: m.sessionId, name: m.name, confirm: true }); }),
    button("Keep", null, () => box.remove()));
  box.append(btns);
  $("prompts").append(box);
}

// A COLLEAGUE FORGOTTEN FOR GOOD: the desk's question, word for word as the terminal asks it, with its buttons. A yes
// asks the library again when its page is open here, so the page drops the colleague — and never opens it otherwise.
function confirmColleagueForget(m) {
  if (activeSessionId() !== m.sessionId) activate(m.sessionId);
  const box = el("div", "prompt");
  box.append(el("div", "q", m.text));
  const btns = el("div", "btns");
  btns.append(button("Forget it", "danger", () => {
    box.remove();
    send({ type: "colleagueForget", sessionId: m.sessionId, name: m.name, confirm: true });
    if (state.views.has(COLLEAGUES_ID)) send({ type: "colleagues", sessionId: m.sessionId });
  }), button("Keep", null, () => box.remove()));
  box.append(btns);
  $("prompts").append(box);
}

// A COLLEAGUE'S ROUNDS DISTILLED AGAIN: the desk's question, as the terminal asks it. The yes sends back the order the
// question named — its round and model — so what runs is what was asked; what it learned is said in the conversation.
function confirmColleagueDistill(m) {
  if (activeSessionId() !== m.sessionId) activate(m.sessionId);
  const box = el("div", "prompt");
  box.append(el("div", "q", m.text));
  const btns = el("div", "btns");
  btns.append(button("Distil", null, () => {
    box.remove();
    send({ type: "colleagueDistill", sessionId: m.sessionId, name: m.name, from: m.from, model: m.model, confirm: true });
  }), button("Cancel", null, () => box.remove()));
  box.append(btns);
  $("prompts").append(box);
}

// THE QUESTION IN THE ROW IT IS ABOUT: no session needs to be open to delete one. The next redraw of the list puts the
// row back as it was, which is also what Keep does.
function confirmDelete(m) {
  const row = [...document.querySelectorAll("#sessions .row")].find((r) => r.dataset.id === m.id);
  if (!row) { flash(m.text); return; }
  row.classList.add("asking");
  const ask = el("div", "ask");
  ask.append(el("small", null, m.text));
  const btns = el("div", "btns");
  btns.append(button("Delete", "danger", (e) => { e?.stopPropagation(); send({ type: "deleteSession", id: m.id, confirm: true }); }),
    button("Keep", null, (e) => { e?.stopPropagation(); drawSessions(); }));
  ask.append(btns);
  ask.addEventListener("click", (e) => e.stopPropagation());
  row.replaceChildren(ask);
}

// A FINISHED WORKER WHOSE COMMANDS STILL RUN: the terminal's three answers — stop them, keep them, or not now.
function confirmWorkerDelete(m) {
  const box = el("div", "prompt");
  box.append(el("div", "q", m.text));
  const btns = el("div", "btns");
  const answer = (keep) => { box.remove(); send({ type: "workerAction", sessionId: m.sessionId, agentId: m.agentId, action: "delete", keepCommands: keep }); };
  btns.append(button("Stop them and delete", "danger", () => answer(false)),
    button("Keep them running", null, () => answer(true)),
    button("Cancel", null, () => box.remove()));
  box.append(btns);
  $("prompts").append(box);
}

function confirmClose(m) {
  if (activeSessionId() !== m.sessionId) activate(m.sessionId);
  const box = el("div", "prompt");
  box.append(el("div", "q", m.text));
  const btns = el("div", "btns");
  btns.append(button("Close anyway", "danger", () => { box.remove(); send({ type: "close", sessionId: m.sessionId, confirm: true }); }),
    button("Keep it", null, () => box.remove()));
  box.append(btns);
  $("prompts").append(box);
}

// A CLOSED SESSION TAKES ITS TABS WITH IT: its workers, files, commands and shells — a shell's own end said in it first.
function closed(id) {
  for (const [key, view] of [...state.views]) {
    // THE CONFIG AND PLUGINS PAGES ARE THE SERVER'S, NOT THE SESSION'S: they only remember where they were opened from.
    if (view.kind === "config" || view.kind === "plugins") { if (view.parent === id) view.parent = null; continue; }
    if (key !== id && view.parent !== id) continue;
    if (view.kind === "shell") shellEnded(view, null, "the session closed, so the shell ended");
    if (view.term) view.term.dispose();
    state.views.delete(key);
  }
  for (const [pid, p] of state.prompts) if (p.message.sessionId === id) state.prompts.delete(pid);
  state.panels.delete(id);
  if (!state.views.has(state.active)) activate(state.views.keys().next().value || null);
  else drawTabs();
}

// ---- composer ---------------------------------------------------------------------------------------------------

const input = $("input");

// ---- images in the composer: pasted or dropped, shown as chips, sent with the message ----------------------------

const attached = [];   // { name, data (base64), url }
const MAX_IMAGE_BYTES = 16 * 1024 * 1024;

function drawAttachments() {
  const box = $("attachments");
  box.replaceChildren(...attached.map((a, i) => {
    const chip = el("span", "chip");
    const img = el("img");
    img.src = a.url;
    img.alt = a.name;
    const x = el("button", "x icon", "×");
    x.type = "button";
    x.title = "Remove this image";
    x.addEventListener("click", () => { attached.splice(i, 1); drawAttachments(); });
    chip.append(img, el("span", null, a.name), x);
    return chip;
  }));
  box.hidden = attached.length === 0;
}

function attachFiles(files) {
  for (const f of files) {
    if (!f.type.startsWith("image/")) continue;
    if (f.size > MAX_IMAGE_BYTES) { flash(f.name + " is too large to send."); continue; }
    const reader = new FileReader();
    reader.onload = () => {
      // A DATA URL FOR THE THUMBNAIL, not a blob: one — the page's policy lets images come from itself and data: only.
      const url = String(reader.result);
      attached.push({ name: f.name || "pasted image", data: url.split(",")[1] || "", url });
      drawAttachments();
    };
    reader.readAsDataURL(f);
  }
}

input.addEventListener("paste", (e) => {
  const files = [...(e.clipboardData?.files || [])].filter((f) => f.type.startsWith("image/"));
  // TEXT WINS, as in the terminal: a spreadsheet copies its cells and a picture of them, and the cells were meant.
  if (!files.length || (e.clipboardData.getData("text/plain") || "").trim()) return;
  e.preventDefault();
  attachFiles(files);
});
$("composer").addEventListener("dragover", (e) => { if ([...(e.dataTransfer?.types || [])].includes("Files")) { e.preventDefault(); $("composer").classList.add("drop"); } });
$("composer").addEventListener("dragleave", () => $("composer").classList.remove("drop"));
$("composer").addEventListener("drop", (e) => {
  $("composer").classList.remove("drop");
  if (!e.dataTransfer?.files?.length) return;
  e.preventDefault();
  attachFiles(e.dataTransfer.files);
});

$("composer").addEventListener("submit", (e) => {
  e.preventDefault();
  const text = input.value;
  if ((!text.trim() && !attached.length) || !state.active) return;
  // THE SERVER TAKES 24 MB A MESSAGE, base64 and all: over it, the images stay here to be trimmed rather than lost.
  const size = attached.reduce((n, a) => n + a.data.length, 0);
  if (size > 22 * 1024 * 1024) { flash("These images are too large to send together — remove one or two."); return; }
  const message = { type: "say", sessionId: state.active, text };
  if (attached.length) {
    message.images = attached.map((a) => ({ name: a.name, data: a.data }));
    attached.length = 0;
    drawAttachments();
  }
  send(message);
  remember(text);
  input.value = "";
  completion(false);
  resize();
});

// ---- the composer: the terminal's completions, history and keys -------------------------------------------------------

// WHAT THE COMPOSER OFFERS IS THE TERMINAL'S: the server builds the rows with the same call the terminal's menu does —
// commands with their summaries, then their verbs and values, and paths after an @ — less what a browser may not run.
// Each row carries the whole line choosing it gives, and where the caret goes. Numbered, so a late answer to an earlier
// keystroke is dropped rather than drawn over a newer one.
let completeN = 0, completeTimer = null, shown = [];

function wantsCompletion(text, caret) {
  if (text.startsWith("/") && !text.includes("\n")) return true;
  let s = caret;
  while (s > 0 && !/\s/.test(text[s - 1])) s--;
  return s < caret && text[s] === "@";
}

function requestCompletion() {
  clearTimeout(completeTimer);
  // A WORKER TAKES MESSAGES, NOT COMMANDS — no list in its tab.
  const view = state.views.get(state.active);
  const text = input.value, caret = input.selectionStart ?? text.length;
  if (!view || view.kind === "worker" || !wantsCompletion(text, caret)) { completion(false); return; }
  const n = ++completeN;
  // A SHORT PAUSE, as the terminal's walk waits: a typed word costs one request, not one per letter.
  completeTimer = setTimeout(() => send({ type: "complete", sessionId: activeSessionId(), text, caret, n }), 80);
}

function completion(rows) {
  const list = $("completion");
  if (rows === false || !rows || !rows.length) { shown = []; list.hidden = true; if (rows === false) completeN++; return; }
  shown = rows;
  // A ROW WITH NO TEXT IS SHOWN, NOT CHOSEN: a value the user types themselves, which the list names so it can be found.
  const first = rows.findIndex((r) => r.text != null);
  list.replaceChildren(...rows.map((r, i) => {
    const d = el("div", (r.text == null ? "hint" : "") + (i === first ? " on" : ""));
    d.append(el("span", "label", r.label));
    if (r.summary) d.append(el("span", "summary", r.summary));
    if (r.text != null) d.addEventListener("mousedown", (e) => { e.preventDefault(); choose(r); });
    return d;
  }));
  list.hidden = false;
}

function choose(row) {
  input.value = row.text;
  input.focus();
  input.setSelectionRange(row.caret, row.caret);
  completion(false);
  resize();
  // NOT FINISHED, SO THE LIST GOES ON: a verb that takes a value ("/memory open ") or a folder ("@src/") offers what
  // comes next, as the terminal's does.
  const head = row.text.slice(0, row.caret);
  if (head.endsWith(" ") || head.endsWith("/")) requestCompletion();
}

function selected() {
  const items = [...$("completion").children];
  const on = items.findIndex((i) => i.classList.contains("on"));
  return on >= 0 ? shown[on] : null;
}

function move(by) {
  const items = [...$("completion").children];
  let i = items.findIndex((x) => x.classList.contains("on"));
  for (let step = 0; step < items.length; step++) {
    i = (i + by + items.length) % items.length;
    if (shown[i] && shown[i].text != null) break;
  }
  items.forEach((x, n) => x.classList.toggle("on", n === i));
  items[i]?.scrollIntoView({ block: "nearest" });
}

// ↑/↓ WALK WHAT WAS SENT, as the terminal's composer does — per session, from the line the caret is on: ↑ only from the
// first line, ↓ only from the last, so moving through a multi-line message still works.
function remember(text) {
  const view = state.views.get(state.active);
  if (!view || !text.trim()) return;
  view.history = view.history || [];
  if (view.history[view.history.length - 1] !== text) view.history.push(text);
  view.recall = view.history.length;
}

function recall(by) {
  const view = state.views.get(state.active);
  const h = view?.history;
  if (!h || !h.length) return false;
  const at = view.recall ?? h.length;
  const next = Math.min(Math.max(at + by, 0), h.length);
  if (next === at) return false;
  if (at === h.length) view.draft = input.value;
  view.recall = next;
  input.value = next === h.length ? (view.draft || "") : h[next];
  input.setSelectionRange(input.value.length, input.value.length);
  resize();
  return true;
}

input.addEventListener("keydown", (e) => {
  const list = $("completion");
  if (!list.hidden && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
    e.preventDefault();
    move(e.key === "ArrowDown" ? 1 : -1);
    return;
  }
  if (!list.hidden && (e.key === "Tab" && !e.shiftKey || e.key === "Enter" && !e.shiftKey)) {
    const pick = selected();
    // A LINE THAT IS ALREADY WHAT THE ROW GIVES IS RUN, not chosen again: Enter on "/clear" sends it.
    if (pick && !(e.key === "Enter" && pick.text.trim() === input.value.trim())) {
      e.preventDefault();
      choose(pick);
      return;
    }
    completion(false);
  }
  if (e.key === "Escape") {
    if (!list.hidden) { completion(false); return; }
    if (document.body.classList.contains("in-aside")) { e.preventDefault(); leaveAside(); return; }
    // ESCAPE STOPS A RUNNING TURN, as in the terminal; what was queued comes back to this composer as a "restore".
    if (!$("stop").hidden) { e.preventDefault(); send({ type: "stop", sessionId: activeSessionId() }); }
    return;
  }
  // SHIFT+TAB CYCLES THE EDIT MODE, as in the terminal: always ask, accept edits, auto when a classifier reviews writes.
  if (e.key === "Tab" && e.shiftKey && state.views.get(state.active)?.kind !== "worker") {
    e.preventDefault();
    send({ type: "cycleMode", sessionId: activeSessionId() });
    return;
  }
  if (e.key === "ArrowUp" && !input.value.slice(0, input.selectionStart).includes("\n") && recall(-1)) { e.preventDefault(); return; }
  if (e.key === "ArrowDown" && !input.value.slice(input.selectionEnd).includes("\n") && recall(1)) { e.preventDefault(); return; }
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); $("composer").requestSubmit(); }
});

input.addEventListener("input", () => {
  resize();
  const view = state.views.get(state.active);
  if (view) view.recall = view.history?.length;
  requestCompletion();
});

function resize() {
  input.style.height = "auto";
  input.style.height = Math.min(input.scrollHeight, window.innerHeight * 0.4) + "px";
}

// COPY ON A CODE BLOCK: its code as text. The button is in render()'s output, so one listener serves every block.
document.addEventListener("click", (e) => {
  const copy = e.target.closest && e.target.closest(".copy");
  if (!copy) return;
  const code = copy.closest(".code")?.querySelector("code");
  if (!code || !navigator.clipboard) { copy.textContent = "Select to copy"; return; }
  navigator.clipboard.writeText(code.textContent).then(() => {
    copy.textContent = "Copied";
    setTimeout(() => { copy.textContent = "Copy"; }, 1200);
  }, () => { copy.textContent = "Select to copy"; });
});

$("stop").addEventListener("click", () => send({ type: "stop", sessionId: activeSessionId() }));
$("back").addEventListener("click", () => {
  const view = state.views.get(state.active);
  if (view && view.kind === "worker") { activate(view.parent); return; }
  document.body.dataset.view = "list";
  drawSessions();
});
$("menu").addEventListener("click", () => document.body.classList.toggle("drawer"));
$("new").addEventListener("click", () => { document.body.classList.remove("drawer"); openPicker(); });

// ---- new session picker -----------------------------------------------------------------------------------------

const picker = { under: null };

function openPicker() {
  picker.under = null;
  $("picker").hidden = false;
  send({ type: "folders" });
}

function drawFolders(m) {
  picker.under = m.under || null;
  $("picker-path").textContent = m.under || "Choose a folder";
  $("picker-open").disabled = !m.under;
  $("picker-up").disabled = !m.under;
  $("picker-list").replaceChildren(...m.folders.map((f) => {
    const d = el("div", null, (m.under ? f.split("/").pop() : f) + "/");
    d.addEventListener("click", () => send({ type: "folders", under: f }));
    return d;
  }));
}

$("picker-close").addEventListener("click", () => { $("picker").hidden = true; });
$("picker-open").addEventListener("click", () => {
  if (!picker.under) return;
  $("picker").hidden = true;
  send({ type: "create", folder: picker.under });
});
$("picker-up").addEventListener("click", () => {
  if (!picker.under) return;
  send({ type: "folders", under: picker.under.replace(/\/[^/]+\/?$/, "") || "/" });
});

window.matchMedia("(max-width: 699px)").addEventListener("change", drawSessions);

drawPanel();
connect();
