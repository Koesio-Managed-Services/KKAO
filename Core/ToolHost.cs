using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace KKAO
{
    internal enum ToolRunState
    {
        Stopped,
        // Processus lance, en attente de "hello"
        Starting,
        Running,
        // "quit" envoye (ou tube ferme par KKAO), en attente de la fin du processus
        Stopping,
        // L'outil a demande "retry" : relance programmee
        Waiting,
    }

    internal sealed class ToolNotification
    {
        public ToolNotification(ToolHost tool, string title, string text, string kind, bool fromKkao)
        {
            Tool = tool;
            Title = title;
            Text = text;
            Kind = kind;
            FromKkao = fromKkao;
        }

        public ToolHost Tool { get; }
        public string Title { get; }
        public string Text { get; }
        // info, warn ou error
        public string Kind { get; }
        // true : message de KKAO lui-meme (outil arrete, impossible a lancer...)
        public bool FromKkao { get; }
    }

    // Un outil heberge (protocole KKAO v2, voir PROTOCOLE.md) : un tube nomme serveur + le processus de l'outil.
    // Tout l'etat est manipule sur le fil de l'interface ; les fils de lecture y renvoient chaque evenement.
    internal sealed class ToolHost
    {
        public const int ProtocolVersion = 2;
        public const int MaxConsecutiveRetries = 3;
        public const int MaxPendingCommands = 5;
        public const int PendingCommandTimeoutMs = 30000;
        const int MaxIconBase64 = 256 * 1024;
        static readonly string[] Features = { "actions", "settings", "icons", "startOnDemand" };
        const int HelloTimeoutMs = 30000;
        const int ExitDrainTimeoutMs = 2000;
        const int MaxLineBytes = 8 * 1024 * 1024;
        const int MaxRetryDelayMs = 120000;
        const int PipeBufferSize = 64 * 1024;
        const int ErrorBrokenPipe = 109;
        const int ErrorNoData = 232;

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly UTF8Encoding Utf8Lenient = new UTF8Encoding(false, false);
        static readonly int KkaoPid = Process.GetCurrentProcess().Id;

        readonly ToolManager _owner;
        readonly SynchronizationContext _ui;
        readonly HashSet<string> _unknownTypesLogged = new HashSet<string>();
        readonly List<PendingCommand> _pending = new List<PendingCommand>();
        Session _session;
        WinFormsTimer _respawnTimer;
        int _sessionCounter;
        string _lastMenuSummary;

        public ToolHost(ToolConfig config, ToolManager owner, SynchronizationContext ui)
        {
            Config = config;
            _owner = owner;
            _ui = ui;
        }

        public ToolConfig Config { get; private set; }
        public string Id => Config.Id;
        // Nom impose par tools.json / le paquet, sinon celui que l'appli donne dans son hello
        public string Name => Config.ConfigName ?? HelloName ?? Config.Name;
        public ToolRunState RunState { get; private set; } = ToolRunState.Stopped;
        public IReadOnlyList<MenuNode> Menu { get; private set; } = new List<MenuNode>();
        // v2 : catalogue d'actions et reglages exposes du lancement en cours
        public IReadOnlyList<ActionNode> Actions { get; private set; } = new List<ActionNode>();
        public bool HasActionsCatalog { get; private set; }
        public IReadOnlyList<SettingsSection> Settings { get; private set; } = new List<SettingsSection>();
        public bool HasSettings { get; private set; }
        // Incremente a chaque message "settings" (la fenetre sait qu'un setSetting a ete traite)
        public int SettingsRevision { get; private set; }
        public string HelloName { get; private set; }
        public byte[] HelloIcon { get; private set; }
        public int ToolProtocol { get; private set; }
        public int PendingCommandCount => _pending.Count;
        public StatusState Status { get; private set; }
        public string StatusTooltip { get; private set; }
        // status.busy (2.3) : ne pas fermer pour une mise a jour automatique ; « rec » compte toujours comme occupe
        public bool BusyFlag { get; private set; }
        public bool IsBusy => IsAlive && (BusyFlag || Status == StatusState.Rec);
        // Arret inattendu ou demarrage impossible : pastille rouge jusqu'au prochain demarrage
        public bool Crashed { get; private set; }
        public string CrashSummary { get; private set; }
        public int StartCount { get; private set; }
        public int ConsecutiveRetries { get; private set; }
        public int IgnoredLines { get; private set; }
        public int UnknownMessages { get; private set; }
        public List<int> ExitCodes { get; } = new List<int>();
        public string ToolVersion { get; private set; }

        // Processus lance (ou sa fin en cours de traitement)
        public bool IsAlive => _session != null;
        // Connecte, "hello" recu : on peut lui envoyer des commandes
        public bool IsReady => _session != null && _session.Connected && _session.HelloReceived && !_session.PipeClosedByUs;
        public int? ProcessId => _session?.ProcessId;
        public string PipePath => _session?.PipePath;

        #region Demarrage

        public void Start(bool userInitiated)
        {
            if (!Config.Protocol)
            {
                LaunchDetached();
                return;
            }
            if (_session != null || _owner.Quitting) return;
            CancelRespawn();
            if (userInitiated) ConsecutiveRetries = 0;
            Crashed = false;
            CrashSummary = null;
            ResetToolState();

            if (string.IsNullOrEmpty(Config.Exe) || !File.Exists(Config.Exe))
            {
                Fail("programme introuvable : " + (Config.Exe ?? Config.ExeRaw ?? "(« exe » manquant)"));
                return;
            }

            var s = new Session { Number = ++_sessionCounter };
            try
            {
                s.Pipe = CreatePipe(s);
            }
            catch (Exception ex)
            {
                Log.Error("[" + Id + "] creation du tube", ex);
                Fail("tube nommé impossible à créer (" + ex.Message + ")");
                return;
            }
            try
            {
                s.Pipe.BeginWaitForConnection(ar => OnConnected(s, ar), null);
            }
            catch (Exception ex)
            {
                Log.Error("[" + Id + "] attente de connexion", ex);
                s.ReaderDone = true;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Config.Exe,
                Arguments = CommandLine.Join(Config.Args.Concat(new[] { "--kkao", s.PipePath })),
                WorkingDirectory = !string.IsNullOrEmpty(Config.Cwd) && Directory.Exists(Config.Cwd) ? Config.Cwd : Path.GetDirectoryName(Config.Exe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
            };
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (o, e) => OnOutput(s, e.Data, false);
            process.ErrorDataReceived += (o, e) => OnOutput(s, e.Data, true);
            process.Exited += (o, e) => Post(() => OnProcessExited(s));
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                process.Dispose();
                s.Finalized = true;
                try { s.Pipe.Dispose(); } catch { }
                Log.Error("[" + Id + "] lancement de " + Config.Exe, ex);
                Fail(ex.Message);
                return;
            }

            s.Process = process;
            try { s.ProcessId = process.Id; } catch { }
            s.StartedUtc = DateTime.UtcNow;
            _session = s;
            StartCount++;
            RunState = ToolRunState.Starting;
            Log.Write("[" + Id + "] demarre : pid " + s.ProcessId + ", tube " + s.PipeName + (StartCount > 1 ? ", lancement n°" + StartCount : ""));
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log.Error("[" + Id + "] lecture de stdout/stderr", ex);
                s.StdoutDone = s.StderrDone = true;
            }
            s.HelloTimer = OneShot(HelloTimeoutMs, () =>
            {
                s.HelloTimer = null;
                if (_session == s && !s.HelloReceived && !s.ProcessExited)
                    Log.Write("[" + Id + "] toujours pas de hello apres " + HelloTimeoutMs / 1000 + " s (l'outil connait-il --kkao ?)");
            });
            Changed();
        }

        // protocol: false : simple lanceur, pas de tube ni de --kkao
        public void LaunchDetached()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Config.Exe ?? Config.ExeRaw ?? "",
                    Arguments = CommandLine.Join(Config.Args),
                    UseShellExecute = true,
                };
                if (!string.IsNullOrEmpty(Config.Cwd) && Directory.Exists(Config.Cwd)) startInfo.WorkingDirectory = Config.Cwd;
                using (Process.Start(startInfo)) { }
                StartCount++;
                Log.Write("[" + Id + "] lance (simple lanceur) : " + startInfo.FileName);
            }
            catch (Exception ex)
            {
                Log.Error("[" + Id + "] lancement de " + Config.Exe, ex);
                _owner.RaiseNotification(new ToolNotification(this, Name + " n'a pas pu démarrer", ex.Message, "error", true));
            }
        }

        NamedPipeServerStream CreatePipe(Session s)
        {
            // Nom du protocole : kkao-<idOutil>-<pidKKAO> ; variante numerotee si une ancienne instance traine encore
            var baseName = "kkao-" + Id + "-" + KkaoPid;
            IOException last = null;
            foreach (var name in new[] { baseName, baseName + "-" + s.Number })
            {
                try
                {
                    var pipe = NewServer(name);
                    s.PipeName = name;
                    return pipe;
                }
                catch (IOException ex)
                {
                    last = ex;
                    Log.Write("[" + Id + "] tube " + name + " indisponible : " + ex.Message);
                }
            }
            throw last;
        }

        static NamedPipeServerStream NewServer(string name)
        {
            // Seul l'utilisateur courant peut se connecter (pas les autres sessions du poste)
            PipeSecurity security;
            try
            {
                security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            catch (Exception ex)
            {
                security = null;
                Log.Write("Tube " + name + " : securite par defaut (" + ex.Message + ")");
            }
            if (security != null)
            {
                try
                {
                    return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBufferSize, PipeBufferSize, security);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Write("Tube " + name + " : securite par defaut (" + ex.Message + ")");
                }
            }
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBufferSize, PipeBufferSize);
        }

        #endregion

        #region Commandes de l'utilisateur

        // Arreter : "quit" (l'outil a 10 s, KKAO ne le tue jamais)
        public void Stop()
        {
            if (RunState == ToolRunState.Waiting)
            {
                CancelRespawn();
                RunState = ToolRunState.Stopped;
                Log.Write("[" + Id + "] relance annulee");
                Changed();
                return;
            }
            var s = _session;
            if (s == null) return;
            s.ExpectExit = true;
            if (!s.QuitSent && !s.QuitPending)
            {
                if (IsReady) SendQuit(s);
                else
                {
                    s.QuitPending = true;
                    Log.Write("[" + Id + "] arret demande : quit envoye des que l'outil aura dit hello");
                }
            }
            if (!s.ProcessExited) RunState = ToolRunState.Stopping;
            Changed();
        }

        public void Restart()
        {
            if (_session == null)
            {
                Start(userInitiated: true);
                return;
            }
            _session.RestartAfterExit = true;
            Stop();
        }

        // Clic sur un element du menu de l'outil : l'id est renvoye tel quel
        public bool SendCommand(string id, string source = null)
        {
            var s = _session;
            if (s == null || !IsReady)
            {
                Log.Write("[" + Id + "] commande « " + Log.Truncate(id, 80) + " » ignoree : outil non connecte");
                return false;
            }
            // L'outil pourra passer sa fenetre au premier plan (KKAO l'a, son menu vient d'etre clique)
            if (s.ProcessId > 0) NativeMethods.AllowSetForegroundWindow(s.ProcessId);
            Log.Write("[" + Id + "] commande : " + Log.Truncate(id, 200) + (source != null ? " (" + source + ")" : ""));
            var message = new Dictionary<string, object> { { "type", "command" }, { "id", id } };
            if (source != null) message["source"] = source;
            Send(s, message);
            return true;
        }

        // Action (menu rapide, raccourci de la fenetre…) : envoyee tout de suite, ou mise en attente et appli demarree a la demande
        public bool RunAction(string id, string source)
        {
            if (string.IsNullOrEmpty(id) || !Config.Protocol) return false;
            if (IsReady) return SendCommand(id, source);
            if (_owner.Quitting) return false;
            PurgeExpiredPending();
            if (_pending.Count >= MaxPendingCommands)
            {
                Log.Write("[" + Id + "] commande « " + Log.Truncate(id, 80) + " » ignoree : " + MaxPendingCommands + " deja en attente");
                return false;
            }
            _pending.Add(new PendingCommand { Id = id, Source = source, AtUtc = DateTime.UtcNow });
            Log.Write("[" + Id + "] commande « " + Log.Truncate(id, 80) + " » en attente du demarrage");
            OneShot(PendingCommandTimeoutMs + 200, () =>
            {
                if (!PurgeExpiredPending()) return;
                _owner.RaiseNotification(new ToolNotification(this, Name + " n'a pas démarré",
                    "L'action demandée a été abandonnée. Réessayez dans un instant.", "warn", true));
            });
            if (RunState == ToolRunState.Stopped) Start(userInitiated: true);
            else if (RunState == ToolRunState.Stopping && _session != null) _session.RestartAfterExit = true;
            Changed();
            return true;
        }

        // Reglage expose modifie dans la fenetre de KKAO
        public bool SendSetSetting(string id, object value)
        {
            var s = _session;
            if (s == null || !IsReady) return false;
            Log.Write("[" + Id + "] reglage modifie : " + Log.Truncate(id, 80));
            Send(s, new Dictionary<string, object> { { "type", "setSetting" }, { "id", id }, { "value", value } });
            return true;
        }

        // Mise a jour installee : nouveaux chemins (l'appli doit etre arretee)
        public void ReplaceConfig(ToolConfig config)
        {
            if (config == null || !string.Equals(config.Id, Id, StringComparison.OrdinalIgnoreCase)) return;
            Config = config;
            Changed();
        }

        // true : des commandes ont ete abandonnees
        bool PurgeExpiredPending()
        {
            var limit = DateTime.UtcNow.AddMilliseconds(-PendingCommandTimeoutMs);
            var removed = _pending.RemoveAll(p => p.AtUtc < limit);
            if (removed > 0) Log.Write("[" + Id + "] " + removed + " commande(s) en attente abandonnee(s) : pas de hello en " + PendingCommandTimeoutMs / 1000 + " s");
            return removed > 0;
        }

        void FlushPending(Session s)
        {
            PurgeExpiredPending();
            if (_pending.Count == 0) return;
            var batch = _pending.ToList();
            _pending.Clear();
            foreach (var command in batch) SendCommand(command.Id, command.Source);
        }

        // KKAO quitte (ou test) : tube ferme sans "quit" ; l'outil doit quitter de lui-meme
        public void ClosePipe()
        {
            var s = _session;
            if (s == null || s.PipeClosedByUs) return;
            s.ExpectExit = true;
            s.PipeClosedByUs = true;
            try { s.Pipe.Dispose(); } catch { }
            Log.Write("[" + Id + "] tube ferme par KKAO");
            if (!s.ProcessExited) RunState = ToolRunState.Stopping;
            Changed();
        }

        // Test automatique uniquement : jamais appele par l'interface
        internal bool KillForTest(int waitMs)
        {
            var process = _session?.Process;
            if (process == null) return false;
            try
            {
                if (process.WaitForExit(waitMs)) return false;
                process.Kill();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void CancelRespawn()
        {
            if (_respawnTimer == null) return;
            _respawnTimer.Stop();
            _respawnTimer.Dispose();
            _respawnTimer = null;
        }

        #endregion

        #region Tube : connexion, lecture, ecriture

        void OnConnected(Session s, IAsyncResult result)
        {
            try
            {
                s.Pipe.EndWaitForConnection(result);
            }
            catch (Exception)
            {
                // Tube ferme avant toute connexion (outil termine, KKAO qui quitte)
                Post(() => OnReaderDone(s));
                return;
            }
            s.ConnectedOnPool = true;
            try
            {
                s.Writer = new StreamWriter(s.Pipe, Utf8NoBom, 4096, true) { AutoFlush = true, NewLine = "\n" };
            }
            catch (Exception ex)
            {
                Log.Error("[" + Id + "] ecriture dans le tube", ex);
            }
            Post(() => OnConnectedUi(s));
            var reader = new Thread(() => ReadLoop(s)) { IsBackground = true, Name = "KKAO lecture " + Id };
            reader.Start();
        }

        void OnConnectedUi(Session s)
        {
            if (s.Finalized) return;
            s.Connected = true;
            Log.Write("[" + Id + "] connecte au tube");
        }

        void ReadLoop(Session s)
        {
            var buffer = new byte[16384];
            var line = new MemoryStream();
            var skipping = false;
            try
            {
                while (true)
                {
                    var count = s.Pipe.Read(buffer, 0, buffer.Length);
                    if (count <= 0) break;
                    var start = 0;
                    for (var i = 0; i < count; i++)
                    {
                        if (buffer[i] != (byte)'\n') continue;
                        if (!skipping)
                        {
                            line.Write(buffer, start, i - start);
                            EmitLine(s, line);
                        }
                        line.SetLength(0);
                        skipping = false;
                        start = i + 1;
                    }
                    if (skipping || start >= count) continue;
                    line.Write(buffer, start, count - start);
                    if (line.Length > MaxLineBytes)
                    {
                        skipping = true;
                        line.SetLength(0);
                        Post(() =>
                        {
                            IgnoredLines++;
                            Log.Write("[" + Id + "] ligne de plus de 8 Mo ignoree");
                        });
                    }
                }
                if (!skipping && line.Length > 0) EmitLine(s, line);
            }
            catch (Exception ex)
            {
                if (!(ex is ObjectDisposedException) && !s.PipeClosedByUs)
                    Log.Write("[" + Id + "] lecture du tube : " + ex.GetBaseException().Message);
            }
            finally
            {
                Post(() => OnReaderDone(s));
            }
        }

        void EmitLine(Session s, MemoryStream line)
        {
            var bytes = line.GetBuffer();
            var length = (int)line.Length;
            var offset = 0;
            // BOM UTF-8 en trop : tolere
            if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                offset = 3;
                length -= 3;
            }
            var text = Utf8Lenient.GetString(bytes, offset, length).TrimEnd('\r');
            if (text.Trim().Length == 0) return;
            Post(() => OnLine(s, text));
        }

        void Send(Session s, Dictionary<string, object> message)
        {
            if (s.Writer == null || s.PipeClosedByUs || s.Finalized) return;
            var json = Json.Serialize(message);
            var writer = s.Writer;
            // Ecritures dans l'ordre, hors du fil de l'interface (un outil fige ne bloque pas KKAO)
            s.WriteChain = s.WriteChain.ContinueWith(_ =>
            {
                try
                {
                    writer.WriteLine(json);
                }
                catch (Exception ex)
                {
                    if (s.WriteFailed) return;
                    s.WriteFailed = true;
                    // Outil deja parti (retry, fin du processus) : normal, la deconnexion est journalisee par ailleurs
                    var code = ex.GetBaseException().HResult & 0xFFFF;
                    if (s.PipeClosedByUs || ex.GetBaseException() is ObjectDisposedException || code == ErrorBrokenPipe || code == ErrorNoData) return;
                    Log.Write("[" + Id + "] ecriture dans le tube impossible : " + ex.GetBaseException().Message);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        void SendQuit(Session s)
        {
            s.QuitSent = true;
            s.QuitPending = false;
            Log.Write("[" + Id + "] quit envoye");
            Send(s, new Dictionary<string, object> { { "type", "quit" } });
        }

        #endregion

        #region Messages de l'outil

        void OnLine(Session s, string text)
        {
            if (s.Finalized || s != _session) return;
            var message = Json.ParseObject(text);
            if (message == null)
            {
                IgnoredLines++;
                Log.Write("[" + Id + "] ligne illisible ignoree (" + text.Length + " caracteres)");
                return;
            }
            var type = Json.GetString(message, "type");
            switch (type)
            {
                case "hello":
                    OnHello(s, message);
                    break;
                case "menu":
                    OnMenu(message);
                    break;
                case "actions":
                    OnActions(message);
                    break;
                case "settings":
                    OnSettings(message);
                    break;
                case "status":
                    OnStatus(message);
                    break;
                case "notify":
                    OnNotify(message);
                    break;
                case "retry":
                    OnRetry(s, message);
                    break;
                case "log":
                    Log.Write("[" + Id + "] " + Log.Truncate(Json.GetString(message, "text") ?? "", 2000));
                    break;
                default:
                    UnknownMessages++;
                    var label = Log.Truncate(type ?? "(sans type)", 60);
                    if (_unknownTypesLogged.Add(label)) Log.Write("[" + Id + "] message de type inconnu ignore : " + label);
                    break;
            }
        }

        void OnHello(Session s, Dictionary<string, object> message)
        {
            var tool = Json.GetString(message, "tool");
            var protocol = Json.GetInt(message, "protocol");
            ToolVersion = Json.GetString(message, "version");
            Log.Write("[" + Id + "] hello : " + Log.Truncate(Json.GetString(message, "name") ?? tool ?? "?", 80) + " " + Log.Truncate(ToolVersion ?? "?", 40)
                + ", protocole " + (protocol?.ToString() ?? "?"));
            if (protocol.HasValue && protocol.Value != ProtocolVersion)
                Log.Write("[" + Id + "] attention : l'outil parle le protocole " + protocol + ", KKAO le protocole " + ProtocolVersion);
            if (!string.IsNullOrEmpty(tool) && !string.Equals(tool, Id, StringComparison.OrdinalIgnoreCase))
                Log.Write("[" + Id + "] attention : l'outil se presente comme « " + Log.Truncate(tool, 60) + " »");
            ToolProtocol = protocol ?? 1;
            var name = OneLine(Json.GetString(message, "name"), 60);
            if (name != null) HelloName = name;
            var icon = Json.GetString(message, "icon");
            if (!string.IsNullOrEmpty(icon))
            {
                if (icon.Length > MaxIconBase64) Log.Write("[" + Id + "] icone du hello ignoree (plus de 256 Ko)");
                else
                {
                    try { HelloIcon = Convert.FromBase64String(icon); }
                    catch (FormatException) { Log.Write("[" + Id + "] icone du hello illisible (base64)"); }
                }
            }

            s.HelloReceived = true;
            Send(s, new Dictionary<string, object>
            {
                { "type", "welcome" },
                { "host", Branding.Name },
                { "version", Program.Version },
                { "protocol", ProtocolVersion },
                { "features", Features },
            });
            if (s.HelloTimer != null)
            {
                s.HelloTimer.Stop();
                s.HelloTimer.Dispose();
                s.HelloTimer = null;
            }
            if (RunState == ToolRunState.Starting) RunState = ToolRunState.Running;
            if (s.QuitPending && !s.QuitSent) SendQuit(s);
            else FlushPending(s);
            Changed();
        }

        void OnActions(Dictionary<string, object> message)
        {
            var actions = ActionNode.ParseList(Json.Get(message, "items"));
            var summary = actions.Count + " action(s)";
            if (!HasActionsCatalog || Actions.Count != actions.Count) Log.Write("[" + Id + "] catalogue : " + summary);
            Actions = actions;
            HasActionsCatalog = true;
            _owner.OnActionsReceived(this);
            Changed();
        }

        void OnSettings(Dictionary<string, object> message)
        {
            var sections = SettingsSection.ParseList(Json.Get(message, "sections"));
            if (!HasSettings) Log.Write("[" + Id + "] reglages exposes : " + sections.Sum(x => x.Items.Count) + " element(s)");
            Settings = sections;
            HasSettings = true;
            SettingsRevision++;
            Changed();
        }

        void OnMenu(Dictionary<string, object> message)
        {
            var items = MenuNode.ParseList(Json.Get(message, "items"));
            Menu = items;
            var summary = MenuStats.Of(items).ToString();
            if (summary != _lastMenuSummary)
            {
                _lastMenuSummary = summary;
                Log.Write("[" + Id + "] menu : " + summary);
            }
            Changed();
        }

        void OnStatus(Dictionary<string, object> message)
        {
            var raw = Json.GetString(message, "state");
            var state = Status;
            if (raw != null && !StatusStates.TryParse(raw, out state))
            {
                Log.Write("[" + Id + "] etat inconnu « " + Log.Truncate(raw, 30) + " » : traite comme idle");
                state = StatusState.Idle;
            }
            if (state != Status) Log.Write("[" + Id + "] etat : " + state.ToString().ToLowerInvariant());
            Status = state;
            StatusTooltip = OneLine(Json.GetString(message, "tooltip"), 200);
            var busy = Json.GetBool(message, "busy") ?? false;
            if (busy != BusyFlag) Log.Write("[" + Id + "] " + (busy ? "occupee (pas de mise a jour automatique)" : "plus occupee"));
            BusyFlag = busy;
            Changed();
        }

        void OnNotify(Dictionary<string, object> message)
        {
            var title = OneLine(Json.GetString(message, "title"), 200);
            var text = Json.GetString(message, "text") ?? "";
            var kind = (Json.GetString(message, "kind") ?? "info").Trim().ToLowerInvariant();
            if (kind != "warn" && kind != "error") kind = "info";
            // Le texte peut etre personnel : seul le titre va au journal
            Log.Write("[" + Id + "] notification (" + kind + ") : " + Log.Truncate(title ?? "", 80));
            _owner.RaiseNotification(new ToolNotification(this, string.IsNullOrWhiteSpace(title) ? Name : title, text, kind, false));
        }

        void OnRetry(Session s, Dictionary<string, object> message)
        {
            var after = Math.Max(0, Math.Min(MaxRetryDelayMs, Json.GetInt(message, "after") ?? 3000));
            s.Retry = new RetryRequest { After = after, Reason = OneLine(Json.GetString(message, "reason"), 300), AtUtc = DateTime.UtcNow };
            Log.Write("[" + Id + "] retry dans " + after + " ms : " + (s.Retry.Reason ?? ""));
        }

        #endregion

        #region Fin du processus

        void OnOutput(Session s, string data, bool isError)
        {
            if (data == null)
            {
                Post(() =>
                {
                    if (isError) s.StderrDone = true;
                    else s.StdoutDone = true;
                    TryFinalize(s);
                });
                return;
            }
            if (data.Length == 0) return;
            Log.Write("[" + Id + "] " + (isError ? "stderr" : "stdout") + " : " + Log.Truncate(data, 1000));
        }

        void OnReaderDone(Session s)
        {
            if (s.ReaderDone) return;
            s.ReaderDone = true;
            if (_session == s && !s.ProcessExited && !s.Finalized)
            {
                if (s.Connected && !s.PipeClosedByUs) Log.Write("[" + Id + "] l'outil a ferme le tube");
                s.Connected = false;
                Changed();
            }
            TryFinalize(s);
        }

        void OnProcessExited(Session s)
        {
            if (s.ProcessExited || s.Finalized) return;
            s.ProcessExited = true;
            try { s.ExitCode = s.Process.ExitCode; }
            catch (Exception) { s.ExitCode = -1; }
            ExitCodes.Add(s.ExitCode);
            Log.Write("[" + Id + "] processus termine (code " + FormatExitCode(s.ExitCode) + ")");
            if (s.HelloTimer != null)
            {
                s.HelloTimer.Stop();
                s.HelloTimer.Dispose();
                s.HelloTimer = null;
            }
            // Jamais connecte : plus personne ne viendra, on libere l'attente de connexion
            if (!s.ConnectedOnPool)
            {
                try { s.Pipe.Dispose(); } catch { }
            }
            // Derniers messages (retry...) et sorties standard : on leur laisse 2 s pour arriver
            s.DrainTimer = OneShot(ExitDrainTimeoutMs, () =>
            {
                s.DrainTimer = null;
                FinalizeExit(s);
            });
            TryFinalize(s);
        }

        void TryFinalize(Session s)
        {
            if (!s.Finalized && s.ProcessExited && s.ReaderDone && s.StdoutDone && s.StderrDone) FinalizeExit(s);
        }

        void FinalizeExit(Session s)
        {
            if (s.Finalized) return;
            s.Finalized = true;
            if (s.DrainTimer != null)
            {
                s.DrainTimer.Stop();
                s.DrainTimer.Dispose();
                s.DrainTimer = null;
            }
            try { s.Pipe.Dispose(); } catch { }
            try { s.Process.Dispose(); } catch { }
            if (_session != s) return;
            _session = null;
            ResetToolState();
            var code = s.ExitCode;

            if (_owner.Quitting)
            {
                RunState = ToolRunState.Stopped;
            }
            else if (s.ExpectExit)
            {
                RunState = ToolRunState.Stopped;
                ConsecutiveRetries = 0;
                Log.Write("[" + Id + "] arrete");
                if (s.RestartAfterExit)
                {
                    Changed();
                    Start(userInitiated: true);
                    return;
                }
            }
            else if (s.Retry != null)
            {
                // Il avait bien tourne avant de demander a etre relance : la serie repart de zero
                if ((s.Retry.AtUtc - s.StartedUtc).TotalSeconds > 60) ConsecutiveRetries = 0;
                ConsecutiveRetries++;
                if (ConsecutiveRetries <= MaxConsecutiveRetries)
                {
                    RunState = ToolRunState.Waiting;
                    Log.Write("[" + Id + "] relance dans " + s.Retry.After + " ms (" + ConsecutiveRetries + "/" + MaxConsecutiveRetries + ")");
                    _respawnTimer = OneShot(Math.Max(1, s.Retry.After), () =>
                    {
                        _respawnTimer = null;
                        if (RunState != ToolRunState.Waiting) return;
                        RunState = ToolRunState.Stopped;
                        Start(userInitiated: false);
                    });
                }
                else
                {
                    RunState = ToolRunState.Stopped;
                    ConsecutiveRetries = 0;
                    SetCrashed("n'a pas pu démarrer");
                    Log.Write("[" + Id + "] abandon apres " + MaxConsecutiveRetries + " relances");
                    _owner.RaiseNotification(new ToolNotification(this, Name + " n'a pas pu démarrer",
                        (string.IsNullOrWhiteSpace(s.Retry.Reason) ? "L'outil a demandé à être relancé." : s.Retry.Reason)
                        + " (" + MaxConsecutiveRetries + " relances sans succès)", "error", true));
                }
            }
            else if (code != 0)
            {
                RunState = ToolRunState.Stopped;
                ConsecutiveRetries = 0;
                SetCrashed("arrêté (code " + FormatExitCode(code) + ")");
                _owner.RaiseNotification(new ToolNotification(this, Name + " s'est arrêté (code " + FormatExitCode(code) + ")",
                    "Pour le relancer : menu " + Branding.Name + " > " + Name + " > Démarrer.", "error", true));
            }
            else
            {
                RunState = ToolRunState.Stopped;
                ConsecutiveRetries = 0;
                Log.Write("[" + Id + "] s'est ferme de lui-meme");
            }
            // Commandes en attente d'un demarrage qui n'aboutira pas (une relance "retry" les garde)
            if (RunState == ToolRunState.Stopped && _pending.Count > 0)
            {
                var dropped = _pending.Count;
                _pending.Clear();
                Log.Write("[" + Id + "] " + dropped + " commande(s) en attente abandonnee(s) : l'outil s'est arrete");
                if (!_owner.Quitting && !s.ExpectExit)
                    _owner.RaiseNotification(new ToolNotification(this, Name + " n'a pas démarré", "L'action demandée a été abandonnée.", "warn", true));
            }
            Changed();
        }

        void Fail(string reason)
        {
            _pending.Clear();
            RunState = ToolRunState.Stopped;
            SetCrashed("n'a pas pu démarrer");
            Log.Write("[" + Id + "] ne peut pas demarrer : " + reason);
            _owner.RaiseNotification(new ToolNotification(this, Name + " n'a pas pu démarrer", reason, "error", true));
            Changed();
        }

        void SetCrashed(string summary)
        {
            Crashed = true;
            CrashSummary = summary;
        }

        void ResetToolState()
        {
            Menu = new List<MenuNode>();
            Status = StatusState.Idle;
            StatusTooltip = null;
            BusyFlag = false;
            _lastMenuSummary = null;
            Actions = new List<ActionNode>();
            HasActionsCatalog = false;
            Settings = new List<SettingsSection>();
            HasSettings = false;
        }

        #endregion

        #region Outils

        public static string FormatExitCode(int code) => code >= 0 && code < 0x10000 ? code.ToString() : "0x" + code.ToString("X8");

        static string OneLine(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var sb = new StringBuilder(text.Length);
            foreach (var c in text.Trim()) sb.Append(char.IsControl(c) ? ' ' : c);
            return Log.Truncate(sb.ToString(), max);
        }

        void Post(Action action)
        {
            try
            {
                _ui.Post(_ => action(), null);
            }
            catch (Exception)
            {
                // KKAO en fin de fermeture (boucle de messages terminee) : plus rien a traiter
            }
        }

        void Changed() => _owner.RaiseChanged(this);

        static WinFormsTimer OneShot(int milliseconds, Action action)
        {
            var timer = new WinFormsTimer { Interval = Math.Max(1, milliseconds) };
            timer.Tick += (o, e) =>
            {
                timer.Stop();
                timer.Dispose();
                action();
            };
            timer.Start();
            return timer;
        }

        sealed class RetryRequest
        {
            public int After;
            public string Reason;
            public DateTime AtUtc;
        }

        sealed class PendingCommand
        {
            public string Id;
            public string Source;
            public DateTime AtUtc;
        }

        // Un lancement de l'outil (les evenements d'un lancement precedent sont ignores)
        sealed class Session
        {
            public int Number;
            public string PipeName;
            public NamedPipeServerStream Pipe;
            public StreamWriter Writer;
            public Task WriteChain = Task.FromResult(true);
            public volatile bool WriteFailed;
            public Process Process;
            public int ProcessId;
            public DateTime StartedUtc;
            public volatile bool ConnectedOnPool;
            public volatile bool PipeClosedByUs;
            public bool Connected, HelloReceived, QuitSent, QuitPending, ExpectExit, RestartAfterExit;
            public RetryRequest Retry;
            public bool ProcessExited, ReaderDone, StdoutDone, StderrDone, Finalized;
            public int ExitCode;
            public WinFormsTimer DrainTimer, HelloTimer;

            public string PipePath => @"\\.\pipe\" + PipeName;
        }

        #endregion
    }
}
