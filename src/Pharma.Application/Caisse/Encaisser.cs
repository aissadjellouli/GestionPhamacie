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
/// <param name="TypePrestationId">
/// Prestation (script 105) : ligne sans produit ni lot (ProduitId et LotId null, ni boîte ni raison de lot) ; sinon null et ProduitId + LotId
/// obligatoires. Exactement l'un des deux (<see cref="LigneCommandeValide"/>).
/// </param>
public sealed record LigneCommande(long? ProduitId, long? LotId, int Quantite, IReadOnlyList<long>? Boites = null, string? RaisonLot = null,
    long? TypePrestationId = null)
{
    public bool EstPrestation => TypePrestationId is not null;
}

/// <summary>Une ligne du panier est soit un produit et son lot, soit une prestation (jamais les deux, jamais un produit sans lot).</summary>
public static class LigneCommandeValide
{
    public static void Verifier(LigneCommande l)
    {
        var produit = l.ProduitId is not null && l.LotId is not null && l.TypePrestationId is null;
        var prestation = l.TypePrestationId is not null && l.ProduitId is null && l.LotId is null
                         && l.Boites is not { Count: > 0 } && string.IsNullOrWhiteSpace(l.RaisonLot);
        if (!produit && !prestation)
            throw new RegleMetierException("Ligne du panier invalide : un produit et son lot, ou une prestation.", "lignes");
    }
}

/// <summary>Raison à demander pour le lot d'une ligne (Domain.Ventes.SituationLot) et lot plus ancien en stock.</summary>
public sealed record SituationLotVente(long LotId, string Situation, string? NumeroLot = null, long? LotDelaisseId = null, string? LotDelaisse = null,
    DateOnly? PeremptionDelaisse = null);
/// <summary>
/// Ordonnance présentée au comptoir : saisie pour un psychotrope (médecin obligatoire) ou scannée
/// (image conservée sur le serveur, désignée par son jeton).
/// </summary>
public sealed record OrdonnanceSaisie(long? MedecinId, DateOnly DatePrescription, string? Numero,
    string? JetonScan = null, string? TexteReconnu = null, PorteurOrdonnance? Porteur = null, int? DureeTraitementJours = null)
{
    /// <summary>Durée du traitement (jours), facultative : psychotrope ou stupéfiant seulement, inscrite au registre (2026-11-16).</summary>
    public const int DureeMax = 3650;
}

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
    string? VoletChifa = null, bool RemiseAdditionnee = false, Guid? CleEncaissement = null,
    IReadOnlyList<ChoixPrixChifa>? ChoixChifa = null, bool FactureeParChifa = false, string? NumeroFactureChifa = null,
    PersonneVente? RemisA = null, PersonneVente? Pour = null, string? MotifSansVoletCode = null, string? MotifSansVoletTexte = null,
    decimal MontantAvoir = 0);

/// <summary>
/// Volet Chifa, écart de prix d'au moins le seuil : choix du vendeur pour un produit (« aligne » : prix Chifa ; « garde » : notre prix,
/// écart payé par le patient). PrixAttendu : prix unitaire montré au poste, revérifié par le serveur (refus s'il n'est pas conforme).
/// </summary>
public sealed record ChoixPrixChifa(long ProduitId, string Choix, decimal? PrixAttendu = null);

/// <summary>Ligne du panier (ordre des lignes envoyées) reprise d'un volet Chifa dont le prix diffère du nôtre.</summary>
/// <param name="AChoisir">Écart d'au moins le seuil : à choisir par un employé qui peut modifier les prix (sinon le plus élevé d'office).</param>
public sealed record EcartChifaLigne(int Index, long ProduitId, string Produit, decimal PrixChifa, decimal NotrePrix, decimal PrixApplique,
    string Choix, bool AChoisir);

/// <summary>
/// Médecin en tiers payant (2026-11-15) : obligatoire quand l'organisme paie une partie (au taux, ou reste à charge saisi inférieur au
/// total) ; quand il ne paie rien (part 0 : le patient paie tout), facultatif, mais une vente sans ordonnance se fait avec le droit
/// « vendre sans ordonnance » ou le code du titulaire ou d'un ayant droit. Volet Chifa scanné : facultatif (montants de Chifa).
/// </summary>
public static class RegleMedecinTiersPayant
{
    public static bool Exige(bool tiersPayant, decimal partOrganisme, bool voletChifa) => tiersPayant && !voletChifa && partOrganisme > 0;
    public static bool SansPartOrganisme(bool tiersPayant, decimal partOrganisme, bool voletChifa) => tiersPayant && !voletChifa && partOrganisme <= 0;
}

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
    decimal MontantDon = 0, decimal ReductionsAuto = 0, IReadOnlyList<ReductionLigne>? Reductions = null,
    IReadOnlyList<EcartChifaLigne>? EcartsChifa = null, decimal EcartChifaPatient = 0, decimal SeuilEcartChifa = 0);

/// <summary>EcartChifaPatient : écart Chifa payé par le patient (notre prix au-dessus du prix Chifa), compris dans PartPatient.</summary>
public sealed record VenteEncaissee(
    long VenteId, string Numero, decimal MontantTotal, decimal PartOrganisme, decimal PartPatient,
    ModePaiement Mode, decimal Rendu, decimal EcartChifaPatient = 0, string? CatalogueChifa = null, decimal PayeParAvoir = 0);

/// <summary>Ce que l'adaptateur enregistre en une transaction.</summary>
/// <summary>BoitesParLot : boîtes identifiées vendues, rattachées à la ligne de leur lot (pour les retrouver au remboursement).</summary>
public sealed record VenteAEnregistrer(
    Vente Vente, Reglement Reglement, OrdonnanceSaisie? Ordonnance,
    IReadOnlySet<long> ProduitsSousRegistre, bool CreditAccorde, IReadOnlyDictionary<long, IReadOnlyList<long>>? BoitesParLot = null,
    Pharma.Domain.Ventes.QrChifa? VoletChifa = null, Guid? CleEncaissement = null, bool PeutDepasserPlafond = false,
    string CanalOrganisme = Caisse.CanalOrganisme.Logiciel, string? FactureChifa = null,
    PersonneVente? RemisA = null, PersonneVente? Pour = null, string? MotifSansVoletCode = null, string? MotifSansVoletTexte = null);

/// <summary>
/// Plafond de crédit revérifié dans la transaction d'encaissement (patient verrouillé, solde relu) : deux ventes à crédit simultanées
/// ne peuvent pas le dépasser ensemble. Vrai si la dette après la vente reste dans le plafond, ou si l'employé peut le dépasser.
/// </summary>
public static class PlafondEncaissement
{
    public static bool Respecte(decimal soldeRelu, decimal montant, decimal plafond, bool peutDepasser) =>
        peutDepasser || montant <= 0 || soldeRelu + montant <= plafond;

    public static string Message(decimal soldeRelu, decimal montant, decimal plafond) =>
        $"Plafond de crédit dépassé : {soldeRelu + montant:N2} DA pour un plafond de {plafond:N2} DA (une autre vente à crédit vient d’être enregistrée pour ce patient).";
}

/// <summary>
/// Vente déjà enregistrée avec une clé d'encaissement. Mode : celui du paiement enregistré (null si le patient n'avait rien à payer) ;
/// Annulee : la vente a été annulée depuis. Lignes : produits, lots et quantités enregistrés (null : non relus, pas comparés).
/// PayeParAvoir : part payée par l'avoir du patient (paiements « avoir » de la vente).
/// </summary>
public sealed record VenteDejaEncaissee(long VenteId, string Numero, decimal MontantTotal, decimal PartOrganisme, decimal PartPatient,
    ModePaiement? Mode, bool Annulee, IReadOnlyList<LigneDejaEncaissee>? Lignes = null, decimal PayeParAvoir = 0);

/// <summary>Ligne d'une vente déjà enregistrée avec une clé d'encaissement.</summary>
/// <summary>Ligne d'une vente déjà enregistrée : produit et lot, ou prestation (TypePrestationId, script 105).</summary>
public sealed record LigneDejaEncaissee(long? ProduitId, long? LotId, int Quantite, long? TypePrestationId = null);

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
    /// <summary>Types de prestation (script 105), actifs ou non (le contrôle est fait par la vente).</summary>
    Task<IReadOnlyDictionary<long, TypePrestation>> PrestationsAsync(IEnumerable<long> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<long, TypePrestation>>(new Dictionary<long, TypePrestation>());
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
    Surveillance.Surveiller? surveillance = null, IRemisesAutomatiques? remisesAuto = null, ITraceErreurs? trace = null,
    IVoletsChifa? voletsChifa = null, Securite.AutorisationCourante? autorisation = null,
    Referentiels.IMotifsReglables? motifs = null, IAvoirs? avoirs = null,
    Psychotropes.ControleAlertesPsychotropes? alertesPsychotropes = null)
{
    /// <summary>Totaux recalculés par le serveur (mêmes règles que l'encaissement), sans rien enregistrer.</summary>
    public async Task<ApercuVente> CalculerAsync(CommandeVente commande, long utilisateurId, CancellationToken ct = default)
    {
        var (vente, produits, ecarts, seuil) = await ConstruireAsync(commande, utilisateurId, sessionId: null, ct);
        var situations = await RaisonsAsync(commande, vente, produits, exiger: false, ct);
        return new ApercuVente(
            vente.MontantTotal + vente.MontantRemise + vente.MontantDon, vente.PartOrganisme, vente.MontantRemise,
            vente.PartPatient, vente.MontantTotal, vente.ContientSousRegistre,
            vente.Lignes.Select(l => l.PrixUnitaire).ToList(), vente.Lignes.Sum(l => l.Shp * l.Quantite),
            vente.Lignes.Where(l => l.ProduitId is { } pid && produits.TryGetValue(pid, out var p) && p.EstMedicament).Sum(l => l.PrixUnitaire * l.Quantite),
            Bloques(produits), vente.ContientSurOrdonnance,
            // Prestation (sans lot) : aucune raison de lot à demander
            commande.Lignes.Select(l => l.LotId is { } lid
                ? situations.GetValueOrDefault(lid) ?? new SituationLotVente(lid, SituationLot.Aucune)
                : new SituationLotVente(0, SituationLot.Aucune)).ToList(),
            vente.MontantDon, vente.MontantReductionsAuto,
            vente.Lignes.Select(l => new ReductionLigne(l.RemiseAutoPct, l.PctApplique, l.Reduction, l.Medicament && l.Reduction > 0)).ToList(),
            ecarts, vente.EcartChifaPatient, seuil);
    }

    /// <summary>
    /// Volet Chifa (tiers payant) : chaque ligne d'un produit du volet prend le prix Chifa ou le nôtre (EcartPrixChifa : seuil de
    /// Administration › Caisse, droit prix.modifier, sinon le plus élevé d'office) ; la part de l'organisme se calcule sur le prix Chifa.
    /// exiger : à l'encaissement, un choix non autorisé ou manquant, ou un prix attendu non conforme, est refusé. droitPrix : imposé
    /// (vente déjà enregistrée, recalculée pour comparaison) au lieu d'être vérifié.
    /// </summary>
    private async Task<(List<EcartChifaLigne> Ecarts, decimal Seuil)> AppliquerVoletChifaAsync(CommandeVente c, Vente vente,
        IReadOnlyDictionary<long, Produit> produits, long utilisateurId, bool exiger, bool? droitPrix, CancellationToken ct)
    {
        var ecarts = new List<EcartChifaLigne>();
        if (voletsChifa is null || !c.TiersPayant || LectureVoletChifa.Lire(c.VoletChifa) is not { } qr) return (ecarts, 0);
        var associes = await voletsChifa.ProduitsAsync(qr.Lignes.Select(l => l.Code).Distinct(), ct);
        var prixChifa = new Dictionary<long, decimal>();
        foreach (var l in qr.Lignes)
            if (associes.TryGetValue(l.Code, out var p)) prixChifa.TryAdd(p.Id, l.PrixUnitaire);
        if (prixChifa.Count == 0) return (ecarts, 0);

        var seuil = (await sessions.ParametresAsync(ct)).SeuilEcartChifa;
        var aChoisir = vente.Lignes.Any(l => l.ProduitId is { } lp && prixChifa.TryGetValue(lp, out var pc) && EcartPrixChifa.AChoisir(pc, l.PrixUnitaire, seuil));
        // Droit vérifié seulement s'il y a un choix à faire (le code comptoir d'un ayant droit l'accorde sur place)
        // Action dédiée (CodeComptoir.ActionChoixPrixChifa) : accordable par code même sur une connexion par code comptoir
        var aLeDroit = droitPrix ?? (aChoisir && (await droits.APermissionAsync(utilisateurId, Permissions.PrixModifier, ct)
            || await droits.APermissionAsync(utilisateurId, CodeComptoir.ActionChoixPrixChifa, ct)));
        for (var i = 0; i < vente.Lignes.Count; i++)
        {
            var ligne = vente.Lignes[i];
            // Prestation : jamais sur un volet Chifa
            if (ligne.ProduitId is not { } produitId || !prixChifa.TryGetValue(produitId, out var pc)) continue;
            var nom = produits.TryGetValue(produitId, out var produit) ? produit.NomCommercial : "Produit";
            var notre = ligne.PrixUnitaire;
            var demande = c.ChoixChifa?.FirstOrDefault(x => x.ProduitId == ligne.ProduitId);
            decimal prix;
            string? choix;
            try
            {
                (prix, choix) = EcartPrixChifa.Appliquer(pc, notre, seuil, demande?.Choix, aLeDroit, exiger, nom);
            }
            catch (PrixChifaNonAutoriseException e)
            {
                throw new DroitRefuseException(CodeComptoir.ActionChoixPrixChifa, "choisir le prix d’une ligne du volet Chifa", e.Message);
            }
            if (exiger && demande?.PrixAttendu is { } attendu && attendu != prix)
                throw new RegleMetierException(
                    $"{nom} : prix {attendu:N2} DA non conforme à la règle des écarts Chifa (prix Chifa {pc:N2} DA, notre prix {notre:N2} DA, prix applicable {prix:N2} DA). Rescannez le volet.",
                    "prix_chifa");
            vente.AppliquerVoletChifa(ligne, pc, prix, choix);
            if (choix is not null)
                ecarts.Add(new EcartChifaLigne(i, produitId, nom, pc, notre, prix, choix, EcartPrixChifa.AChoisir(pc, notre, seuil)));
        }
        return (ecarts, seuil);
    }

    /// <summary>
    /// Remise du vendeur sur des produits hors médicaments : dans son maximum, sinon validée sur place par le code d'un employé dont le
    /// maximum la couvre. Sur des médicaments, c'est un don partiel du reste à charge : droit de don, sinon code d'un employé qui l'a.
    /// Les remises automatiques, décidées par le titulaire, ne demandent rien.
    /// </summary>
    /// <summary>
    /// Vente « Facturée par Chifa » sans n° de facture (décision du titulaire, 2026-11-15) : motif obligatoire, actif dans la liste
    /// « volet_absent » (Administration › Motifs et catégories), « Autre » avec un texte. Liste non branchée (tests) : code exigé seulement.
    /// </summary>
    private async Task<(string Code, string? Texte)> MotifSansVoletAsync(CommandeVente commande, CancellationToken ct)
    {
        const string Quoi = "de la vente sans n° de facture Chifa";
        if (motifs is null)
        {
            if (string.IsNullOrWhiteSpace(commande.MotifSansVoletCode)) throw new RegleMetierException($"Choisissez le motif {Quoi}.", "motif");
            var t = string.IsNullOrWhiteSpace(commande.MotifSansVoletTexte) ? null : commande.MotifSansVoletTexte.Trim();
            var code = commande.MotifSansVoletCode.Trim();
            if (code == Pharma.Domain.Referentiels.RegleMotifs.Autre && t is null) throw new RegleMetierException("Motif « Autre » : précisez le motif.", "motif_texte");
            return (code, code == Pharma.Domain.Referentiels.RegleMotifs.Autre ? t : null);
        }
        var (c, _, texte) = await new Referentiels.MotifsActifs(motifs)
            .ChoisirAsync(Pharma.Domain.Referentiels.RegleMotifs.VoletAbsent, commande.MotifSansVoletCode, commande.MotifSansVoletTexte, Quoi, ct);
        return (c, texte);
    }

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
        var lotIds = c.Lignes.Where(l => l.LotId is not null).Select(l => l.LotId!.Value).Distinct().ToList();
        var situations = lotIds.Count == 0 ? new Dictionary<long, SituationLotVente>() : await preparation.SituationsAsync(lotIds, ct);
        foreach (var ligne in vente.Lignes)
        {
            // Prestation : pas de lot
            if (ligne.LotId is not { } lotId || ligne.ProduitId is not { } produitId) continue;
            if (situations.GetValueOrDefault(lotId) is not { } s) continue;
            var choix = c.Lignes.Where(l => l.LotId == lotId).Select(l => l.RaisonLot).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
            var produit = produits.TryGetValue(produitId, out var p) ? p.NomCommercial : "Produit";
            var lot = s.NumeroLot ?? lotId.ToString();
            ligne.RaisonLot = SituationLot.Raison(s.Situation, choix, produit, lot, s.LotDelaisse, exiger);
        }
        return situations;
    }

    /// <summary>Boîtes identifiées par lot : jamais deux fois la même, au plus autant que de boîtes vendues du lot.</summary>
    private static Dictionary<long, IReadOnlyList<long>>? BoitesParLot(CommandeVente commande)
    {
        var parLot = commande.Lignes.Where(l => l.Boites is { Count: > 0 } && l.LotId is not null).GroupBy(l => l.LotId!.Value).ToDictionary(
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
        var (vente, produits, ecartsChifa, _) = await ConstruireAsync(commande, utilisateurId, session.Id, ct, exigerChifa: true);
        // Volet Chifa repris par son QR : relu par le serveur, en tiers payant, une seule fois (index unique en base)
        Pharma.Domain.Ventes.QrChifa? volet = null;
        if (!string.IsNullOrWhiteSpace(commande.VoletChifa))
        {
            volet = LectureVoletChifa.Lire(commande.VoletChifa) ?? throw new RegleMetierException("Le QR du volet Chifa n’est pas reconnu.", "volet_chifa");
            if (!FormatFactureChifa.EstValide(volet.NumeroFacture))   // script 101 : chifa_facture sur 8 chiffres
                throw new RegleMetierException($"Le n° de facture du volet ({volet.NumeroFacture}) ne compte pas 8 chiffres : volet non repris.", "volet_chifa");
            if (!commande.TiersPayant) throw new RegleMetierException("Un volet Chifa se reprend en tiers payant.", "volet_chifa");
            // Organisme d'un volet (2026-11-15) : CNAS ou CASNOS seulement (même carte Chifa), choisi par le vendeur
            if (voletsChifa is not null && !OrganismeVoletChifa.Accepte(commande.OrganismeId is { } org ? await voletsChifa.CodeOrganismeAsync(org, ct) : null))
                throw new RegleMetierException("Un volet Chifa se reprend en tiers payant CNAS ou CASNOS seulement.", "organisme");
        }
        // Canal de la part organisme (script 089) : volet ou case « facturée par Chifa » => chifa ; organisme MDN => mdn ; sinon logiciel
        var codeOrganisme = commande.TiersPayant && voletsChifa is not null && commande.OrganismeId is { } idOrg
            ? await voletsChifa.CodeOrganismeAsync(idOrg, ct) : null;
        string? factureChifa = null;
        (string Code, string? Texte)? motifSansVolet = null;
        if (commande.FactureeParChifa && volet is null)
        {
            if (!commande.TiersPayant || (voletsChifa is not null && !OrganismeVoletChifa.Accepte(codeOrganisme)))
                throw new RegleMetierException("« Facturée par Chifa » : tiers payant CNAS ou CASNOS seulement.", "organisme");
            // Circuit « la caisse d'abord » (2026-11-15) : sans n° de facture Chifa, la vente attend son volet (rattaché au scan du QR)
            factureChifa = string.IsNullOrWhiteSpace(commande.NumeroFactureChifa) ? null : CanalOrganisme.NumeroFacture(commande.NumeroFactureChifa);
            if (factureChifa is not null && voletsChifa is not null && await voletsChifa.VenteDuVoletAsync(factureChifa, ct) is { } dejaChifa)
                throw new RegleMetierException($"La facture Chifa n° {factureChifa} est déjà rattachée à la vente {dejaChifa}.", "facture_chifa");
            // Sans n° (décision du titulaire, 2026-11-15) : motif obligatoire, choisi dans la liste réglable « volet_absent »
            if (factureChifa is null)
                motifSansVolet = await MotifSansVoletAsync(commande, ct);
        }
        var canal = CanalOrganisme.Determiner(commande.TiersPayant, volet is not null, commande.FactureeParChifa && volet is null, codeOrganisme);
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
            // Durée du traitement : obligatoire pour un psychotrope ou stupéfiant (décision du titulaire du 2026-11-19, script 153 : base
            // de l'alerte « patient encore sous traitement »), gardée seulement dans ce cas
            if (vente.ContientSousRegistre && o.DureeTraitementJours is null or < 1 or > OrdonnanceSaisie.DureeMax)
                throw new RegleMetierException($"Psychotrope ou stupéfiant : indiquez la durée du traitement prescrite (1 à {OrdonnanceSaisie.DureeMax} jours).", "duree_traitement");
            if (!vente.ContientSousRegistre && commande.Ordonnance!.DureeTraitementJours is not null)
                commande = commande with { Ordonnance = commande.Ordonnance with { DureeTraitementJours = null } };
        }
        // Registre renforcé (lot 8) : une alerte psychotrope non forcée (motif) bloque l'encaissement
        if (vente.ContientSousRegistre && alertesPsychotropes is not null)
            await alertesPsychotropes.ExigerAsync(utilisateurId, new Psychotropes.DelivrancePsychotrope(commande.PatientId,
                commande.Ordonnance?.MedecinId, commande.Ordonnance?.Numero, commande.Ordonnance?.DatePrescription,
                vente.Lignes.Where(l => l.ProduitId is not null).Select(l => new Psychotropes.LignePsychotrope(l.ProduitId!.Value, l.Quantite)).ToList(),
                commande.Ordonnance?.DureeTraitementJours), ct);
        // « Remis à » (avec ordonnance) / « Pour » (sans ordonnance) : la vente et sa dette restent au nom du patient (RemiseVente)
        var (remisA, pour) = RemiseVente.Verifier(commande.PatientId, commande.Ordonnance is not null, commande.RemisA, commande.Pour,
            commande.Ordonnance?.Porteur?.Nom);
        foreach (var lie in new[] { remisA?.PatientId, pour?.PatientId })
            if (lie is { } lieId && await patients.TrouverAsync(lieId, ct) is null)
                throw new RegleMetierException("« Remis à » ou « Pour » : patient introuvable.");
        // Tiers payant : médecin obligatoire quand l'organisme paie une partie (RegleMedecinTiersPayant)
        var voletScanne = !string.IsNullOrWhiteSpace(commande.VoletChifa);
        if (RegleMedecinTiersPayant.Exige(commande.TiersPayant, vente.PartOrganisme, voletScanne) && commande.Ordonnance?.MedecinId is null)
            throw new RegleMetierException("Tiers payant : l’organisme paie une partie, indiquez le médecin prescripteur.", "nom_medecin");
        vente.VerifierAvantEncaissement(commande.Ordonnance is not null);
        // Liste I ou II sans ordonnance : droit « vendre sans ordonnance », sinon autorisation par le code d'un employé qui l'a.
        // Psychotrope ou stupéfiant : l'ordonnance reste obligatoire pour tous (contrôlé ci-dessus)
        var sansOrdonnance = produits.Values.Where(p => p.EstSurOrdonnance).Select(p => p.NomCommercial).ToList();
        // Tiers payant sans part de l'organisme : vente sans ordonnance des médicaments remboursables, même validation
        var tiersSansPart = RegleMedecinTiersPayant.SansPartOrganisme(commande.TiersPayant, vente.PartOrganisme, voletScanne);
        if (tiersSansPart)
            sansOrdonnance = sansOrdonnance.Union(produits.Values.Where(p => p.Remboursable).Select(p => p.NomCommercial)).ToList();
        if (commande.Ordonnance is { } ord && (ord.MedecinId is not null || ord.JetonScan is not null)) sansOrdonnance.Clear();
        // Volet Chifa repris (2026-11-15) : ordonnance considérée comme présentée (liste I ou II sans validation « sans ordonnance ») ;
        // psychotrope : ordonnance et médecin toujours exigés (registre, contrôlés plus haut)
        if (voletScanne) sansOrdonnance.Clear();
        if (sansOrdonnance.Count > 0)
            await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendreSansOrdonnance,
                tiersSansPart ? "vendre sans ordonnance en tiers payant sans part de l’organisme" : "vendre sans ordonnance un médicament de liste I ou II", ct);

        Reglement reglement;
        var credit = false;
        var peutDepasser = false;
        if (commande.Mode == ModePaiement.Credit)
        {
            await droits.ExigerAsync(utilisateurId, Permissions.CreditAccorder, "mettre une vente en crédit", ct);
            var patient = commande.PatientId is { } pid ? await patients.TrouverAsync(pid, ct) : null;
            if (patient is null)
                throw new RegleMetierException("Crédit : choisissez le patient à qui la vente est accordée.");
            var depasser = await droits.APermissionAsync(utilisateurId, Permissions.CreditDepasserPlafond, ct);
            peutDepasser = depasser;
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
        {
            // Avoir du patient (script 106) : combinable avec espèces, carte ou chèque ; solde revérifié sous verrou à l'écriture
            if (commande.MontantAvoir > 0)
            {
                var solde = commande.PatientId is { } pa && avoirs is not null ? await avoirs.SoldeAsync(pa, ct) : 0;
                RegleAvoir.VerifierUtilisation(commande.MontantAvoir, solde, vente.PartPatient, commande.PatientId is not null);
            }
            reglement = Reglement.Pour(vente, commande.Mode, commande.MontantRecu, Math.Max(commande.MontantAvoir, 0));
        }
        if (commande.Mode == ModePaiement.Credit && commande.MontantAvoir > 0)
            throw new RegleMetierException("L’avoir ne se combine pas avec le crédit : utilisez-le avec espèces, carte ou chèque.", "avoir");

        vente.Valider();
        var sousRegistre = produits.Values.Where(p => p.EstSousRegistre).Select(p => p.Id).ToHashSet();
        long id;
        string numero;
        try
        {
            (id, numero) = await ventes.EnregistrerAsync(
                new VenteAEnregistrer(vente, reglement, commande.Ordonnance, sousRegistre, credit, BoitesParLot(commande), volet,
                    commande.CleEncaissement, peutDepasser, canal, factureChifa, remisA, pour, motifSansVolet?.Code, motifSansVolet?.Texte), ct);
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

        // Écarts de prix du volet Chifa : produit, prix Chifa, notre prix, prix appliqué, choix, vendeur et valideur éventuel (code comptoir)
        if (ecartsChifa.Count > 0 && journal is not null)
            await journal.EcrireAsync(utilisateurId, "ecart_prix_chifa", "vente", id,
                new Dictionary<string, object?>
                {
                    ["ticket"] = numero, ["volet"] = volet?.NumeroFacture,
                    ["lignes"] = ecartsChifa.Select(e => new { produit = e.Produit, prix_chifa = e.PrixChifa, notre_prix = e.NotrePrix, prix_applique = e.PrixApplique, choix = e.Choix }).ToList(),
                    ["ecart_patient"] = vente.EcartChifaPatient,
                    ["vendeur"] = utilisateurId,
                    ["valide_par"] = autorisation?.AutoriseParId,
                    ["valideur"] = autorisation?.AutoriseParNom
                },
                ecartsChifa.Any(e => e.Choix is EcartPrixChifa.Aligne or EcartPrixChifa.Garde) ? Permissions.PrixModifier : Permissions.CaisseVendre, ct);

        // Reste à charge saisi à la main sur une vente avec volet (décision du titulaire, 2026-11-15) : permis, mais journalisé
        // (n° du volet, part de l'organisme calculée sur le prix Chifa, valeur saisie, écart, vendeur et valideur éventuel)
        if (volet is not null && vente.ResteSaisi is { } resteSaisi && journal is not null)
            await journal.EcrireAsync(utilisateurId, ResteChargeManuelVolet, "vente", id,
                DetailResteManuelVolet(numero, volet.NumeroFacture, vente, resteSaisi, autorisation?.AutoriseParNom), Permissions.CaisseVendre, ct);

        // Droits Chifa expirés (script 102, D-4) : avertissement journalisé seulement, la vente n'est jamais bloquée
        await DroitsChifa.JournaliserSiExpiresAsync(patients, journal, commande.PatientId,
            commande.TiersPayant && OrganismeVoletChifa.Accepte(codeOrganisme), horloge.Aujourdhui, utilisateurId, id, numero, ct,
            credit: commande.Mode == ModePaiement.Credit);
        // Vente à crédit sans tiers payant Chifa (2026-11-15) : droits expirés ou bientôt expirés, même avertissement renforcé, jamais bloquant
        if (commande.Mode == ModePaiement.Credit && !(commande.TiersPayant && OrganismeVoletChifa.Accepte(codeOrganisme)))
            await DroitsChifa.JournaliserCreditSansTiersPayantAsync(patients, journal, commande.PatientId, horloge.Aujourdhui,
                (await sessions.ParametresAsync(ct)).JoursAlerteFinDroit, utilisateurId, id, numero, ct);

        // Catalogue d'après le volet (règle du titulaire, 2026-11-15) : après l'enregistrement de la vente seulement (vente abandonnée
        // ou refusée : catalogue intact), quel que soit le vendeur ; la vente déjà enregistrée n'en dépend pas
        string? catalogueChifa = null;
        if (volet is not null && voletsChifa is not null)
            try
            {
                catalogueChifa = await CatalogueVoletChifa.AppliquerAsync(voletsChifa, journal, volet, utilisateurId, CancellationToken.None);
            }
            catch (Exception e)
            {
                // La vente est enregistrée : un échec ici ne l'annule pas, il est inscrit au journal du serveur
                trace?.Erreur($"Mise à jour du catalogue d’après le volet Chifa n° {volet.NumeroFacture} impossible", e);
            }

        await AlerterLotsDelaissesAsync(vente);
        // Surveillance de la caisse : ventes hors horaires, remises en série (anomalies notifiées au titulaire)
        if (surveillance is not null) await surveillance.ExecuterAsync(CancellationToken.None);

        return new VenteEncaissee(id, numero, vente.MontantTotal, vente.PartOrganisme, vente.PartPatient,
            reglement.Mode, reglement.Rendu, vente.EcartChifaPatient, catalogueChifa, reglement.Avoir);
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
        // Monnaie sur ce qui restait à payer dans le mode choisi : la part payée par l'avoir est retirée, comme au premier envoi
        var reste = v.PartPatient - v.PayeParAvoir;
        var rendu = mode == ModePaiement.Especes && commande.MontantRecu is { } recu && recu > reste ? recu - reste : 0;
        return new VenteEncaissee(v.VenteId, v.Numero, v.MontantTotal, v.PartOrganisme, v.PartPatient, mode, rendu, PayeParAvoir: v.PayeParAvoir);
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
            static Dictionary<(long?, long?, long?), int> ParLot(IEnumerable<(long? Produit, long? Lot, long? Prestation, int Quantite)> lignes) =>
                lignes.GroupBy(l => (l.Produit, l.Lot, l.Prestation)).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantite));
            var avant = ParLot(enregistrees.Select(l => (l.ProduitId, l.LotId, l.TypePrestationId, l.Quantite)));
            var envoye = ParLot(commande.Lignes.Select(l => (l.ProduitId, l.LotId, l.TypePrestationId, l.Quantite)));
            if (avant.Count != envoye.Count || avant.Any(a => envoye.GetValueOrDefault(a.Key) != a.Value)) return false;
        }
        Vente recalculee;
        try
        {
            (recalculee, _, _, _) = await ConstruireAsync(commande, 0, sessionId: null, ct, controlerLots: false, droitPrixChifa: true);
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
            foreach (var a in await lotsDelaisses.AlerterAsync(vente.Lignes.Where(l => l.ProduitId is not null).Select(l => l.ProduitId!.Value).Distinct().ToList(), CancellationToken.None))
                if (alertesTitulaire is not null) await alertesTitulaire.LotDelaisseAsync(a.Message, a.Niveau == "urgent", CancellationToken.None);
        }, "lots délaissés après une vente", trace);
    }

    private async Task<(Vente, IReadOnlyDictionary<long, Produit>, List<EcartChifaLigne>, decimal)> ConstruireAsync(
        CommandeVente c, long utilisateurId, long? sessionId, CancellationToken ct, bool controlerLots = true, bool exigerChifa = false,
        bool? droitPrixChifa = null)
    {
        if (c.Lignes.Count == 0)
            throw new RegleMetierException("Le panier est vide.");

        foreach (var l in c.Lignes) LigneCommandeValide.Verifier(l);
        var produits = await preparation.ProduitsAsync(c.Lignes.Where(l => l.ProduitId is not null).Select(l => l.ProduitId!.Value).Distinct(), ct);
        var lots = await preparation.LotsAsync(c.Lignes.Where(l => l.LotId is not null).Select(l => l.LotId!.Value).Distinct(), ct);
        // Prestations (script 105) : sans produit ni lot
        var idsPrestations = c.Lignes.Where(l => l.TypePrestationId is not null).Select(l => l.TypePrestationId!.Value).Distinct().ToList();
        var prestations = idsPrestations.Count == 0
            ? new Dictionary<long, TypePrestation>() : await preparation.PrestationsAsync(idsPrestations, ct);

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
            if (ligne.TypePrestationId is { } tpId)
            {
                if (!prestations.TryGetValue(tpId, out var prestation))
                    throw new RegleMetierException("Une prestation du panier n’existe plus.", "prestation");
                vente.AjouterPrestation(prestation, ligne.Quantite);
                continue;
            }
            if (!produits.TryGetValue(ligne.ProduitId!.Value, out var produit) || !produit.Actif)
                throw new RegleMetierException("Un produit du panier n’existe plus ou n’est plus actif.");
            // Repris de l'ancien logiciel sans n° d'enregistrement, ou psychotrope (stupéfiant) créé par un employé :
            // sa classe n'a pas été confirmée par le titulaire
            if (produit.AVerifier)
                throw new RegleMetierException(
                    $"{produit.NomCommercial} : produit à vérifier par le titulaire avant la vente (repris de l’ancien logiciel, ou classé d’office psychotrope ou stupéfiant à sa création) : Administration, Nomenclature nationale, « Produits à vérifier ».");
            // Créé sans prix par un employé sans le droit prix.modifier : invendable tant qu'un ayant droit n'a pas fixé son prix
            if (produit.PrixPublic <= 0)
                throw new RegleMetierException(
                    $"{produit.NomCommercial} : prix de vente non fixé, à faire fixer par un ayant droit (fiche produit) avant la vente.");
            if (!lots.TryGetValue(ligne.LotId!.Value, out var lot))
                throw new RegleMetierException($"{produit.NomCommercial} : lot introuvable, rescannez la boîte.");
            vente.AjouterProduit(produit, lot, ligne.Quantite, aujourdhui, autoriserBloque: true, controlerLot: controlerLots);   // blocage contrôlé à l'encaissement
        }

        // Volet Chifa : prix Chifa ou le nôtre selon l'écart, part de l'organisme sur le prix Chifa (catalogue jamais modifié)
        var (ecarts, seuil) = await AppliquerVoletChifaAsync(c, vente, produits, utilisateurId, exigerChifa, droitPrixChifa, ct);

        // Tiers payant : reste à charge fixé par le caissier, au lieu du taux de prise en charge
        if (c.TiersPayant && c.ResteACharge is { } reste)
            vente.SaisirResteACharge(reste);

        // Remises automatiques du jour (vente libre, produits non remboursables), puis remise du vendeur
        if (remisesAuto is not null && vente.Type == TypeVente.Libre)
            vente.AppliquerRemisesAutomatiques(await remisesAuto.PourcentagesAsync(produits.Keys.ToList(), aujourdhui, ct));
        vente.AppliquerRemise(c.RemisePct, c.RemiseAdditionnee);

        return (vente, produits, ecarts, seuil);
    }

    /// <summary>Action du journal : reste à charge saisi à la main sur une vente reprise d'un volet Chifa (2026-11-15).</summary>
    public const string ResteChargeManuelVolet = "reste_charge_manuel_volet";

    /// <summary>
    /// Détail du journal « reste à charge saisi sur un volet » : part de l'organisme calculée (prix Chifa × taux), reste qu'elle laissait,
    /// reste saisi, part retenue et écart (reste saisi − reste calculé ; négatif : le patient paie moins que le calcul). Le vendeur est
    /// l'auteur de l'entrée ; valideur : employé qui a autorisé par son code, s'il y en a un.
    /// </summary>
    public static Dictionary<string, object?> DetailResteManuelVolet(string ticket, string volet, Vente vente, decimal resteSaisi, string? valideur)
    {
        var brut = vente.Lignes.Sum(l => l.PrixUnitaire * l.Quantite);
        var partCalculee = vente.PartOrganismeCalculee ?? vente.PartOrganisme;
        var resteCalcule = brut - partCalculee;
        return new Dictionary<string, object?>
        {
            ["ticket"] = ticket, ["volet"] = volet,
            ["part_organisme_calculee"] = partCalculee, ["reste_calcule"] = resteCalcule,
            ["reste_saisi"] = resteSaisi, ["part_organisme_retenue"] = vente.PartOrganisme,
            ["ecart"] = resteSaisi - resteCalcule, ["valideur"] = valideur
        };
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
