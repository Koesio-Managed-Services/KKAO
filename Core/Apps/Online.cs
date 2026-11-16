using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Text;
using System.Threading;

namespace KKAO.Apps
{
    // Ou KKAO trouve ses applis et ses mises a jour : les releases du depot GitHub public.
    //   catalog.json de la derniere release : applis (url des paquets) + version de KKAO (url de KKAO.exe)
    internal static class Distribution
    {
        public const string Repository = "Koesio-Managed-Services/KKAO";
        public const string RepositoryUrl = "https://github.com/" + Repository;
        public const string LatestCatalogUrl = RepositoryUrl + "/releases/latest/download/" + AppCatalog.FileName;
        public const string LatestExeUrl = RepositoryUrl + "/releases/latest/download/" + Paths.TechName + ".exe";

        public static string TagFor(string version) => "v" + version;

        public static string ReleaseBaseUrl(string tag) => RepositoryUrl + "/releases/download/" + tag + "/";

        // Variable d'environnement KKAO_CATALOG_URL : autre catalogue (test, miroir interne) ; "off" : pas de recherche en ligne
        public static string CatalogUrl
        {
            get
            {
                var custom = Environment.GetEnvironmentVariable("KKAO_CATALOG_URL")?.Trim();
                if (string.Equals(custom, "off", StringComparison.OrdinalIgnoreCase)) return null;
                return string.IsNullOrEmpty(custom) ? LatestCatalogUrl : custom;
            }
        }
    }

    // Telechargements (HTTPS GitHub, ou file:/// pour le test) : proxy de Windows avec l'identite de l'utilisateur, redirections suivies
    internal static class Downloader
    {
        static Downloader()
        {
            // .NET 4.8 suit les reglages TLS de Windows (TLS 1.2 et plus) ; on ne force rien sauf si un reglage ancien l'a bride
            var current = ServicePointManager.SecurityProtocol;
            if (current != SecurityProtocolType.SystemDefault && (current & SecurityProtocolType.Tls12) == 0)
                ServicePointManager.SecurityProtocol = current | SecurityProtocolType.Tls12;
        }

        public static string UserAgent => Branding.Name + "/" + Program.Version + " (+" + Distribution.RepositoryUrl + ")";

        static WebResponse Open(string url, int timeoutMs)
        {
            var request = WebRequest.Create(url);
            request.Timeout = timeoutMs;
            if (request is HttpWebRequest http)
            {
                http.UserAgent = UserAgent;
                http.ReadWriteTimeout = 60000;
                http.AllowAutoRedirect = true;
                http.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore);
                var proxy = WebRequest.DefaultWebProxy;
                if (proxy != null) proxy.Credentials = CredentialCache.DefaultCredentials;
                http.Proxy = proxy;
            }
            return request.GetResponse();
        }

        public static string GetString(string url, int timeoutMs = 20000)
        {
            using (var response = Open(url, timeoutMs))
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        // Ecrit d'abord <path>.part, renomme a la fin : jamais un fichier a moitie telecharge sous son vrai nom.
        // expectedSize > 0 : taille annoncee par le catalogue, verifiee. progress(octets recus, total) ~10 fois par seconde.
        public static void DownloadFile(string url, string path, long expectedSize, Action<long, long> progress, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var part = path + ".part";
            try
            {
                using (var response = Open(url, 30000))
                {
                    var total = expectedSize > 0 ? expectedSize : response.ContentLength;
                    long done = 0;
                    using (var input = response.GetResponseStream())
                    using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                    {
                        var buffer = new byte[81920];
                        var clock = Stopwatch.StartNew();
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            done += read;
                            if (expectedSize > 0 && done > expectedSize)
                                throw new InstallException("Le fichier reçu est plus gros qu'annoncé : téléchargement refusé.");
                            if (clock.ElapsedMilliseconds >= 100)
                            {
                                progress?.Invoke(done, total);
                                clock.Restart();
                            }
                        }
                    }
                    if (expectedSize > 0 && done != expectedSize)
                        throw new InstallException("Téléchargement incomplet (" + Bundler.FormatSize(done) + " sur " + Bundler.FormatSize(expectedSize) + ") : réessayez.");
                    progress?.Invoke(done, total > 0 ? total : done);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(part, path);
            }
            catch
            {
                TryDelete(part);
                throw;
            }
        }

        // Message pour l'utilisateur (sans jargon) a partir d'une erreur de telechargement
        public static string Describe(Exception ex)
        {
            ex = ex is AggregateException agg ? agg.GetBaseException() : ex;
            if (ex is InstallException) return ex.Message;
            if (ex is WebException web)
            {
                switch (web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                    case WebExceptionStatus.ConnectFailure:
                        return "pas de connexion Internet (ou GitHub inaccessible depuis ce réseau)";
                    case WebExceptionStatus.Timeout:
                        return "GitHub ne répond pas (délai dépassé)";
                    case WebExceptionStatus.TrustFailure:
                    case WebExceptionStatus.SecureChannelFailure:
                        return "connexion sécurisée à GitHub refusée (proxy ou antivirus qui inspecte le trafic ?)";
                    case WebExceptionStatus.ProtocolError:
                        var code = (int)((web.Response as HttpWebResponse)?.StatusCode ?? 0);
                        if (code == 404) return "fichier introuvable sur GitHub (404) : la publication est peut-être en cours, réessayez dans quelques minutes";
                        if (code == 407) return "le proxy de votre réseau demande une authentification";
                        return "GitHub a répondu par une erreur " + code;
                }
                return "téléchargement impossible (" + web.Status + ")";
            }
            if (ex is IOException || ex is UnauthorizedAccessException) return "écriture impossible sur ce PC : " + ex.Message;
            return ex.Message;
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }

    // Nouvelle version de KKAO annoncee par le catalogue en ligne
    internal sealed class KkaoRelease
    {
        public string Version { get; set; }
        public string Url { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Notes { get; set; }

        public static KkaoRelease Parse(object root)
        {
            var node = Json.Get(root, "kkao");
            var version = Json.GetString(node, "version")?.Trim();
            var url = Json.GetString(node, "url")?.Trim();
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url)) return null;
            return new KkaoRelease
            {
                Version = version,
                Url = url,
                Sha256 = Json.GetString(node, "sha256"),
                Size = AppCatalog.ToLong(Json.Get(node, "size")),
                Notes = Json.GetString(node, "notes"),
            };
        }

        public Dictionary<string, object> ToJson()
        {
            var node = new Dictionary<string, object>
            {
                { "version", Version },
                { "url", Url },
                { "sha256", Sha256 },
                { "size", Size },
            };
            if (!string.IsNullOrEmpty(Notes)) node["notes"] = Notes;
            return node;
        }
    }

    internal sealed class RemoteSnapshot
    {
        public AppCatalog Catalog { get; set; }
        public KkaoRelease Kkao { get; set; }
    }

    // Catalogue en ligne : lu au demarrage puis toutes les 6 h, garde en cache (affichage immediat, hors ligne)
    internal sealed class RemoteCatalogService
    {
        public const string CacheFileName = "catalogue-en-ligne.json";

        public RemoteCatalogService(string url, string cacheDir)
        {
            Url = url;
            CacheDir = cacheDir;
        }

        public string Url { get; }
        public string CacheDir { get; }
        public bool Enabled => !string.IsNullOrEmpty(Url);

        // Modifies sur le fil de l'interface uniquement (Apply / SetError)
        public AppCatalog Catalog { get; private set; } = AppCatalog.Empty();
        public KkaoRelease Kkao { get; private set; }
        public DateTime? LastSuccess { get; private set; }
        public string LastError { get; private set; }

        string IconsDir => Path.Combine(CacheDir, "icones");

        public void LoadCache()
        {
            var path = Path.Combine(CacheDir, CacheFileName);
            try
            {
                if (!File.Exists(path)) return;
                var snapshot = Parse(File.ReadAllText(path, Encoding.UTF8));
                if (snapshot == null) return;
                Catalog = snapshot.Catalog;
                Kkao = snapshot.Kkao;
                LastSuccess = File.GetLastWriteTime(path);
            }
            catch (Exception ex)
            {
                Log.Write("Catalogue en ligne (copie locale) : " + ex.Message);
            }
        }

        // Fil de fond : catalogue + icones manquantes, copie locale. Erreur : exception (Downloader.Describe)
        public RemoteSnapshot Fetch()
        {
            var text = Downloader.GetString(Url);
            var snapshot = Parse(text) ?? throw new InstallException("le catalogue en ligne est illisible");
            foreach (var app in snapshot.Catalog.Apps.Where(a => a.IconPath == null && !string.IsNullOrEmpty(a.IconUrl)))
            {
                var icon = IconFile(app);
                try
                {
                    Downloader.DownloadFile(app.IconUrl, icon, 0, null, CancellationToken.None);
                    app.IconPath = icon;
                }
                catch (Exception ex)
                {
                    Log.Write("[" + app.Id + "] icône en ligne : " + Downloader.Describe(ex));
                }
            }
            Directory.CreateDirectory(CacheDir);
            FileUtil.WriteAllTextAtomic(Path.Combine(CacheDir, CacheFileName), text);
            return snapshot;
        }

        public void Apply(RemoteSnapshot snapshot)
        {
            Catalog = snapshot.Catalog;
            Kkao = snapshot.Kkao;
            LastSuccess = DateTime.Now;
            LastError = null;
        }

        public void SetError(string message) => LastError = message;

        RemoteSnapshot Parse(string text)
        {
            var root = Json.ParseObject(text);
            if (root == null) return null;
            var catalog = AppCatalog.Parse(text, null);
            foreach (var problem in catalog.Problems) Log.Write("Catalogue en ligne : " + problem);
            foreach (var app in catalog.Apps)
            {
                var icon = IconFile(app);
                if (File.Exists(icon)) app.IconPath = icon;
            }
            return new RemoteSnapshot { Catalog = catalog, Kkao = KkaoRelease.Parse(root) };
        }

        string IconFile(CatalogApp app) => Path.Combine(IconsDir, app.Id + "-" + (app.Version ?? "0") + ".png");
    }

    // KKAO.exe --verify-online <rapport.txt> [--catalog <url>] : ce que vivra un collegue, sans rien installer chez soi.
    // Catalogue en ligne, chaque paquet telecharge, empreinte verifiee, installation d'essai (dossier temporaire), KKAO.exe telecharge.
    internal static class OnlineCheck
    {
        public static int Run(string reportPath, string catalogUrl)
        {
            reportPath = Path.GetFullPath(string.IsNullOrWhiteSpace(reportPath) ? "kkao-verif-en-ligne.txt" : reportPath);
            catalogUrl = string.IsNullOrWhiteSpace(catalogUrl) ? Distribution.LatestCatalogUrl : catalogUrl;
            var lines = new List<string>();
            var ok = true;
            void Line(string text) => lines.Add(text);
            var temp = Path.Combine(Path.GetTempPath(), "kkao-en-ligne-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var watch = Stopwatch.StartNew();
            Line("Vérification en ligne de " + Branding.Name + " " + Program.Version + " — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("Catalogue : " + catalogUrl);
            try
            {
                var service = new RemoteCatalogService(catalogUrl, Path.Combine(temp, "cache"));
                var snapshot = service.Fetch();
                var catalog = snapshot.Catalog;
                Line("Catalogue lu : " + catalog.Apps.Count + " appli(s)" + (catalog.Problems.Count > 0 ? ", problèmes : " + string.Join(" / ", catalog.Problems) : ""));
                if (catalog.Apps.Count == 0 || catalog.Problems.Count > 0) ok = false;

                var installer = new AppInstaller(Path.Combine(temp, "apps"));
                foreach (var app in catalog.Apps)
                {
                    var step = Stopwatch.StartNew();
                    try
                    {
                        if (!app.HasDownload) throw new InstallException("pas d'adresse de téléchargement ou d'empreinte");
                        var file = Path.Combine(temp, "telechargements", app.Id + "-" + app.Version + ".zip");
                        Downloader.DownloadFile(app.Url, file, app.Size, null, CancellationToken.None);
                        var seconds = step.Elapsed.TotalSeconds;
                        installer.Install(file, app.Id, app.Sha256, null, CancellationToken.None);
                        var tool = installer.ScanInstalled(null).FirstOrDefault(t => t.Id == app.Id);
                        if (tool == null || !File.Exists(tool.Exe)) throw new InstallException("exe absent après installation");
                        Line("OK  " + app.Name + " " + app.Version + " : " + Bundler.FormatSize(app.Size) + " téléchargés en " + seconds.ToString("0.0")
                            + " s, empreinte vérifiée, installée → " + tool.Exe.Substring(temp.Length) + (app.IconPath != null ? ", icône reçue" : ", ICÔNE ABSENTE"));
                        if (app.IconPath == null) ok = false;
                        installer.Uninstall(app.Id);
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        Line("ÉCHEC  " + app.Name + " : " + Downloader.Describe(ex));
                    }
                }

                var kkao = snapshot.Kkao;
                if (kkao == null)
                {
                    ok = false;
                    Line("ÉCHEC  le catalogue n'annonce pas de version de " + Branding.Name);
                }
                else
                {
                    try
                    {
                        var exe = Path.Combine(temp, "kkao", Paths.TechName + ".exe");
                        Downloader.DownloadFile(kkao.Url, exe, kkao.Size, null, CancellationToken.None);
                        var sha = AppInstaller.Sha256Of(exe, null, CancellationToken.None);
                        var fileVersion = VersionUtil.Short(FileVersionInfo.GetVersionInfo(exe).ProductVersion);
                        var shaOk = string.Equals(sha, kkao.Sha256, StringComparison.OrdinalIgnoreCase);
                        var versionOk = VersionUtil.Compare(fileVersion, kkao.Version) == 0;
                        if (!shaOk || !versionOk) ok = false;
                        Line((shaOk && versionOk ? "OK  " : "ÉCHEC  ") + Branding.Name + " " + kkao.Version + " : " + Bundler.FormatSize(kkao.Size)
                            + ", empreinte " + (shaOk ? "vérifiée" : "DIFFÉRENTE") + ", version du fichier " + fileVersion);
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        Line("ÉCHEC  " + Branding.Name + ".exe : " + Downloader.Describe(ex));
                    }
                    try
                    {
                        // Le lien direct donne aux collegues (README) : derniere release
                        var probe = Path.Combine(temp, "lien", Paths.TechName + ".exe");
                        Downloader.DownloadFile(Distribution.LatestExeUrl, probe, 0, null, CancellationToken.None);
                        var same = string.Equals(AppInstaller.Sha256Of(probe, null, CancellationToken.None), kkao.Sha256, StringComparison.OrdinalIgnoreCase);
                        if (!same) ok = false;
                        Line((same ? "OK  " : "ÉCHEC  ") + "lien direct " + Distribution.LatestExeUrl + (same ? " → même fichier" : " → fichier différent du catalogue"));
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        Line("ÉCHEC  lien direct " + Distribution.LatestExeUrl + " : " + Downloader.Describe(ex));
                    }
                }
            }
            catch (Exception ex)
            {
                ok = false;
                Line("ÉCHEC  catalogue : " + Downloader.Describe(ex));
            }
            finally
            {
                AppInstaller.TryDeleteDirectory(temp);
            }
            Line((ok ? "RÉSULTAT : tout est bon" : "RÉSULTAT : au moins un problème") + " (" + watch.Elapsed.TotalSeconds.ToString("0") + " s)");
            File.WriteAllLines(reportPath, lines, new UTF8Encoding(true));
            return ok ? 0 : 1;
        }
    }
}
