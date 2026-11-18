using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Commun;

/// <summary>
/// Champ de saisie libre dont on propose les valeurs déjà saisies (suggestions pendant la frappe, décision du titulaire 2026-11-18).
/// Table, colonne et condition sont écrites ICI, jamais reçues du poste : le poste n'envoie que la clé.
/// </summary>
/// <param name="Cle">Clé envoyée par le poste (GET /api/suggestions/{cle}?q=).</param>
/// <param name="Table">Table lue (identifiant SQL fixe).</param>
/// <param name="Colonne">Colonne de texte proposée (identifiant SQL fixe).</param>
/// <param name="Condition">Condition SQL fixe ajoutée au filtre (null : aucune), jamais construite avec une saisie.</param>
/// <param name="Droits">Un seul de ces droits suffit ; <see cref="ChampsSuggestions.Titulaire"/> : réservé au pharmacien titulaire.</param>
/// <param name="LettresMin">Lettres tapées au minimum avant de proposer : 2 (charte) ; 0 seulement pour un champ à valeurs très courtes
/// dont toutes les valeurs sont proposées dès l'entrée dans le champ (rayon d'un emplacement : A, B, C…).</param>
/// <param name="Alphabetique">Vrai : valeurs triées par ordre alphabétique (sans majuscules ni accents) ; faux : les plus fréquentes d'abord.</param>
public sealed record ChampSuggestion(string Cle, string Table, string Colonne, string? Condition, IReadOnlyList<string> Droits,
    int LettresMin = ChampsSuggestions.LettresMin, bool Alphabetique = false);

/// <summary>
/// Liste blanche des champs à valeurs proposées. JAMAIS de colonne de la table patient ni de texte libre pouvant contenir le nom d'un
/// patient (motifs, raisons, commentaires) : ces valeurs seraient montrées à d'autres employés pour d'autres ventes.
/// Ajouter un champ : une ligne ici (table et colonne vérifiées dans database/init/schema.sql, droit de l'écran qui l'utilise),
/// une ligne dans docs/regles-metier.md, et le composant ValeursSaisies côté poste.
/// </summary>
public static class ChampsSuggestions
{
    /// <summary>Propositions au plus par saisie (charte : 10).</summary>
    public const int Limite = 10;
    /// <summary>Lettres tapées au minimum avant de proposer (charte : 2), sauf champ de la liste qui en demande moins (rayon : 0).</summary>
    public const int LettresMin = 2;
    /// <summary>Droit particulier : réservé au pharmacien titulaire (écrans Financements).</summary>
    public const string Titulaire = PermissionServiceExtensions.MarqueTitulaire;

    /// <summary>
    /// Libellé d'un mouvement de trésorerie saisi à la main : dépense ou encaissement, hors caisse et hors écriture automatique, et non
    /// inscrit par un usage de financement ni par le paiement d'une échéance de prêt (libellés composés par le logiciel).
    /// </summary>
    public const string LibelleMouvementSaisi =
        "nature IN ('depense', 'encaissement') AND NOT automatique AND session_caisse_id IS NULL"
        + " AND NOT EXISTS (SELECT 1 FROM utilisation_financement u WHERE u.mouvement_id = mouvement_tresorerie.id OR u.depense_id = mouvement_tresorerie.id)"
        + " AND NOT EXISTS (SELECT 1 FROM paiement_echeance_pret e WHERE e.mouvement_charges_id = mouvement_tresorerie.id)";

    public static readonly IReadOnlyDictionary<string, ChampSuggestion> Liste = new[]
    {
        // Fiche d'un médecin : à la caisse (nouveau médecin) et dans Factures › Médecins
        new ChampSuggestion("specialite_medecin", "medecin", "specialite", null, [Permissions.CaisseVendre, Permissions.FacturesGerer]),
        // Sortie du tiroir « dépense » : libellé (café, ampoules, timbres…)
        new ChampSuggestion("libelle_depense_tiroir", "mouvement_tiroir", "libelle", "detail = 'depense'", [Permissions.CaisseOuvrirTiroir]),
        // Sortie du tiroir payée à un livreur ou à un fournisseur hors grossistes : son nom
        new ChampSuggestion("beneficiaire_tiroir", "mouvement_tiroir", "beneficiaire", "detail = 'fournisseur'", [Permissions.CaisseOuvrirTiroir]),
        // Fiche produit : forme (comprimé, sirop…)
        new ChampSuggestion("forme_produit", "produit", "forme", null, [Permissions.CatalogueCreer]),
        // Règlement d'un grossiste par chèque ou effet : banque
        new ChampSuggestion("banque_reglement_fournisseur", "reglement_fournisseur", "banque", null, [Permissions.AchatsRegler]),
        // Compte de trésorerie : banque
        new ChampSuggestion("banque_compte_tresorerie", "compte_tresorerie", "banque", null, [Permissions.TresorerieGerer]),
        // Mouvement de trésorerie saisi à la main (Trésorerie › dépense ou encaissement). Exclus, car écrits par le logiciel : versements
        // de la caisse et transferts (« A → B »), versements d'organisme (« Versement CNAS · bordereau … » par défaut), mouvements des
        // financements (« … · pour : … », « … (payée par : …) », « Échéance du … · prêt … »), dont les dépenses qu'ils inscrivent.
        new ChampSuggestion("libelle_mouvement_tresorerie", "mouvement_tresorerie", "libelle", LibelleMouvementSaisi, [Permissions.TresorerieGerer]),
        // Prêt bancaire sans compte de la pharmacie : prêteur (écran Financements, réservé au titulaire)
        new ChampSuggestion("preteur_pret", "pret_bancaire", "preteur", null, [Titulaire]),
        // Emplacements : rayon. Les rayons font une lettre (A, B, C…) : tous les rayons existants sont proposés dès l'entrée dans le
        // champ, triés (décision du titulaire, 2026-11-18) ; un nouveau rayon peut toujours être tapé
        new ChampSuggestion("rayon_emplacement", "emplacement", "rayon", null, [Permissions.AdminParametres], LettresMin: 0, Alphabetique: true),
        // Financements › usage d'un prêt ou d'un apport (versement ou dépense payée) : libellé saisi ; les usages écrits par le logiciel
        // (règlement d'un grossiste, traite) et les libellés mis par défaut sont exclus (écran réservé au titulaire)
        new ChampSuggestion("libelle_usage_financement", "utilisation_financement", "libelle",
            "usage IN ('tresorerie', 'depense') AND libelle NOT IN ('Apport du titulaire', 'Versement du prêt')", [Titulaire]),
        // Financements › remboursement au titulaire : libellé saisi (le libellé mis par défaut est exclu ; réservé au titulaire)
        new ChampSuggestion("libelle_remboursement_titulaire", "remboursement_titulaire", "libelle", "libelle <> 'Remboursement du titulaire'", [Titulaire]),
    }.ToDictionary(c => c.Cle, StringComparer.Ordinal);
}

/// <summary>Lecture des valeurs déjà saisies d'un champ de la liste blanche (sans accents ni majuscules, distinctes, non vides).</summary>
public interface ISuggestionsDepot
{
    Task<IReadOnlyList<string>> ValeursAsync(ChampSuggestion champ, string texte, int limite, CancellationToken ct = default);
}

/// <summary>
/// GET /api/suggestions/{champ}?q= : clé inconnue refusée (422), droit de l'écran exigé (403), rien sous le minimum de lettres du champ
/// (2, ou 0 pour le rayon d'un emplacement), 10 valeurs au plus.
/// </summary>
public sealed class ProposerValeurs(ISuggestionsDepot depot, IPermissionService droits)
{
    public async Task<IReadOnlyList<string>> ExecuterAsync(string? cle, string? texte, long utilisateurId, CancellationToken ct = default)
    {
        if (cle is null || !ChampsSuggestions.Liste.TryGetValue(cle, out var champ))
            throw new RegleMetierException("Champ sans propositions de valeurs.");
        var autorise = false;
        foreach (var d in champ.Droits)
        {
            autorise = d == ChampsSuggestions.Titulaire
                ? await droits.EstTitulaireAsync(utilisateurId, ct)
                : await droits.APermissionAsync(utilisateurId, d, ct);
            if (autorise) break;
        }
        if (!autorise) throw new DroitRefuseException(champ.Droits[0], "voir les valeurs déjà saisies de ce champ");
        var t = texte?.Trim() ?? "";
        if (t.Length < champ.LettresMin) return [];
        var valeurs = await depot.ValeursAsync(champ, t, ChampsSuggestions.Limite, ct);
        return valeurs.Take(ChampsSuggestions.Limite).ToList();
    }
}
