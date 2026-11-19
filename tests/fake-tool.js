'use strict';
// Faux outil pour le test automatique de KKAO (KKAO.exe --selftest <rapport.json>).
// Parle le protocole KKAO v1 (..\PROTOCOLE.md) sur le tube recu par --kkao.
//   --id <id>                identifiant (fake1...)
//   --status <etat>          etat publie au demarrage (idle, active, warn, pause, rec, error)
//   --noise <n>              n lignes de 1 Ko sur stdout AVANT la connexion (KKAO doit les lire en continu)
//   --retry-once [<marqueur>] premier lancement : hello, retry puis code 75 ; ensuite normal (fichier marqueur)
//   --retry-always           retry + code 75 a chaque lancement
//   --retry-after <ms>       valeur "after" du retry (500 par defaut)
// Commandes reconnues : status:<etat>, busy, free (status.busy), crash:<code>, closepipe ; toute commande est renvoyee en log "command:<id>".
const net = require('net');
const fs = require('fs');
const os = require('os');
const path = require('path');

const argv = process.argv.slice(2);
function opt(name) {
  const i = argv.indexOf(name);
  if (i < 0) return undefined;
  const v = argv[i + 1];
  return v !== undefined && !v.startsWith('--') ? v : true;
}

const id = typeof opt('--id') === 'string' ? opt('--id') : 'fake';
const status0 = typeof opt('--status') === 'string' ? opt('--status') : 'idle';
const noise = Number(opt('--noise')) || 0;
const retryOnce = opt('--retry-once');
const retryAlways = opt('--retry-always') !== undefined;
const retryAfter = Number(opt('--retry-after')) || 500;
const pipeArg = opt('--kkao');
// --v2 : protocole 2 (icone dans le hello, catalogue "actions", reglages exposes "settings", setSetting)
const v2 = opt('--v2') !== undefined;
// PNG 1x1 violet (icone du hello)
const ICON = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mPMtfzPAAAEYgIJ3d0zDQAAAABJRU5ErkJggg==';
const ACTIONS = [
  { id: 'quickNote', label: 'Nouvelle note…', shortcut: 'Ctrl+Alt+F1', default: true, pin: true },
  { id: 'newPostIt', label: 'Nouveau post-it', pin: true },
  { id: 'showPostIts', label: 'Afficher les post-it', check: true, checked: false },
  { id: 'later', label: 'Plus tard', enabled: false, default: true },
  { id: 'id invalide', label: 'Ignorée (espace dans l\'id)' },
  { id: 'settings', label: 'Réglages…', description: 'Ouvre les réglages' }
];
let settingValues = { mode: 'a', flag: true, hotkey: 'Ctrl+Alt+F1' };
let settingError = null;
function settingsMessage() {
  return {
    type: 'settings', sections: [
      { title: 'Général', items: [
        { id: 'mode', label: 'Mode', type: 'choice', value: settingValues.mode, options: [{ value: 'a', label: 'A' }, { value: 'b', label: 'B' }], error: settingError },
        { id: 'flag', label: 'Option', type: 'toggle', value: settingValues.flag },
        { id: 'hotkey.quickNote', label: 'Nouvelle note', type: 'hotkey', value: settingValues.hotkey },
        { id: 'secret', label: 'Type inconnu', type: 'motdepasse', value: 'x' },
        { id: 'open', label: 'Ouvrir…', type: 'button', action: 'settings' }
      ] },
      { title: 'Vide', items: [] }
    ]
  };
}

// Filet de securite : un faux outil oublie ne vit jamais plus de 2 minutes
setTimeout(() => process.exit(0), 120000);

for (let i = 0; i < noise; i++) process.stdout.write('fake ' + id + ' bruit ' + i + ' ' + '.'.repeat(1000) + '\n');
console.log('fake ' + id + ' stdout ok');
console.error('fake ' + id + ' stderr ok');

if (typeof pipeArg !== 'string') {
  console.error('fake ' + id + ' : --kkao manquant');
  process.exit(2);
}
// Nom complet (\\.\pipe\...) ou court : les deux sont acceptes
const pipePath = pipeArg.startsWith('\\\\') ? pipeArg : '\\\\.\\pipe\\' + pipeArg;

const MENU = [
  { id: 'newNote', label: 'Nouvelle note…', shortcut: 'Ctrl+Alt+N', pin: true, bold: true },
  { id: 'showPostIts', label: 'Afficher les post-it', check: true, checked: true },
  { type: 'separator' },
  { id: 'zone-1', label: 'Zone 1920×1080', radio: true, checked: false },
  { id: 'zone-é', label: 'Zone 1280×720', radio: true, checked: true },
  { type: 'separator' },
  {
    label: 'Reprendre une procédure', items: [
      { id: 'proc-a', label: 'Procédure A & B' },
      { id: 'proc-b', label: 'Procédure désactivée', enabled: false },
      { label: 'Archives', items: [{ id: 'proc-old', label: 'Vieille procédure' }] }
    ]
  },
  { type: 'futur', label: 'Type inconnu : ignoré' },
  { id: 'settings', label: 'Réglages…', champFutur: 42 }
];

function connect(done) {
  const t0 = Date.now();
  const attempt = () => {
    const sock = net.connect(pipePath);
    sock.once('connect', () => { sock.removeAllListeners('error'); done(sock); });
    sock.once('error', (e) => {
      sock.destroy();
      if (Date.now() - t0 >= 10000) {
        console.error('fake ' + id + ' : tube injoignable (' + e.message + ')');
        process.exit(3);
      }
      setTimeout(attempt, 200);
    });
  };
  attempt();
}

connect((sock) => {
  let exitCode = 0;
  let leaving = false;
  let stayAfterClose = false;
  const send = (obj) => { if (!sock.destroyed) sock.write(JSON.stringify(obj) + '\n'); };
  // Tout est ecrit (et lu par KKAO) avant la fin du processus
  const leave = (code) => {
    exitCode = code;
    if (leaving) return;
    leaving = true;
    sock.end(() => process.exit(exitCode));
    setTimeout(() => process.exit(exitCode), 1500);
  };

  send(v2 ? { type: 'hello', protocol: 2, tool: id, name: 'Fake ' + id, version: '0.0.2', icon: ICON }
          : { type: 'hello', protocol: 1, tool: id, name: 'Fake ' + id, version: '0.0.1' });

  const marker = retryOnce === undefined ? null
    : typeof retryOnce === 'string' ? retryOnce : path.join(os.tmpdir(), 'kkao-fake-' + id + '.retry');
  if (retryAlways || (marker && !fs.existsSync(marker))) {
    if (marker) fs.writeFileSync(marker, String(Date.now()));
    send({ type: 'retry', after: retryAfter, reason: 'test du faux outil ' + id });
    leave(75);
    return;
  }

  sock.write('ceci n\'est pas du JSON\n');
  send({ type: 'zzz-inconnu', champ: 1 });
  send({ type: 'menu', items: MENU });
  send({ type: 'status', state: status0, tooltip: 'Fake ' + id + ' — ' + status0 });
  send({ type: 'notify', title: 'Notification ' + id, text: 'Bonjour é ✓ & <b>', kind: 'warn' });
  send({ type: 'log', text: 'demarre' });

  let buf = '';
  sock.setEncoding('utf8');
  sock.on('data', (d) => {
    buf += d;
    let i;
    while ((i = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, i).replace(/\r$/, '');
      buf = buf.slice(i + 1);
      if (line.trim()) onLine(line);
    }
  });
  // KKAO a ferme le tube (il quitte ou a plante) : on quitte proprement
  const gone = () => { if (!stayAfterClose) process.exit(exitCode); };
  sock.on('end', gone);
  sock.on('close', gone);
  sock.on('error', gone);

  function onLine(line) {
    let msg;
    try { msg = JSON.parse(line); } catch (e) { send({ type: 'log', text: 'ligne illisible recue' }); return; }
    if (msg.type === 'welcome') {
      send({ type: 'log', text: 'welcome:' + msg.host + ':' + msg.protocol + ':' + msg.version });
      const features = Array.isArray(msg.features) ? msg.features : [];
      if (v2 && features.indexOf('actions') >= 0) send({ type: 'actions', items: ACTIONS });
      if (v2 && features.indexOf('settings') >= 0) send(settingsMessage());
    } else if (msg.type === 'setSetting') {
      // "mode" accepte a ou b ; le reste est enregistre tel quel ; reglages toujours republies
      if (msg.id === 'mode') {
        if (msg.value === 'a' || msg.value === 'b') { settingValues.mode = msg.value; settingError = null; }
        else settingError = 'Valeur refusée : ' + msg.value;
      } else if (msg.id === 'flag') settingValues.flag = !!msg.value;
      else if (msg.id === 'hotkey.quickNote') settingValues.hotkey = String(msg.value);
      send({ type: 'log', text: 'setSetting:' + msg.id + ':' + JSON.stringify(msg.value) });
      send(settingsMessage());
    } else if (msg.type === 'command') {
      const cid = String(msg.id);
      send({ type: 'log', text: 'command:' + cid + (msg.source ? ':' + msg.source : '') });
      let m;
      if ((m = /^status:(\w+)$/.exec(cid))) {
        send({ type: 'status', state: m[1], tooltip: 'Fake ' + id + ' — ' + m[1] });
      } else if (cid === 'busy' || cid === 'free') {
        send({ type: 'status', state: 'active', busy: cid === 'busy', tooltip: 'Fake ' + id + ' — ' + cid });
      } else if ((m = /^crash:(\d+)$/.exec(cid))) {
        exitCode = Number(m[1]);
        stayAfterClose = true;
        setTimeout(() => process.exit(exitCode), 50);
      } else if (cid === 'closepipe') {
        // L'outil ferme son cote du tube mais reste vivant un instant : KKAO doit voir la deconnexion d'abord
        stayAfterClose = true;
        sock.end(() => sock.destroy());
        setTimeout(() => process.exit(0), 600);
      }
    } else if (msg.type === 'quit') {
      send({ type: 'log', text: 'quit' });
      leave(0);
    }
  }
});
