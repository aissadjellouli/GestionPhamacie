using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>
/// Informations Chifa d'une vente (2026-11-15, script 100) : date des soins, centre payeur, employé qui l'a saisie dans CHIFA-Officine,
/// majoration (type de la liste réglable « majoration_chifa » et montant).
/// </summary>
public sealed record InfosChifa(DateOnly? DateSoins, long? CentrePayeurId, long? SaisiPar, string? MajorationCode, decimal MajorationMontant);

/// <summary>
/// Règles des informations Chifa d'une vente. Date des soins : saisie, jamais dans le futur (la 2e date du QR n'y est pas rangée
/// d'office, décision D-1). Majoration (D-3) : montant de 0 à <see cref="MajorationMax"/> DA, type obligatoire dès qu'il y a un montant ;
/// elle s'ajoute à la part organisme (créance sur l'organisme, ticket), jamais à la part du patient.
/// </summary>
public static class RegleInfosChifa
{
    public const decimal MajorationMax = 1_000_000m;

    public static InfosChifa Verifier(InfosChifa infos, DateOnly aujourdhui)
    {
        if (infos.DateSoins is { } d && d > aujourdhui)
            throw new RegleMetierException("Date des soins : elle ne peut pas être dans le futur.", "date_soins");
        var montant = infos.MajorationMontant;
        if (montant < 0) throw new RegleMetierException("Majoration : le montant ne peut pas être négatif.", "majoration_montant");
        if (montant > MajorationMax) throw new RegleMetierException($"Majoration : {MajorationMax:N0} DA au plus.", "majoration_montant");
        if (decimal.Round(montant, 2) != montant)
            throw new RegleMetierException("Majoration : deux décimales au plus.", "majoration_montant");
        var code = string.IsNullOrWhiteSpace(infos.MajorationCode) ? null : infos.MajorationCode.Trim();
        if (montant > 0 && code is null) throw new RegleMetierException("Majoration : choisissez son type.", "majoration_type");
        // Sans montant, pas de type gardé (la base l'admet, mais il ne dirait rien)
        return infos with { MajorationCode = montant > 0 ? code : null, CentrePayeurId = infos.CentrePayeurId is > 0 ? infos.CentrePayeurId : null,
            SaisiPar = infos.SaisiPar is > 0 ? infos.SaisiPar : null };
    }

    /// <summary>Part organisme facturée : part de la caisse (ou part Chifa retenue) plus la majoration (D-3).</summary>
    public static decimal PartOrganismeTotale(decimal partOrganisme, decimal majoration) => partOrganisme + Math.Max(0, majoration);
}

/// <summary>
/// « Exclure du traitement » une vente Chifa (2026-11-15, script 100) : sans nouveau statut de vente ; la vente sort de la liste
/// « Ventes en attente de volet » (et des candidates au rattachement) et ne doit pas entrer dans un bordereau. Réversible (« Rétablir »).
/// </summary>
public static class RegleExclusionChifa
{
    /// <summary>Exclusion permise : vente Chifa validée, pas déjà exclue, pas dans un bordereau.</summary>
    public static void VerifierExclusion(string canal, string statut, bool exclue, bool dansBordereau)
    {
        if (canal != "chifa") throw new RegleMetierException("Seule une vente facturée par Chifa s’exclut du traitement Chifa.", "vente");
        if (statut != "validee") throw new RegleMetierException("Cette vente n’est pas validée (annulée ou en cours) : rien à exclure.", "vente");
        if (exclue) throw new RegleMetierException("Cette vente est déjà exclue du traitement Chifa.", "vente");
        if (dansBordereau) throw new RegleMetierException("Cette vente est déjà dans un bordereau : elle ne peut plus être exclue.", "vente");
    }

    public static void VerifierRetablissement(bool exclue)
    {
        if (!exclue) throw new RegleMetierException("Cette vente n’est pas exclue du traitement Chifa.", "vente");
    }
}

/// <summary>Centre payeur d'un organisme Chifa (script 100) : code libre de 1 à 10 caractères (D-2), unique par organisme ; libellé.</summary>
public static class RegleCentrePayeur
{
    public const int CodeMax = 10;
    public const int NomMax = 120;

    public static (string Code, string Nom) Verifier(string? code, string? nom)
    {
        var c = code?.Trim() ?? "";
        if (c.Length == 0) throw new RegleMetierException("Indiquez le code du centre payeur.", "code");
        if (c.Length > CodeMax) throw new RegleMetierException($"Code du centre : {CodeMax} caractères au plus.", "code");
        var n = string.Join(' ', (nom ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (n.Length == 0) throw new RegleMetierException("Indiquez le libellé du centre payeur.", "nom");
        if (n.Length > NomMax) throw new RegleMetierException($"Libellé du centre : {NomMax} caractères au plus.", "nom");
        return (c, n);
    }
}
