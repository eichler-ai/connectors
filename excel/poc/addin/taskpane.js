/* Excel Bridge task pane: dials the local bridge over WebSocket and runs whatever scripts it sends.
 *
 * A script is the body of an async function with one parameter, `context` (the Excel.RequestContext
 * from Excel.run). Whatever it returns is JSON-serialised back to the bridge. */

(function () {
  "use strict";

  // Script Lab loads libraries before the template body exists; wait for the DOM in that case.
  function start() {
    try {
      main();
    } catch (e) {
      // Surface a startup failure in the page instead of dying silently (Script Lab hides the console).
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

  // Same origin as the page by default; a host page (e.g. a Script Lab snippet) can point elsewhere.
  var WS_URL = window.BRIDGE_WS_URL || "wss://" + location.host + "/ws";
  var MAX_RESULT_BYTES = 16 << 20; // 16 MiB so a whole-document export fits; larger results are truncated and flagged
  var RECONNECT_MS = 2000;

  var statusEl = document.getElementById("status") || document.body.appendChild(document.createElement("div"));
  var envEl = document.getElementById("env") || document.body.appendChild(document.createElement("div"));
  var logEl = document.getElementById("log") || document.body.appendChild(document.createElement("pre"));
  var ws = null;
  var reconnectTimer = null;
  var workbookName = "";
  var replaced = false;

  function log(msg, cls) {
    var line = document.createElement("div");
    if (cls) line.className = cls;
    line.textContent = new Date().toISOString().slice(11, 23) + " " + msg;
    logEl.appendChild(line);
    while (logEl.childNodes.length > 200) logEl.removeChild(logEl.firstChild);
    logEl.scrollTop = logEl.scrollHeight;
  }

  function setStatus(text, cls) {
    statusEl.textContent = text;
    statusEl.className = cls || "";
  }

  function hostInfo() {
    var d = (window.Office && Office.context && Office.context.diagnostics) || {};
    return { host: String(d.host || "?"), platform: String(d.platform || "?"), version: String(d.version || "?") };
  }

  function connect() {
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
    setStatus("Connecting to " + WS_URL + " …");
    try {
      ws = new WebSocket(WS_URL);
    } catch (e) {
      // Thrown synchronously for mixed-content or malformed URLs; this is one of the things the POC exists to observe.
      setStatus("WebSocket constructor threw: " + e.message, "bad");
      log("WebSocket constructor threw: " + e.message, "err");
      scheduleReconnect();
      return;
    }
    ws.onopen = function () {
      var h = hostInfo();
      ws.send(JSON.stringify({ type: "hello", host: h.host, platform: h.platform, version: h.version, workbook: workbookName }));
      setStatus("Connected to bridge. Waiting for scripts.", "ok");
      log("connected");
    };
    ws.onclose = function (ev) {
      setStatus("Disconnected (code " + ev.code + (ev.reason ? ", " + ev.reason : "") + "). Reconnecting…", "bad");
      log("closed: code=" + ev.code + " reason=" + (ev.reason || "-") + " clean=" + ev.wasClean);
      scheduleReconnect();
    };
    ws.onerror = function () {
      // The browser hides the reason from script; the close code that follows is all we get.
      log("socket error (see close code)", "err");
    };
    ws.onmessage = function (ev) {
      var req;
      try { req = JSON.parse(ev.data); } catch (e) { log("bad message: " + ev.data, "err"); return; }
      if (req.type === "replaced") {
        // A newer instance of this page connected; stand down instead of fighting it for the bridge.
        replaced = true;
        setStatus("Replaced by a newer connection. Click Reconnect to take over.", "bad");
        log("replaced by a newer connection; not reconnecting");
        return;
      }
      runScript(req);
    };
  }

  function scheduleReconnect() {
    if (replaced) return;
    if (!reconnectTimer) reconnectTimer = setTimeout(function () { reconnectTimer = null; connect(); }, RECONNECT_MS);
  }

  function serializeError(e) {
    var out = { name: String((e && e.name) || "Error"), message: String((e && e.message) || e) };
    if (e && e.code) out.code = String(e.code);
    if (e && e.debugInfo) out.debugInfo = e.debugInfo;
    if (e && e.stack) out.stack = String(e.stack);
    return out;
  }

  // Serialise the return value. Office.js proxy objects are not plain data: a loaded object has
  // toJSON() that emits its loaded properties, an unloaded one emits {} or throws, so the script
  // author is expected to return values, not proxies. We just let JSON.stringify do its thing and
  // report whatever happens.
  function serializeResult(value) {
    var text = JSON.stringify(value === undefined ? null : value);
    if (text === undefined) text = "null"; // functions, symbols
    var truncated = false;
    if (text.length > MAX_RESULT_BYTES) {
      text = JSON.stringify({ truncated: true, bytes: text.length, head: text.slice(0, 4096) });
      truncated = true;
    }
    return { text: text, truncated: truncated };
  }

  function runScript(req) {
    var t0 = performance.now();
    var deadline = req.timeoutMs ? " (bridge deadline " + req.timeoutMs + "ms)" : "";
    log("run " + req.id + ": " + req.script.length + " chars" + deadline);
    var fn;
    try {
      fn = new Function("context", "\"use strict\";\nreturn (async function () {\n" + req.script + "\n})();");
    } catch (e) {
      log(req.id + " compile error: " + e.message, "err");
      reply({ id: req.id, ok: false, error: serializeError(e), durationMs: performance.now() - t0 });
      return;
    }
    var result;
    if (typeof Excel === "undefined") {
      reply({ id: req.id, ok: false, error: { name: "NoHost", message: "task pane is not running inside Excel" }, durationMs: 0 });
      return;
    }
    Excel.run(function (context) {
      return Promise.resolve(fn(context)).then(function (v) { result = v; });
    }).then(function () {
      var s = serializeResult(result);
      var ms = performance.now() - t0;
      log(req.id + " ok in " + ms.toFixed(0) + "ms, " + s.text.length + " bytes" + (s.truncated ? " (truncated)" : ""));
      // Send with the result already stringified so a giant object is not serialised twice.
      reply({ id: req.id, ok: true, result: "__RAW__", durationMs: ms, truncated: s.truncated }, s.text);
    }).catch(function (e) {
      var ms = performance.now() - t0;
      log(req.id + " failed in " + ms.toFixed(0) + "ms: " + (e && e.message), "err");
      if (e && e.debugInfo) log("  debugInfo: " + JSON.stringify(e.debugInfo), "err");
      reply({ id: req.id, ok: false, error: serializeError(e), durationMs: ms });
    });
  }

  function reply(obj, rawResult) {
    var text = JSON.stringify(obj);
    if (rawResult !== undefined) text = text.replace("\"__RAW__\"", function () { return rawResult; });
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(text);
    else log("reply for " + obj.id + " dropped: socket not open", "err");
  }

  var btn = document.getElementById("reconnect"); if (btn) btn.onclick = function () { replaced = false; if (ws) ws.close(); connect(); };
  btn = document.getElementById("clear"); if (btn) btn.onclick = function () { logEl.textContent = ""; };

  var readyFired = false;
  // Outside a host (plain tab, headless probe) Office.onReady may never fire; still bring the socket up.
  setTimeout(function () {
    if (!readyFired) { log("Office.onReady did not fire within 5s; connecting anyway", "err"); connect(); }
  }, 5000);

  Office.onReady(function (info) {
    readyFired = true;
    var h = hostInfo();
    envEl.textContent = "host=" + info.host + " platform=" + info.platform + " office.js=" + h.version +
      "\nrequirement sets: ExcelApi " + highestExcelApi() + "\nua=" + navigator.userAgent;
    if (typeof Excel === "undefined" || !info.host) {
      // Opened in a plain browser tab: no workbook, but the socket can still be exercised.
      log("not inside Excel (host=" + info.host + "); scripts will fail but the socket will connect", "err");
      connect();
      return;
    }
    Excel.run(function (context) {
      var wb = context.workbook;
      wb.load("name");
      return context.sync().then(function () { workbookName = wb.name; });
    }).catch(function (e) { log("workbook name: " + e.message, "err"); }).then(connect);
  });

  function highestExcelApi() {
    var versions = ["1.1","1.2","1.3","1.4","1.5","1.6","1.7","1.8","1.9","1.10","1.11","1.12","1.13","1.14","1.15","1.16","1.17","1.18","1.19","1.20"];
    var best = "none";
    var req = Office.context && Office.context.requirements;
    if (!req) return "n/a (no host)";
    for (var i = 0; i < versions.length; i++) {
      if (req.isSetSupported("ExcelApi", versions[i])) best = versions[i];
    }
    return best;
  }
  }
})();
