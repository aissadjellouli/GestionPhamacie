using Pharma.Application.Administration;
using Pharma.Domain.Commun;

namespace Pharma.Application.Caisse;

/// <summary>
/// Shp : par boîte, compris dans PrixUnitaire ; Medicament : catégorie comptée dans « médicaments » ; Reduction : remise (ou don partiel
/// sur un médicament) de la ligne, au pourcentage PctReduction.
/// </summary>
public sealed record LigneTicket(string Produit, int Quantite, decimal PrixUnitaire, decimal Montant, decimal PartOrganisme, decimal Shp = 0, bool Medicament = true,
    decimal Reduction = 0, decimal PctReduction = 0, bool DonPartiel = false, decimal? TauxTva = null)
{
    /// <summary>Libellé imprimé : prestation (TauxTva renseigné, script 105) suivie de son taux de TVA.</summary>
    public string Designation => LibelleTicket.Ligne(Produit, TauxTva);
}

/// <summary>Libellé d'une ligne de ticket : une prestation (sans stock, script 105) porte son taux de TVA.</summary>
public static class LibelleTicket
{
    public static string Ligne(string produit, decimal? tauxTva) =>
        tauxTva is { } t ? $"{produit} (prestation, TVA {t.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} %)" : produit;
}

/// <summary>Contenu du ticket de caisse ; le poste le met en page et l'imprime.</summary>
public sealed record TicketVente(
    string Numero, DateTime Date, string Vendeur, string? Poste, string Statut,
    string? Organisme, decimal? TauxTiersPayant, IReadOnlyList<LigneTicket> Lignes,
    decimal MontantBrut, decimal Remise, decimal PartOrganisme, decimal PartPatient, string? Mode,
    string? Pharmacie, string? Adresse, string? Telephone, string? Nif, IReadOnlyList<string>? Retours = null, decimal DonPartiel = 0,
    decimal EcartChifa = 0, string? Client = null, decimal MajorationChifa = 0);

/// <summary>
/// Ticket en PDF (2026-11-15) : montré à l'écran quand l'imprimante de tickets du poste enregistre un fichier (Microsoft Print to PDF…)
/// au lieu d'imprimer ; même contenu que le ticket imprimé. Rendu : monnaie rendue (espèces), sinon null.
/// </summary>
public interface IDocumentsTicket
{
    /// <summary>Page au format rouleau (largeur bornée par FormatTicket, hauteur selon le contenu).</summary>
    byte[] Ticket(TicketVente t, decimal? rendu, int largeurMm = Pharma.Domain.Caisse.FormatTicket.LargeurParDefaut);
}

/// <summary>Reçu d'un règlement de crédit tel qu'enregistré : ResteDu = dette du patient juste après ce règlement.</summary>
public sealed record RecuCreditEnregistre(string Numero, DateTime Date, decimal Montant, string Mode, decimal ResteDu, string? Employe, string? Poste);

public interface IRecusCredit
{
    Task<RecuCreditEnregistre?> LireAsync(string numero, CancellationToken ct = default);
}

/// <summary>
/// Reçu de règlement de crédit en PDF (2026-11-15), comme le ticket de vente : montré à l'écran quand l'imprimante de tickets du poste
/// enregistre un fichier. Même contenu que le reçu imprimé : montant, mode, reste dû, sans le nom du patient.
/// </summary>
public sealed class ImprimerRecuCredit(IRecusCredit recus, IDocumentsTicket documents, IPharmacie pharmacie)
{
    public static TicketVente Contenu(RecuCreditEnregistre r) => new(r.Numero, r.Date, r.Employe ?? "", r.Poste, "validee", null, null,
        [new LigneTicket($"Encaissement de dette (reste dû {r.ResteDu.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} DA)", 1, r.Montant, r.Montant, 0)],
        r.Montant, 0, 0, r.Montant, r.Mode, null, null, null, null);

    /// <returns>null si le reçu n'existe pas (404 côté API, revue 2026-11-15 M5).</returns>
    public async Task<(byte[] Contenu, string NomFichier)?> ExecuterAsync(string numero, int? largeurMm, CancellationToken ct = default)
    {
        if (await recus.LireAsync(numero.Trim(), ct) is not { } r) return null;
        var p = await pharmacie.LireAsync(ct);
        var t = Contenu(r) with { Pharmacie = p.Nom, Adresse = p.Adresse, Telephone = p.Telephone, Nif = p.Nif };
        return (documents.Ticket(t, null, Pharma.Domain.Caisse.FormatTicket.Borner(largeurMm)), $"recu_{r.Numero}.pdf");
    }
}

public interface ITicketLecture
{
    /// <summary>Vente et ses lignes, sans les coordonnées de la pharmacie ; null si introuvable.</summary>
    Task<TicketVente?> LireAsync(long venteId, CancellationToken ct = default);
}

/// <summary>Ticket d'une vente (impression au comptoir ou réimpression). Aucune donnée du patient n'y figure, sauf « Remis à » / « Pour » (Client : « Karim · remis à Ali », 2026-11-15).</summary>
public sealed class ObtenirTicket(ITicketLecture tickets, IPharmacie pharmacie)
{
    public async Task<TicketVente> ExecuterAsync(long venteId, CancellationToken ct = default)
    {
        var t = await tickets.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.");
        var p = await pharmacie.LireAsync(ct);
        return t with { Pharmacie = p.Nom, Adresse = p.Adresse, Telephone = p.Telephone, Nif = p.Nif };
    }
}
