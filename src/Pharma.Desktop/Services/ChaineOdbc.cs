namespace Pharma.Desktop.Services;

/// <summary>
/// Construit la chaîne de connexion ODBC à partir des champs de l'écran de reprise (pilote ou source déclarée,
/// emplacement, base, utilisateur, mot de passe). Les mots-clés dépendent de la famille du pilote.
/// </summary>
public static class ChaineOdbc
{
    /// <summary>Valeur du sélecteur de pilote pour une source ODBC déjà déclarée sur le PC serveur.</summary>
    public const string SourceDeclaree = "DSN";

    public enum Famille { Fichier, Dossier, Serveur, Firebird, Sqlite }

    public static Famille FamilleDe(string? pilote)
    {
        var p = pilote ?? "";
        bool A(string s) => p.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (A("Text Driver") || A("dBASE") || A("CSV")) return Famille.Dossier;
        if (A("Access Driver") || A("Excel Driver")) return Famille.Fichier;
        if (A("Firebird")) return Famille.Firebird;
        if (A("SQLite")) return Famille.Sqlite;
        return Famille.Serveur;     // SQL Server, MySQL, MariaDB, PostgreSQL, HFSQL C/S… : serveur et base
    }

    /// <summary>Fichiers CSV (pilote texte ou lecteur intégré) : l'encodage des fichiers peut être imposé.</summary>
    public static bool EstCsv(string? pilote) => FamilleDe(pilote) == Famille.Dossier && !(pilote ?? "").Contains("dBASE", StringComparison.OrdinalIgnoreCase);

    /// <param name="encodage">Fichiers CSV : auto (détecté par le serveur), utf-8 ou windows-1252.</param>
    public static string Construire(string? pilote, string? dsn, string? emplacement, string? baseDeDonnees, string? utilisateur, string? motDePasse,
        string? encodage = null)
    {
        var morceaux = new List<string>();
        void Ajouter(string cle, string? valeur) { if (!string.IsNullOrWhiteSpace(valeur)) morceaux.Add($"{cle}={Echapper(valeur.Trim())}"); }

        if (pilote == SourceDeclaree)
        {
            Ajouter("DSN", dsn);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(pilote)) return "";
            morceaux.Add($"Driver={{{pilote}}}");
            switch (FamilleDe(pilote))
            {
                case Famille.Fichier: Ajouter("Dbq", emplacement); break;
                case Famille.Dossier:
                    if (pilote.Contains("dBASE", StringComparison.OrdinalIgnoreCase)) Ajouter("DefaultDir", emplacement);
                    else
                    {
                        Ajouter("Dbq", emplacement);
                        if (!pilote.Contains("CSV", StringComparison.OrdinalIgnoreCase)) morceaux.Add("Extensions=asc,csv,tab,txt");
                        if (encodage is "utf-8" or "windows-1252") Ajouter("Encodage", encodage);
                    }
                    break;
                case Famille.Firebird: Ajouter("Dbname", emplacement); break;
                case Famille.Sqlite: Ajouter("Database", emplacement); break;
                default:
                    Ajouter("Server", emplacement);
                    Ajouter("Database", baseDeDonnees);
                    if (string.IsNullOrWhiteSpace(utilisateur) && pilote.Contains("SQL Server", StringComparison.OrdinalIgnoreCase))
                        morceaux.Add("Trusted_Connection=yes");
                    break;
            }
        }
        Ajouter("Uid", utilisateur);
        Ajouter("Pwd", motDePasse);
        return morceaux.Count == 0 ? "" : string.Join(";", morceaux) + ";";
    }

    /// <summary>Valeur entre accolades si elle contient un caractère spécial pour ODBC (; { } =).</summary>
    public static string Echapper(string valeur) =>
        valeur.IndexOfAny([';', '{', '}', '=']) >= 0 || valeur != valeur.Trim() ? "{" + valeur.Replace("}", "}}") + "}" : valeur;

    /// <summary>Chaîne affichée : le mot de passe est remplacé par des étoiles.</summary>
    public static string Masquer(string chaine, string? motDePasse) =>
        string.IsNullOrEmpty(motDePasse) ? chaine : chaine.Replace("Pwd=" + Echapper(motDePasse), "Pwd=••••••");
}
