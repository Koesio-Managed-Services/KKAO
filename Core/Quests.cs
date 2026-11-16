using System.Collections.Generic;
using System.Linq;

namespace KKAO
{
    // « Premiers pas » : petites missions qui font decouvrir KKAO, comme dans un jeu (progression, succes debloques)
    internal sealed class Quest
    {
        public Quest(string id, string title, string description, string glyph, string target)
        {
            Id = id;
            Title = title;
            Description = description;
            Glyph = glyph;
            Target = target;
        }

        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        // Glyphe Segoe MDL2 / Fluent Icons
        public string Glyph { get; }
        // Page ou aller pour l'accomplir
        public string Target { get; }
    }

    internal static class Quests
    {
        public const string InstallKkao = "install-kkao";
        public const string InstallApp = "install-app";
        public const string ChooseTheme = "choose-theme";
        public const string QuickAction = "quick-action";
        public const string CustomizeMenu = "customize-menu";
        public const string AppSettings = "app-settings";

        public static readonly IReadOnlyList<Quest> All = new List<Quest>
        {
            new Quest(InstallKkao, "Installer " + Branding.Name, Branding.Name + " s'installe pour vous seul, en quelques secondes, sans droits administrateur.", "", "settings"),
            new Quest(InstallApp, "Installer une première appli", "Choisissez une appli dans le catalogue et cliquez sur Installer.", "", "apps"),
            new Quest(ChooseTheme, "Choisir son style", "Sombre ou clair : " + Branding.Name + " s'adapte à vos goûts.", "", "settings"),
            new Quest(QuickAction, "Lancer une action rapide", "Clic droit sur l'icône " + Branding.Name + ", ou un clic dans « Actions rapides ».", "", "quick"),
            new Quest(CustomizeMenu, "Personnaliser le menu rapide", "Ajoutez, renommez ou réordonnez vos actions préférées.", "", "quick"),
            new Quest(AppSettings, "Régler une appli depuis " + Branding.Name, "Ouvrez la page d'une appli et changez un de ses réglages.", "", "apps"),
        };

        public static Quest Find(string id) => All.FirstOrDefault(q => q.Id == id);

        public static int DoneCount(KkaoSettings settings) => All.Count(q => settings.QuestDone(q.Id));

        // Succes affiche quand une mission est accomplie
        public static string AchievementTitle(string id)
        {
            switch (id)
            {
                case InstallKkao: return "Bienvenue à bord !";
                case InstallApp: return "Premier outil dans la Kaisse";
                case ChooseTheme: return "Question de style";
                case QuickAction: return "Rapide comme l'éclair";
                case CustomizeMenu: return "Tout est à sa place";
                case AppSettings: return "Chef d'orchestre";
                default: return "Succès débloqué";
            }
        }
    }
}
