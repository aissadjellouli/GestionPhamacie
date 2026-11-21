using Pharma.Desktop.Services;

namespace Pharma.Desktop.Composants.Mise;

/// <summary>
/// Droit d'ouvrir un écran : une seule description, lue par la garde de la mise en page (écran réservé : retour à l'accueil)
/// et par les tuiles navigables (2026-11-21 : une tuile n'est un lien que si l'employé peut ouvrir l'écran qu'elle désigne,
/// jamais de lien mort). Même contrôle que le menu : droits de l'écran du domaine (<see cref="Domaines"/>) et écrans réservés.
/// </summary>
public static class AccesEcrans
{
    /// <summary>Écrans réservés à un droit (comme le menu) : sans l'un de ces droits, la mise en page renvoie à l'accueil.</summary>
    public static readonly (string Chemin, string[] Droits)[] Reserves =
    [
        ("rapports", ["rapports.ventes"]), ("pilotage-financier", ["role.titulaire", "rapports.marges"]), ("surveillance",["role.titulaire"]), ("financements", ["role.titulaire"]),("factures", ["factures.gerer"]), ("tresorerie", ["tresorerie.gerer"]), ("dettes", ["tresorerie.gerer"]),
        ("achats", ["achats.commander", "achats.receptionner", "achats.retour_fournisseur"]), ("stock/registre", ["psychotropes.registre"]),
        ("stock/etiquettes", ["stock.consulter", "achats.receptionner"]),
        // Domaine Patients (fichier, droits Chifa, patients en attente) : droit de vendre, comme les routes de l'API
        ("patients", ["caisse.vendre"]),
        .. NavAdmin.Accessibles.Select(a => (a.Lien, new[] { a.Permission }))
    ];

    /// <summary>Écrans du super admin : les seuls qu'il ouvre, et qu'il est seul à ouvrir.</summary>
    public static readonly string[] EcransSuperAdmin = ["admin/restauration", "admin/reprise", "admin/profils-reprise"];

    /// <summary>Écran réservé qui désigne ce chemin (sous-chemins compris) ; (null, []) : aucun.</summary>
    public static (string? Chemin, string[] Droits) Reserve(string chemin) =>
        Reserves.FirstOrDefault(e => chemin == e.Chemin || chemin.StartsWith(e.Chemin + "/", StringComparison.Ordinal));

    /// <summary>Chemin d'une adresse de lien (« ./?rubrique=stock » → « », « dettes?onglet=patient » → « dettes »).</summary>
    public static string Chemin(string lien)
    {
        var chemin = lien.Split('?', '#')[0].Trim();
        if (chemin.StartsWith("./", StringComparison.Ordinal)) chemin = chemin[2..];
        return chemin.Trim('/');
    }

    /// <summary>
    /// L'employé connecté peut-il ouvrir l'écran de ce lien ? Super admin : ses écrans seulement ; sinon ni les écrans du super admin,
    /// ni un écran réservé sans l'un de ses droits, ni un écran du menu non permis (droit manquant ou écran « à venir »).
    /// </summary>
    public static bool PeutOuvrir(SessionPoste session, string lien)
    {
        var chemin = Chemin(lien);
        if (session.SuperAdmin) return EcransSuperAdmin.Contains(chemin);
        if (EcransSuperAdmin.Contains(chemin)) return false;
        var (reserve, droits) = Reserve(chemin);
        if (reserve is not null && !droits.Any(session.A)) return false;
        var (_, ecran) = Domaines.De(chemin);
        return ecran is null || ecran.Permis(session);
    }
}
