using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Application.Securite;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

public sealed record LigneCommande(long ProduitId, long LotId, int Quantite);
/// <summary>
/// Ordonnance présentée au comptoir : saisie pour un psychotrope (médecin obligatoire) ou scannée
/// (image conservée sur le serveur, désignée par son jeton).
/// </summary>
public sealed record OrdonnanceSaisie(long? MedecinId, DateOnly DatePrescription, string? Numero,
    string? JetonScan = null, string? TexteReconnu = null);

/// <summary>Panier envoyé par le poste, avec le règlement choisi.</summary>
public sealed record CommandeVente(
    IReadOnlyList<LigneCommande> Lignes,
    bool TiersPayant, long? OrganismeId, decimal? TauxTiersPayant,
    long? PatientId, OrdonnanceSaisie? Ordonnance,
    decimal RemisePct, ModePaiement Mode, decimal? MontantRecu);

public sealed record ApercuVente(
    decimal MontantBrut, decimal PartOrganisme, decimal MontantRemise, decimal PartPatient,
    decimal MontantTotal, bool OrdonnanceRequise);

public sealed record VenteEncaissee(
    long VenteId, string Numero, decimal MontantTotal, decimal PartOrganisme, decimal PartPatient,
    ModePaiement Mode, decimal Rendu);

/// <summary>Ce que l'adaptateur enregistre en une transaction.</summary>
public sealed record VenteAEnregistrer(
    Vente Vente, Reglement Reglement, OrdonnanceSaisie? Ordonnance,
    IReadOnlySet<long> ProduitsSousRegistre, bool CreditAccorde);

public interface IPreparationVente
{
    Task<IReadOnlyDictionary<long, Produit>> ProduitsAsync(IEnumerable<long> ids, CancellationToken ct = default);
    Task<IReadOnlyDictionary<long, Lot>> LotsAsync(IEnumerable<long> ids, CancellationToken ct = default);
}

public interface IVentesEcriture
{
    /// <summary>
    /// Vente, lignes, stock, mouvements, paiement, ordonnance, registre des psychotropes, crédit et journal,
    /// en une seule transaction. Renvoie l'identifiant et le numéro de ticket.
    /// </summary>
    Task<(long Id, string Numero)> EnregistrerAsync(VenteAEnregistrer vente, CancellationToken ct = default);
}

public sealed class Encaisser(
    IPreparationVente preparation, IPatients patients, IVentesEcriture ventes,
    ISessionsCaisse sessions, IPermissionService droits, IHorloge horloge, Ordonnances.IStockageOrdonnances scans)
{
    /// <summary>Totaux recalculés par le serveur (mêmes règles que l'encaissement), sans rien enregistrer.</summary>
    public async Task<ApercuVente> CalculerAsync(CommandeVente commande, long utilisateurId, CancellationToken ct = default)
    {
        var (vente, _) = await ConstruireAsync(commande, utilisateurId, sessionId: null, ct);
        return new ApercuVente(
            vente.MontantTotal + vente.MontantRemise, vente.PartOrganisme, vente.MontantRemise,
            vente.PartPatient, vente.MontantTotal, vente.ContientSousRegistre);
    }

    public async Task<VenteEncaissee> ExecuterAsync(CommandeVente commande, long utilisateurId, CancellationToken ct = default)
    {
        var session = await sessions.SessionOuverteAsync(utilisateurId, ct)
                      ?? throw new RegleMetierException("Ouvrez la caisse avant d’encaisser.");

        var (vente, produits) = await ConstruireAsync(commande, utilisateurId, session.Id, ct);

        if (vente.ContientSousRegistre)
            await droits.ExigerAsync(utilisateurId, Permissions.PsychotropesDelivrer, "délivrer un psychotrope", ct);
        if (commande.RemisePct > 0)
            await droits.ExigerAsync(utilisateurId, Permissions.CaisseRemise, "accorder une remise", ct);

        if (commande.Ordonnance is { } o)
        {
            if (vente.ContientSousRegistre && o.MedecinId is null)
                throw new RegleMetierException("Psychotrope ou stupéfiant : indiquez le médecin prescripteur.");
            if (o.JetonScan is { } jeton && !scans.Existe(jeton))
                throw new RegleMetierException("Image de l’ordonnance introuvable sur le serveur : scannez-la de nouveau.");
            if (o.DatePrescription > horloge.Aujourdhui)
                throw new RegleMetierException("La date de prescription ne peut pas être dans le futur.");
        }
        vente.VerifierAvantEncaissement(commande.Ordonnance is not null);

        Reglement reglement;
        var credit = false;
        if (commande.Mode == ModePaiement.Credit)
        {
            await droits.ExigerAsync(utilisateurId, Permissions.CreditAccorder, "mettre une vente en crédit", ct);
            var patient = commande.PatientId is { } pid ? await patients.TrouverAsync(pid, ct) : null;
            if (patient is null)
                throw new RegleMetierException("Crédit : choisissez le patient à qui la vente est accordée.");
            var depasser = await droits.APermissionAsync(utilisateurId, Permissions.CreditDepasserPlafond, ct);
            if (vente.PartPatient > 0)
            {
                patient.AccorderCredit(vente.PartPatient, depasser);   // refuse au-delà du plafond sans le droit
                credit = true;
            }
            reglement = new Reglement(ModePaiement.Credit, vente.PartPatient, null);
        }
        else
            reglement = Reglement.Pour(vente, commande.Mode, commande.MontantRecu);

        vente.Valider();
        var sousRegistre = produits.Values.Where(p => p.EstSousRegistre).Select(p => p.Id).ToHashSet();
        var (id, numero) = await ventes.EnregistrerAsync(
            new VenteAEnregistrer(vente, reglement, commande.Ordonnance, sousRegistre, credit), ct);

        return new VenteEncaissee(id, numero, vente.MontantTotal, vente.PartOrganisme, vente.PartPatient,
            reglement.Mode, reglement.Rendu);
    }

    private async Task<(Vente, IReadOnlyDictionary<long, Produit>)> ConstruireAsync(
        CommandeVente c, long utilisateurId, long? sessionId, CancellationToken ct)
    {
        if (c.Lignes.Count == 0)
            throw new RegleMetierException("Le panier est vide.");

        var produits = await preparation.ProduitsAsync(c.Lignes.Select(l => l.ProduitId).Distinct(), ct);
        var lots = await preparation.LotsAsync(c.Lignes.Select(l => l.LotId).Distinct(), ct);

        var vente = new Vente
        {
            UtilisateurId = utilisateurId,
            SessionCaisseId = sessionId,
            Type = c.TiersPayant ? TypeVente.TiersPayant : TypeVente.Libre,
            OrganismeId = c.TiersPayant ? c.OrganismeId : null,
            TauxTiersPayant = c.TiersPayant ? c.TauxTiersPayant : null,
            PatientId = c.PatientId
        };

        var aujourdhui = horloge.Aujourdhui;
        foreach (var ligne in c.Lignes)
        {
            if (!produits.TryGetValue(ligne.ProduitId, out var produit) || !produit.Actif)
                throw new RegleMetierException("Un produit du panier n’existe plus ou n’est plus actif.");
            if (!lots.TryGetValue(ligne.LotId, out var lot))
                throw new RegleMetierException($"{produit.NomCommercial} : lot introuvable, rescannez la boîte.");
            vente.AjouterProduit(produit, lot, ligne.Quantite, aujourdhui);
        }

        if (c.RemisePct > 0)
            vente.AppliquerRemise(c.RemisePct, await droits.RemiseMaxAsync(utilisateurId, ct));

        return (vente, produits);
    }
}
