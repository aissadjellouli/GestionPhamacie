using Pharma.Domain.Commun;

namespace Pharma.Domain.Stock;

/// <summary>
/// Lot déjà en stock avec le même produit et le même n° de lot : du même grossiste, ou (AutreGrossiste) bloqué chez un autre
/// grossiste (un rappel bloque ce n° de lot chez tous les grossistes, y compris pour une livraison à venir).
/// </summary>
public sealed record LotExistant(long Id, DateOnly DatePeremption, bool DateAVerifier, int Quantite, decimal PrixAchatHt, bool Bloque,
    bool AutreGrossiste = false);

/// <summary>Situation à trancher par l'utilisateur à la réception.</summary>
public enum ConflitLot
{
    /// <summary>Même n° de lot déjà en stock avec une autre date de péremption (souvent une erreur de saisie).</summary>
    DateDifferente,
    /// <summary>Ce n° de lot est bloqué (rappel).</summary>
    LotBloque
}

/// <summary>Choix de l'utilisateur pour une ligne en conflit.</summary>
/// <param name="LotSepare">Date différente : créer un lot séparé avec la date saisie (sinon : corriger la date).</param>
/// <param name="EntrerBloque">Lot bloqué : entrer les boîtes en stock, bloquées (sinon : les mettre de côté).</param>
public sealed record ChoixLot(bool LotSepare = false, bool EntrerBloque = false);

/// <summary>Où vont les boîtes reçues : lot existant à compléter, ou nouveau lot (bloqué ou non).</summary>
public sealed record DecisionLot(LotExistant? Completer, bool NouveauBloque);

/// <summary>
/// Un lot = produit + n° de lot + grossiste + péremption. Le même lot relivré complète le lot existant ;
/// chaque boîte garde le prix de sa livraison. Une date différente ou un lot bloqué sont tranchés par l'utilisateur.
/// </summary>
public static class RegleLot
{
    /// <summary>« am55 » et « AM55 » désignent le même lot.</summary>
    public static string Normaliser(string? numero) => (numero ?? "").Trim().ToUpperInvariant();

    /// <summary>Lot à compléter : même date, ou lot unique repris avec une date provisoire (« à vérifier »).</summary>
    private static LotExistant? Cible(IReadOnlyList<LotExistant> memeNumero, DateOnly peremption)
    {
        var propres = Propres(memeNumero);
        return propres.FirstOrDefault(l => l.DatePeremption == peremption) ?? (propres is [{ DateAVerifier: true } seul] ? seul : null);
    }

    /// <summary>Lots de ce grossiste (seuls complétés par la livraison, seuls comparés pour la date).</summary>
    public static IReadOnlyList<LotExistant> Propres(IReadOnlyList<LotExistant> memeNumero) => memeNumero.Where(l => !l.AutreGrossiste).ToList();

    public static IReadOnlyList<ConflitLot> Conflits(IReadOnlyList<LotExistant> memeNumero, DateOnly peremption)
    {
        var cible = Cible(memeNumero, peremption);
        var conflits = new List<ConflitLot>();
        if (cible is null && Propres(memeNumero).Count > 0) conflits.Add(ConflitLot.DateDifferente);
        // Lot bloqué ici, ou ce n° de lot bloqué (rappel) chez un autre grossiste
        if ((cible?.Bloque ?? Propres(memeNumero).Any(l => l.Bloque)) || memeNumero.Any(l => l.AutreGrossiste && l.Bloque))
            conflits.Add(ConflitLot.LotBloque);
        return conflits;
    }

    /// <summary>Applique le choix de l'utilisateur ; sans choix, un conflit est refusé avec un message qui propose les deux options.</summary>
    public static DecisionLot Decider(IReadOnlyList<LotExistant> memeNumero, DateOnly peremption, ChoixLot choix, string produit, string numero)
    {
        var conflits = Conflits(memeNumero, peremption);
        if (conflits.Contains(ConflitLot.DateDifferente) && !choix.LotSepare)
        {
            var dates = string.Join(", ", Propres(memeNumero).Select(l => l.DatePeremption.ToString("dd/MM/yyyy")).Distinct());
            throw new RegleMetierException(
                $"{produit}, lot {numero} : déjà en stock avec la péremption du {dates}, pas du {peremption:dd/MM/yyyy}. " +
                "Corrigez la date ou choisissez « Créer un lot séparé ».");
        }
        if (conflits.Contains(ConflitLot.LotBloque) && !choix.EntrerBloque)
            throw new RegleMetierException(
                $"{produit}, lot {numero} : lot bloqué (rappel). Mettez ces boîtes de côté ou choisissez « Entrer en stock bloqué ».");

        var cible = Cible(memeNumero, peremption);
        return new DecisionLot(cible, cible is null && conflits.Contains(ConflitLot.LotBloque));
    }
}
