/* Excel MCP Bridge task pane: dials the hub's /excel/bridge WebSocket and runs the scripts it sends,
 * speaking bridge protocol v1 (hub/protocol/protocol.go). Ported from the proof of concept; the
 * runner semantics are the POC's (a script is the body of an async function taking `context`, the
 * Excel.RequestContext from Excel.run; its return value is JSON-serialised back; errors verbatim).
 *
 * The hello carries a bridge token the user obtains by signing in with Microsoft from this pane
 * (PRD §06 path 1): the hub's /bridge/authorize runs in an Office dialog and hands back a 90-day
 * token, kept in OfficeRuntime.storage. The token maps to the same user_id an OAuth MCP session
 * for that account has, which is how Claude's list_instances finds this pane.
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
  // The hub's pane sign-in and revocation endpoints live at the origin root, not under the
  // connector (hub/internal/authserver/bridge.go).
  var AUTHORIZE_URL = location.origin + "/bridge/authorize";
  var REVOKE_URL = location.origin + "/bridge/revoke";
  // One credential per hub origin and connector, so a pane pointed at staging and one at prod
  // never hand each other the wrong token.
  var CRED_KEY = "hub.bridge:" + location.origin + ":" + CONNECTOR;
  var RECONNECT_MIN_MS = 1000, RECONNECT_MAX_MS = 30000;

  var statusEl = document.getElementById("status");
  var envEl = document.getElementById("env");
  var logEl = document.getElementById("log");
  var signedOutEl = document.getElementById("signed-out");
  var signedInEl = document.getElementById("signed-in");
  var whoEl = document.getElementById("who");
  var signInBtn = document.getElementById("sign-in");

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
  var signingOut = false; // the close we are about to see is ours; do not reconnect
  var documents = [];
  var resultLimit = 16 << 20;
  var running = null; // id of the exec currently inside Excel.run, for cancel replies
  // The bridge credential: {token, user, expires_at} from the hub's sign-in page, or null.
  var cred = null;

  function log(msg, cls) {
    var line = document.createElement("div");
    if (cls) line.className = cls;
    line.textContent = new Date().toISOString().slice(11, 23) + " " + msg;
    logEl.appendChild(line);
    while (logEl.childNodes.length > 200) logEl.removeChild(logEl.firstChild);
    logEl.scrollTop = logEl.scrollHeight;
  }
  function setStatus(text, cls) { statusEl.textContent = text; statusEl.className = cls || ""; }

  function hostInfo() {
    var d = (window.Office && Office.context && Office.context.diagnostics) || {};
    return { app: String(d.host || "none"), platform: String(d.platform || "browser"), version: String(d.version || "") };
  }

  // --- Credential storage ------------------------------------------------------------------
  // OfficeRuntime.storage is the Office way: it persists across sessions and is shared with the
  // shared runtime. localStorage is the fallback when it is absent (a plain tab, an old host).
  // Both are wrapped in promises so the callers read the same either way.
  function storage() {
    var rt = window.OfficeRuntime && OfficeRuntime.storage;
    if (rt && rt.getItem) {
      return {
        name: "OfficeRuntime.storage",
        get: function (k) { return rt.getItem(k); },
        set: function (k, v) { return rt.setItem(k, v); },
        remove: function (k) { return rt.removeItem(k); }
      };
    }
    return {
      name: "localStorage",
      get: function (k) { return new Promise(function (res) { try { res(localStorage.getItem(k)); } catch (e) { res(null); } }); },
      set: function (k, v) { return new Promise(function (res, rej) { try { localStorage.setItem(k, v); res(); } catch (e) { rej(e); } }); },
      remove: function (k) { return new Promise(function (res) { try { localStorage.removeItem(k); } catch (e) { /* gone anyway */ } res(); }); }
    };
  }

  function loadCredential() {
    var st = storage();
    return st.get(CRED_KEY).then(function (raw) {
      if (!raw) return null;
      var c;
      try { c = JSON.parse(raw); } catch (e) { return null; }
      if (!c || typeof c.token !== "string" || !c.token) return null;
      if (c.expires_at && Date.parse(c.expires_at) < Date.now()) {
        log("stored sign-in expired " + c.expires_at + "; sign in again");
        st.remove(CRED_KEY);
        return null;
      }
      return c;
    }, function (e) { log("cannot read stored sign-in from " + st.name + ": " + (e && e.message), "err"); return null; });
  }

  function saveCredential(c) {
    var st = storage();
    return st.set(CRED_KEY, JSON.stringify({ token: c.token, user: c.user || "", expires_at: c.expires_at || "" }))
      .then(function () { log("sign-in stored in " + st.name); },
            function (e) { log("cannot store sign-in in " + st.name + ": " + (e && e.message) + "; it lasts until this pane reloads", "err"); });
  }

  function renderAccount() {
    signedOutEl.hidden = !!cred;
    signedInEl.hidden = !cred;
    whoEl.textContent = cred ? (cred.user || "Microsoft account") : "";
  }

  // --- Sign in -----------------------------------------------------------------------------
  // Opens the hub's /bridge/authorize. Inside Office that is an Office dialog (its own window;
  // the page hands the token back with messageParent). Outside, or when the dialog API is
  // missing, a plain popup that postMessages to us. Either way onCredential finishes it.
  var dialog = null;
  var pendingPopup = null;

  function signInURL() {
    var h = hostInfo();
    return AUTHORIZE_URL + "?connector=" + encodeURIComponent(CONNECTOR) + "&label=" + encodeURIComponent(h.app + "/" + h.platform);
  }

  // switch=1 tells the hub to clear its session and go to Microsoft even
  // though this browser is already signed in (hub/internal/authserver/
  // bridge.go), so the account chooser fires instead of silently reusing
  // whichever account the hub session holds.
  function switchAccountURL() {
    return signInURL() + "&switch=1";
  }

  function signIn(url) {
    url = url || signInURL();
    var ui = window.Office && Office.context && Office.context.ui;
    if (ui && ui.displayDialogAsync) {
      setStatus("Signing in… complete the Microsoft sign-in in the window that opened.");
      log("opening sign-in dialog");
      ui.displayDialogAsync(url, { height: 65, width: 35, promptBeforeOpen: false }, function (result) {
        if (result.status === Office.AsyncResultStatus.Failed) {
          var code = result.error && result.error.code;
          log("dialog failed: " + code + " " + (result.error && result.error.message), "err");
          if (code === 12007) { setStatus("A sign-in window is already open.", "bad"); return; }
          if (code === 12011) { setStatus("The browser blocked the sign-in window. Allow pop-ups for this site and try again.", "bad"); return; }
          // 12004 (domain not trusted), 12005 (not https), anything else: try a plain popup.
          signInPopup(url);
          return;
        }
        dialog = result.value;
        dialog.addEventHandler(Office.EventType.DialogMessageReceived, function (arg) {
          var payload = parsePayload(arg.message);
          if (!payload) { log("dialog sent something that is not a sign-in: " + String(arg.message).slice(0, 80), "err"); return; }
          try { dialog.close(); } catch (e) { /* already closed */ }
          dialog = null;
          onCredential(payload);
        });
        dialog.addEventHandler(Office.EventType.DialogEventReceived, function (arg) {
          var code = arg.error;
          dialog = null;
          if (code === 12006) { log("sign-in window closed before finishing"); if (!cred) setStatus("Sign-in cancelled. Sign in to connect.", "bad"); return; }
          log("dialog event " + code, "err");
          setStatus("The sign-in window could not load (" + code + "). Try again.", "bad");
        });
      });
      return;
    }
    signInPopup(url);
  }

  function signInPopup(url) {
    log("opening sign-in popup (no dialog API)");
    setStatus("Signing in… complete the Microsoft sign-in in the window that opened.");
    pendingPopup = window.open(url, "hub-signin", "popup,width=520,height=680");
    if (!pendingPopup) { setStatus("The browser blocked the sign-in window. Allow pop-ups for this site and try again.", "bad"); }
  }
  window.addEventListener("message", function (ev) {
    if (ev.origin !== location.origin) return;
    var payload = parsePayload(ev.data);
    if (!payload) return;
    try { if (pendingPopup && !pendingPopup.closed) pendingPopup.close(); } catch (e) { /* cross-window close refused */ }
    pendingPopup = null;
    onCredential(payload);
  });

  function parsePayload(raw) {
    var p = raw;
    if (typeof raw === "string") { try { p = JSON.parse(raw); } catch (e) { return null; } }
    if (!p || typeof p.token !== "string" || !p.token) return null;
    if (p.connector && p.connector !== CONNECTOR) { log("sign-in was for connector " + p.connector, "err"); return null; }
    return { token: p.token, user: String(p.user || ""), expires_at: String(p.expires_at || "") };
  }

  function onCredential(c) {
    cred = c;
    renderAccount();
    log("signed in as " + (c.user || "?") + (c.expires_at ? ", until " + c.expires_at.slice(0, 10) : ""));
    saveCredential(c).then(function () {
      replaced = false;
      reconnectDelay = RECONNECT_MIN_MS;
      if (ws && ws.readyState !== WebSocket.CLOSED) ws.close(); else connect();
    });
  }

  // --- Switch account ------------------------------------------------------------------------
  // Forget the stored token first, so a switch that is abandoned partway (the dialog closed, the
  // hub unreachable) does not leave the pane quietly using the old account's still-valid token —
  // it falls back to signed-out. Then run the same sign-in dance with switch=1, which makes the
  // hub reach Microsoft's chooser instead of reusing its session.
  function switchAccount() {
    cred = null;
    renderAccount();
    storage().remove(CRED_KEY);
    log("switching Microsoft account");
    signIn(switchAccountURL());
  }

  // --- Sign out ----------------------------------------------------------------------------
  // Forget the token here and revoke it on the hub (best effort: a hub that cannot be reached
  // still lets the token expire, and the kill switch covers the rest).
  function signOut() {
    var old = cred;
    cred = null;
    renderAccount();
    signingOut = true;
    if (ws && ws.readyState !== WebSocket.CLOSED) ws.close(1000, "signed out");
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    storage().remove(CRED_KEY);
    setStatus("Signed out. Sign in to connect.");
    log("signed out");
    if (!old) return;
    fetch(REVOKE_URL, {
      method: "POST", keepalive: true,
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: "token=" + encodeURIComponent(old.token)
    }).then(function (r) { log("revoked on the MCP Server: HTTP " + r.status); },
            function (e) { log("could not revoke on the MCP Server (" + e.message + "); it expires on its own", "err"); });
  }

  // The hub refused the token: it was revoked (sign-out elsewhere, kill switch) or expired.
  // Forget it so the pane shows Sign in instead of retrying a dead credential.
  function credentialRejected(reason) {
    cred = null;
    renderAccount();
    storage().remove(CRED_KEY);
    setStatus("The MCP Server rejected the sign-in (" + reason + "). Sign in again.", "bad");
  }

  signInBtn.onclick = function () { signIn(); };
  document.getElementById("switch-account").onclick = switchAccount;
  document.getElementById("sign-out").onclick = signOut;

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
    if (!cred) { setStatus("Sign in to connect this workbook to the MCP Server."); return; }
    signingOut = false;
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
      if (!cred) { ws.close(1000, "signed out"); return; }
      send("hello", {
        connector: CONNECTOR, protocol_version: PROTOCOL_VERSION, bridge_version: BRIDGE_VERSION,
        instance_id: instanceId, host: hostInfo(), documents: documents, token: cred.token
      });
      reconnectDelay = RECONNECT_MIN_MS;
      setStatus("Connected. Waiting for scripts.", "ok");
      log("connected as " + instanceId);
    };
    ws.onclose = function (ev) {
      var why = "code " + ev.code + (ev.reason ? ", " + ev.reason : "");
      log("closed: " + why + " clean=" + ev.wasClean);
      if (signingOut) { signingOut = false; return; }
      if (!cred) { setStatus("Signed out."); return; }
      if (replaced) { setStatus("Replaced by a newer connection. Click Reconnect to take over.", "bad"); return; }
      // 1008 is how the hub refuses a hello (bad token, wrong version); retrying will not help.
      if (ev.code === 1008) {
        if (/token rejected/.test(ev.reason || "")) { credentialRejected(ev.reason); return; }
        setStatus("Rejected by the MCP Server (" + (ev.reason || "policy violation") + ").", "bad");
        return;
      }
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
        case "export": runExport(p); break;
        case "import": runImport(p); break;
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
    if (replaced || reconnectTimer || !cred) return;
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

  // --- Export (export_file, hub PRD §10/§11) ------------------------------------------------
  // The hub asks for a file with `export`; the pane produces it and POSTs the bytes straight to
  // /<connector>/files?id=<id> over plain HTTPS (bridge token in Authorization), never through the
  // WebSocket and never as base64 in a script. csv has no Office.js "save as" — it is the active
  // sheet's used range, serialised the way Excel's own CSV export reads it (POC 08-csv-export.js);
  // xlsx/pdf are the whole document via getFileAsync (POC 09/10), assembled into a Blob directly
  // instead of the POC's base64 round trip, since a Blob is exactly what fetch's body wants.
  function runExport(req) {
    var id = req.id, format = req.format;
    log("export " + id + ": format=" + format);
    exportBytes(format).then(function (blob) {
      return uploadExport(id, blob);
    }).then(function () {
      log("export " + id + ": uploaded ok");
      send("result", { id: id, ok: true, duration_ms: 0 });
    }).catch(function (e) {
      log("export " + id + " failed: " + (e && e.message), "err");
      send("result", { id: id, ok: false, error: serializeError(e), duration_ms: 0 });
    });
  }

  function exportBytes(format) {
    if (typeof Excel === "undefined") return Promise.reject(new Error("the task pane is not running inside Excel"));
    switch (format) {
      case "csv": return exportCsv();
      case "xlsx": return exportWholeDocument(Office.FileType.Compressed, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
      case "pdf": return exportWholeDocument(Office.FileType.Pdf, "application/pdf");
      default: return Promise.reject(new Error("unsupported export format " + format));
    }
  }

  function exportCsv() {
    return Excel.run(function (context) {
      var sheet = context.workbook.worksheets.getActiveWorksheet();
      var used = sheet.getUsedRange(true);
      used.load("text");
      return context.sync().then(function () {
        var q = function (s) { return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
        var csv = used.text.map(function (row) { return row.map(q).join(","); }).join("\r\n") + "\r\n";
        return new Blob([csv], { type: "text/csv" });
      });
    });
  }

  // Whole-document export: getFileAsync + getSliceAsync, assembled into a Blob. Slices arrive as
  // Uint8Array (or an ArrayBuffer-like on some hosts); closeAsync always runs, success or failure,
  // the same discipline the POC scripts used with their `finally`.
  function exportWholeDocument(type, mime) {
    return new Promise(function (resolve, reject) {
      Office.context.document.getFileAsync(type, { sliceSize: 4 * 1024 * 1024 }, function (r) {
        if (r.status !== Office.AsyncResultStatus.Succeeded) { reject(new Error(r.error.code + ": " + r.error.message)); return; }
        var file = r.value;
        var chunks = [];
        var i = 0;
        function done(err, blob) { file.closeAsync(function () { if (err) reject(err); else resolve(blob); }); }
        (function next() {
          if (i >= file.sliceCount) { done(null, new Blob(chunks, { type: mime })); return; }
          file.getSliceAsync(i, function (sr) {
            if (sr.status !== Office.AsyncResultStatus.Succeeded) { done(new Error(sr.error.code + ": " + sr.error.message)); return; }
            var bytes = sr.value.data instanceof Uint8Array ? sr.value.data : new Uint8Array(sr.value.data);
            chunks.push(bytes);
            i += 1;
            next();
          });
        })();
      });
    });
  }

  function uploadExport(id, blob) {
    if (!cred) return Promise.reject(new Error("not signed in"));
    var url = BASE + "/files?id=" + encodeURIComponent(id);
    return fetch(url, { method: "POST", headers: { "Authorization": "Bearer " + cred.token }, body: blob })
      .then(function (resp) {
        if (resp.ok) return;
        return resp.text().then(function (t) { throw new Error("upload HTTP " + resp.status + ": " + t); });
      });
  }

  // --- Import (import_workbook, hub PRD §10/§11 reversed) -----------------------------------
  // The hub stages the xlsx bytes in its file store and hands the pane a signed URL instead of
  // sending the bytes over the WebSocket (mirrors export in reverse). The pane fetches it, converts
  // to base64 (Office.js's insertWorksheetsFromBase64 takes base64, not a Blob/ArrayBuffer — the
  // one place base64 is unavoidable, and it happens here, never in a user script or a tool argument
  // that isn't explicitly the file content), and inserts the sheets into the CURRENTLY OPEN
  // workbook. The signed URL is a cross-origin GET to storage.googleapis.com; if that fetch fails in
  // a way that looks like CORS (a TypeError with no HTTP status — fetch cannot distinguish a CORS
  // rejection from a network error), the pane reports a clear error rather than a bare "Failed to
  // fetch" so the failure is diagnosable from the tool result. There is no base64-in-message
  // fallback: the same-origin signed-URL path is live-verified against the staging bucket (see the
  // PR), and building a second wire shape for a fallback that live testing shows unnecessary would be
  // exactly the speculative complexity CONVENTIONS.md asks not to carry.
  function runImport(req) {
    var id = req.id;
    log("import " + id + ": url=" + req.url);
    fetchAndInsert(req).then(function (sheets) {
      log("import " + id + ": inserted, added [" + sheets.added.join(", ") + "]");
      send("result", { id: id, ok: true, result: { added_sheets: sheets.added, all_sheets: sheets.all }, duration_ms: sheets.ms });
    }).catch(function (e) {
      log("import " + id + " failed: " + (e && e.message), "err");
      send("result", { id: id, ok: false, error: serializeError(e), duration_ms: 0 });
    });
  }

  function arrayBufferToBase64(buf) {
    var bytes = new Uint8Array(buf);
    var chunk = 0x8000; // one string-from-char-code call per chunk; avoids a stack-limit crash on a large file
    var chars = [];
    for (var i = 0; i < bytes.length; i += chunk) {
      chars.push(String.fromCharCode.apply(null, bytes.subarray(i, i + chunk)));
    }
    return btoa(chars.join(""));
  }

  function fetchAndInsert(req) {
    if (typeof Excel === "undefined") return Promise.reject(new Error("the task pane is not running inside Excel"));
    var t0 = performance.now();
    return fetch(req.url).then(function (resp) {
      if (!resp.ok) throw new Error("fetch of the signed URL failed: HTTP " + resp.status);
      return resp.arrayBuffer();
    }, function (e) {
      // fetch() rejects with a plain TypeError for a network error and for a CORS rejection alike;
      // this is the pane's one chance to say which is likely before the generic error propagates.
      throw new Error("fetch of the signed URL failed (network error or CORS): " + (e && e.message));
    }).then(function (buf) {
      var b64 = arrayBufferToBase64(buf);
      return Excel.run(function (context) {
        var before = context.workbook.worksheets;
        before.load("items/name");
        return context.sync().then(function () {
          var beforeNames = before.items.map(function (s) { return s.name; });
          var opts = req.options || {};
          var insertOpts = {};
          if (opts.sheet_names_to_insert && opts.sheet_names_to_insert.length) insertOpts.sheetNamesToInsert = opts.sheet_names_to_insert;
          insertOpts.positionType = opts.position_type || "End";
          if (opts.relative_to_sheet) insertOpts.relativeTo = context.workbook.worksheets.getItem(opts.relative_to_sheet);
          context.workbook.insertWorksheetsFromBase64(b64, insertOpts);
          var after = context.workbook.worksheets;
          after.load("items/name");
          return context.sync().then(function () {
            var afterNames = after.items.map(function (s) { return s.name; });
            var added = afterNames.filter(function (n) { return beforeNames.indexOf(n) === -1; });
            return { added: added, all: afterNames, ms: performance.now() - t0 };
          });
        });
      });
    });
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

  // The stored credential is read once Office is ready (OfficeRuntime.storage needs it), then
  // the socket comes up if there is one; otherwise the pane waits for Sign in.
  function start() {
    loadCredential().then(function (c) {
      cred = c;
      renderAccount();
      connect();
    });
  }

  var readyFired = false;
  // Outside a host (plain tab, probe) Office.onReady may never fire; still bring the pane up.
  setTimeout(function () {
    if (!readyFired) { log("Office.onReady did not fire within 5s; starting anyway", "err"); start(); }
  }, 5000);

  Office.onReady(function (info) {
    readyFired = true;
    var h = hostInfo();
    envEl.textContent = "host=" + info.host + " platform=" + info.platform + " office.js=" + h.version + " hub=" + location.host +
      "\nExcelApi " + highestExcelApi() + "\ninstance " + instanceId;
    if (typeof Excel === "undefined" || !info.host) {
      log("not inside Excel (host=" + info.host + "); scripts will fail but the socket will connect", "err");
      start();
      return;
    }
    refreshDocuments(function () {
      start();
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
