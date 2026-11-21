using Pharma.Application.Administration;
using Pharma.Domain.Commun;
using Pharma.Domain.Reglages;

namespace Pharma.Application.Synchronisation;

/// <summary>Une ligne « avant / après » d'une proposition de réglages communs (valeur actuelle de l'officine, valeur proposée).</summary>
public sealed record ComparaisonReglage(string Cle, string Libelle, int Avant, int Apres, string Unite)
{
    public bool Change => Avant != Apres;
}

/// <summary>
/// Lot S7.4 : réglages communs proposés par le titulaire à distance (ordre « reglages », liste fermée de ReglagesCommuns). À la
/// réception : liste fermée, types et bornes revérifiés (sinon refus définitif, jamais « à confirmer »). À l'acceptation du titulaire
/// local (mot de passe vérifié par ConfirmerOrdreCentral) : la valeur proposée remplace celle du réglage général par le cas d'usage de
/// la saisie locale EnregistrerSeuils (mêmes règles : rouge plus court que l'orange ; table « fixée par le serveur central » refusée ;
/// recalcul des alertes ; journal « seuils_alerte » avant / après au nom du titulaire). Les seuils par grossiste et les exceptions par
/// produit ne changent jamais. Une règle non respectée refuse l'ordre définitivement (motif remonté au central).
/// </summary>
public sealed class ReglagesCommunsOrdre(ISeuilsAdmin seuils, EnregistrerSeuils enregistrer)
{
    /// <summary>Champ de l'écran où montrer un mot de passe refusé.</summary>
    public const string ChampMotDePasse = "mot_de_passe_titulaire";

    /// <summary>Valeurs proposées, lues strictement (liste fermée, entiers, bornes) ; sinon refus définitif.</summary>
    public static IReadOnlyDictionary<string, int> Valeurs(OrdreRecu o)
    {
        var (valeurs, erreurs) = ReglagesCommuns.Lire(o.DonneesJson);
        return erreurs.Count == 0 ? valeurs : throw new OrdreInapplicableException(erreurs[0]);
    }

    /// <summary>Avant (réglage général actuel de l'officine) / après (valeur proposée), dans l'ordre de la liste fermée.</summary>
    public async Task<IReadOnlyList<ComparaisonReglage>> ComparerAsync(OrdreRecu o, CancellationToken ct = default)
    {
        var proposes = Valeurs(o);
        var g = (await seuils.LireAsync(ct)).General;
        return ReglagesCommuns.Liste.Where(r => proposes.ContainsKey(r.Cle))
            .Select(r => new ComparaisonReglage(r.Cle, r.Libelle, Lire(g, r.Cle), proposes[r.Cle], r.Unite)).ToList();
    }

    public static string TexteComparaison(IReadOnlyList<ComparaisonReglage> lignes) =>
        lignes.Count == 0 ? "Aucun réglage." : string.Join(" ; ", lignes.Select(l => l.Change ? $"{l.Libelle} : {l.Avant} → {l.Apres} {l.Unite}"
            : $"{l.Libelle} : {l.Avant} {l.Unite} (inchangé)")) + ".";

    /// <summary>Applique la proposition au nom du titulaire qui accepte ; rend le résultat remonté au central (nombre de réglages changés).</summary>
    public async Task<string> AppliquerAsync(OrdreRecu o, long titulaireId, CancellationToken ct = default)
    {
        var proposes = Valeurs(o);
        var actuels = await seuils.LireAsync(ct);
        var g = actuels.General;
        foreach (var (cle, v) in proposes) g = Ecrire(g, cle, v);
        var changes = proposes.Count(p => Lire(actuels.General, p.Key) != p.Value);
        if (changes == 0) return "aucun changement (valeurs déjà en vigueur)";
        try
        {
            await enregistrer.ExecuterAsync(actuels with { General = g }, titulaireId, ct);
        }
        catch (Exception e) when (e is RegleMetierException or Reglages.ReglageImposeException)
        {
            // Règle locale (rouge ≥ orange une fois combiné) ou réglage fixé par le serveur central : refus définitif, rien n'est écrit
            throw new OrdreInapplicableException(e.Message);
        }
        return $"{changes} réglage(s) appliqué(s)";
    }

    internal static int Lire(ParametresAlerte g, string cle) => cle switch
    {
        "jours_orange" => g.JoursOrange,
        "jours_rouge" => g.JoursRouge,
        "horizon_risque_jours" => g.HorizonRisqueJours,
        "delai_fournisseur_jours" => g.DelaiFournisseurJours,
        "jours_securite" => g.JoursSecurite,
        "jours_cible_commande" => g.JoursCibleCommande,
        "jours_dormant" => g.JoursDormant,
        "seuil_lot_delaisse" => g.SeuilLotDelaisse,
        "seuil_lot_delaisse_collectif" => g.SeuilLotDelaisseCollectif,
        _ => throw new OrdreInapplicableException($"Réglage hors de la liste permise : {cle}.")
    };

    internal static ParametresAlerte Ecrire(ParametresAlerte g, string cle, int v) => cle switch
    {
        "jours_orange" => g with { JoursOrange = v },
        "jours_rouge" => g with { JoursRouge = v },
        "horizon_risque_jours" => g with { HorizonRisqueJours = v },
        "delai_fournisseur_jours" => g with { DelaiFournisseurJours = v },
        "jours_securite" => g with { JoursSecurite = v },
        "jours_cible_commande" => g with { JoursCibleCommande = v },
        "jours_dormant" => g with { JoursDormant = v },
        "seuil_lot_delaisse" => g with { SeuilLotDelaisse = v },
        "seuil_lot_delaisse_collectif" => g with { SeuilLotDelaisseCollectif = v },
        _ => throw new OrdreInapplicableException($"Réglage hors de la liste permise : {cle}.")
    };
}
