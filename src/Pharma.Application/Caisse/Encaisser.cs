using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Application.Securite;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

/// <summary>Boites : boîtes identifiées scannées pour cette ligne (étiquette interne ou n° de série), au plus la quantité.</summary>
/// <param name="RaisonLot">Lot plus récent servi alors qu'un lot plus ancien est en stock : raison choisie ou validée (Domain.Ventes.RaisonLot).</param>
public sealed record LigneCommande(long ProduitId, long LotId, int Quantite, IReadOnlyList<long>? Boites = null, string? RaisonLot = null);

/// <summary>Raison à demander pour le lot d'une ligne (Domain.Ventes.SituationLot) et lot plus ancien en stock.</summary>
public sealed record SituationLotVente(long LotId, string Situation, string? NumeroLot = null, long? LotDelaisseId = null, string? LotDelaisse = null,
    DateOnly? PeremptionDelaisse = null);
/// <summary>
/// Ordonnance présentée au comptoir : saisie pour un psychotrope (médecin obligatoire) ou scannée
/// (image conservée sur le serveur, désignée par son jeton).
/// </summary>
public sealed record OrdonnanceSaisie(long? MedecinId, DateOnly DatePrescription, string? Numero,
    string? JetonScan = null, string? TexteReconnu = null, PorteurOrdonnance? Porteur = null);

/// <summary>Porteur de l'ordonnance quand ce n'est pas le patient : inscrit au registre des psychotropes.</summary>
public sealed record PorteurOrdonnance(string? Nom, string? Adresse, string? PieceIdentite);

/// <summary>Panier envoyé par le poste, avec le règlement choisi.</summary>
/// <param name="CleEncaissement">
/// Clé du panier créée par le poste (renouvelée après une vente réussie, un panier vidé ou mis en attente) : un nouvel essai du
/// même encaissement rend la vente déjà enregistrée au lieu d'en créer une seconde.
/// </param>
public sealed record CommandeVente(
    IReadOnlyList<LigneCommande> Lignes,
    bool TiersPayant, long? OrganismeId, decimal? TauxTiersPayant,
    long? PatientId, OrdonnanceSaisie? Ordonnance,
    decimal RemisePct, ModePaiement Mode, decimal? MontantRecu, decimal? ResteACharge = null, Catalogue.ValidationBlocage? Blocage = null,
    string? VoletChifa = null, bool RemiseAdditionnee = false, Guid? CleEncaissement = null);

/// <summary>Réductions d'une ligne (ordre des lignes envoyées) : remise automatique, pourcentage appliqué, montant ; DonPartiel pour un médicament.</summary>
public sealed record ReductionLigne(decimal RemiseAutoPct, decimal PctApplique, decimal Montant, bool DonPartiel);

/// <summary>Remises automatiques en cours (décidées par le titulaire) : pourcentage par produit, celui du produit l'emportant sur sa catégorie.</summary>
public interface IRemisesAutomatiques
{
    Task<IReadOnlyDictionary<long, decimal>> PourcentagesAsync(IReadOnlyCollection<long> produitIds, DateOnly jour, CancellationToken ct = default);
}

/// <summary>PrixLignes : prix de vente d'une boîte (PPA du lot + SHP), dans l'ordre des lignes envoyées.</summary>
public sealed record ApercuVente(
    decimal MontantBrut, decimal PartOrganisme, decimal MontantRemise, decimal PartPatient,
    decimal MontantTotal, bool OrdonnanceRequise, IReadOnlyList<decimal>? PrixLignes = null, decimal Shp = 0, decimal TotalMedicaments = 0,
    IReadOnlyList<string>? ProduitsBloques = null, bool SurOrdonnance = false, IReadOnlyList<SituationLotVente>? Situations = null,
    decimal MontantDon = 0, decimal ReductionsAuto = 0, IReadOnlyList<ReductionLigne>? Reductions = null);

public sealed record VenteEncaissee(
    long VenteId, string Numero, decimal MontantTotal, decimal PartOrganisme, decimal PartPatient,
    ModePaiement Mode, decimal Rendu);

/// <summary>Ce que l'adaptateur enregistre en une transaction.</summary>
/// <summary>BoitesParLot : boîtes identifiées vendues, rattachées à la ligne de leur lot (pour les retrouver au remboursement).</summary>
public sealed record VenteAEnregistrer(
    Vente Vente, Reglement Reglement, OrdonnanceSaisie? Ordonnance,
    IReadOnlySet<long> ProduitsSousRegistre, bool CreditAccorde, IReadOnlyDictionary<long, IReadOnlyList<long>>? BoitesParLot = null,
    Pharma.Domain.Ventes.QrChifa? VoletChifa = null, Guid? CleEncaissement = null);

/// <summary>
/// Vente déjà enregistrée avec une clé d'encaissement. Mode : celui du paiement enregistré (null si le patient n'avait rien à payer) ;
/// Annulee : la vente a été annulée depuis. Lignes : produits, lots et quantités enregistrés (null : non relus, pas comparés).
/// </summary>
public sealed record VenteDejaEncaissee(long VenteId, string Numero, decimal MontantTotal, decimal PartOrganisme, decimal PartPatient,
    ModePaiement? Mode, bool Annulee, IReadOnlyList<LigneDejaEncaissee>? Lignes = null);

/// <summary>Ligne d'une vente déjà enregistrée avec une clé d'encaissement.</summary>
public sealed record LigneDejaEncaissee(long ProduitId, long LotId, int Quantite);

/// <summary>
/// Un autre encaissement du même panier (même clé) vient d'enregistrer la vente : l'adaptateur a tout annulé de son côté,
/// l'encaissement rend la vente déjà enregistrée.
/// </summary>
public sealed class EncaissementDejaEnregistreException(Guid cle)
    : Exception("Ce panier vient d’être encaissé par un autre envoi du poste.")
{
    public Guid Cle { get; } = cle;
}

public interface IPreparationVente
{
    Task<IReadOnlyDictionary<long, Produit>> ProduitsAsync(IEnumerable<long> ids, CancellationToken ct = default);
    Task<IReadOnlyDictionary<long, Lot>> LotsAsync(IEnumerable<long> ids, CancellationToken ct = default);
    /// <summary>Pour chaque lot : raison à demander si un lot plus ancien du produit est en stock (fonction SQL situation_lot).</summary>
    Task<IReadOnlyDictionary<long, SituationLotVente>> SituationsAsync(IEnumerable<long> lotIds, CancellationToken ct = default);
}

public interface IVentesEcriture
{
    /// <summary>
    /// Vente, lignes, stock, mouvements, paiement, ordonnance, registre des psychotropes, crédit et journal,
    /// en une seule transaction. Renvoie l'identifiant et le numéro de ticket. Clé d'encaissement déjà prise par une vente
    /// enregistrée entre-temps : <see cref="EncaissementDejaEnregistreException"/>, rien n'est enregistré.
    /// </summary>
    Task<(long Id, string Numero)> EnregistrerAsync(VenteAEnregistrer vente, CancellationToken ct = default);

    /// <summary>Vente enregistrée avec cette clé d'encaissement, ou null.</summary>
    Task<VenteDejaEncaissee?> VenteParCleAsync(Guid cle, CancellationToken ct = default);
}

public sealed class Encaisser(
    IPreparationVente preparation, IPatients patients, IVentesEcriture ventes,
    ISessionsCaisse sessions, IPermissionService droits, IHorloge horloge, Ordonnances.IStockageOrdonnances scans,
    IDetenteursDroit? detenteurs = null, Stock.ICampagnesInventaire? campagnes = null,
    VerificationCodeComptoir? codesComptoir = null, IJournal? journal = null,
    Alertes.IAlertesLotsDelaisses? lotsDelaisses = null, Notifications.IAlertesTitulaire? alertesTitulaire = null,
    Surveillance.Surveiller? surveillance = null, IRemisesAutomatiques? remisesAuto = null, ITraceErreurs? trace = null)
{
    /// <summary>Totaux recalculés par le serveur (mêmes règles que l'encaissement), sans rien enregistrer.</summary>
    public async Task<ApercuVente> CalculerAsync(CommandeVente commande, long utilisateurId, CancellationToken ct = default)
    {
        var (vente, produits) = await ConstruireAsync(commande, utilisateurId, sessionId: null, ct);
        var situations = await RaisonsAsync(commande, vente, produits, exiger: false, ct);
        return new ApercuVente(
            vente.MontantTotal + vente.MontantRemise + vente.MontantDon, vente.PartOrganisme, vente.MontantRemise,
            vente.PartPatient, vente.MontantTotal, vente.ContientSousRegistre,
            vente.Lignes.Select(l => l.PrixUnitaire).ToList(), vente.Lignes.Sum(l => l.Shp * l.Quantite),
            vente.Lignes.Where(l => produits.TryGetValue(l.ProduitId, out var p) && p.EstMedicament).Sum(l => l.PrixUnitaire * l.Quantite),
            Bloques(produits), vente.ContientSurOrdonnance,
            commande.Lignes.Select(l => situations.GetValueOrDefault(l.LotId) ?? new SituationLotVente(l.LotId, SituationLot.Aucune)).ToList(),
            vente.MontantDon, vente.MontantReductionsAuto,
            vente.Lignes.Select(l => new ReductionLigne(l.RemiseAutoPct, l.PctApplique, l.Reduction, l.Medicament && l.Reduction > 0)).ToList());
    }

    /// <summary>
    /// Remise du vendeur sur des produits hors médicaments : dans son maximum, sinon validée sur place par le code d'un employé dont le
    /// maximum la couvre. Sur des médicaments, c'est un don partiel du reste à charge : droit de don, sinon code d'un employé qui l'a.
    /// Les remises automatiques, décidées par le titulaire, ne demandent rien.
    /// </summary>
    private async Task ExigerReductionsAsync(Vente vente, long utilisateurId, CancellationToken ct)
    {
        if (vente.RemiseManuelleSurProduits && !await droits.RemiseAutoriseeAsync(utilisateurId, vente.RemisePct, ct))
        {
            var max = await droits.RemiseMaxAsync(utilisateurId, ct);
            throw new DroitRefuseException(Permissions.CaisseRemise, "accorder une remise", max <= 0
                ? $"Remise de {vente.RemisePct:0.##} % : vous n’avez pas le droit d’accorder une remise. Faites valider par un employé qui l’a."
                : $"Remise de {vente.RemisePct:0.##} % : votre maximum est de {max:0.##} %. Faites valider par un employé dont le maximum le permet.",
                vente.RemisePct);   // la fenêtre « Faire autoriser » ne propose que les employés dont le maximum couvre cette remise
        }
        if (vente.DonManuelSurMedicaments && !await droits.APermissionAsync(utilisateurId, Permissions.StockDonner, ct))
            throw new DroitRefuseException(Permissions.StockDonner, "faire un don partiel du reste à charge sur un médicament",
                $"Remise de {vente.RemisePct:0.##} % sur un médicament : c’est un don partiel du reste à charge, et vous n’avez pas le droit de don. Faites valider par un employé qui l’a.");
    }

    /// <summary>
    /// Lot plus récent servi alors qu'un lot plus ancien est en stock : raison choisie par le vendeur, ou « péremption plus proche »
    /// vérifiée par le logiciel et validée par le vendeur. Exigée à l'encaissement (exiger) ; reportée sur les lignes de la vente.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, SituationLotVente>> RaisonsAsync(CommandeVente c, Vente vente,
        IReadOnlyDictionary<long, Produit> produits, bool exiger, CancellationToken ct)
    {
        var situations = await preparation.SituationsAsync(c.Lignes.Select(l => l.LotId).Distinct(), ct);
        foreach (var ligne in vente.Lignes)
        {
            if (situations.GetValueOrDefault(ligne.LotId) is not { } s) continue;
            var choix = c.Lignes.Where(l => l.LotId == ligne.LotId).Select(l => l.RaisonLot).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
            var produit = produits.TryGetValue(ligne.ProduitId, out var p) ? p.NomCommercial : "Produit";
            var lot = s.NumeroLot ?? ligne.LotId.ToString();
            ligne.RaisonLot = SituationLot.Raison(s.Situation, choix, produit, lot, s.LotDelaisse, exiger);
        }
        return situations;
    }

    /// <summary>Boîtes identifiées par lot : jamais deux fois la même, au plus autant que de boîtes vendues du lot.</summary>
    private static Dictionary<long, IReadOnlyList<long>>? BoitesParLot(CommandeVente commande)
    {
        var parLot = commande.Lignes.Where(l => l.Boites is { Count: > 0 }).GroupBy(l => l.LotId).ToDictionary(
            g => g.Key, g => (IReadOnlyList<long>)g.SelectMany(l => l.Boites!).ToList());
        foreach (var (lot, boites) in parLot)
        {
            if (boites.Distinct().Count() != boites.Count) throw new RegleMetierException("Une même boîte a été scannée deux fois.");
            if (boites.Count > commande.Lignes.Where(l => l.LotId == lot).Sum(l => l.Quantite))
                throw new RegleMetierException("Plus de boîtes scannées que de boîtes vendues sur une ligne : corrigez la quantité.");
        }
        return parLot.Count == 0 ? null : parLot;
    }

    /// <summary>Produits du panier bloqués à la vente, avec leur motif (à faire valider par une personne habilitée).</summary>
    private static List<string> Bloques(IReadOnlyDictionary<long, Produit> produits) =>
        produits.Values.Where(p => p.BloqueVente).Select(p => p.MotifBlocage is { } m ? $"{p.NomCommercial} ({m})" : p.NomCommercial).ToList();

    /// <summary>Nomme les employés qui peuvent délivrer : l'un d'eux reprend la vente (Changer d'utilisateur, par son code).</summary>
    private async Task<string> MessagePsychotropeAsync(CancellationToken ct)
    {
        var liste = detenteurs is null ? [] : await detenteurs.ListerAsync(Permissions.PsychotropesDelivrer, ct);
        if (liste.Count == 0)
            return "Psychotrope ou stupéfiant : aucun employé actif n’a le droit de le délivrer. Le titulaire peut l’accorder dans Utilisateurs et droits.";
        var noms = string.Join(", ", liste.Select(d => d.CodeComptoir ? d.NomComplet + " (code comptoir)" : d.NomComplet));
        return $"Psychotrope ou stupéfiant : délivrance réservée à {noms}. Demandez à l’une de ces personnes de reprendre la vente (« Changer d’utilisateur »).";
    }

    /// <summary>La vente est enregistrée sur la caisse du poste, au nom de l'employé connecté.</summary>
    public async Task<VenteEncaissee> ExecuterAsync(CommandeVente commande, long utilisateurId, string? poste, CancellationToken ct = default)
    {
        // Même panier renvoyé par le poste (réponse arrivée après son délai d'attente, nouvel essai) : la vente déjà enregistrée,
        // jamais une seconde
        if (commande.CleEncaissement is { } cleDeja && await DejaEncaisseeAsync(cleDeja, commande, ct) is { } deja)
            return deja;

        var session = await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct)
                      ?? throw new RegleMetierException("Ouvrez la caisse du poste avant d’encaisser.");

        await Stock.VentesSuspendues.VerifierAsync(campagnes, ct);
        var (vente, produits) = await ConstruireAsync(commande, utilisateurId, session.Id, ct);
        // Volet Chifa repris par son QR : relu par le serveur, en tiers payant, une seule fois (index unique en base)
        Pharma.Domain.Ventes.QrChifa? volet = null;
        if (!string.IsNullOrWhiteSpace(commande.VoletChifa))
        {
            volet = LectureVoletChifa.Lire(commande.VoletChifa) ?? throw new RegleMetierException("Le QR du volet Chifa n’est pas reconnu.", "volet_chifa");
            if (!commande.TiersPayant) throw new RegleMetierException("Un volet Chifa se reprend en tiers payant.", "volet_chifa");
        }
        // Lot plus récent servi alors qu'un lot plus ancien est en stock : raison obligatoire (ou péremption vérifiée, validée)
        await RaisonsAsync(commande, vente, produits, exiger: true, ct);

        if (vente.ContientSousRegistre && !await droits.APermissionAsync(utilisateurId, Permissions.PsychotropesDelivrer, ct))
            throw new DroitRefuseException(Permissions.PsychotropesDelivrer, "délivrer un psychotrope", await MessagePsychotropeAsync(ct));
        await ExigerReductionsAsync(vente, utilisateurId, ct);
        // Produit bloqué à la vente : validé par l'employé s'il en a le droit, sinon par le code d'un pharmacien ou assistant
        var bloques = Bloques(produits);
        var validePar = await Catalogue.AutorisationBlocage.ExigerAsync(droits, codesComptoir, utilisateurId, commande.Blocage, poste, "à la vente", bloques, ct);

        if (commande.Ordonnance is { } o)
        {
            // Une ordonnance est toujours rattachée à un patient (ordonnance.patient_id obligatoire)
            if (commande.PatientId is null)
                throw new RegleMetierException("Ordonnance : choisissez le patient à qui elle est prescrite.");
            if (vente.ContientSousRegistre && o.MedecinId is null)
                throw new RegleMetierException("Psychotrope ou stupéfiant : indiquez le médecin prescripteur.");
            if (o.JetonScan is { } jeton && !scans.Existe(jeton))
                throw new RegleMetierException("Image de l’ordonnance introuvable sur le serveur : scannez-la de nouveau.");
            if (o.DatePrescription > horloge.Aujourdhui)
                throw new RegleMetierException("La date de prescription ne peut pas être dans le futur.");
            // Porteur différent du patient : nom, adresse et pièce d'identité au registre (psychotropes seulement)
            commande = commande with { Ordonnance = o with { Porteur = vente.ContientSousRegistre && o.Porteur is { } p ? VerifierPorteur(p) : null } };
        }
        vente.VerifierAvantEncaissement(commande.Ordonnance is not null);
        // Liste I ou II sans ordonnance : droit « vendre sans ordonnance », sinon autorisation par le code d'un employé qui l'a.
        // Psychotrope ou stupéfiant : l'ordonnance reste obligatoire pour tous (contrôlé ci-dessus)
        var sansOrdonnance = produits.Values.Where(p => p.EstSurOrdonnance).Select(p => p.NomCommercial).ToList();
        if (commande.Ordonnance is { } ord && (ord.MedecinId is not null || ord.JetonScan is not null)) sansOrdonnance.Clear();
        if (sansOrdonnance.Count > 0)
            await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendreSansOrdonnance, "vendre sans ordonnance un médicament de liste I ou II", ct);

        Reglement reglement;
        var credit = false;
        if (commande.Mode == ModePaiement.Credit)
        {
            await droits.ExigerAsync(utilisateurId, Permissions.CreditAccorder, "mettre une vente en crédit", ct);
            var patient = commande.PatientId is { } pid ? await patients.TrouverAsync(pid, ct) : null;
            if (patient is null)
                throw new RegleMetierException("Crédit : choisissez le patient à qui la vente est accordée.");
            var depasser = await droits.APermissionAsync(utilisateurId, Permissions.CreditDepasserPlafond, ct);
            // Au-delà du plafond : droit « dépasser le plafond », que le code d'un employé habilité peut accorder sur place
            if (vente.PartPatient > 0 && !depasser && patient.SoldeCredit + vente.PartPatient > patient.PlafondCredit)
                throw new DroitRefuseException(Permissions.CreditDepasserPlafond, "dépasser le plafond de crédit",
                    $"Plafond de crédit dépassé : {patient.SoldeCredit + vente.PartPatient:N2} DA pour un plafond de {patient.PlafondCredit:N2} DA.");
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
        long id;
        string numero;
        try
        {
            (id, numero) = await ventes.EnregistrerAsync(
                new VenteAEnregistrer(vente, reglement, commande.Ordonnance, sousRegistre, credit, BoitesParLot(commande), volet,
                    commande.CleEncaissement), ct);
        }
        catch (EncaissementDejaEnregistreException e)
        {
            // Deux envois simultanés du même panier : l'autre a enregistré la vente, celui-ci la rend
            return await DejaEncaisseeAsync(e.Cle, commande, ct)
                   ?? throw new RegleMetierException("Ce panier vient d’être encaissé par un autre envoi : vérifiez les dernières ventes avant de réessayer.");
        }

        if (validePar is { } vp && journal is not null)
            await journal.EcrireAsync(utilisateurId, "vente_produit_bloque", "vente", id,
                new Dictionary<string, object?> { ["ticket"] = numero, ["produits"] = bloques, ["valide_par"] = vp.Id, ["validateur"] = vp.NomComplet },
                Permissions.ProduitsBloquer, ct);

        if (sansOrdonnance.Count > 0 && journal is not null)
            await journal.EcrireAsync(utilisateurId, "vente_sans_ordonnance", "vente", id,
                new Dictionary<string, object?> { ["ticket"] = numero, ["produits"] = sansOrdonnance },
                Permissions.CaisseVendreSansOrdonnance, ct);

        await AlerterLotsDelaissesAsync(vente);
        // Surveillance de la caisse : ventes hors horaires, remises en série (anomalies notifiées au titulaire)
        if (surveillance is not null) await surveillance.ExecuterAsync(CancellationToken.None);

        return new VenteEncaissee(id, numero, vente.MontantTotal, vente.PartOrganisme, vente.PartPatient,
            reglement.Mode, reglement.Rendu);
    }

    /// <summary>
    /// Vente déjà enregistrée avec cette clé : rendue telle quelle (monnaie recalculée sur le montant reçu renvoyé par le poste).
    /// Annulée depuis : refus clair, le caissier recommence avec un nouveau panier.
    /// </summary>
    private async Task<VenteEncaissee?> DejaEncaisseeAsync(Guid cle, CommandeVente commande, CancellationToken ct)
    {
        if (await ventes.VenteParCleAsync(cle, ct) is not { } v) return null;
        if (v.Annulee)
            throw new RegleMetierException($"Ce panier a déjà été encaissé (ticket {v.Numero}) puis la vente a été annulée : videz le panier et recommencez.");
        if (!await MemeContenuAsync(v, commande, ct))
            throw new RegleMetierException(
                $"Cette vente a déjà été enregistrée (ticket N° {v.Numero}) avec un contenu différent : vérifiez avant de réencaisser.");
        var mode = v.Mode ?? commande.Mode;
        var rendu = mode == ModePaiement.Especes && commande.MontantRecu is { } recu && recu > v.PartPatient ? recu - v.PartPatient : 0;
        return new VenteEncaissee(v.VenteId, v.Numero, v.MontantTotal, v.PartOrganisme, v.PartPatient, mode, rendu);
    }

    /// <summary>
    /// Le panier renvoyé avec la clé est-il celui de la vente enregistrée ? Mêmes produits, lots et quantités, et même montant à payer
    /// par le patient, recalculé sans contrôler le stock (déjà sorti par la vente enregistrée). Montant impossible à recalculer
    /// (produit retiré entre-temps…) : seules les lignes sont comparées.
    /// </summary>
    private async Task<bool> MemeContenuAsync(VenteDejaEncaissee v, CommandeVente commande, CancellationToken ct)
    {
        if (v.Lignes is { } enregistrees)
        {
            static Dictionary<(long, long), int> ParLot(IEnumerable<(long Produit, long Lot, int Quantite)> lignes) =>
                lignes.GroupBy(l => (l.Produit, l.Lot)).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantite));
            var avant = ParLot(enregistrees.Select(l => (l.ProduitId, l.LotId, l.Quantite)));
            var envoye = ParLot(commande.Lignes.Select(l => (l.ProduitId, l.LotId, l.Quantite)));
            if (avant.Count != envoye.Count || avant.Any(a => envoye.GetValueOrDefault(a.Key) != a.Value)) return false;
        }
        Vente recalculee;
        try
        {
            (recalculee, _) = await ConstruireAsync(commande, 0, sessionId: null, ct, controlerLots: false);
        }
        catch (RegleMetierException)
        {
            return true;
        }
        return recalculee.PartPatient == v.PartPatient;
    }

    /// <summary>
    /// Après la vente : ancien lot délaissé au profit d'un lot plus récent (seuil atteint par un vendeur) ; alerte au titulaire, prévenu
    /// par SMS ou e-mail. La vente est enregistrée : un échec ici ne l'annule pas ; il est inscrit au journal du serveur (et l'alerte
    /// qui n'a pas pu partir est signalée au titulaire par le bandeau de l'accueil).
    /// </summary>
    private Task AlerterLotsDelaissesAsync(Vente vente)
    {
        if (lotsDelaisses is null) return Task.CompletedTask;
        return Notifications.AlerteNonEnvoyeeSignalee.SansBlocageAsync(async () =>
        {
            foreach (var a in await lotsDelaisses.AlerterAsync(vente.Lignes.Select(l => l.ProduitId).Distinct().ToList(), CancellationToken.None))
                if (alertesTitulaire is not null) await alertesTitulaire.LotDelaisseAsync(a.Message, a.Niveau == "urgent", CancellationToken.None);
        }, "lots délaissés après une vente", trace);
    }

    private async Task<(Vente, IReadOnlyDictionary<long, Produit>)> ConstruireAsync(
        CommandeVente c, long utilisateurId, long? sessionId, CancellationToken ct, bool controlerLots = true)
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
            // Repris de l'ancien logiciel sans n° d'enregistrement, ou psychotrope (stupéfiant) créé par un employé :
            // sa classe n'a pas été confirmée par le titulaire
            if (produit.AVerifier)
                throw new RegleMetierException(
                    $"{produit.NomCommercial} : produit à vérifier par le titulaire avant la vente (repris de l’ancien logiciel, ou classé d’office psychotrope ou stupéfiant à sa création) : Administration, Nomenclature nationale, « Produits à vérifier ».");
            if (!lots.TryGetValue(ligne.LotId, out var lot))
                throw new RegleMetierException($"{produit.NomCommercial} : lot introuvable, rescannez la boîte.");
            vente.AjouterProduit(produit, lot, ligne.Quantite, aujourdhui, autoriserBloque: true, controlerLot: controlerLots);   // blocage contrôlé à l'encaissement
        }

        // Tiers payant : reste à charge fixé par le caissier, au lieu du taux de prise en charge
        if (c.TiersPayant && c.ResteACharge is { } reste)
            vente.SaisirResteACharge(reste);

        // Remises automatiques du jour (vente libre, produits non remboursables), puis remise du vendeur
        if (remisesAuto is not null && vente.Type == TypeVente.Libre)
            vente.AppliquerRemisesAutomatiques(await remisesAuto.PourcentagesAsync(produits.Keys.ToList(), aujourdhui, ct));
        vente.AppliquerRemise(c.RemisePct, c.RemiseAdditionnee);

        return (vente, produits);
    }

    /// <summary>Les trois informations sont exigées dès qu'un porteur est déclaré (arrêté du 29 juin 2013, art. 7).</summary>
    public static PorteurOrdonnance VerifierPorteur(PorteurOrdonnance p)
    {
        static string Requis(string? v, string champ) => string.IsNullOrWhiteSpace(v)
            ? throw new RegleMetierException($"Porteur de l’ordonnance : {champ} obligatoire.")
            : v.Trim().Length > 200 ? throw new RegleMetierException($"Porteur de l’ordonnance : {champ} trop long (200 caractères au plus).")
            : v.Trim();
        return new PorteurOrdonnance(Requis(p.Nom, "nom et prénom"), Requis(p.Adresse, "adresse"), Requis(p.PieceIdentite, "pièce d’identité"));
    }
}
