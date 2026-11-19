using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KKAO.Apps;

namespace KKAO.Ui
{
    // Mises a jour automatiques (KKAO 2.3) :
    //  - telechargement en arriere-plan pendant que l'ancienne version tourne (empreinte verifiee) ;
    //  - KKAO d'abord : les applis attendent qu'il ait redemarre sur sa nouvelle version ;
    //  - appli arretee : tout de suite ; appli ouverte : au demarrage de KKAO, des qu'elle se ferme,
    //    ou quand le PC est inactif (30 min) et qu'elle n'est pas occupee (status.busy, rec) ;
    //  - retour arriere si la nouvelle version ne se signale pas (hello) dans les 30 s ou s'arrete d'elle-meme.
    internal sealed partial class KkaoController
    {
        public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromMinutes(30);
        // Remplacable pour le test automatique
        public Func<TimeSpan> IdleProbe { get; set; } = NativeMethods.IdleTime;
        public int VerifyTimeoutMs { get; set; } = 30000;
        // Echec (telechargement, installation) : nouvel essai automatique apres ce delai
        public TimeSpan AutoRetryDelay { get; set; } = TimeSpan.FromHours(1);

        readonly HashSet<string> _autoDownloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> _autoRetryAfter = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _verifying = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool _kkaoAutoDownloading;
        string _kkaoReadyVersion;
        System.Windows.Forms.Timer _autoTimer;

        const string KkaoKey = "kkao";
        // Version de KKAO vers laquelle un redemarrage automatique a ete tente (echec si on est encore en dessous au demarrage suivant)
        const string KkaoAttemptKey = "kkao-essai";

        // Nouvelle version de KKAO a installer automatiquement (sauf si une tentative a echoue)
        KkaoRelease AutoKkao
        {
            get
            {
                var release = KkaoUpdate;
                return release != null && Settings.SkippedVersion(KkaoKey) != release.Version ? release : null;
            }
        }

        public bool AutoUpdate
        {
            get => Settings.AutoUpdate;
            set
            {
                if (Settings.AutoUpdate == value) return;
                Settings.AutoUpdate = value;
                Log.Write("Mises à jour automatiques " + (value ? "activées" : "désactivées"));
                RaiseChanged();
                if (value) AutoUpdateTick();
            }
        }

        bool UserIdle
        {
            get
            {
                try { return IdleProbe() >= IdleThreshold; }
                catch (Exception) { return false; }
            }
        }

        bool RetryLater(string key) => _autoRetryAfter.TryGetValue(key, out var after) && DateTime.UtcNow < after;

        void PostponeRetry(string key) => _autoRetryAfter[key] = DateTime.UtcNow + AutoRetryDelay;

        // Lance par TrayApp : un passage par minute (une appli qui se ferme est mise a jour dans la minute)
        public void StartAutoUpdates()
        {
            if (_autoTimer != null) return;
            _autoTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            _autoTimer.Tick += (s, e) => AutoUpdateTick();
            _autoTimer.Start();
        }

        public void StopAutoUpdates()
        {
            _autoTimer?.Stop();
            _autoTimer?.Dispose();
            _autoTimer = null;
        }

        public void AutoUpdateTick()
        {
            if (!Settings.AutoUpdate || Tools.Quitting) return;

            // 1. KKAO d'abord
            var kkao = AutoKkao;
            if (kkao != null && !_kkaoUpdating)
            {
                if (_kkaoReadyVersion != kkao.Version)
                {
                    if (!_kkaoAutoDownloading && !RetryLater(KkaoKey)) StartKkaoDownload(kkao);
                }
                else if (CanRestartKkaoNow())
                {
                    ApplyKkaoUpdate(kkao, "PC inactif depuis " + (int)IdleThreshold.TotalMinutes + " min");
                }
                return;
            }
            if (kkao != null) return;

            // 2. Les applis
            foreach (var info in Snapshot().Where(a => a.UpdateAvailable && !a.Installing))
            {
                var app = Catalog.Find(info.Id) ?? Catalog.FindByAlias(info.Id);
                if (app == null || !app.CanInstall || Settings.SkippedVersion(app.Id) == app.Version) continue;
                if (_autoDownloads.Contains(app.Id) || RetryLater(app.Id)) continue;
                if (app.NeedsDownload && !IsDownloaded(app))
                {
                    StartAppDownload(app);
                    continue;
                }
                var tool = Tools.Find(info.Id);
                if (tool == null || (!tool.IsAlive && tool.RunState != ToolRunState.Waiting))
                {
                    Log.Write("[" + info.Id + "] mise à jour automatique vers " + app.Version + " (appli arrêtée)");
                    InstallApp(info.Id, startAfter: false, auto: true);
                }
                else if (tool.IsAlive && !tool.IsBusy && UserIdle)
                {
                    // Fermee proprement (quit), mise a jour, relancee
                    Log.Write("[" + info.Id + "] mise à jour automatique vers " + app.Version + " (PC inactif, appli pas occupée)");
                    InstallApp(info.Id, startAfter: false, auto: true);
                }
            }
        }

        bool CanRestartKkaoNow() => UserIdle && !AnyInstalling && !Tools.Tools.Any(t => t.IsBusy);

        void StartKkaoDownload(KkaoRelease release)
        {
            _kkaoAutoDownloading = true;
            Log.Write("Mise à jour automatique : téléchargement de " + Branding.Name + " " + release.Version);
            RaiseChanged();
            DownloadKkao(release, null).ContinueWith(task => Post(() =>
            {
                _kkaoAutoDownloading = false;
                if (task.IsFaulted)
                {
                    PostponeRetry(KkaoKey);
                    Log.Write("Mise à jour automatique de " + Branding.Name + " : " + Downloader.Describe(task.Exception?.GetBaseException()) + " (nouvel essai plus tard)");
                }
                else
                {
                    _kkaoReadyVersion = release.Version;
                    Log.Write(Branding.Name + " " + release.Version + " téléchargé (empreinte vérifiée) : installé à la prochaine pause ou au prochain démarrage");
                }
                RaiseChanged();
                if (!task.IsFaulted) AutoUpdateTick();
            }));
        }

        // KKAO redemarre sur sa nouvelle version ; les applis ouvertes seront relancees par elle
        void ApplyKkaoUpdate(KkaoRelease release, string reason)
        {
            _kkaoUpdating = true;
            _kkaoUpdatePhase = Branding.Name + " redémarre…";
            var running = Tools.Tools.Where(t => t.IsAlive).Select(t => t.Id).ToList();
            Settings.SetResumeAfterUpdate(running);
            Settings.SetSkippedVersion(KkaoAttemptKey, release.Version);
            Log.Write("Mise à jour automatique de " + Branding.Name + " vers " + release.Version + " (" + reason + ")"
                + (running.Count > 0 ? " ; relancées ensuite : " + string.Join(", ", running) : ""));
            RaiseChanged();
            RelaunchRequest?.Invoke(KkaoUpdateExe(release), "--update");
        }

        void StartAppDownload(CatalogApp app)
        {
            _autoDownloads.Add(app.Id);
            var file = DownloadPath(app);
            Log.Write("[" + app.Id + "] mise à jour automatique : téléchargement de " + app.Version + " (" + Bundler.FormatSize(app.Size) + ")");
            RaiseChanged();
            Task.Run(() => Downloader.DownloadFile(app.Url, file, app.Size, null, CancellationToken.None)).ContinueWith(task => Post(() =>
            {
                _autoDownloads.Remove(app.Id);
                if (task.IsFaulted)
                {
                    Downloader.TryDelete(file);
                    PostponeRetry(app.Id);
                    Log.Write("[" + app.Id + "] téléchargement automatique impossible : " + Downloader.Describe(task.Exception?.GetBaseException()) + " (nouvel essai plus tard)");
                }
                else Log.Write("[" + app.Id + "] " + app.Version + " téléchargée : installée dès que possible");
                RaiseChanged();
                if (!task.IsFaulted) AutoUpdateTick();
            }));
        }

        // Paquets telecharges d'anciennes versions : supprimes (ceux du catalogue actuel sont gardes)
        void PruneDownloads()
        {
            try
            {
                if (!Directory.Exists(DownloadsDir)) return;
                var keep = new HashSet<string>(Catalog.Apps.Where(a => a.NeedsDownload).Select(DownloadPath), StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(DownloadsDir))
                {
                    if (keep.Contains(file)) continue;
                    var baseFile = file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 5) : null;
                    if (baseFile != null && keep.Contains(baseFile) && _autoDownloads.Count > 0) continue;
                    Downloader.TryDelete(file);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Nettoyage des téléchargements : " + ex.Message);
            }
        }

        #region Demarrage de KKAO

        // Nouvelle version de KKAO deja telechargee : KKAO redemarre dessus avant de lancer les applis
        public bool KkaoUpdateReadyAtStartup()
        {
            var attempt = Settings.SkippedVersion(KkaoAttemptKey);
            if (attempt != null && VersionUtil.Compare(Program.Version, attempt) >= 0) Settings.SetSkippedVersion(KkaoAttemptKey, null);
            if (!Settings.AutoUpdate) return false;
            var release = AutoKkao;
            if (release == null) return false;
            if (attempt == release.Version)
            {
                // Redemarrage precedent sur cette version sans resultat : pas de boucle, mise a jour a la main seulement
                Settings.SetSkippedVersion(KkaoAttemptKey, null);
                Settings.SetSkippedVersion(KkaoKey, release.Version);
                Log.Write("Mise à jour automatique de " + Branding.Name + " vers " + release.Version + " restée sans effet : plus de nouvel essai automatique");
                return false;
            }
            return IsKkaoDownloaded(release);
        }

        public void ApplyKkaoUpdateAtStartup()
        {
            var release = AutoKkao;
            if (release == null) return;
            _kkaoUpdating = true;
            Settings.SetSkippedVersion(KkaoAttemptKey, release.Version);
            Log.Write("Mise à jour automatique de " + Branding.Name + " vers " + release.Version + " (au démarrage, avant les applis)");
            RelaunchRequest?.Invoke(KkaoUpdateExe(release), "--update");
        }

        // Applis dont la nouvelle version est deja la : installees avant leur lancement. Renvoie les ids retenus (pas encore a lancer).
        // mustStart : applis a lancer apres l'installation (demarrage avec KKAO, ou ouvertes avant la mise a jour de KKAO)
        public HashSet<string> InstallReadyUpdatesAtStartup(ICollection<string> mustStart)
        {
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Settings.AutoUpdate || AutoKkao != null) return held;
            foreach (var info in Snapshot().Where(a => a.UpdateAvailable && !a.Installing))
            {
                var app = Catalog.Find(info.Id) ?? Catalog.FindByAlias(info.Id);
                if (app == null || !app.CanInstall || Settings.SkippedVersion(app.Id) == app.Version) continue;
                if (app.NeedsDownload && !IsDownloaded(app)) continue;
                var start = mustStart.Contains(info.Id, StringComparer.OrdinalIgnoreCase);
                Log.Write("[" + info.Id + "] mise à jour automatique vers " + app.Version + " (au démarrage de " + Branding.Name + ")");
                InstallApp(info.Id, startAfter: start, auto: true);
                if (_installs.TryGetValue(info.Id, out var state)) state.StartAnyway = start;
                held.Add(info.Id);
            }
            return held;
        }

        #endregion

        #region Verification et retour arriere

        // A chaque changement d'une appli : premiere fois qu'une version mise a jour demarre → surveillance
        void WatchVerification(ToolHost tool)
        {
            if (tool == null || !tool.IsAlive || _verifying.Contains(tool.Id)) return;
            var version = Settings.VerifyPending(tool.Id);
            if (version == null) return;
            if (!string.Equals(tool.Config.Version, version, StringComparison.OrdinalIgnoreCase) || !tool.Config.Protocol)
            {
                Settings.SetVerifyPending(tool.Id, null);
                return;
            }
            _verifying.Add(tool.Id);
            var id = tool.Id;
            var watch = Stopwatch.StartNew();
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (s, e) =>
            {
                string failure = null;
                if (tool.IsReady)
                {
                    Settings.SetVerifyPending(id, null);
                    Log.Write("[" + id + "] " + version + " a bien démarré : ancienne version supprimée");
                    Task.Run(() => Installer.DropPrevious(id));
                }
                else if (!tool.IsAlive && tool.Crashed) failure = "elle s'est arrêtée au démarrage";
                else if (!tool.IsAlive) { /* arretee normalement : verifiee au prochain demarrage */ }
                else if (watch.ElapsedMilliseconds > VerifyTimeoutMs) failure = "elle ne s'est pas signalée en " + VerifyTimeoutMs / 1000 + " s";
                else return;
                timer.Stop();
                timer.Dispose();
                _verifying.Remove(id);
                if (failure != null) RollBack(tool, version, failure);
            };
            timer.Start();
        }

        void RollBack(ToolHost tool, string version, string failure)
        {
            var id = tool.Id;
            var name = tool.Name;
            Log.Write("[" + id + "] " + version + " ne démarre pas (" + failure + ") : retour à la version précédente");
            Settings.SetVerifyPending(id, null);
            Settings.SetSkippedVersion(id, version);
            if (!Installer.HasPrevious(id))
            {
                Message?.Invoke(name + " " + version + " ne démarre pas", "Aucune version précédente n'a été gardée : réinstallez-la depuis « Applications ».", true);
                return;
            }
            Tools.StopAndWait(tool, 12000, stopped =>
            {
                Task.Run(() => Installer.Rollback(id)).ContinueWith(task => Post(() =>
                {
                    if (task.IsFaulted || task.Result == null)
                    {
                        var reason = task.Exception?.GetBaseException().Message ?? "version précédente illisible";
                        Log.Write("[" + id + "] retour arrière impossible : " + reason);
                        Message?.Invoke(name + " " + version + " ne démarre pas", "Retour à la version précédente impossible : " + reason, true);
                        RaiseChanged();
                        return;
                    }
                    var config = task.Result.ToToolConfig(Installer.AppDir(id));
                    var restored = Tools.AddOrReplace(config);
                    Log.Write("[" + id + "] revenue à la version " + config.Version);
                    Message?.Invoke(name + " " + version + " ne démarre pas",
                        "Retour automatique à la version " + config.Version + ". Cette version ne sera plus installée automatiquement.", true);
                    restored.Start(userInitiated: false);
                    RaiseChanged();
                }));
            });
        }

        #endregion

        #region Pour la fenetre

        // Ce que la mise a jour automatique prepare, en une phrase par element
        public List<string> AutoUpdatePlan()
        {
            var lines = new List<string>();
            if (!Settings.AutoUpdate) return lines;
            var kkao = AutoKkao;
            if (kkao != null)
            {
                if (_kkaoAutoDownloading) lines.Add(Branding.Name + " " + kkao.Version + " : téléchargement en arrière-plan…");
                else if (_kkaoReadyVersion == kkao.Version)
                    lines.Add(Branding.Name + " " + kkao.Version + " est prêt : il s'installera pendant une pause de 30 min ou au prochain démarrage.");
            }
            foreach (var info in Snapshot().Where(a => a.UpdateAvailable))
            {
                var app = Catalog.Find(info.Id) ?? Catalog.FindByAlias(info.Id);
                if (app == null) continue;
                var label = info.Name + " " + app.Version;
                if (Settings.SkippedVersion(app.Id) == app.Version) lines.Add(label + " ne démarrait pas : ignorée (installez-la à la main pour réessayer).");
                else if (info.Installing) lines.Add(label + " : installation…");
                else if (_autoDownloads.Contains(app.Id)) lines.Add(label + " : téléchargement en arrière-plan…");
                else if (kkao != null) lines.Add(label + " : après la mise à jour de " + Branding.Name + ".");
                else if (Tools.Find(info.Id)?.IsAlive == true)
                    lines.Add(label + " est prête : installée dès que vous fermez l'appli, ou pendant une pause de 30 min.");
            }
            return lines;
        }

        #endregion
    }
}
