using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

public sealed record BoiteVendue(long Id, string NumeroSerie);

/// <summary>Ligne de la vente : ce qui peut encore être rendu, et les boîtes identifiées encore chez le patient.</summary>
public sealed record LigneRetournable(long LigneVenteId, long LotId, string Produit, string NumeroLot, int QuantiteVendue, int DejaRendue,
    decimal MontantPatient, IReadOnlyList<BoiteVendue> Boites);

public sealed record VenteRetournable(VenteAnnulable Vente, IReadOnlyList<LigneRetournable> Lignes);

/// <summary>Produit rendu : quantité, destination choisie, boîtes identifiées rendues (si la ligne en a).</summary>
public sealed record LigneRetourDemandee(long LigneVenteId, int Quantite, string? Destination, IReadOnlyList<long>? Boites);

public sealed record LigneRetourAEcrire(long LigneVenteId, long LotId, int Quantite, DestinationRetour Destination, decimal Montant, IReadOnlyList<long> Boites);

public sealed record RetourAEcrire(long VenteId, string Numero, string Motif, long UtilisateurId, long ValidePar, string? Mode, decimal Montant,
    long? SessionCaisseId, string Poste, IReadOnlyList<LigneRetourAEcrire> Lignes);

public interface IRetoursClient
{
    Task<VenteRetournable?> LireAsync(long venteId, CancellationToken ct = default);
    Task EnregistrerAsync(RetourAEcrire r, CancellationToken ct = default);
}

public sealed record ResultatRetour(string Numero, decimal ARendre, string? Mode, int RemisEnStock, int ADetruire, string? ValidePar);

public sealed class LireVenteRetournable(IRetoursClient retours)
{
    public async Task<VenteRetournable> ExecuterAsync(long venteId, CancellationToken ct = default) =>
        await retours.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.");
}

/// <summary>
/// Retour de médicaments par le patient, à l'appréciation du pharmacien (droit « caisse.retour_especes ») ou validé sur place
/// par le code d'un employé qui l'a. Chaque produit est remis en stock sur son lot, ou mis à détruire. L'argent rendu sort de
/// la caisse ouverte du poste ; vente à crédit : la dette du patient diminue.
/// </summary>
public sealed class EnregistrerRetourClient(IRetoursClient retours, ISessionsCaisse sessions, IPermissionService droits,
    VerificationCodeComptoir codes, IJournal journal)
{
    public async Task<ResultatRetour> ExecuterAsync(long venteId, string? motif, IReadOnlyList<LigneRetourDemandee> demandees, long utilisateurId,
        string? poste, string? validateurIdentifiant, string? validateurCode, CancellationToken ct = default)
    {
        var nomPoste = PosteCaisse.Exiger(poste);
        var texte = RegleAnnulation.Motif(motif);
        var vr = await retours.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.");
        var v = vr.Vente;
        RegleRetour.VerifierVente(v.Statut, v.DansUnBordereau, v.PartOrganisme);
        if (demandees.Count == 0) throw new RegleMetierException("Choisissez au moins un produit rendu.");

        // Les lignes portent le prix avant la remise de la vente : on rend au prorata de ce que le patient a payé
        var brutPatient = vr.Lignes.Sum(l => l.MontantPatient);
        var lignes = new List<LigneRetourAEcrire>();
        foreach (var d in demandees)
        {
            var l = vr.Lignes.FirstOrDefault(x => x.LigneVenteId == d.LigneVenteId)
                    ?? throw new RegleMetierException("Ce produit ne fait pas partie de la vente.");
            if (lignes.Any(x => x.LigneVenteId == l.LigneVenteId)) throw new RegleMetierException($"{l.Produit} : produit indiqué deux fois.");
            RegleRetour.VerifierQuantite(l.Produit, d.Quantite, l.QuantiteVendue, l.DejaRendue);
            var destination = RegleRetour.Destination(d.Destination);

            // Boîtes identifiées : ce sont elles, et pas d'autres, que le patient rapporte
            IReadOnlyList<long> boites = [];
            if (l.Boites.Count > 0)
            {
                boites = (d.Boites ?? []).Distinct().ToList();
                if (boites.Count != d.Quantite || boites.Any(b => l.Boites.All(x => x.Id != b)))
                    throw new RegleMetierException($"{l.Produit} : cochez les {d.Quantite} boîte(s) rendue(s) parmi celles vendues (n° de série).");
            }
            lignes.Add(new LigneRetourAEcrire(l.LigneVenteId, l.LotId, d.Quantite, destination,
                RegleRetour.MontantPaye(l.MontantPatient, l.QuantiteVendue, d.Quantite, v.PartPatient, brutPatient), boites));
        }
        // Total au plus ce qui reste de la part payée ; dernier retour : le solde exact (arrondis)
        var toutRendu = vr.Lignes.All(l => l.DejaRendue + (lignes.FirstOrDefault(x => x.LigneVenteId == l.LigneVenteId)?.Quantite ?? 0) >= l.QuantiteVendue);
        var total = RegleRetour.TotalARendre(lignes.Sum(l => l.Montant), v.PartPatient, v.DejaRendu, toutRendu);
        if (total != lignes.Sum(l => l.Montant) && lignes.Count > 0)
        {
            // Écart d'arrondi porté sur la dernière ligne, pour que le détail corresponde au total rendu
            var ecart = total - lignes.Sum(l => l.Montant);
            lignes[^1] = lignes[^1] with { Montant = Math.Max(lignes[^1].Montant + ecart, 0) };
        }

        // Le pharmacien décide : droit de l'employé, sinon validation sur place
        long validePar = utilisateurId;
        string? nomValidateur = null;
        if (!await droits.APermissionAsync(utilisateurId, Permissions.CaisseRetourEspeces, ct))
        {
            if (string.IsNullOrWhiteSpace(validateurIdentifiant))
                throw new RegleMetierException("Le retour d’un produit est décidé par le pharmacien ou l’assistant : il doit valider avec son code.");
            var validateur = await codes.VerifierAsync(validateurIdentifiant, validateurCode, nomPoste, ct);
            if (!await droits.APermissionAsync(validateur.Id, Permissions.CaisseRetourEspeces, ct))
                throw new DroitRefuseException(Permissions.CaisseRetourEspeces, "valider le retour d’un produit");
            (validePar, nomValidateur) = (validateur.Id, validateur.NomComplet);
        }

        // Part payée par le patient pour les boîtes rendues : rendue, ou retirée de sa dette pour une vente à crédit
        var montant = lignes.Sum(l => l.Montant);
        var session = await sessions.SessionDuPosteAsync(nomPoste, ct);
        if (v.Mode == "especes" && montant > 0 && session is null)
            throw new RegleMetierException("Ouvrez la caisse du poste : l’argent rendu au patient doit en sortir.");

        await retours.EnregistrerAsync(new RetourAEcrire(v.Id, v.Numero, texte, utilisateurId, validePar, v.Mode, montant, session?.Id, nomPoste, lignes), ct);
        await journal.EcrireAsync(utilisateurId, "retour_client", "vente", v.Id,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["motif"] = texte, ["montant"] = montant, ["mode"] = v.Mode,
                ["remis_en_stock"] = lignes.Where(l => l.Destination == DestinationRetour.Stock).Sum(l => l.Quantite),
                ["a_detruire"] = lignes.Where(l => l.Destination == DestinationRetour.Destruction).Sum(l => l.Quantite),
                ["valide_par"] = validePar
            }, Permissions.CaisseRetourEspeces, ct);
        return new ResultatRetour(v.Numero, v.Mode == "credit" ? 0 : montant, v.Mode,
            lignes.Where(l => l.Destination == DestinationRetour.Stock).Sum(l => l.Quantite),
            lignes.Where(l => l.Destination == DestinationRetour.Destruction).Sum(l => l.Quantite), nomValidateur);
    }
}
