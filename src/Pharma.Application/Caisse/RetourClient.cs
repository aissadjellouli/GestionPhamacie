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

/// <summary>
/// Montant : part payée par le patient pour les boîtes rendues. Vente à crédit : ReductionDette en est la part retirée de la dette,
/// le reste (déjà remboursé par le patient) lui est rendu en espèces depuis la caisse ouverte du poste.
/// </summary>
public sealed record RetourAEcrire(long VenteId, string Numero, string Motif, long UtilisateurId, long ValidePar, string? Mode, decimal Montant,
    long? SessionCaisseId, string Poste, IReadOnlyList<LigneRetourAEcrire> Lignes, decimal ReductionDette = 0, long? PatientDetteId = null,
    bool AccordClient = false, string? MotifCode = null, string? MotifTexte = null, decimal AvoirRecredite = 0, long? PatientAvoirId = null);

public interface IRetoursClient
{
    Task<VenteRetournable?> LireAsync(long venteId, CancellationToken ct = default);
    Task EnregistrerAsync(RetourAEcrire r, CancellationToken ct = default);
}

/// <summary>DetteDiminuee : part du retour retirée de la dette du patient (vente à crédit, ou dette soldée à la demande du caissier).</summary>
public sealed record ResultatRetour(string Numero, decimal ARendre, string? Mode, int RemisEnStock, int ADetruire, string? ValidePar,
    decimal DetteDiminuee = 0, decimal AvoirRecredite = 0);

public sealed class LireVenteRetournable(IRetoursClient retours)
{
    public async Task<VenteRetournable> ExecuterAsync(long venteId, CancellationToken ct = default) =>
        await retours.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.");
}

/// <summary>
/// Retour de médicaments par le patient, à l'appréciation du pharmacien (droit « caisse.retour_especes ») ou validé sur place
/// par le code d'un employé qui l'a. Chaque produit est remis en stock sur son lot, ou mis à détruire. L'argent rendu sort de
/// la caisse ouverte du poste ; vente à crédit : la dette du patient diminue, et ce qu'il a déjà remboursé lui est rendu en espèces.
/// Remboursement d'une vente payée entièrement retiré de la dette, avec l'accord du client : rien ne sort de la caisse, le
/// caissier valide seul.
/// </summary>
public sealed class EnregistrerRetourClient(IRetoursClient retours, ISessionsCaisse sessions, IPermissionService droits,
    VerificationCodeComptoir codes, IJournal journal, Patients.IPatients? patients = null, Securite.AutorisationCourante? autorisation = null,
    Referentiels.MotifsActifs? motifs = null)
{
    public async Task<ResultatRetour> ExecuterAsync(long venteId, string? motif, IReadOnlyList<LigneRetourDemandee> demandees, long utilisateurId,
        string? poste, string? validateurIdentifiant, string? validateurCode, bool solderDette = false, long? patientDetteId = null,
        CancellationToken ct = default, bool accordClient = false, string? motifCode = null)
    {
        var nomPoste = PosteCaisse.Exiger(poste);
        // Motif choisi dans la liste réglable « retour_client » (script 104), obligatoire ; « Autre » avec un texte, sinon le texte
        // est une précision facultative. Sans la liste (anciens appels internes) : motif libre comme avant.
        string? code = null, precision = null;
        string texte;
        if (motifs is not null)
        {
            (code, texte, precision) = await motifs.ChoisirAsync(Domain.Referentiels.RegleMotifs.RetourClient, motifCode, motif, "du retour", ct);
            var detail = string.IsNullOrWhiteSpace(motif) ? null : motif.Trim();
            if (code != Domain.Referentiels.RegleMotifs.Autre && detail is not null)
            {
                if (detail.Length > Domain.Referentiels.RegleMotifs.TexteMax)
                    throw new RegleMetierException($"Motif : {Domain.Referentiels.RegleMotifs.TexteMax} caractères au plus.", "motif_texte");
                (texte, precision) = ($"{texte} : {detail}", detail);
            }
        }
        else texte = RegleAnnulation.Motif(motif);
        var vr = await retours.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.");
        var v = vr.Vente;
        RegleRetour.VerifierVente(v.Statut, v.DansUnBordereau, v.PartOrganisme);
        if (demandees.Count == 0) throw new RegleMetierException("Choisissez au moins un produit rendu.");

        // Lignes nettes de leurs réductions (remise, don partiel) ; ventes antérieures sans réduction par ligne : prorata de ce que le patient a payé
        var brutPatient = vr.Lignes.Sum(l => l.MontantPatient);
        var lignes = new List<LigneRetourAEcrire>();
        foreach (var d in demandees)
        {
            var l = vr.Lignes.FirstOrDefault(x => x.LigneVenteId == d.LigneVenteId)
                    ?? throw new RegleMetierException("Ce produit ne fait pas partie de la vente.");
            if (lignes.Any(x => x.LigneVenteId == l.LigneVenteId)) throw new RegleMetierException($"{l.Produit} : produit indiqué deux fois.");
            RegleRetour.VerifierQuantite(l.Produit, d.Quantite, l.QuantiteVendue, l.DejaRendue);
            var destination = RegleRetour.Destination(d.Destination);

            // Boîtes identifiées cochées parmi celles de la vente ; le reste de la quantité, ce sont des boîtes vendues sans
            // être scannées (scan facultatif) : au plus celles encore chez le patient
            var boites = (d.Boites ?? []).Distinct().ToList();
            if (boites.Any(b => l.Boites.All(x => x.Id != b)))
                throw new RegleMetierException($"{l.Produit} : une boîte cochée ne fait pas partie de cette vente (n° de série ou étiquette).");
            var nonIdentifiees = Math.Max(l.QuantiteVendue - l.DejaRendue - l.Boites.Count, 0);
            if (boites.Count > d.Quantite || d.Quantite - boites.Count > nonIdentifiees)
                throw new RegleMetierException(nonIdentifiees == 0
                    ? $"{l.Produit} : cochez les {d.Quantite} boîte(s) rendue(s) parmi celles vendues (n° de série ou étiquette)."
                    : $"{l.Produit} : {nonIdentifiees} boîte(s) vendue(s) sans identité ; cochez les autres boîtes rendues parmi celles vendues.");
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

        // Part payée par le patient pour les boîtes rendues : rendue, ou retirée de sa dette pour une vente à crédit
        // (ce que le patient a déjà remboursé lui est rendu en espèces, comme pour une annulation)
        var montant = lignes.Sum(l => l.Montant);
        // Vente payée en partie par avoir (2026-11-15, D2, script 109) : l'avoir est recrédité d'abord, à hauteur de ce qui a été payé par
        // avoir et pas encore recrédité ; seul le reste suit le mode d'origine (espèces, carte, chèque, crédit)
        var (avoirRecredite, resteAutreMode) = v.PatientId is null ? (0m, montant)
            : RegleAvoir.RetourPayeParAvoir(montant, v.PayeParAvoir, v.AvoirRecrediteParRetours);
        // Vente payée (espèces, carte, chèque) à un patient qui doit de l'argent par ailleurs : avec l'accord du client, le
        // remboursement est retiré de cette dette (solderDette) ; seul le surplus est alors rendu.
        // Vente sans patient : s'il s'est nommé au retour (patientDetteId), c'est sa dette, relue ici, qui est diminuée.
        var dette = v.SoldeCreditPatient;
        long? patientDette = null;
        if (solderDette && v.Mode != "credit" && patientDetteId is { } pid)
        {
            if (v.Patient is not null) throw new RegleMetierException("Cette vente a déjà un patient : c’est sa dette qui peut être soldée.");
            var patient = (patients is null ? null : await patients.TrouverAsync(pid, ct)) ?? throw new RegleMetierException("Patient introuvable.");
            (dette, patientDette) = (patient.SoldeCredit, pid);
        }
        // Vente à crédit : la dette baisse au plus de ce qui reste impayé sur la vente ; seul ce qui a vraiment été payé est rendu
        var (reductionDette, aRendre) = v.Mode == "credit"
            ? RegleAnnulation.RemboursementCredit(resteAutreMode, Math.Max(v.PartPatient - v.PayeParAvoir - (v.DejaRendu - v.AvoirRecrediteParRetours), 0), v.PayeSurVente, dette)
            : solderDette ? (Math.Min(Math.Max(dette, 0), resteAutreMode), resteAutreMode - Math.Min(Math.Max(dette, 0), resteAutreMode)) : (0m, resteAutreMode);
        var deduitAuChoix = solderDette && v.Mode != "credit" && reductionDette > 0;
        if (deduitAuChoix && !accordClient) throw RegleAnnulation.AccordClientManquant();

        // Le pharmacien décide : droit de l'employé, sinon validation sur place. Tout déduit de la dette avec l'accord du
        // client (rien ne sort de la caisse) : le caissier valide seul
        long validePar = utilisateurId;
        string? nomValidateur = null;
        var sansArgentRendu = deduitAuChoix && aRendre == 0;
        if (!sansArgentRendu && !await droits.APermissionAsync(utilisateurId, Permissions.CaisseRetourEspeces, ct))
        {
            if (string.IsNullOrWhiteSpace(validateurIdentifiant))
                throw new RegleMetierException("Le retour d’un produit est décidé par le pharmacien ou l’assistant : il doit valider avec son code.");
            var validateur = await codes.VerifierAsync(validateurIdentifiant, validateurCode, nomPoste, ct, utilisateurId);
            if (!await droits.APermissionAsync(validateur.Id, Permissions.CaisseRetourEspeces, ct))
                throw new DroitRefuseException(Permissions.CaisseRetourEspeces, "valider le retour d’un produit");
            (validePar, nomValidateur) = (validateur.Id, validateur.NomComplet);
        }
        // Droit donné sur place par le code d'un collègue (en-têtes d'autorisation) : c'est ce collègue qui valide
        else if (autorisation?.AutoriseParId is { } par) (validePar, nomValidateur) = (par, autorisation.AutoriseParNom);

        var session = await sessions.SessionDuPosteAsync(nomPoste, ct);
        if (v.Mode is "especes" or "credit" && aRendre > 0 && session is null)
            throw new RegleMetierException("Ouvrez la caisse du poste : l’argent rendu au patient doit en sortir.");

        await retours.EnregistrerAsync(new RetourAEcrire(v.Id, v.Numero, texte, utilisateurId, validePar, v.Mode, montant, session?.Id, nomPoste, lignes,
            reductionDette, reductionDette > 0 ? patientDette : null, deduitAuChoix, code, precision, avoirRecredite,
            avoirRecredite > 0 ? v.PatientId : null), ct);
        await journal.EcrireAsync(utilisateurId, "retour_client", "vente", v.Id,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["motif"] = texte, ["motif_code"] = code, ["montant"] = montant, ["mode"] = v.Mode,
                ["dette_diminuee"] = reductionDette, ["rendu"] = aRendre, ["dette_soldee_au_choix"] = solderDette && v.Mode != "credit" && reductionDette > 0,
                ["patient_dette"] = reductionDette > 0 ? patientDette : null, ["accord_client"] = deduitAuChoix ? true : null,
                ["avoir_recredite"] = avoirRecredite > 0 ? avoirRecredite : null,
                ["remis_en_stock"] = lignes.Where(l => l.Destination == DestinationRetour.Stock).Sum(l => l.Quantite),
                ["a_detruire"] = lignes.Where(l => l.Destination == DestinationRetour.Destruction).Sum(l => l.Quantite),
                ["valide_par"] = validePar
            }, Permissions.CaisseRetourEspeces, ct);
        return new ResultatRetour(v.Numero, aRendre, v.Mode == "credit" && aRendre > 0 ? "especes" : v.Mode,
            lignes.Where(l => l.Destination == DestinationRetour.Stock).Sum(l => l.Quantite),
            lignes.Where(l => l.Destination == DestinationRetour.Destruction).Sum(l => l.Quantite), nomValidateur, reductionDette, avoirRecredite);
    }
}
