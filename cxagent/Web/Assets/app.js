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
function setPanel(open) {
  if (wide()) {
    document.body.classList.toggle("no-panel", !open);
    store("cxagent-panel", open ? "1" : "0");
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
      state.commands = m.commands || [];
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
    case "closed": closed(m.sessionId); break;
    case "gone":
      // NO LONGER THERE — unloaded, or from a server since restarted. Back to the list, where it can be resumed.
      if (state.views.has(m.id)) { closed(m.id); document.body.dataset.view = "list"; }
      else flash("That session is no longer there.");
      break;
    case "folders": drawFolders(m); break;
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
  row.append(el("span", "dot " + r.state));
  const t = el("div", "t");
  t.append(el("b", null, title(r)));
  const sub = r.state === "held" ? r.heldBy : r.state === "waiting" ? "waiting on you" : r.state === "working" ? "working"
    : narrow() ? shortFolder(r.folder) : ago(r.updatedAt);
  t.append(el("small", null, sub));
  row.append(t);
  if (r.state === "idle" || r.state === "working" || r.state === "waiting") {
    const x = el("button", "x icon", "×");
    x.type = "button";
    x.title = "Close this session";
    x.addEventListener("click", (e) => { e.stopPropagation(); send({ type: "close", sessionId: r.id }); });
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

function drawTabs() {
  const tabs = $("tabs");
  tabs.replaceChildren();
  for (const [id, view] of state.views) {
    const worker = view.kind === "worker";
    const row = worker ? null : state.rows.find((r) => r.id === id);
    const w = worker ? workerRunning(view) : null;
    const tab = el("button", "tab" + (id === state.active ? " on" : "") + (worker ? " worker" : ""));
    tab.type = "button";
    tab.append(el("span", "dot " + (worker ? (w && w.running ? "working" : "") : (row ? row.state : "idle"))),
      document.createTextNode(worker ? "⤷ " + view.name : view.title || "session"));
    tab.addEventListener("click", () => activate(id));
    if (worker) {
      // A WORKER'S TAB CLOSES ON THE PAGE ONLY — the worker carries on, and its card still opens it again.
      const x = el("span", "x", "×");
      x.title = "Close this tab";
      x.addEventListener("click", (e) => { e.stopPropagation(); dropView(id); });
      tab.append(x);
    }
    tabs.append(tab);
  }
  const plus = el("button", "tab", "+");
  plus.type = "button";
  plus.title = "New session";
  plus.addEventListener("click", openPicker);
  tabs.append(plus);
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
    model: m.model, folder: m.folder, title: m.name });
  if (fresh || state.active === m.sessionId) activate(m.sessionId);
  else drawTabs();
}

function openWorker(sessionId, agentId) {
  const key = sessionId + "~" + agentId;
  if (state.views.has(key)) activate(key);
  else send({ type: "openWorker", sessionId, agentId, since: 0 });
}

// THE SESSION BEHIND THE ACTIVE VIEW: a worker's tab shows its parent's panel, model and prompts.
function activeSessionId() {
  const v = state.views.get(state.active);
  return v ? (v.kind === "worker" ? v.parent : v.id) : null;
}

function activate(id) {
  const view = id ? state.views.get(id) : null;
  state.active = view ? id : null;
  $("chat").replaceChildren(view ? view.log : $("empty"));
  document.body.dataset.view = view ? "chat" : "list";
  $("input").disabled = !view;
  $("send").disabled = !view;
  $("input").placeholder = view && view.kind === "worker" ? "Message " + view.name + "…" : "Message, or / for commands";
  $("crumb").textContent = view ? view.folder : "";
  $("phone-title").textContent = view ? (view.kind === "worker" ? "⤷ " + view.name : view.title) : "";
  drawWorkerHead();
  drawPrompts();
  drawSessions();
  drawPanel();
  drawPicks();
  drawStop();
  scrollDown(true);
  if (view && !narrow()) $("input").focus();
}

function dropView(id) {
  state.views.delete(id);
  if (state.active === id) activate(state.views.keys().next().value || null);
  else drawTabs();
}

function drawWorkerHead() {
  const view = state.views.get(state.active);
  const head = $("worker-head");
  if (!view || view.kind !== "worker") { head.hidden = true; return; }
  head.hidden = false;
  head.replaceChildren();
  const w = workerRunning(view);
  const running = !!(w && w.running);
  head.append(running ? el("span", "spin violet") : el("span", w && w.state === "Failed" ? "mark-bad" : "mark-ok", w && w.state === "Failed" ? "✗" : "✓"));
  head.append(el("span", "tag", "Worker"), el("b", null, view.name));
  const facts = [view.workerType, view.model, w ? w.state.toLowerCase() + (w.background ? " · background" : "") : ""].filter(Boolean);
  head.append(el("span", "meta", facts.join(" · ")));
  if (view.task) head.append(el("span", "meta", view.task));
  head.append(el("span", "spacer"));
  head.append(button("Back to session", "ghost", () => activate(view.parent)));
  if (running) {
    if (!w.background)
      head.append(button("Background", null, () => send({ type: "workerAction", sessionId: view.parent, agentId: view.agentId, action: "background" })));
    head.append(button("Stop", "danger", () => send({ type: "workerAction", sessionId: view.parent, agentId: view.agentId, action: "stop" })));
  }
}

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
  if (done.length && !running) parts.push(seconds.toFixed(1) + "s");
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
  callRow(view, m, card.table, null);
}

function endCall(view, m) {
  const call = view.tools.get(m.job);
  if (!call) return;
  call.done = true;
  call.state = m.state;
  call.sandbox = m.sandbox;
  call.sandboxWarning = m.sandboxWarning;
  call.ms = m.ms != null ? m.ms : Date.now() - call.started;
  const ok = m.state === "Succeeded", bad = m.state === "Failed";
  call.markCell.replaceChildren(el("span", ok ? "mark-ok" : bad ? "mark-bad" : "mark-skip", ok ? "✓" : bad ? "✗" : "–"));
  call.timeCell.replaceChildren(document.createTextNode(call.ms < 1000 ? call.ms + "ms" : (call.ms / 1000).toFixed(1) + "s"));
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
  summary.append(mark, el("span", "tag", "Worker"), who, model, task, progress);
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

// ---- events ------------------------------------------------------------------------------------------------------

function event(m) {
  const view = state.views.get(m.sessionId);
  if (!view || m.seq <= view.seq) return;
  view.seq = m.seq;
  switch (m.kind) {
    case "user":
      append(view, el("div", "user", m.text));
      break;
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
      else t.thinking.s.replaceChildren(el("span", "mark", "◆"), document.createTextNode("Thought for " + m.seconds + "s · " + m.words + " words"));
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
    case "worker": {
      const card = view.cards.get(m.job);
      if (!card) break;
      card.agentId = m.agentId;
      card.open.disabled = false;
      if (m.workerType) card.who.textContent = m.workerType;
      if (m.task) card.task.textContent = m.task;
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
  if (!panel) return;
  // A SELECT THE USER HAS OPEN IS LEFT ALONE: the panel arrives every second, and rebuilding it would shut the list.
  const busy = document.activeElement;
  if (busy !== $("model")) options($("model"), panel.choices.models || [], panel.model);
  if (busy !== $("mode")) options($("mode"), panel.choices.modes || [], panel.mode, (n) => n.replace(/-/g, " "));

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
  drawStatusline(panel);
  if (!panel) {
    $("pane-session").replaceChildren(el("p", "none", "Open a session to see its figures."));
    $("pane-tasks").replaceChildren(el("p", "none", "Open a session to see its workers and commands."));
    return;
  }
  drawSessionPane(panel);
  drawTasksPane(panel, sid);
}

function drawStatusline(panel) {
  const line = $("statusline");
  if (!panel) { line.textContent = ""; return; }
  const c = panel.context;
  const pct = c.used && c.window ? Math.round(100 * c.used / c.window) + "% context" : "";
  line.textContent = [shortFolder(panel.here.folder), panel.here.git, panel.model, pct, tokens(panel.tokens.total) + " tokens",
    money(panel.tokens.cost)].filter(Boolean).join("  ·  ");
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

  pane.append(el("h3", null, "Here"));
  const folder = kv("Folder", shortFolder(p.here.folder));
  folder.title = p.here.folder;
  pane.append(folder);
  if (p.here.git) pane.append(kv("Branch", p.here.git));
  pane.append(kv("Skills loaded", String(p.here.skills)));
  pane.append(kv("Always-allow rules", String(p.here.permissions)));
  pane.append(kv("Turn limit", String(p.here.turnCap)));
  for (const s of p.here.mcp) pane.append(kv("MCP · " + s.name, s.failed ? "failed" : s.tools + " tools", s.failed ? "bad" : null));
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

function drawTasksPane(p, sid) {
  const pane = $("pane-tasks");
  pane.replaceChildren(el("h3", null, "Workers"));
  if (!p.workers.length) pane.append(el("p", "none", "No workers in this session."));
  for (const w of p.workers) {
    const acts = [button("Open", null, () => { openWorker(sid, w.agentId); if (!wide()) setPanel(false); })];
    if (w.running) acts.push(button("Stop", "danger", () => send({ type: "workerAction", sessionId: sid, agentId: w.agentId, action: "stop" })));
    const sub = [w.type, w.state.toLowerCase(), clock(w.seconds * 1000), w.background ? "background" : ""].filter(Boolean).join(" · ");
    const row = taskRow(w.running ? "working" : w.state === "Failed" ? "held" : "", el("b", null, w.name), sub, acts);
    if (w.task) row.querySelector(".t").append(el("small", "task-text", w.task));
    pane.append(row);
  }
  pane.append(el("h3", null, "Commands"));
  if (!p.commands.length) pane.append(el("p", "none", "No background commands."));
  for (const c of p.commands) {
    const running = c.state === "Running";
    const acts = running ? [button("Stop", "danger", () => send({ type: "stopCommand", sessionId: sid, pid: c.pid }))] : [];
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
  const box = $("prompts");
  for (const n of [...box.children]) if (n.dataset.prompt) n.remove();
  const sid = activeSessionId();
  for (const p of state.prompts.values()) {
    if (p.message.sessionId !== sid) continue;
    const node = promptNode(p.message);
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
    box.append(el("div", "q", "Trust this folder?"), el("div", "what", m.folder),
      el("div", "why", "Trusted folders allow reads and read-only commands without asking."));
    btns.append(button("Trust this folder", "good", () => answer({ trusted: true })), button("Don't trust", "danger", () => answer({ trusted: false })));
  } else if (m.kind === "question") {
    const answers = [];
    for (const q of m.questions) {
      const part = el("div", "question");
      part.append(el("div", "q", q.header ? q.header + " — " + q.question : q.question));
      const name = "q" + Math.random().toString(36).slice(2);
      const picks = [];
      for (const o of q.options) {
        const label = el("label");
        const input = el("input");
        input.type = q.multiple ? "checkbox" : "radio";
        input.name = name;
        input.value = o.label;
        label.append(input, el("span", null, o.label), o.description ? el("small", null, o.description) : "");
        part.append(label);
        picks.push(input);
      }
      const other = el("input");
      other.type = "text";
      other.placeholder = q.options.length ? "or type your own answer" : "your answer";
      part.append(other);
      box.append(part);
      answers.push(() => {
        const picked = picks.filter((i) => i.checked).map((i) => i.value);
        if (other.value.trim()) picked.push(other.value.trim());
        return picked.join(", ");
      });
    }
    btns.append(button("Answer", "good", () => answer({ answers: answers.map((f) => f()) })), button("Skip", null, () => answer({ cancelled: true })));
  }
  box.append(btns);
  return box;
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

// A CLOSED SESSION TAKES ITS WORKERS' TABS WITH IT.
function closed(id) {
  for (const key of [...state.views.keys()]) if (key === id || key.startsWith(id + "~")) state.views.delete(key);
  for (const [pid, p] of state.prompts) if (p.message.sessionId === id) state.prompts.delete(pid);
  state.panels.delete(id);
  if (!state.views.has(state.active)) activate(state.views.keys().next().value || null);
  else drawTabs();
}

// ---- composer ---------------------------------------------------------------------------------------------------

const input = $("input");

$("composer").addEventListener("submit", (e) => {
  e.preventDefault();
  const text = input.value;
  if (!text.trim() || !state.active) return;
  send({ type: "say", sessionId: state.active, text });
  input.value = "";
  completion(false);
  resize();
});

input.addEventListener("keydown", (e) => {
  const list = $("completion");
  if (!list.hidden && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
    e.preventDefault();
    const items = [...list.children];
    const on = items.findIndex((i) => i.classList.contains("on"));
    const next = (on + (e.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
    items.forEach((i, n) => i.classList.toggle("on", n === next));
    return;
  }
  if (!list.hidden && (e.key === "Tab" || (e.key === "Enter" && list.querySelector(".on")))) {
    e.preventDefault();
    const pick = list.querySelector(".on") || list.firstChild;
    if (pick) { input.value = pick.textContent + " "; completion(false); }
    return;
  }
  if (e.key === "Escape") { completion(false); return; }
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); $("composer").requestSubmit(); }
});

input.addEventListener("input", () => {
  resize();
  const text = input.value;
  // A WORKER TAKES MESSAGES, NOT COMMANDS — no completion list in its tab.
  const worker = state.views.get(state.active)?.kind === "worker";
  completion(!worker && text.startsWith("/") && !text.includes(" ") ? text : false);
});

function completion(prefix) {
  const list = $("completion");
  if (prefix === false) { list.hidden = true; return; }
  const matches = state.commands.filter((c) => c.startsWith(prefix));
  list.replaceChildren(...matches.map((c) => {
    const d = el("div", null, c);
    d.addEventListener("mousedown", (e) => { e.preventDefault(); input.value = c + " "; completion(false); input.focus(); });
    return d;
  }));
  list.hidden = matches.length === 0;
}

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
