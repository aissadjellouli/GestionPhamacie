using Pharma.Application.Administration;
using Pharma.Domain.Commun;

namespace Pharma.Application.Caisse;

/// <summary>
/// Shp : par boîte, compris dans PrixUnitaire ; Medicament : catégorie comptée dans « médicaments » ; Reduction : remise (ou don partiel
/// sur un médicament) de la ligne, au pourcentage PctReduction.
/// </summary>
public sealed record LigneTicket(string Produit, int Quantite, decimal PrixUnitaire, decimal Montant, decimal PartOrganisme, decimal Shp = 0, bool Medicament = true,
    decimal Reduction = 0, decimal PctReduction = 0, bool DonPartiel = false);

/// <summary>Contenu du ticket de caisse ; le poste le met en page et l'imprime.</summary>
public sealed record TicketVente(
    string Numero, DateTime Date, string Vendeur, string? Poste, string Statut,
    string? Organisme, decimal? TauxTiersPayant, IReadOnlyList<LigneTicket> Lignes,
    decimal MontantBrut, decimal Remise, decimal PartOrganisme, decimal PartPatient, string? Mode,
    string? Pharmacie, string? Adresse, string? Telephone, string? Nif, IReadOnlyList<string>? Retours = null, decimal DonPartiel = 0);

/// <summary>
/// Ticket en PDF (2026-11-15) : montré à l'écran quand l'imprimante de tickets du poste enregistre un fichier (Microsoft Print to PDF…)
/// au lieu d'imprimer ; même contenu que le ticket imprimé. Rendu : monnaie rendue (espèces), sinon null.
/// </summary>
public interface IDocumentsTicket
{
    byte[] Ticket(TicketVente t, decimal? rendu);
}

public interface ITicketLecture
{
    /// <summary>Vente et ses lignes, sans les coordonnées de la pharmacie ; null si introuvable.</summary>
    Task<TicketVente?> LireAsync(long venteId, CancellationToken ct = default);
}

/// <summary>Ticket d'une vente (impression au comptoir ou réimpression). Aucune donnée du patient n'y figure.</summary>
public sealed class ObtenirTicket(ITicketLecture tickets, IPharmacie pharmacie)
{
    public async Task<TicketVente> ExecuterAsync(long venteId, CancellationToken ct = default)
    {
        var t = await tickets.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.");
        var p = await pharmacie.LireAsync(ct);
        return t with { Pharmacie = p.Nom, Adresse = p.Adresse, Telephone = p.Telephone, Nif = p.Nif };
    }
}
