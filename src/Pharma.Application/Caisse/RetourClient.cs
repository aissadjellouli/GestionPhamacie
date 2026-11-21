using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

public sealed record BoiteVendue(long Id, string NumeroSerie);

/// <summary>
/// Ligne de la vente : ce qui peut encore être rendu, et les boîtes identifiées encore chez le patient. MontantOrganisme : part de
/// l'organisme de la ligne enregistrée à la caisse (0 : ligne non remboursée, toujours rendable).
/// </summary>
public sealed record LigneRetournable(long LigneVenteId, long LotId, string Produit, string NumeroLot, int QuantiteVendue, int DejaRendue,
    decimal MontantPatient, IReadOnlyList<BoiteVendue> Boites, decimal MontantOrganisme = 0);

/// <summary>
/// Part de l'organisme de la vente vue pour un retour (script 155). PartRetenue : part Chifa si la vente a été régularisée, sinon part
/// de la caisse (majoration non comprise) ; DejaRetiree : part organisme déjà retirée par des retours ; Canal : logiciel, chifa ou mdn ;
/// FactureOrganisme : facture d'organisme émise par le logiciel qui porte la vente (canal logiciel : avoir AV- manuel à faire) ;
/// FactureChifa : n° de facture Chifa de la vente (à corriger dans CHIFA-Officine).
/// </summary>
public sealed record OrganismeRetour(decimal PartRetenue, decimal DejaRetiree, string Canal, string? FactureOrganisme = null, string? FactureChifa = null)
{
    public static readonly OrganismeRetour Aucun = new(0, 0, "logiciel");
}

public sealed record VenteRetournable(VenteAnnulable Vente, IReadOnlyList<LigneRetournable> Lignes, OrganismeRetour? Organisme = null);

/// <summary>Produit rendu : quantité, destination choisie, boîtes identifiées rendues (si la ligne en a).</summary>
public sealed record LigneRetourDemandee(long LigneVenteId, int Quantite, string? Destination, IReadOnlyList<long>? Boites);

/// <summary>
/// Montant : part payée par le patient rendue ; MontantOrganisme : part de l'organisme retirée de sa créance (script 155) ;
/// MontantRegularisation : part de la régularisation « à la charge du patient » des boîtes rendues, rendue au patient (script 158).
/// </summary>
public sealed record LigneRetourAEcrire(long LigneVenteId, long LotId, int Quantite, DestinationRetour Destination, decimal Montant, IReadOnlyList<long> Boites,
    decimal MontantOrganisme = 0, decimal MontantRegularisation = 0);

/// <summary>
/// Montant : tout ce que le retour rend au patient (part payée par le patient pour les boîtes rendues, plus la part de la régularisation à
/// sa charge, script 158). Vente à crédit : ReductionDette en est la part retirée de la dette, le reste (déjà remboursé par le patient)
/// lui est rendu en espèces depuis la caisse ouverte du poste. ReductionRegularisation : part impayée de la régularisation retirée de sa
/// dette (relevé : correction rattachée à la vente). ImputationDettes : argent dû au patient imputé sur ses dettes comme un encaissement
/// de dette (relevé : remboursement, ventes les plus anciennes d'abord), sans passer par le tiroir.
/// </summary>
public sealed record RetourAEcrire(long VenteId, string Numero, string Motif, long UtilisateurId, long ValidePar, string? Mode, decimal Montant,
    long? SessionCaisseId, string Poste, IReadOnlyList<LigneRetourAEcrire> Lignes, decimal ReductionDette = 0, long? PatientDetteId = null,
    bool AccordClient = false, string? MotifCode = null, string? MotifTexte = null, decimal AvoirRecredite = 0, long? PatientAvoirId = null,
    decimal ReductionRegularisation = 0, decimal ImputationDettes = 0)
{
    /// <summary>Argent rendu par le tiroir (ou la carte, le chèque) : le reste, une fois l'avoir et les dettes servis.</summary>
    public decimal Rendu => Montant - ReductionDette - ReductionRegularisation - ImputationDettes - AvoirRecredite;
}

public interface IRetoursClient
{
    Task<VenteRetournable?> LireAsync(long venteId, CancellationToken ct = default);
    Task EnregistrerAsync(RetourAEcrire r, CancellationToken ct = default);
}

/// <summary>
/// DetteDiminuee : part du retour retirée de la dette du patient (vente à crédit, ou dette soldée à la demande du caissier).
/// PartOrganismeRetiree : part de l'organisme des boîtes rendues, retirée de sa créance (script 155) ; Canal, FactureOrganisme et
/// FactureChifa : pour le rappel affiché (correction dans CHIFA-Officine, avoir AV- manuel d'une facture d'organisme).
/// Script 158 : DetteDiminuee comprend aussi la part impayée de la régularisation et ImputeSurDettes (argent dû imputé sur ses dettes) ;
/// PartRegularisation : part de la régularisation à la charge du patient des boîtes rendues ; DetteRestante : ce qu'il doit encore.
/// </summary>
public sealed record ResultatRetour(string Numero, decimal ARendre, string? Mode, int RemisEnStock, int ADetruire, string? ValidePar,
    decimal DetteDiminuee = 0, decimal AvoirRecredite = 0, decimal PartOrganismeRetiree = 0, string? Canal = null, string? FactureOrganisme = null,
    string? FactureChifa = null, decimal ImputeSurDettes = 0, decimal PartRegularisation = 0, decimal DetteRestante = 0);

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
/// Vente en tiers payant (script 155, option A du 2026-11-21) : la part de l'organisme des boîtes rendues est retirée de sa créance
/// (prorata de la part retenue) si la vente n'est sur aucun bordereau ; déposée, refus ; ligne non remboursée, toujours permise.
/// Taux 100 % : rien n'est rendu au patient (stock et créance seulement), motif et validation du droit « caisse.retour_especes » exigés.
/// </summary>
public sealed class EnregistrerRetourClient(IRetoursClient retours, ISessionsCaisse sessions, IPermissionService droits,
    VerificationCodeComptoir codes, IJournal journal, Patients.IPatients? patients = null, Securite.AutorisationCourante? autorisation = null,
    Referentiels.MotifsActifs? motifs = null)
{
    public async Task<ResultatRetour> ExecuterAsync(long venteId, string? motif, IReadOnlyList<LigneRetourDemandee> demandees, long utilisateurId,
        string? poste, string? validateurIdentifiant, string? validateurCode, bool solderDette = false, long? patientDetteId = null,
        CancellationToken ct = default, bool accordClient = false, string? motifCode = null, string? dettes = null)
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
        RegleRetour.VerifierVente(v.Statut);
        if (demandees.Count == 0) throw new RegleMetierException("Choisissez au moins un produit rendu.");

        // Part organisme (script 155, ajusté par le script 157) : ce qui reste de la part retenue (Chifa si régularisée), réparti au
        // prorata de la part de la caisse des boîtes remboursées encore chez le patient ; majoration à part (v_vente_part_organisme)
        var organisme = vr.Organisme ?? OrganismeRetour.Aucun;
        var partCaisseRestante = vr.Lignes.Sum(l => RegleRetour.PartCaisseRestante(l.MontantOrganisme, l.QuantiteVendue, l.DejaRendue));
        var ratioOrganisme = RegleRetour.RatioRetenu(organisme.PartRetenue - organisme.DejaRetiree, partCaisseRestante);
        // Régularisation « à la charge du patient » faite au rattachement du volet (décision du titulaire du 2026-11-21, script 158) :
        // ce qui en reste est rendu au prorata de la part de la caisse des boîtes rendues (même base que la part organisme)
        var regul = v.Regularisation is { Sort: RegularisationPartOrganisme.ChargePatient } g && g.Restante > 0 ? g : null;

        // Lignes nettes de leurs réductions (remise, don partiel) ; ventes antérieures sans réduction par ligne : prorata de ce que le patient a payé
        var brutPatient = vr.Lignes.Sum(l => l.MontantPatient);
        var lignes = new List<LigneRetourAEcrire>();
        foreach (var d in demandees)
        {
            var l = vr.Lignes.FirstOrDefault(x => x.LigneVenteId == d.LigneVenteId)
                    ?? throw new RegleMetierException("Ce produit ne fait pas partie de la vente.");
            if (lignes.Any(x => x.LigneVenteId == l.LigneVenteId)) throw new RegleMetierException($"{l.Produit} : produit indiqué deux fois.");
            RegleRetour.VerifierQuantite(l.Produit, d.Quantite, l.QuantiteVendue, l.DejaRendue);
            // Boîte remboursée : vente sur aucun bordereau (en préparation : la retirer d'abord ; déposée : refus)
            if (l.MontantOrganisme > 0) RegleRetour.VerifierPartOrganisme(v.Deposee, v.DansUnBordereau);
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
                RegleRetour.MontantPaye(l.MontantPatient, l.QuantiteVendue, d.Quantite, v.PartPatient, brutPatient), boites,
                RegleRetour.MontantOrganisme(l.MontantOrganisme, l.QuantiteVendue, d.Quantite, ratioOrganisme),
                regul is null ? 0 : RegleRetour.MontantRegularisation(l.MontantOrganisme, l.QuantiteVendue, d.Quantite, regul.Restante, partCaisseRestante)));
        }
        // Part organisme : au plus ce qui reste de la part retenue ; dernières boîtes remboursées rendues : le solde exact (arrondis),
        // l'écart porté sur la dernière ligne rendue qui a une part organisme
        var avecOrganisme = vr.Lignes.Where(l => l.MontantOrganisme > 0).ToList();
        var toutRembourseRendu = avecOrganisme.Count > 0 && lignes.Any(x => x.MontantOrganisme > 0)
            && avecOrganisme.All(l => l.DejaRendue + (lignes.FirstOrDefault(x => x.LigneVenteId == l.LigneVenteId)?.Quantite ?? 0) >= l.QuantiteVendue);
        var calculeOrganisme = lignes.Sum(l => l.MontantOrganisme);
        var totalOrganisme = ratioOrganisme == 0 ? 0
            : RegleRetour.TotalOrganisme(calculeOrganisme, organisme.PartRetenue, organisme.DejaRetiree, toutRembourseRendu);
        if (totalOrganisme != calculeOrganisme && lignes.FindLastIndex(x => x.MontantOrganisme > 0) is var dernier and >= 0)
            lignes[dernier] = lignes[dernier] with { MontantOrganisme = Math.Max(lignes[dernier].MontantOrganisme + totalOrganisme - calculeOrganisme, 0) };
        var partOrganismeRetiree = lignes.Sum(l => l.MontantOrganisme);
        // Part de la régularisation : au plus ce qui en reste ; dernières boîtes remboursées rendues : le solde exact (arrondis)
        if (regul is not null)
        {
            var dernieresRendues = avecOrganisme.Count > 0
                && avecOrganisme.All(l => l.DejaRendue + (lignes.FirstOrDefault(x => x.LigneVenteId == l.LigneVenteId)?.Quantite ?? 0) >= l.QuantiteVendue);
            var calculeRegul = lignes.Sum(l => l.MontantRegularisation);
            var totalRegul = dernieresRendues ? regul.Restante : Math.Min(calculeRegul, regul.Restante);
            var porteuse = lignes.FindLastIndex(x => vr.Lignes.First(l => l.LigneVenteId == x.LigneVenteId).MontantOrganisme > 0);
            if (totalRegul != calculeRegul && porteuse >= 0)
                lignes[porteuse] = lignes[porteuse] with { MontantRegularisation = Math.Max(lignes[porteuse].MontantRegularisation + totalRegul - calculeRegul, 0) };
        }
        var partRegularisation = lignes.Sum(l => l.MontantRegularisation);
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
        var montantPatient = lignes.Sum(l => l.Montant);
        // Tout ce que le retour rend au patient : sa part des boîtes rendues et la part de la régularisation à sa charge (script 158)
        var montant = montantPatient + partRegularisation;
        // Vente payée en partie par avoir (2026-11-15, D2, script 109) : l'avoir est recrédité d'abord, à hauteur de ce qui a été payé par
        // avoir et pas encore recrédité ; seul le reste suit le mode d'origine (espèces, carte, chèque, crédit)
        var (avoirRecredite, resteAutreMode) = v.PatientId is null ? (0m, montantPatient)
            : RegleAvoir.RetourPayeParAvoir(montantPatient, v.PayeParAvoir, v.AvoirRecrediteParRetours);
        // Vente sans patient : s'il s'est nommé au retour (patientDetteId) et l'accepte, c'est sa dette, relue ici, qui est diminuée.
        var dette = v.SoldeCreditPatient;
        long? patientDette = null;
        if (solderDette && v.Mode != "credit" && patientDetteId is { } pid)
        {
            if (v.Patient is not null) throw new RegleMetierException("Cette vente a déjà un patient : c’est sa dette qui peut être soldée.");
            var patient = (patients is null ? null : await patients.TrouverAsync(pid, ct)) ?? throw new RegleMetierException("Patient introuvable.");
            (dette, patientDette) = (patient.SoldeCredit, pid);
        }
        // Vente à crédit : la dette baisse au plus de ce qui reste impayé sur la vente ; seul ce qui a vraiment été payé est rendu
        var (reductionDette, renduPartPatient) = v.Mode == "credit"
            ? RegleAnnulation.RemboursementCredit(resteAutreMode, Math.Max(v.PartPatient - v.PayeParAvoir - (v.DejaRendu - v.AvoirRecrediteParRetours), 0), v.PayeSurVente, dette)
            : patientDette is not null ? (Math.Min(Math.Max(dette, 0), resteAutreMode), resteAutreMode - Math.Min(Math.Max(dette, 0), resteAutreMode)) : (0m, resteAutreMode);
        var deduitAuChoix = patientDette is not null && reductionDette > 0;

        // Patient de la vente qui doit encore de l'argent (décision du titulaire du 2026-11-21, script 158) : l'argent qu'il a payé et que le
        // retour lui rend n'est rendu qu'après le choix du vendeur : « déduire de ses dettes » (recommandé ; imputé comme un encaissement de
        // dette) ou « rendre quand même ». Ancienne demande « solder la dette » : vaut « déduire ». Patient inconnu : rendu normalement.
        var patientVente = v.PatientId is not null || v.Patient is not null;
        var choix = dettes ?? (solderDette && patientVente && patientDetteId is null ? RegleRetour.DettesDeduire : null);
        var rem = patientVente
            ? RegleRetour.Rembourser(reductionDette, renduPartPatient, partRegularisation, regul?.Due ?? 0, dette, true, choix)
            : new RemboursementRetour(reductionDette, 0, 0, renduPartPatient, false, 0);
        var aRendre = rem.ARendre;
        // Déduire de la dette une vente payée : jamais sans l'accord du client
        if ((deduitAuChoix || rem.ImputeSurDettes > 0) && !accordClient) throw RegleAnnulation.AccordClientManquant();

        // Le pharmacien décide : droit de l'employé, sinon validation sur place. Tout déduit de la dette avec l'accord du
        // client (rien ne sort de la caisse) : le caissier valide seul ; « rendre quand même » garde la validation
        long validePar = utilisateurId;
        string? nomValidateur = null;
        var sansArgentRendu = (deduitAuChoix || rem.ImputeSurDettes > 0) && aRendre == 0;
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

        // Argent rendu : mode de la vente ; vente à crédit, ou sans paiement (part du patient nulle) : en espèces
        var modeRendu = v.Mode is null or "credit" ? "especes" : v.Mode;
        var session = await sessions.SessionDuPosteAsync(nomPoste, ct);
        if (modeRendu == "especes" && aRendre > 0 && session is null)
            throw new RegleMetierException("Ouvrez la caisse du poste : l’argent rendu au patient doit en sortir.");

        var detteDiminuee = rem.DetteVenteDiminuee + rem.RegularisationDiminuee + rem.ImputeSurDettes;
        await retours.EnregistrerAsync(new RetourAEcrire(v.Id, v.Numero, texte, utilisateurId, validePar, v.Mode, montant, session?.Id, nomPoste, lignes,
            rem.DetteVenteDiminuee, rem.DetteVenteDiminuee > 0 ? patientDette : null, deduitAuChoix || rem.ImputeSurDettes > 0, code, precision, avoirRecredite,
            avoirRecredite > 0 ? v.PatientId : null, rem.RegularisationDiminuee, rem.ImputeSurDettes), ct);
        await journal.EcrireAsync(utilisateurId, "retour_client", "vente", v.Id,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["motif"] = texte, ["motif_code"] = code, ["montant"] = montant, ["mode"] = v.Mode,
                ["dette_diminuee"] = detteDiminuee, ["rendu"] = aRendre, ["dette_soldee_au_choix"] = deduitAuChoix || rem.ImputeSurDettes > 0,
                ["patient_dette"] = rem.DetteVenteDiminuee > 0 ? patientDette : null, ["accord_client"] = deduitAuChoix || rem.ImputeSurDettes > 0 ? true : null,
                ["avoir_recredite"] = avoirRecredite > 0 ? avoirRecredite : null,
                ["part_organisme_retiree"] = partOrganismeRetiree > 0 ? partOrganismeRetiree : null,
                ["canal_organisme"] = partOrganismeRetiree > 0 ? organisme.Canal : null,
                // Script 158 : part de la régularisation à la charge du patient rendue, sa part impayée retirée de la dette, choix du vendeur
                ["part_regularisation"] = partRegularisation > 0 ? partRegularisation : null,
                ["regularisation_dette_diminuee"] = rem.RegularisationDiminuee > 0 ? rem.RegularisationDiminuee : null,
                ["choix_dettes"] = rem.ChoixExige ? choix : null,
                ["dette_patient"] = rem.ChoixExige ? rem.DetteRestante : null,
                ["impute_sur_dettes"] = rem.ImputeSurDettes > 0 ? rem.ImputeSurDettes : null,
                ["remis_en_stock"] = lignes.Where(l => l.Destination == DestinationRetour.Stock).Sum(l => l.Quantite),
                ["a_detruire"] = lignes.Where(l => l.Destination == DestinationRetour.Destruction).Sum(l => l.Quantite),
                ["valide_par"] = validePar
            }, Permissions.CaisseRetourEspeces, ct);
        // Imputation sur les dettes : inscrite au journal comme un encaissement de dette (sans argent, origine : le retour)
        if (rem.ImputeSurDettes > 0)
            await journal.EcrireAsync(utilisateurId, "reglement_credit", "patient", v.PatientId,
                new Dictionary<string, object?>
                {
                    ["montant"] = rem.ImputeSurDettes, ["mode"] = "retour_client", ["reste_du"] = rem.DetteRestante - rem.ImputeSurDettes,
                    ["origine"] = "retour_client", ["ticket"] = v.Numero
                }, null, ct);
        return new ResultatRetour(v.Numero, aRendre, aRendre > 0 ? modeRendu : v.Mode,
            lignes.Where(l => l.Destination == DestinationRetour.Stock).Sum(l => l.Quantite),
            lignes.Where(l => l.Destination == DestinationRetour.Destruction).Sum(l => l.Quantite), nomValidateur, detteDiminuee, avoirRecredite,
            partOrganismeRetiree, organisme.Canal, organisme.FactureOrganisme, organisme.FactureChifa, rem.ImputeSurDettes, partRegularisation,
            Math.Max(Math.Max(dette, 0) - detteDiminuee, 0));
    }
}
