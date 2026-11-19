using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace KKAO
{
    // L'ensemble des applis (tools.json + applis installees) : demarrage auto, etat global, fermeture de KKAO
    internal sealed class ToolManager
    {
        const int QuitTimeoutMs = 10000;
        readonly SynchronizationContext _ui;

        public ToolManager(ToolsFile config, KkaoSettings settings, SynchronizationContext ui, Func<ToolConfig, bool> filter = null)
            : this(config?.Tools ?? new List<ToolConfig>(), settings, ui, filter)
        {
            Config = config;
        }

        public ToolManager(IEnumerable<ToolConfig> tools, KkaoSettings settings, SynchronizationContext ui, Func<ToolConfig, bool> filter = null)
        {
            Settings = settings;
            _ui = ui;
            foreach (var tool in tools)
            {
                if (filter != null && !filter(tool)) continue;
                MigrateAliases(tool);
                Tools.Add(new ToolHost(tool, this, ui));
            }
        }

        // Appli renommee : reglages, catalogue garde et menu rapide de ses anciens id repris
        void MigrateAliases(ToolConfig tool)
        {
            foreach (var alias in tool.Aliases) Settings?.MigrateToolId(alias, tool.Id);
        }

        public ToolsFile Config { get; }
        public KkaoSettings Settings { get; }
        public List<ToolHost> Tools { get; } = new List<ToolHost>();
        public bool Quitting { get; private set; }

        public event Action<ToolHost> Changed;
        public event Action<ToolNotification> Notification;
        // Appli ajoutee (installation) ou retiree (desinstallation)
        public event Action ToolsChanged;

        internal void RaiseChanged(ToolHost tool) => Changed?.Invoke(tool);

        internal void RaiseNotification(ToolNotification notification) => Notification?.Invoke(notification);

        public ToolHost Find(string id) => Tools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

        // Appli installee (ou mise a jour) pendant que KKAO tourne
        public ToolHost AddOrReplace(ToolConfig config)
        {
            MigrateAliases(config);
            var existing = Find(config.Id);
            if (existing != null)
            {
                existing.ReplaceConfig(config);
                ToolsChanged?.Invoke();
                return existing;
            }
            var host = new ToolHost(config, this, _ui);
            Tools.Add(host);
            ToolsChanged?.Invoke();
            return host;
        }

        public void Remove(ToolHost tool)
        {
            if (tool == null || !Tools.Remove(tool)) return;
            ToolsChanged?.Invoke();
        }

        // Choix de l'utilisateur (settings.json), sinon "autostart" de tools.json / du paquet
        public bool EffectiveAutostart(ToolHost tool) => Settings.Autostart(tool.Id) ?? tool.Config.Autostart;

        public void SetAutostart(ToolHost tool, bool value)
        {
            Settings.SetAutostart(tool.Id, value);
            Log.Write("[" + tool.Id + "] demarrer avec " + Branding.Name + " : " + (value ? "oui" : "non"));
            RaiseChanged(tool);
        }

        // hold : applis retenues (mise a jour en cours, demarrees ensuite)
        public void StartAutostart(Func<ToolHost, bool> hold = null)
        {
            foreach (var tool in Tools)
                if (EffectiveAutostart(tool) && hold?.Invoke(tool) != true) tool.Start(userInitiated: false);
        }

        // Catalogue d'actions : celui du lancement en cours, sinon le dernier garde (appli arretee), sinon les "pin" du menu (appli v1)
        public List<ActionNode> ActionsOf(ToolHost tool)
        {
            if (tool.HasActionsCatalog) return tool.Actions.ToList();
            var cached = Settings.CachedActions(tool.Id).Select(ActionNode.Parse).Where(a => a != null).ToList();
            if (cached.Count > 0) return cached;
            return MenuNode.Flatten(tool.Menu).Where(n => n.Pin && n.IsClickable).Select(ActionNode.FromMenu).ToList();
        }

        internal void OnActionsReceived(ToolHost tool) => Settings.SetCachedActions(tool.Id, tool.Actions.Select(a => a.ToJson()));

        // Etat le plus important : rec > error > pause > warn > active > idle (un outil arrete par erreur compte comme error)
        public StatusState AggregateState()
        {
            var state = StatusState.Idle;
            foreach (var tool in Tools)
            {
                if (tool.IsAlive) state = StatusStates.Max(state, tool.Status);
                if (tool.Crashed && !tool.IsAlive) state = StatusStates.Max(state, StatusState.Error);
            }
            return state;
        }

        // Infobulle de l'icone : nom de KKAO puis l'etat des outils qui ont quelque chose a dire (63 caracteres maximum)
        public string BuildTooltip()
        {
            var lines = new List<string> { Quitting ? Branding.Name + " — fermeture…" : Branding.Name };
            var busy = Tools
                .Where(t => (t.IsAlive && t.Status != StatusState.Idle) || (t.Crashed && !t.IsAlive))
                .OrderByDescending(t => t.IsAlive ? (int)t.Status : (int)StatusState.Error);
            foreach (var tool in busy)
            {
                if (!tool.IsAlive) lines.Add(tool.Name + " : " + tool.CrashSummary);
                else if (!string.IsNullOrWhiteSpace(tool.StatusTooltip)) lines.Add(tool.StatusTooltip);
                else lines.Add(tool.Name + " : " + StatusStates.Label(tool.Status));
            }
            if (lines.Count == 1 && !Quitting)
            {
                var running = Tools.Where(t => t.IsAlive).Select(t => t.Name).ToList();
                lines.Add(running.Count == 0 ? (Tools.Count == 0 ? "aucune appli installée" : "aucune appli lancée") : string.Join(", ", running));
            }
            return Fit(string.Join("\n", lines), 63);
        }

        public static string Fit(string text, int max)
        {
            if (text == null) return "";
            if (text.Length <= max) return text;
            var cut = max - 1;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut) + "…";
        }

        // Quitter KKAO : "quit" a tous, on attend 10 s au plus (sans jamais tuer un outil), puis on ferme les tubes
        public void QuitAll(Action done)
        {
            if (Quitting) return;
            Quitting = true;
            Log.Write("Fermeture de " + Branding.Name + " : quit envoye aux outils");
            foreach (var tool in Tools)
            {
                tool.CancelRespawn();
                tool.Stop();
            }
            var watch = Stopwatch.StartNew();
            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (s, e) =>
            {
                var alive = Tools.Where(t => t.IsAlive).ToList();
                if (alive.Count > 0 && watch.ElapsedMilliseconds < QuitTimeoutMs) return;
                timer.Stop();
                timer.Dispose();
                if (alive.Count > 0)
                    Log.Write("Fermeture : " + string.Join(", ", alive.Select(t => t.Name)) + " encore ouvert(s) apres 10 s : tube ferme, l'outil quittera seul");
                foreach (var tool in Tools) tool.ClosePipe();
                Log.Write("Fermeture de " + Branding.Name + " en " + watch.ElapsedMilliseconds + " ms");
                done?.Invoke();
            };
            timer.Start();
        }

        // Arret d'une seule appli, avec attente (mise a jour, desinstallation) ; done(true) : arretee
        public void StopAndWait(ToolHost tool, int timeoutMs, Action<bool> done)
        {
            if (!tool.IsAlive && tool.RunState != ToolRunState.Waiting)
            {
                done?.Invoke(true);
                return;
            }
            tool.CancelRespawn();
            tool.Stop();
            var watch = Stopwatch.StartNew();
            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (s, e) =>
            {
                if (tool.IsAlive && watch.ElapsedMilliseconds < timeoutMs) return;
                timer.Stop();
                timer.Dispose();
                done?.Invoke(!tool.IsAlive);
            };
            timer.Start();
        }
    }
}
