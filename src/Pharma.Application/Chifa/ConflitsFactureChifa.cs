using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Chifa;

// ---- Fusion du n° de facture Chifa (2026-11-15, modèle Pharma, lot 2, script 101) ----

/// <summary>
/// Vente dont l'ancien n° (colonne obsolète) et chifa_facture différaient au passage du script 101 (ou dont la recopie créait
/// un doublon de n°). NumeroActuel : valeur de chifa_facture au constat (null : recopie impossible). ValeurRetenue : null tant que non tranché.
/// </summary>
public sealed record ConflitFactureChifa(long VenteId, string NumeroVente, DateTime DateVente, string AncienNumero, string? NumeroActuel,
    string? ValeurRetenue, DateTime? TrancheLe = null, string? TranchePar = null);

/// <summary>Résultat de l'écriture d'une valeur tranchée.</summary>
public enum IssueTrancheFacture { Ecrit, Introuvable, DejaTranche, NumeroDejaPris }

public interface IConflitsFactureChifa
{
    /// <summary>Conflits du script 101 (aTrancher : seulement ceux encore à trancher), du plus ancien au plus récent.</summary>
    Task<IReadOnlyList<ConflitFactureChifa>> ListerAsync(bool aTrancher, CancellationToken ct = default);
    Task<ConflitFactureChifa?> LireAsync(long venteId, CancellationToken ct = default);
    /// <summary>Écrit la valeur retenue dans vente.chifa_facture et marque le conflit tranché, en une transaction.</summary>
    Task<IssueTrancheFacture> TrancherAsync(long venteId, string valeurRetenue, long utilisateurId, CancellationToken ct = default);
}

/// <summary>Règles de la résolution d'un conflit de n° de facture Chifa (testables sans base).</summary>
public static class RegleConflitFactureChifa
{
    /// <summary>Conflit : les deux colonnes remplies (espaces retirés) et différentes.</summary>
    public static bool EstConflit(string? ancien, string? actuel) =>
        !string.IsNullOrWhiteSpace(ancien) && !string.IsNullOrWhiteSpace(actuel) && ancien.Trim() != actuel.Trim();

    /// <summary>Valeur retenue : 8 chiffres exactement (contrôle ck_vente_chifa_facture_format), espaces retirés.</summary>
    public static string ValeurRetenue(string? saisie)
    {
        var v = saisie?.Trim() ?? "";
        if (v.Length == 0) throw new RegleMetierException("Indiquez le n° de facture Chifa retenu.", "valeur_retenue");
        if (!FormatFactureChifa.EstValide(v)) throw new RegleMetierException("N° de facture Chifa : 8 chiffres exactement.", "valeur_retenue");
        return v;
    }
}

/// <summary>Écran « Conflits de n° de facture Chifa » : réservé au titulaire (droit admin.parametres). Chaque décision est inscrite au journal.</summary>
public sealed class ConflitsFactureChifa(IConflitsFactureChifa conflits, IPermissionService droits, IJournal journal)
{
    public const string ActionTrancher = "facture_chifa_conflit_tranche";

    public async Task<IReadOnlyList<ConflitFactureChifa>> ListerAsync(bool aTrancher, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "consulter les conflits de n° de facture Chifa", ct);
        return await conflits.ListerAsync(aTrancher, ct);
    }

    /// <summary>Faux si la vente n'a pas de conflit (404).</summary>
    public async Task<bool> TrancherAsync(long venteId, string? valeurRetenue, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "trancher un conflit de n° de facture Chifa", ct);
        var valeur = RegleConflitFactureChifa.ValeurRetenue(valeurRetenue);
        var c = await conflits.LireAsync(venteId, ct);
        if (c is null) return false;
        switch (await conflits.TrancherAsync(venteId, valeur, utilisateurId, ct))
        {
            case IssueTrancheFacture.Introuvable: return false;
            case IssueTrancheFacture.DejaTranche: throw new RegleMetierException("Ce conflit vient d’être tranché.", "vente");
            case IssueTrancheFacture.NumeroDejaPris:
                throw new RegleMetierException($"Le n° de facture Chifa {valeur} est déjà porté par une autre vente.", "valeur_retenue");
        }
        await journal.EcrireAsync(utilisateurId, ActionTrancher, "vente", venteId,
            new Dictionary<string, object?>
            {
                ["ticket"] = c.NumeroVente, ["ancien_numero"] = c.AncienNumero, ["numero_actuel"] = c.NumeroActuel, ["valeur_retenue"] = valeur
            }, Permissions.AdminParametres, ct);
        return true;
    }
}
