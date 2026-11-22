using System.Globalization;

namespace Pharma.Cloud.Web.Donnees;

/// <summary>
/// Script central 040 : état du mot de passe d'un compte dans le tableau « Comptes » (colonnes mot_de_passe_etat et
/// provisoire_expire_le de cloud_ecran_comptes_liste) : « provisoire (expire le …) », « provisoire expiré », rien s'il est choisi.
/// </summary>
public static class EtatMotDePasse
{
    public const string Colonne = "mot_de_passe";
    public const string Provisoire = "provisoire";
    public const string Expire = "provisoire_expire";

    /// <summary>Motif prérempli du lien « Régénérer le mot de passe » (clé dans l'adresse, jamais un texte saisi).</summary>
    public const string CleMotifExpire = "provisoire_expire";
    public const string MotifExpire = "Mot de passe provisoire expiré";
    public const string MotifNouveau = "Nouveau mot de passe provisoire";

    public static string Texte(string? etat, DateTime? echeance) => etat switch
    {
        Provisoire when echeance is { } e => "provisoire (expire le "
            + (e.Kind == DateTimeKind.Utc ? e.ToLocalTime() : e).ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR")) + ")",
        Provisoire => "provisoire",
        Expire => "provisoire expiré",
        _ => "",
    };

    /// <summary>Table affichée : colonne « Mot de passe » ajoutée (texte), colonnes d'origine gardées mais masquées.</summary>
    public static TableEcran Presenter(TableEcran table)
    {
        if (!table.Colonnes.Contains("mot_de_passe_etat")) return table;
        var colonnes = table.Colonnes.Append(Colonne).ToList();
        var lignes = table.Lignes.Select(l => l.Append(Texte(table.Valeur(l, "mot_de_passe_etat") as string, table.Valeur(l, "provisoire_expire_le") as DateTime?))
            .ToArray()).ToList();
        return new TableEcran(colonnes, lignes, table.Total);
    }

    /// <summary>Compte au mot de passe provisoire (encore valable ou expiré) : lien « Régénérer le mot de passe ».</summary>
    public static bool Regenerable(TableEcran table, object?[] ligne) => table.Valeur(ligne, "mot_de_passe_etat") is Provisoire or Expire;
}
