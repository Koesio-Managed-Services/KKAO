using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace KKAO
{
    // Journal local (%APPDATA%\KKAO\kkao.log, ~1 Mo puis kkao.log.old) : jamais le contenu des notes ni de jeton.
    // Ecriture groupee par un fil de fond : un outil bavard sur stdout n'est jamais ralenti par le disque.
    internal static class Log
    {
        const long MaxBytes = 1000000;
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        static readonly AutoResetEvent Signal = new AutoResetEvent(false);
        static readonly object FileGate = new object();
        static readonly object StartGate = new object();
        static Thread _writer;
        static string _filePath;

        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KKAO");

        // Le test automatique ecrit dans son propre journal (a cote du rapport)
        public static string FilePath
        {
            get => _filePath ?? Path.Combine(DataDir, "kkao.log");
            set => _filePath = value;
        }

        // Copie immediate de chaque ligne (test automatique uniquement)
        public static Action<string> Tap;

        public static void Write(string message)
        {
            try { Tap?.Invoke(message); }
            catch { }
            Pending.Enqueue(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message);
            if (_writer == null) StartWriter();
            Signal.Set();
        }

        public static void Error(string context, Exception ex) => Write(context + " : " + ex);

        // Ecrit tout de suite ce qui attend (fin de KKAO, erreur fatale, ouverture du journal)
        public static void Flush()
        {
            lock (FileGate)
            {
                if (Pending.IsEmpty) return;
                var batch = new StringBuilder();
                while (Pending.TryDequeue(out var line))
                {
                    batch.Append(line).Append(Environment.NewLine);
                    if (batch.Length > 256 * 1024)
                    {
                        Append(batch.ToString());
                        batch.Clear();
                    }
                }
                if (batch.Length > 0) Append(batch.ToString());
            }
        }

        public static string Truncate(string text, int max)
        {
            if (text == null) return "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        static void StartWriter()
        {
            lock (StartGate)
            {
                if (_writer != null) return;
                _writer = new Thread(() =>
                {
                    while (true)
                    {
                        Signal.WaitOne();
                        // les rafales (stdout d'un outil) partent en un seul ajout au fichier
                        Thread.Sleep(50);
                        Flush();
                    }
                })
                { IsBackground = true, Name = "KKAO journal" };
                _writer.Start();
            }
        }

        static void Append(string text)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, text, Utf8);
            }
            catch
            {
                // le journal ne doit jamais faire planter KKAO
            }
        }
    }

    internal static class FileUtil
    {
        // Ecriture atomique : fichier temporaire puis remplacement
        public static void WriteAllTextAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // Garde une copie d'un fichier illisible au lieu de l'ecraser
        public static void KeepCorruptCopy(string path)
        {
            try { File.Copy(path, path + ".corrompu-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); }
            catch { }
        }
    }
}
