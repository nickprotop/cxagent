// cxagent web — the page. One WebSocket carries everything; every session event is numbered, so a dropped connection
// comes back with "since" and loses nothing. Model and tool text reaches the page only as textContent, or through
// render(), which escapes before it formats.

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
  open: new Map(),      // sessionId → view
  active: null,
  prompts: new Map(),   // promptId → { message, node }
};

// ---- theme ------------------------------------------------------------------------------------------------------

function storedTheme() {
  try { return localStorage.getItem("cxagent-theme"); } catch { return null; }
}
function applyTheme(theme) {
  if (theme) document.documentElement.dataset.theme = theme;
  else delete document.documentElement.dataset.theme;
}
applyTheme(storedTheme());
$("theme").addEventListener("click", () => {
  const dark = getComputedStyle(document.documentElement).colorScheme.includes("dark");
  const next = dark ? "light" : "dark";
  applyTheme(next);
  try { localStorage.setItem("cxagent-theme", next); } catch { /* a private window keeps it for this visit only */ }
});

// ---- connection -------------------------------------------------------------------------------------------------

function connect() {
  const scheme = location.protocol === "https:" ? "wss:" : "ws:";
  const socket = new WebSocket(scheme + "//" + location.host + "/ws");
  state.socket = socket;
  socket.addEventListener("open", () => {
    state.backoff = 1000;
    $("conn").classList.remove("down");
    // CARRY ON WHERE EACH OPEN SESSION LEFT OFF.
    for (const [id, view] of state.open) send({ type: "open", id, since: view.seq });
  });
  socket.addEventListener("message", (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    handle(m);
  });
  socket.addEventListener("close", () => {
    $("conn").classList.add("down");
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
    case "banner":
      showBanners([m.text]);
      break;
    case "sessions":
      state.rows = m.rows || [];
      drawSessions();
      break;
    case "opened":
      opened(m);
      break;
    case "reset": {
      const view = state.open.get(m.sessionId);
      if (view) { view.log.replaceChildren(); view.turns.clear(); view.tools.clear(); view.agents.clear(); }
      break;
    }
    case "event":
      event(m);
      break;
    case "prompt":
      prompt(m);
      break;
    case "promptResolved":
      resolved(m.promptId, m.by);
      break;
    case "notice": {
      const view = state.open.get(m.sessionId);
      if (view) { const n = el("div", "msg notice"); n.innerHTML = render(m.text); append(view, n); }
      break;
    }
    case "confirmClose":
      confirmClose(m);
      break;
    case "closed":
      closed(m.sessionId);
      break;
    case "folders":
      drawFolders(m);
      break;
    case "error":
      flash(m.message);
      break;
  }
}

function showBanners(lines) {
  const box = $("banners");
  box.replaceChildren(...lines.map((l) => el("div", "banner", l)));
}

function flash(text) {
  const view = state.open.get(state.active);
  if (view) append(view, el("div", "msg said Warning", text));
  else showBanners([text]);
}

// ---- sessions list ----------------------------------------------------------------------------------------------

function title(row) {
  return row.title || row.folder.split("/").filter(Boolean).pop() || row.folder;
}

// THE LAST TWO PARTS OF A PATH — enough to tell projects apart on a phone without a line of temp folders.
function shortFolder(path) {
  const parts = path.split("/").filter(Boolean);
  return (parts.length > 2 ? "…/" : "/") + parts.slice(-2).join("/");
}

function ago(iso) {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (!(s >= 0)) return "";
  if (s < 60) return "just now";
  if (s < 3600) return Math.floor(s / 60) + "m ago";
  if (s < 86400) return Math.floor(s / 3600) + "h ago";
  return Math.floor(s / 86400) + "d ago";
}

function narrow() { return window.matchMedia("(max-width: 699px)").matches; }

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
      const g = r.folder;
      if (!groups.has(g)) groups.set(g, []);
      groups.get(g).push(r);
    }
  }
  for (const [label, rows] of groups) {
    if (rows.length === 0) continue;
    const group = el("div", "group", narrow() ? label : shortFolder(label));
    group.title = label;
    box.append(group);
    for (const r of rows) box.append(sessionRow(r));
  }
  drawTabs();
}

function sessionRow(r) {
  const row = el("div", "row" + (r.id === state.active || r.agentId === activeAgent() ? " on" : ""));
  row.append(el("span", "dot " + r.state));
  const t = el("div", "t");
  t.append(el("b", null, title(r)));
  const sub = r.state === "held" ? r.heldBy : r.state === "waiting" ? "waiting on you" : r.state === "working" ? "working"
    : narrow() ? shortFolder(r.folder) : ago(r.updatedAt);
  t.append(el("small", null, sub));
  row.append(t);
  if (r.state === "idle" || r.state === "working" || r.state === "waiting") {
    const x = el("button", "x icon", "×");
    x.title = "Close this session";
    x.addEventListener("click", (e) => { e.stopPropagation(); send({ type: "close", sessionId: r.id }); });
    row.append(x);
  }
  row.addEventListener("click", () => {
    document.body.classList.remove("drawer");
    if (r.state === "held") { flash("That session is " + r.heldBy + "."); return; }
    if (state.open.has(r.id)) activate(r.id);
    else send({ type: "open", id: r.id, since: 0 });
  });
  return row;
}

function activeAgent() {
  const v = state.open.get(state.active);
  return v ? v.agentId : null;
}

function drawTabs() {
  const tabs = $("tabs");
  tabs.replaceChildren();
  for (const [id, view] of state.open) {
    const row = state.rows.find((r) => r.id === id);
    const tab = el("button", "tab" + (id === state.active ? " on" : ""));
    const dot = el("span", "dot " + (row ? row.state : "idle"));
    tab.append(dot, document.createTextNode(" " + (view.title || "session")));
    tab.addEventListener("click", () => activate(id));
    tabs.append(tab);
  }
  const plus = el("button", "tab", "+");
  plus.addEventListener("click", () => openPicker());
  tabs.append(plus);
}

// ---- a session's view -------------------------------------------------------------------------------------------

function opened(m) {
  let view = state.open.get(m.sessionId);
  const fresh = !view;
  if (!view) {
    const log = el("div", "session-view");
    view = { id: m.sessionId, log, seq: 0, turns: new Map(), tools: new Map(), agents: new Map(), working: false };
    state.open.set(m.sessionId, view);
  }
  Object.assign(view, {
    agentId: m.agentId,
    folder: m.folder,
    model: m.model,
    mode: m.mode,
    sandbox: m.sandbox,
    title: (state.rows.find((r) => r.id === m.sessionId) || {}).title || m.folder.split("/").filter(Boolean).pop(),
  });
  // A RECONNECT RE-OPENS EVERY SESSION; only one the user just asked for comes to the front.
  if (fresh || state.active === m.sessionId) activate(m.sessionId);
  else drawTabs();
}

function activate(id) {
  state.active = id;
  const view = state.open.get(id);
  const chat = $("chat");
  chat.replaceChildren(view ? view.log : $("empty"));
  document.body.dataset.view = view ? "chat" : "list";
  $("input").disabled = !view;
  $("send").disabled = !view;
  drawHeader();
  drawPrompts();
  drawSessions();
  scrollDown(true);
  if (view) $("input").focus();
}

function drawHeader() {
  const v = state.open.get(state.active);
  const show = (id, text, cls) => {
    const e = $(id);
    e.hidden = !text;
    e.textContent = text || "";
    e.className = "pill" + (cls ? " " + cls : "");
  };
  $("folder").textContent = v ? v.folder : "";
  $("phone-title").textContent = v ? v.title : "";
  show("model", v && v.model);
  show("mode", v && modeName(v.mode));
  const sb = v && sandboxName(v.sandbox);
  show("sandbox", sb, sb === "sandboxed" ? "good" : "warn");
  show("phone-sandbox", sb, sb === "sandboxed" ? "good" : "warn");
  $("statusline").textContent = v ? [v.folder, v.model, modeName(v.mode), sb].filter(Boolean).join("  ·  ") : "";
  $("stop").hidden = !(v && v.working);
}

function modeName(mode) {
  return { AlwaysAsk: "always ask", AcceptEdits: "accept edits", Auto: "auto" }[mode] || mode || "";
}
function sandboxName(kind) {
  return { Active: "sandboxed", Failed: "sandbox failed" }[kind] || "";
}

function append(view, node) {
  const stick = isAtBottom();
  view.log.append(node);
  if (view.id === state.active) scrollDown(stick);
}

function isAtBottom() {
  const c = $("chat");
  return c.scrollHeight - c.scrollTop - c.clientHeight < 80;
}
function scrollDown(force) {
  const c = $("chat");
  if (force) c.scrollTop = c.scrollHeight;
}

function turn(view, id) {
  let t = view.turns.get(id);
  if (!t) {
    const node = el("div", "msg assistant working");
    const label = el("div", "label");
    const thinking = el("details", "thinking");
    thinking.hidden = true;
    const summary = el("summary", null, "thinking…");
    const thought = el("pre");
    thinking.append(summary, thought);
    const body = el("div", "body");
    node.append(label, thinking, body);
    t = { node, label, thinking, summary, thought, body, text: "", pending: false };
    view.turns.set(id, t);
    append(view, node);
  }
  return t;
}

function paint(view, t) {
  // ONE RENDER PER FRAME, however many tokens arrived in it.
  if (t.pending) return;
  t.pending = true;
  requestAnimationFrame(() => {
    t.pending = false;
    const stick = isAtBottom();
    t.body.innerHTML = render(t.text);
    if (view.id === state.active) scrollDown(stick);
  });
}

function toolParent(view, agent) {
  // A SUB-AGENT'S TOOLS FOLD UNDER ONE ROW, so its work reads as one step of the parent's.
  if (!agent || agent === view.agentId) return view.log;
  let a = view.agents.get(agent);
  if (!a) {
    const node = el("details", "agent");
    const summary = el("summary", null, "agent · working");
    const inner = el("div", "inner");
    node.append(summary, inner);
    a = { node, summary, inner, count: 0 };
    view.agents.set(agent, a);
    append(view, node);
  }
  return a;
}

function event(m) {
  const view = state.open.get(m.sessionId);
  if (!view) return;
  if (m.seq <= view.seq) return;
  view.seq = m.seq;
  switch (m.kind) {
    case "user":
      append(view, el("div", "msg user", m.text));
      break;
    case "turnBegan":
      turn(view, m.turn);
      setWorking(view, true);
      break;
    case "text": {
      const t = turn(view, m.turn);
      t.text += m.text;
      paint(view, t);
      break;
    }
    case "thinking": {
      const t = turn(view, m.turn);
      t.thinking.hidden = false;
      t.thought.textContent += m.text;
      break;
    }
    case "thinkingEnded": {
      const t = turn(view, m.turn);
      if (m.hidden) t.thinking.hidden = true;
      t.summary.textContent = "thought for " + m.seconds + "s · " + m.words + " words";
      break;
    }
    case "labelled":
      turn(view, m.turn).label.textContent = m.text;
      break;
    case "turnEnded": {
      const t = view.turns.get(m.turn);
      if (t) {
        t.node.classList.remove("working");
        if (!t.text) t.body.hidden = true;
      }
      setWorking(view, false);
      break;
    }
    case "said": {
      const n = el("div", "msg said " + (m.severity || "Info"));
      n.innerHTML = render(m.text || "");
      append(view, n);
      break;
    }
    case "toolStarted": {
      const parent = toolParent(view, m.agent);
      const node = el("details", "tool");
      const summary = el("summary");
      summary.append(document.createTextNode(m.name), el("span", "state", "running"));
      const out = el("pre");
      node.append(summary, out);
      view.tools.set(m.job, { node, summary, out });
      if (parent === view.log) append(view, node);
      else { parent.inner.append(node); parent.count++; parent.summary.textContent = "agent · " + parent.count + " tools"; }
      break;
    }
    case "toolOutput": {
      const t = view.tools.get(m.job);
      if (t) t.out.textContent += m.text;
      break;
    }
    case "toolEnded": {
      const t = view.tools.get(m.job);
      if (!t) break;
      t.node.classList.add(m.state);
      t.summary.querySelector(".state").textContent = m.state === "Succeeded" ? "done" : m.state.toLowerCase();
      if (m.sandbox === "contained") t.summary.append(el("span", "badge", "contained"));
      if (m.sandbox === "uncontained") t.summary.append(el("span", "badge out", "uncontained"));
      if (m.sandboxWarning) t.summary.append(el("span", "badge out", "git files changed"));
      if (m.error) t.out.textContent += (t.out.textContent ? "\n" : "") + m.error;
      if (!t.out.textContent) t.out.hidden = true;
      break;
    }
  }
}

function setWorking(view, working) {
  view.working = working;
  if (view.id === state.active) drawHeader();
}

// ---- prompts ----------------------------------------------------------------------------------------------------

function prompt(m) {
  state.prompts.set(m.id, { message: m, node: null });
  drawPrompts();
  const row = state.rows.find((r) => r.id === m.sessionId);
  if (row) { row.state = "waiting"; drawSessions(); }
}

function resolved(id, by) {
  const p = state.prompts.get(id);
  state.prompts.delete(id);
  drawPrompts();
  if (p && by && by !== "web") {
    const view = state.open.get(p.message.sessionId);
    if (view) append(view, el("div", "msg said", "answered by " + by));
  }
  send({ type: "list" });
}

function drawPrompts() {
  const box = $("prompts");
  box.replaceChildren();
  for (const p of state.prompts.values()) {
    if (p.message.sessionId !== state.active) continue;
    box.append(promptNode(p.message));
  }
  if (box.firstChild) scrollDown(true);
}

function button(text, cls, onClick) {
  const b = el("button", cls, text);
  b.type = "button";
  b.addEventListener("click", onClick);
  return b;
}

function promptNode(m) {
  const box = el("div", "prompt");
  const answer = (fields) => send(Object.assign({ type: "answer", promptId: m.id }, fields));
  if (m.kind === "permission") {
    const heading = m.sandboxFailure ? "Sandbox could not start: " + m.sandboxFailure + ". Run this uncontained?"
      : m.unsandboxedReason ? "Run this outside the sandbox?"
      : m.what === "Shell" ? "Run shell command?" : "Allow this?";
    box.append(el("div", "q", heading), el("div", "what", m.display));
    if (m.requester) box.append(el("div", "why", "asked by " + m.requester));
    if (m.unsandboxedReason) box.append(el("div", "why", "reason: " + m.unsandboxedReason));
    const btns = el("div", "btns");
    btns.append(button("Allow once", "yes", () => answer({ choice: "once" })));
    if (m.offerAlways) btns.append(button("Always allow" + (m.alwaysCovers ? " (" + m.alwaysCovers + ")" : ""), null, () => answer({ choice: "always" })));
    if (m.offerTrust) btns.append(button("Trust folder", null, () => answer({ choice: "trust" })));
    btns.append(button("Deny", "no", () => answer({ choice: "deny" })));
    box.append(btns);
  } else if (m.kind === "trust") {
    box.append(el("div", "q", "Trust this folder?"), el("div", "what", m.folder));
    box.append(el("div", "why", "Trusted folders allow reads and read-only commands without asking."));
    const btns = el("div", "btns");
    btns.append(button("Trust this folder", "yes", () => answer({ trusted: true })), button("Don't trust", "no", () => answer({ trusted: false })));
    box.append(btns);
  } else if (m.kind === "question") {
    const answers = [];
    for (const q of m.questions) {
      const part = el("div");
      part.append(el("div", "q", q.header ? q.header + " — " + q.question : q.question));
      const name = "q" + Math.random().toString(36).slice(2);
      const options = [];
      for (const o of q.options) {
        const label = el("label");
        const input = el("input");
        input.type = q.multiple ? "checkbox" : "radio";
        input.name = name;
        input.value = o.label;
        label.append(input, document.createTextNode(" " + o.label + (o.description ? " — " + o.description : "")));
        part.append(label);
        options.push(input);
      }
      const other = el("input");
      other.type = "text";
      other.placeholder = q.options.length ? "or type your own answer" : "your answer";
      part.append(other);
      box.append(part);
      answers.push(() => {
        const picked = options.filter((i) => i.checked).map((i) => i.value);
        if (other.value.trim()) picked.push(other.value.trim());
        return picked.join(", ");
      });
    }
    const btns = el("div", "btns");
    btns.append(button("Answer", "yes", () => answer({ answers: answers.map((f) => f()) })), button("Skip", "no", () => answer({ cancelled: true })));
    box.append(btns);
  } else if (m.kind === "confirmClose") {
    box.append(el("div", "q", m.text));
    const btns = el("div", "btns");
    btns.append(button("Close anyway", "no", () => { box.remove(); send({ type: "close", sessionId: m.sessionId, confirm: true }); }),
      button("Keep it", null, () => box.remove()));
    box.append(btns);
  }
  return box;
}

function confirmClose(m) {
  if (m.sessionId !== state.active) activate(m.sessionId);
  $("prompts").append(promptNode({ kind: "confirmClose", sessionId: m.sessionId, text: m.text }));
}

function closed(id) {
  state.open.delete(id);
  for (const [pid, p] of state.prompts) if (p.message.sessionId === id) state.prompts.delete(pid);
  if (state.active === id) activate(state.open.keys().next().value || null);
  drawTabs();
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
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    $("composer").requestSubmit();
  }
});

input.addEventListener("input", () => {
  resize();
  const text = input.value;
  completion(text.startsWith("/") && !text.includes(" ") ? text : false);
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

$("stop").addEventListener("click", () => send({ type: "stop", sessionId: state.active }));
$("back").addEventListener("click", () => { document.body.dataset.view = "list"; drawSessions(); });
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
  const parent = picker.under.replace(/\/[^/]+\/?$/, "") || "/";
  send({ type: "folders", under: parent });
});

window.matchMedia("(max-width: 699px)").addEventListener("change", drawSessions);

connect();
