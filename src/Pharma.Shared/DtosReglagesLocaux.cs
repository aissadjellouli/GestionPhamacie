using System.Globalization;

namespace Pharma.Shared;

// ---- Mutualisation des réglages, lot M5 : routes de l'API locale (docs/conception-synchronisation.md § 16 bis.5) ----

/// <summary>Un écart entre la valeur de l'officine et celle du serveur central (texte prêt à afficher).</summary>
public sealed record EcartReglageDto(string Libelle, string Local, string Central);

/// <summary>
/// État d'une des 11 tables de réglages. Mode = mode en vigueur (« local » tant que la synchronisation est désactivée) ; ModeRecu = dernier mode reçu du serveur central.
/// Etat : aucun, applique, non_adopte, rejete. Ecarts : seulement pour une valeur proposée non adoptée (valeur de l'officine à côté de celle du central).
/// </summary>
public sealed record ReglageMutualiseDto(string Table, string Libelle, string Mode, string? ModeRecu, bool AjoutsLocaux, long? VersionRecue, long? VersionAppliquee,
    DateTime? RecuLe, DateTime? AppliqueLe, string Etat, string? MotifRejet, string Origine, string? Auteur, IReadOnlyList<EcartReglageDto> Ecarts);

/// <summary>Corps de « Adopter » : la version du modèle que l'écran a affichée (409 si le serveur central en a publié une autre).</summary>
public sealed record AdopterReglageRequete(long VersionModele);

/// <summary>Réglage fiscal (parametre_fiscal) : conservation 6 à 30 ans, format fixe, séparateur du CSV, fuseau.</summary>
public sealed record ParametreFiscalDto(int ConservationAnnees, string FormatExportDgi, string SeparateurCsv, string Fuseau);

/// <summary>Traçabilité (parametre_tracabilite) : boîte obligatoire ; patient obligatoire : jamais, psychotrope, ordonnance, toujours.</summary>
public sealed record ParametreTracabiliteDto(bool BoiteObligatoire, string PatientObligatoire);

/// <summary>
/// En-tête de réponse des routes de lecture des réglages mutualisés (« X-Reglage-Central »), pour que l'écran grise sans appel de plus :
/// « table=parametre_alerte; mode=impose; etat=applique; version=4; ajouts=1 ». Mode = mode en vigueur (local, propose, impose).
/// </summary>
public sealed record EtatReglageEntete(string Table, string Mode, string Etat, long? Version, bool AjoutsLocaux)
{
    public const string Nom = "X-Reglage-Central";

    public bool Impose => Mode == ContratMutu.ModeImpose;
    public bool Propose => Mode == ContratMutu.ModePropose;
    /// <summary>Valeur proposée que l'officine n'a pas adoptée : l'écran propose « Adopter ».</summary>
    public bool ANonAdopte => Propose && Etat == "non_adopte";
    public bool Rejete => Etat == "rejete";

    public static readonly EtatReglageEntete Aucun = new("", ContratMutu.ModeLocal, "aucun", null, true);

    public string Formater() =>
        $"table={Table}; mode={Mode}; etat={Etat}; version={(Version is { } v ? v.ToString(CultureInfo.InvariantCulture) : "")}; ajouts={(AjoutsLocaux ? 1 : 0)}";

    /// <summary>Lit l'en-tête ; null si absent ou illisible (l'écran se comporte alors comme sans serveur central).</summary>
    public static EtatReglageEntete? Lire(string? valeur)
    {
        if (string.IsNullOrWhiteSpace(valeur)) return null;
        var champs = valeur.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Split('=', 2)).Where(c => c.Length == 2).ToDictionary(c => c[0], c => c[1]);
        if (!champs.TryGetValue("table", out var table) || !champs.TryGetValue("mode", out var mode)) return null;
        long? version = champs.TryGetValue("version", out var v) && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new EtatReglageEntete(table, mode, champs.GetValueOrDefault("etat", "aucun"), version, champs.GetValueOrDefault("ajouts", "1") != "0");
    }
}
