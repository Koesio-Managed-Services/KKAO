namespace KKAO
{
    // Noms AFFICHES de KKAO : le seul endroit a modifier pour renommer le lanceur.
    //   A revoir aussi en cas de renommage : AssemblyName / Product / Description de KKAO.csproj
    //   (nom de l'exe, proprietes du fichier) et le "K" dessine par Tray/IconFactory.cs.
    // Les identifiants TECHNIQUES ne changent jamais (PROTOCOLE.md, « Ce qui ne change jamais ») :
    //   --kkao, tubes kkao-<id>-<pid>, %APPDATA%\KKAO, %LOCALAPPDATA%\Programs\KKAO, valeur Run "KKAO", Local\KKAO.*, kkao.log.
    internal static class Branding
    {
        // Menus, infobulle, bulles, champ "host" du welcome, titres des fenetres
        public const string Name = "KKAO";

        // Nom long (accueil, a propos, desinstallation)
        public const string LongName = "KMS Kaisse À Outils";

        // Phrase d'accroche (accueil, guide)
        public const string Tagline = "Toutes vos applis Koesio, une seule icône.";

        // Editeur (Applications installees de Windows, a propos)
        public const string Publisher = "Koesio";

        public const string PublisherLong = "Koesio Managed Services";
    }
}
