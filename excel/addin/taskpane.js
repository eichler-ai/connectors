/* Excel MCP Bridge task pane: dials the hub's /excel/bridge WebSocket and runs the scripts it sends,
 * speaking bridge protocol v1 (hub/protocol/protocol.go). Ported from the proof of concept; the
 * runner semantics are the POC's (a script is the body of an async function taking `context`, the
 * Excel.RequestContext from Excel.run; its return value is JSON-serialised back; errors verbatim).
 *
 * Every message is a JSON-RPC 2.0 notification {jsonrpc:"2.0", method, params}; execs are
 * correlated by params.id. */

(function () {
  "use strict";

  function start() {
    try {
      main();
    } catch (e) {
      var el = document.getElementById("status") || document.body;
      el.textContent = "bridge client failed to start: " + e.message + "\n" + (e.stack || "");
      throw e;
    }
  }
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", start);
  } else {
    start();
  }

  function main() {

  var PROTOCOL_VERSION = 1;
  var BRIDGE_VERSION = "0.1.0";
  var CONNECTOR = "excel";
  // The page lives at /<connector>/addin/taskpane.html; the socket is /<connector>/bridge on the same
  // origin, so the pane needs no configuration to find its hub.
  var BASE = location.pathname.replace(/\/addin\/[^/]*$/, "");
  var WS_URL = window.BRIDGE_WS_URL || "wss://" + location.host + BASE + "/bridge";
  var TOKEN_KEY = "hub.token";
  var RECONNECT_MIN_MS = 1000, RECONNECT_MAX_MS = 30000;

  var statusEl = document.getElementById("status");
  var envEl = document.getElementById("env");
  var logEl = document.getElementById("log");
  var tokenEl = document.getElementById("token");

  // Stable for this pane: a reconnect presents the same id and the hub treats it as the same bridge
  // coming back rather than a second instance. Kept in sessionStorage so a pane reload — Excel
  // keeps "closed" panes alive, and reloads them freely — replaces the old connection instead of
  // registering a duplicate beside it.
  var instanceId = storedInstanceId();
  function storedInstanceId() {
    var key = "hub.instance_id";
    var id = null;
    try { id = sessionStorage.getItem(key); } catch (e) { /* storage blocked: fall through */ }
    if (!id) {
      id = (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : "pane-" + Date.now() + "-" + Math.random().toString(16).slice(2);
      try { sessionStorage.setItem(key, id); } catch (e) { /* not persisted; still unique for this load */ }
    }
    return id;
  }
  var ws = null;
  var reconnectTimer = null;
  var reconnectDelay = RECONNECT_MIN_MS;
  var replaced = false;
  var documents = [];
  var resultLimit = 16 << 20;
  var running = null; // id of the exec currently inside Excel.run, for cancel replies

  function log(msg, cls) {
    var line = document.createElement("div");
    if (cls) line.className = cls;
    line.textContent = new Date().toISOString().slice(11, 23) + " " + msg;
    logEl.appendChild(line);
    while (logEl.childNodes.length > 200) logEl.removeChild(logEl.firstChild);
    logEl.scrollTop = logEl.scrollHeight;
  }
  function setStatus(text, cls) { statusEl.textContent = text; statusEl.className = cls || ""; }

  function token() { try { return localStorage.getItem(TOKEN_KEY) || ""; } catch (e) { return ""; } }
  tokenEl.value = token();
  document.getElementById("save-token").onclick = function () {
    try { localStorage.setItem(TOKEN_KEY, tokenEl.value.trim()); } catch (e) { log("cannot store token: " + e.message, "err"); return; }
    log("token saved");
    replaced = false;
    if (ws) ws.close(); else connect();
  };

  function hostInfo() {
    var d = (window.Office && Office.context && Office.context.diagnostics) || {};
    return { app: String(d.host || "none"), platform: String(d.platform || "browser"), version: String(d.version || "") };
  }

  function send(method, params) {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ jsonrpc: "2.0", method: method, params: params }));
      return true;
    }
    return false;
  }

  function connect() {
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
    if (!token()) { setStatus("Paste the MCP Server token and click Save.", "bad"); return; }
    setStatus("Connecting to " + WS_URL + " …");
    try {
      ws = new WebSocket(WS_URL);
    } catch (e) {
      setStatus("WebSocket constructor threw: " + e.message, "bad");
      log("WebSocket constructor threw: " + e.message, "err");
      scheduleReconnect();
      return;
    }
    ws.onopen = function () {
      send("hello", {
        connector: CONNECTOR, protocol_version: PROTOCOL_VERSION, bridge_version: BRIDGE_VERSION,
        instance_id: instanceId, host: hostInfo(), documents: documents, token: token()
      });
      reconnectDelay = RECONNECT_MIN_MS;
      setStatus("Connected. Waiting for scripts.", "ok");
      log("connected as " + instanceId);
    };
    ws.onclose = function (ev) {
      var why = "code " + ev.code + (ev.reason ? ", " + ev.reason : "");
      log("closed: " + why + " clean=" + ev.wasClean);
      if (replaced) { setStatus("Replaced by a newer connection. Click Reconnect to take over.", "bad"); return; }
      // 1008 is how the hub refuses a hello (bad token, wrong version); retrying will not help.
      if (ev.code === 1008) { setStatus("Rejected by the MCP Server (" + (ev.reason || "policy violation") + "). Check the token, then Reconnect.", "bad"); return; }
      setStatus("Disconnected (" + why + "). Reconnecting…", "bad");
      scheduleReconnect();
    };
    ws.onerror = function () { log("socket error (see close code)", "err"); };
    ws.onmessage = function (ev) {
      var msg;
      try { msg = JSON.parse(ev.data); } catch (e) { log("bad message: " + ev.data.slice(0, 200), "err"); return; }
      var p = msg.params || {};
      switch (msg.method) {
        case "exec": runScript(p); break;
        case "cancel":
          // A task pane cannot interrupt a running script (single JavaScript thread; Excel.run has no
          // abort). Say so rather than silently ignoring the cancel.
          send("notice", { exec_id: p.id, record: { severity: "warning", code: "cannot-cancel", source: "excel.addin",
            message: "cancel received for " + p.id + (running === p.id ? " while it is running; the pane cannot interrupt a script" : " but it is not running here") } });
          log("cancel " + p.id + ": cannot interrupt", "err");
          break;
        case "replaced":
          replaced = true;
          log("replaced: " + (p.reason || "") + "; not reconnecting");
          break;
        case "ping": send("pong", {}); break;
        case "pong": break;
        case "notice": log("notice from hub: " + JSON.stringify(p.record || p)); break;
        default: log("unknown method " + msg.method, "err");
      }
    };
  }

  function scheduleReconnect() {
    if (replaced || reconnectTimer) return;
    reconnectTimer = setTimeout(function () { reconnectTimer = null; connect(); }, reconnectDelay);
    reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS);
  }

  function serializeError(e) {
    var out = { name: String((e && e.name) || "Error"), message: String((e && e.message) || e) };
    if (e && e.code) out.code = String(e.code);
    if (e && e.debugInfo) out.debug_info = e.debugInfo;
    if (e && e.stack) out.stack = String(e.stack);
    return out;
  }

  // Office.js proxy objects are not plain data: a loaded object's toJSON() emits its loaded
  // properties, an unloaded one emits {} or throws. Scripts return values, not proxies; we let
  // JSON.stringify do its thing and report whatever happens.
  function serializeResult(value, limit) {
    var text = JSON.stringify(value === undefined ? null : value);
    if (text === undefined) text = "null"; // functions, symbols
    var truncated = false;
    if (text.length > limit) {
      text = JSON.stringify({ truncated: true, bytes: text.length, head: text.slice(0, 4096) });
      truncated = true;
    }
    return { text: text, truncated: truncated };
  }

  function runScript(req) {
    var t0 = performance.now();
    var limit = (req.limits && req.limits.result_bytes) || resultLimit;
    log("exec " + req.id + ": " + req.script.length + " chars, timeout " + req.timeout_ms + "ms");
    if (req.language && req.language !== "officejs") {
      reply({ id: req.id, ok: false, error: { name: "UnsupportedLanguage", message: "this bridge runs officejs, not " + req.language }, duration_ms: 0 });
      return;
    }
    var fn;
    try {
      fn = new Function("context", "\"use strict\";\nreturn (async function () {\n" + req.script + "\n})();");
    } catch (e) {
      log(req.id + " compile error: " + e.message, "err");
      reply({ id: req.id, ok: false, error: serializeError(e), duration_ms: performance.now() - t0 });
      return;
    }
    if (typeof Excel === "undefined") {
      reply({ id: req.id, ok: false, error: { name: "NoHost", message: "the task pane is not running inside Excel" }, duration_ms: 0 });
      return;
    }
    var result;
    running = req.id;
    Excel.run(function (context) {
      return Promise.resolve(fn(context)).then(function (v) { result = v; });
    }).then(function () {
      var s = serializeResult(result, limit);
      var ms = performance.now() - t0;
      log(req.id + " ok in " + ms.toFixed(0) + "ms, " + s.text.length + " bytes" + (s.truncated ? " (truncated)" : ""));
      reply({ id: req.id, ok: true, result: "__RAW__", duration_ms: ms, truncated: s.truncated }, s.text);
    }).catch(function (e) {
      var ms = performance.now() - t0;
      log(req.id + " failed in " + ms.toFixed(0) + "ms: " + (e && e.message), "err");
      reply({ id: req.id, ok: false, error: serializeError(e), duration_ms: ms });
    }).then(function () { if (running === req.id) running = null; });
  }

  // Sends a result. The already-stringified script value is spliced in so a giant object is not
  // serialised twice.
  function reply(params, rawResult) {
    var text = JSON.stringify({ jsonrpc: "2.0", method: "result", params: params });
    if (rawResult !== undefined) text = text.replace("\"__RAW__\"", function () { return rawResult; });
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(text);
    else log("result for " + params.id + " dropped: socket not open", "err");
  }

  // documents[]: Excel for the web has one workbook per pane, so this is one entry. Office.js has
  // no workbook id; the OneDrive URL is the closest stable thing, the name the fallback. The active
  // sheet rides along in detail so list_instances can show where a script would land.
  function refreshDocuments(then) {
    if (typeof Excel === "undefined") { documents = []; if (then) then(); return; }
    Excel.run(function (context) {
      var wb = context.workbook;
      wb.load("name");
      var ws = wb.worksheets.getActiveWorksheet();
      ws.load("name");
      return context.sync().then(function () {
        var url = (Office.context.document && Office.context.document.url) || "";
        documents = [{ id: url || wb.name, title: wb.name, path: url, active: true, detail: { sheet: ws.name } }];
      });
    }).catch(function (e) { log("documents: " + e.message, "err"); }).then(function () { if (then) then(); });
  }

  function register() {
    refreshDocuments(function () { if (send("register", { documents: documents })) log("register: " + JSON.stringify(documents.map(function (d) { return d.title + "/" + (d.detail && d.detail.sheet); }))); });
  }

  document.getElementById("reconnect").onclick = function () { replaced = false; reconnectDelay = RECONNECT_MIN_MS; if (ws && ws.readyState !== WebSocket.CLOSED) ws.close(); connect(); };
  document.getElementById("clear").onclick = function () { logEl.textContent = ""; };

  var readyFired = false;
  // Outside a host (plain tab, probe) Office.onReady may never fire; still bring the socket up.
  setTimeout(function () {
    if (!readyFired) { log("Office.onReady did not fire within 5s; connecting anyway", "err"); connect(); }
  }, 5000);

  Office.onReady(function (info) {
    readyFired = true;
    var h = hostInfo();
    envEl.textContent = "host=" + info.host + " platform=" + info.platform + " office.js=" + h.version +
      "\nExcelApi " + highestExcelApi() + "\ninstance " + instanceId;
    if (typeof Excel === "undefined" || !info.host) {
      log("not inside Excel (host=" + info.host + "); scripts will fail but the socket will connect", "err");
      connect();
      return;
    }
    refreshDocuments(function () {
      connect();
      // Sheet switches re-register so the hub's view of the active sheet stays live.
      Excel.run(function (context) {
        context.workbook.worksheets.onActivated.add(function () { register(); return Promise.resolve(); });
        return context.sync();
      }).catch(function (e) { log("onActivated: " + e.message, "err"); });
    });
  });

  function highestExcelApi() {
    var req = Office.context && Office.context.requirements;
    if (!req) return "n/a";
    var best = "none";
    for (var minor = 1; minor <= 30; minor++) {
      if (req.isSetSupported("ExcelApi", "1." + minor)) best = "1." + minor;
    }
    return best;
  }
  }
})();
