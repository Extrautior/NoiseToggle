const fs = require('fs');
const path = require('path');

const root = process.env.NOISETOGGLE_BROADCAST_APP_DIR
  ? path.resolve(process.env.NOISETOGGLE_BROADCAST_APP_DIR)
  : fs.existsSync(path.resolve('app'))
    ? path.resolve('app')
    : path.resolve('broadcast_patch/app');
const mainPath = path.join(root, 'build/electron/main.js');
let main = fs.readFileSync(mainPath, 'utf8');
const marker = '/* NoiseToggle Broadcast Bridge v4 */';
if (main.includes(marker)) {
  console.log('Bridge already present');
  process.exit(0);
}

const bridge = `
;${marker}
(function(){
  try {
    const ntHttp = require('http');
    const ntPath = require('path');
    const ntFs = require('fs');
    const ntPort = 28474;
    const ntSettingsPath = ntPath.join(process.env.APPDATA || '', 'NoiseToggle', 'settings.json');
    function ntJson(res, status, body) {
      const text = JSON.stringify(body);
      res.writeHead(status, {'Content-Type':'application/json','Content-Length':Buffer.byteLength(text)});
      res.end(text);
    }
    function ntToken() {
      try { return JSON.parse(ntFs.readFileSync(ntSettingsPath, 'utf8')).BridgeToken || ''; }
      catch (_) { return ''; }
    }
    function ntAuthorized(req) {
      const remote = (req.socket && req.socket.remoteAddress) || '';
      if (!(remote === '127.0.0.1' || remote === '::1' || remote === '::ffff:127.0.0.1')) return false;
      const expected = ntToken();
      return expected && req.headers.authorization === 'Bearer ' + expected;
    }
    function ntReadBody(req) {
      return new Promise((resolve, reject) => {
        let data = '';
        req.on('data', chunk => {
          data += chunk;
          if (data.length > 1024 * 1024) reject(new Error('request-too-large'));
        });
        req.on('end', () => {
          try { resolve(data ? JSON.parse(data) : {}); }
          catch (_) { reject(new Error('invalid-json')); }
        });
        req.on('error', reject);
      });
    }
    async function ntRendererToggle(enabled) {
      if (!mainWin || mainWin.isDestroyed()) return { ok:false, error:'main-window-not-ready' };
      const script = [
        '(async function(){',
        'const desired = ' + (enabled ? 'true' : 'false') + ';',
        'const wait = ms => new Promise(r => setTimeout(r, ms));',
        'function text(el){ return ((el && el.textContent) || \"\").replace(/\\\\s+/g, \" \").trim(); }',
        'function state(el){',
        '  if (!el) return null;',
        '  if (typeof el.checked === \"boolean\") return el.checked;',
        '  const aria = el.getAttribute && el.getAttribute(\"aria-checked\");',
        '  if (aria === \"true\") return true;',
        '  if (aria === \"false\") return false;',
        '  const cls = String(el.className || \"\").toLowerCase();',
        '  if (/(checked|enabled|active|on)/.test(cls) && !/(unchecked|disabled|off)/.test(cls)) return true;',
        '  return null;',
        '}',
        'function controlsNear(label){',
        '  const out = [];',
        '  let node = label;',
        '  for (let i = 0; node && i < 8; i++, node = node.parentElement) out.push(...node.querySelectorAll(\"input[type=checkbox], [role=switch], [aria-checked], button\"));',
        '  return [...new Set(out)];',
        '}',
        'if (!/noise removal/i.test(document.body && document.body.innerText || \"\") && location.hash) { location.hash = \"\"; await wait(1200); }',
        'let labels = [...document.querySelectorAll(\"body *\")].filter(el => /noise removal/i.test(text(el))).sort((a,b) => text(a).length - text(b).length);',
        'let controls = [];',
        'for (const label of labels) controls.push(...controlsNear(label));',
        'controls = [...new Set(controls)].filter(el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; });',
        'let target = controls.find(el => state(el) !== null) || controls[0];',
        'if (!target) return { ok:false, error:\"toggle-not-found\", labels: labels.length };',
        'const before = state(target);',
        'if (before !== desired) target.click();',
        'let after = state(target);',
        'for (let i = 0; i < 12 && after !== desired; i++) { await wait(250); after = state(target); }',
        'return { ok: after === desired || after === null, before, after, tag: target.tagName, role: target.getAttribute(\"role\"), labels: labels.length, controls: controls.length };',
        '})()'
      ].join('\\n');
      return await mainWin.webContents.executeJavaScript(script, true);
    }
    async function ntSetNoiseRemoval(enabled) {
      try {
        persistenceStorage && persistenceStorage.AddInfo(['AppStorage','MaxineEffects','MicrophoneEffects','microphoneNoiseRemoval','enabled'], enabled);
      } catch (e) {
        log && log.warn && log.warn('NoiseToggle bridge persistence update failed', e);
      }
      let renderer = await ntRendererToggle(enabled);
      return { ok: !!(renderer && renderer.ok), renderer };
    }
    async function ntDebug() {
      const data = {
        ok: true,
        version: 4,
        hasMainWindow: !!(mainWin && !mainWin.isDestroyed()),
        mainWindowUrl: mainWin && !mainWin.isDestroyed() ? mainWin.webContents.getURL() : null,
        backendKeys: [],
        renderer: null,
        effectsGroupsData: null
      };
      try { data.backendKeys = Object.keys(backend_comm || {}).sort(); } catch (e) { data.backendKeysError = String(e && e.message || e); }
      try {
        if (backend_comm && backend_comm.getEffectsGroupsData) data.effectsGroupsData = backend_comm.getEffectsGroupsData();
      } catch (e) {
        data.effectsGroupsError = String(e && e.message || e);
      }
      try {
        if (mainWin && !mainWin.isDestroyed()) {
          data.renderer = await mainWin.webContents.executeJavaScript([
            '({',
            'url: location.href,',
            'hash: location.hash,',
            'title: document.title,',
            'readyState: document.readyState,',
            'bodyText: (document.body && document.body.innerText || \"\").slice(0, 2000),',
            'bodyHtml: (document.body && document.body.innerHTML || \"\").slice(0, 2000)',
            '})'
          ].join('\\n'), true);
        }
      } catch (e) {
        data.rendererError = String(e && e.message || e);
      }
      return data;
    }
    const ntServer = ntHttp.createServer(async (req, res) => {
      try {
        if (!ntAuthorized(req)) return ntJson(res, 401, { ok:false, error:'unauthorized' });
        const url = new URL(req.url, 'http://127.0.0.1:' + ntPort);
        if (req.method === 'GET' && url.pathname === '/noisetoggle/v1/health') return ntJson(res, 200, { ok:true });
        if (req.method === 'GET' && url.pathname === '/noisetoggle/v1/debug') return ntJson(res, 200, await ntDebug());
        if (req.method === 'POST' && url.pathname === '/noisetoggle/v1/microphone-noise-removal') {
          const body = await ntReadBody(req);
          if (typeof body.enabled !== 'boolean') return ntJson(res, 400, { ok:false, error:'enabled-must-be-boolean' });
          const result = await ntSetNoiseRemoval(body.enabled);
          return ntJson(res, result.ok ? 200 : 500, result);
        }
        return ntJson(res, 404, { ok:false, error:'not-found' });
      } catch (e) {
        try { log && log.error && log.error('NoiseToggle bridge request failed', e); } catch (_) {}
        return ntJson(res, 500, { ok:false, error:String(e && e.message || e) });
      }
    });
    ntServer.on('error', e => { try { log && log.warn && log.warn('NoiseToggle bridge server error', e); } catch (_) {} });
    ntServer.listen(ntPort, '127.0.0.1', () => { try { log && log.info && log.info('NoiseToggle bridge listening on 127.0.0.1:' + ntPort); } catch (_) {} });
  } catch (e) {
    try { log && log.error && log.error('NoiseToggle bridge failed to initialize', e); } catch (_) {}
  }
})();
`;
fs.writeFileSync(mainPath, main + bridge, 'utf8');
console.log('Bridge appended to ' + mainPath);
