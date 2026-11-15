using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;

namespace Pharma.Application.Achats;

// ---- Modèles ----------------------------------------------------------------

public sealed record Fournisseur(long Id, string RaisonSociale, string? Telephone, decimal Solde, int? DelaiRepriseJours, string? Email = null,
    string? Adresse = null, string? Nif = null, string? Rc = null, bool Actif = true);

/// <summary>Fiche d'un grossiste, telle qu'on la crée ou la modifie (Id null : création).</summary>
public sealed record FicheGrossiste(long? Id, string RaisonSociale, string? Telephone, string? Email, string? Adresse, string? Nif, string? Rc,
    int? DelaiRepriseJours, bool Actif);

public interface IGrossistes
{
    Task<long> EnregistrerAsync(FicheGrossiste f, CancellationToken ct = default);
    Task<bool> RaisonSocialeExisteAsync(string raisonSociale, long? sauf, CancellationToken ct = default);
}

/// <summary>
/// Création et modification d'un grossiste (droit de commander) : raison sociale, coordonnées, NIF, RC, délai de reprise des
/// produits proches de la péremption ; désactivé, il n'est plus proposé aux commandes ni aux réceptions. Inscrit au journal.
/// </summary>
public sealed class EnregistrerGrossiste(IGrossistes grossistes, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(FicheGrossiste f, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "gérer les grossistes", ct);
        if (string.IsNullOrWhiteSpace(f.RaisonSociale)) throw new RegleMetierException("Indiquez la raison sociale du grossiste.", "raison_sociale");
        if (f.DelaiRepriseJours is < 1 or > 365) throw new RegleMetierException("Le délai de reprise doit être compris entre 1 et 365 jours.", "delai_reprise");
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var fiche = f with
        {
            RaisonSociale = f.RaisonSociale.Trim(), Telephone = Contacts.RegleContact.Telephone(f.Telephone), Email = Contacts.RegleContact.Email(f.Email),
            Adresse = N(f.Adresse), Nif = N(f.Nif), Rc = N(f.Rc)
        };
        if (await grossistes.RaisonSocialeExisteAsync(fiche.RaisonSociale, f.Id, ct))
            throw new RegleMetierException($"Un grossiste « {fiche.RaisonSociale} » existe déjà.", "raison_sociale");
        var id = await grossistes.EnregistrerAsync(fiche, ct);
        await journal.EcrireAsync(utilisateurId, f.Id is null ? "grossiste_cree" : "grossiste_modifie", "fournisseur", id,
            new Dictionary<string, object?>
            {
                ["raison_sociale"] = fiche.RaisonSociale, ["telephone"] = fiche.Telephone, ["email"] = fiche.Email, ["nif"] = fiche.Nif,
                ["delai_reprise_jours"] = fiche.DelaiRepriseJours, ["actif"] = fiche.Actif
            }, Permissions.AchatsCommander, ct);
        return id;
    }
}

/// <summary>Statuts de la base : brouillon, envoyee, recue_partielle, recue, annulee.</summary>
public sealed record CommandeResume(
    long Id, string Numero, long FournisseurId, string Fournisseur, DateTime Date, string Statut,
    int NombreLignes, decimal MontantHt);

public sealed record LigneCommandeAchat(
    long Id, long ProduitId, string Produit, int QteCommandee, int QteRecue, decimal PrixAchatHt);

public sealed record CommandeDetail(CommandeResume Entete, IReadOnlyList<LigneCommandeAchat> Lignes);

/// <summary>Produit à commander (vue v_etat_stock) avec son dernier prix d'achat et son dernier grossiste.</summary>
public sealed record SuggestionCommande(
    long ProduitId, string Produit, string Statut, int Stock, int QteSuggeree,
    decimal? DernierPrixHt, long? DernierFournisseurId, string? DernierFournisseur);

public sealed record DernierAchat(decimal PrixAchatHt, long FournisseurId);

public sealed record ReceptionResume(
    long Id, string Fournisseur, string? NumeroBl, string? NumeroFacture, string? NumeroCommande,
    DateTime Date, int NombreLots, int NombreBoites, decimal MontantTtc, long? FournisseurId = null);

/// <summary>Lot reçu : nouveau ou complété (même lot, même grossiste), avec les étiquettes internes créées pour ses boîtes sans Data Matrix.</summary>
public sealed record LotRecu(long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite, int Etiquettes, bool LotExistant = false);

/// <summary>AReclamer : valeur des boîtes facturées absentes du carton (réception à l'aveugle, écart validé).</summary>
public sealed record ReceptionEnregistree(long ReceptionId, decimal MontantTtc, IReadOnlyList<LotRecu> Lots, decimal AReclamer = 0);

/// <summary>MontantTtc : net à payer au grossiste (dette) ; Totaux : détail de la facture (044).</summary>
public sealed record NouvelleReception(
    long FournisseurId, long? CommandeId, string NumeroBl, string? NumeroFacture,
    IReadOnlyList<LigneReceptionSaisie> Lignes, IReadOnlyDictionary<long, decimal> TauxTva,
    decimal MontantTtc, long UtilisateurId, TotauxFactureAchat? Totaux = null,
    long? ComptageId = null, IReadOnlyList<LigneManquante>? Manquants = null);

/// <summary>Ce qu'il faut savoir d'un produit pour lire la facture du grossiste : liste (SHP), prix public du catalogue, TVA.</summary>
public sealed record InfoAchatProduit(long ProduitId, string Classe, decimal PrixPublic, decimal TauxTva, decimal ShpParBoite,
    bool BloqueAchat = false, string? MotifBlocage = null);

/// <summary>Lot vu pour un retour : provenance, stock, boîtes identifiées encore en stock.</summary>
public sealed record LotRetournable(
    long LotId, long ProduitId, string Produit, string NumeroLot, DateOnly DatePeremption,
    long FournisseurId, string Fournisseur, int Disponible, decimal PrixAchatHt, bool Bloque,
    IReadOnlyList<BoiteEnStock> Boites);

public sealed record BoiteEnStock(long Id, string Code);

public sealed record RetourResume(
    long Id, string Numero, long FournisseurId, string Fournisseur, string Motif, string Statut,
    DateTime CreeLe, DateTime? EnvoyeLe, DateTime? AvoirRecuLe, decimal MontantAttendu, decimal? MontantAvoir,
    IReadOnlyList<LigneRetourAchat> Lignes);

public sealed record LigneRetourAchat(string Produit, string NumeroLot, int Quantite);

public sealed record LotARetourner(long LotId, int Quantite, IReadOnlyList<string> CodesBoites);

public sealed record NouveauRetour(
    long FournisseurId, MotifRetour Motif, IReadOnlyList<(LotRetournable Lot, int Quantite, IReadOnlyList<long> BoiteIds)> Lignes,
    decimal MontantAttendu, long UtilisateurId);

public sealed record LotAEtiqueter(long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, int Disponible, int BoitesSansIdentite);

public sealed record EtiquetteBoite(string Produit, string NumeroLot, DateOnly DatePeremption, decimal PrixPublic, string Fournisseur, string Code, string? Emplacement = null);

/// <summary>
/// Filtre des listes d'achats (bons de commande, réceptions, retours) : grossiste, statut, période, recherche
/// (commandes : n° du bon ; réceptions : n° de BL, de facture ou de bon ; retours : n° ou motif). Un critère absent ne filtre pas.
/// </summary>
public sealed record FiltreAchats(long? FournisseurId = null, string? Statut = null, DateOnly? Du = null, DateOnly? Au = null, string? Recherche = null)
{
    public static FiltreAchats Lire(long? fournisseurId, string? statut, DateOnly? du, DateOnly? au, string? recherche) =>
        new(fournisseurId is > 0 ? fournisseurId : null, string.IsNullOrWhiteSpace(statut) ? null : statut.Trim(), du, au,
            string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim());
}

// ---- Ports ------------------------------------------------------------------

public interface IAchats
{
    Task<IReadOnlyList<Fournisseur>> FournisseursAsync(CancellationToken ct = default);
    Task<Fournisseur?> FournisseurAsync(long id, CancellationToken ct = default);

    Task<Page<CommandeResume>> CommandesAsync(DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default);
    Task<CommandeDetail?> CommandeAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<SuggestionCommande>> SuggestionsAsync(CancellationToken ct = default);
    Task<DernierAchat?> DernierAchatAsync(long produitId, long fournisseurId, CancellationToken ct = default);
    Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default);
    /// <summary>Produit dont le code-barres correspond au GTIN-14 (EAN-13 précédé de 0).</summary>
    Task<(long Id, string Nom)?> ProduitParGtinAsync(string gtin, CancellationToken ct = default);

    /// <summary>Brouillon du grossiste, créé s'il n'existe pas. Renvoie son identifiant.</summary>
    Task<long> BrouillonAsync(long fournisseurId, long utilisateurId, CancellationToken ct = default);
    /// <summary>
    /// Ajoute la quantité si le produit est déjà sur le brouillon, sinon crée la ligne. Ajout, modification et suppression d'une
    /// ligne verrouillent la commande et la refusent si elle n'est plus un brouillon en base (<see cref="RegleCommande.ModifieeEntreTemps"/>).
    /// </summary>
    Task AjouterLigneAsync(long commandeId, long produitId, int quantite, decimal prixHt, CancellationToken ct = default);
    Task ModifierLigneAsync(long commandeId, long ligneId, int quantite, decimal prixHt, CancellationToken ct = default);
    Task SupprimerLigneAsync(long commandeId, long ligneId, CancellationToken ct = default);
    /// <summary>
    /// Change le statut seulement si la commande a encore, en base, le statut lu (<paramref name="statutAttendu"/>) ;
    /// false si elle a été modifiée entre-temps (émise, annulée ou reçue sur un autre poste).
    /// </summary>
    Task<bool> ChangerStatutCommandeAsync(long commandeId, string statutAttendu, string statut, CancellationToken ct = default);
    /// <summary>
    /// Émission en une transaction : commande verrouillée (comme pour l'ajout, la modification ou la suppression d'une ligne), statut
    /// et lignes relus sous ce verrou, refus (<see cref="RegleCommande.ModifieeEntreTemps"/>) si elle n'est plus un brouillon ou si
    /// ses lignes diffèrent de celles contrôlées (<paramref name="lignesControlees"/>), puis passage à « envoyee ». Renvoie la commande
    /// relue dans la transaction : le bon de commande est fait à partir d'elle.
    /// </summary>
    Task<CommandeDetail> EmettreCommandeAsync(long commandeId, IReadOnlyList<LigneCommandeAchat> lignesControlees, CancellationToken ct = default);

    Task<IReadOnlyDictionary<long, decimal>> TauxTvaAsync(IEnumerable<long> produitIds, CancellationToken ct = default);
    /// <summary>Liste du médicament (pour le SHP), prix public du catalogue et TVA de chaque produit.</summary>
    Task<IReadOnlyList<InfoAchatProduit>> InfosAchatAsync(IEnumerable<long> produitIds, CancellationToken ct = default);
    /// <summary>Réception, lots, boîtes sérialisées, mouvements, quantités reçues et dette du grossiste : une seule transaction.</summary>
    Task<ReceptionEnregistree> ReceptionnerAsync(NouvelleReception reception, CancellationToken ct = default);
    Task<Page<ReceptionResume>> ReceptionsAsync(DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default);
    /// <summary>Réceptions du grossiste dont le n° de BL ou de facture est ce numéro (casse et espaces ignorés).</summary>
    Task<IReadOnlyList<ReceptionResume>> ReceptionsAvecDocumentAsync(long fournisseurId, string numero, CancellationToken ct = default);
    /// <summary>Lots du même produit et même n° (normalisé) : ceux du grossiste (toutes dates), et ceux bloqués chez un autre grossiste.</summary>
    Task<IReadOnlyList<LotExistant>> LotsMemeNumeroAsync(long produitId, long fournisseurId, string numeroLot, CancellationToken ct = default);

    Task<LotRetournable?> LotRetournableAsync(long lotId, CancellationToken ct = default);
    /// <summary>Lots encore en stock d'un produit (tous grossistes), éventuellement d'un numéro de lot donné.</summary>
    Task<IReadOnlyList<long>> LotsEnStockAsync(long produitId, string? numeroLot, CancellationToken ct = default);
    /// <summary>Retour en brouillon : le stock du lot et les boîtes désignées sortent tout de suite (mis de côté).</summary>
    Task<long> CreerRetourAsync(NouveauRetour retour, CancellationToken ct = default);
    Task<RetourResume?> RetourAsync(long id, CancellationToken ct = default);
    Task<Page<RetourResume>> RetoursAsync(DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default);
    Task MarquerEnvoyeAsync(long retourId, CancellationToken ct = default);
    /// <summary>Avoir reçu : enregistré sur le retour et déduit de la dette du grossiste.</summary>
    Task EnregistrerAvoirAsync(long retourId, decimal montant, CancellationToken ct = default);
    /// <summary>Annulé ou refusé : le stock et les boîtes reviennent en stock.</summary>
    Task RefuserRetourAsync(long retourId, long utilisateurId, string commentaire, CancellationToken ct = default);

    Task<IReadOnlyList<LotAEtiqueter>> LotsAEtiqueterAsync(CancellationToken ct = default);
    /// <summary>Crée les boîtes et leurs codes EAN-13 internes (fonction creer_boites_internes).</summary>
    Task<IReadOnlyList<EtiquetteBoite>> CreerEtiquettesAsync(long lotId, int nombre, CancellationToken ct = default);
    /// <summary>Étiquettes internes des boîtes d'une livraison encore en stock (réimpression).</summary>
    Task<IReadOnlyList<EtiquetteBoite>> EtiquettesReceptionAsync(long receptionId, CancellationToken ct = default);
}

public interface IDocumentsAchats
{
    /// <param name="avecPrix">Faux si l'utilisateur qui produit le bon ne voit pas les prix d'achat : bon sans prix ni montants.</param>
    byte[] BonCommande(CommandeDetail commande, Fournisseur fournisseur, string? pharmacie, bool avecPrix);
    /// <param name="avecPrix">Faux si l'utilisateur qui produit le bon ne voit pas les prix d'achat : bon sans « avoir attendu » ni prix.</param>
    byte[] BonRetour(RetourResume retour, string? pharmacie, bool avecPrix);
    /// <summary>Planche A4 d'étiquettes 50 × 30 mm avec code EAN-13.</summary>
    byte[] PlancheEtiquettes(IReadOnlyList<EtiquetteBoite> etiquettes);
    /// <summary>Etiquettes ZPL (imprimante thermique 203 dpi), une par boîte.</summary>
    string EtiquettesZpl(IReadOnlyList<EtiquetteBoite> etiquettes);
}

public sealed record DocumentGenere(byte[] Contenu, string NomFichier);

// ---- Cas d'usage : grossistes et commandes ---------------------------------

public sealed class ListerFournisseurs(IAchats achats)
{
    public Task<IReadOnlyList<Fournisseur>> ExecuterAsync(CancellationToken ct = default) => achats.FournisseursAsync(ct);
}

public sealed class ListerCommandes(IAchats achats)
{
    public Task<Page<CommandeResume>> ExecuterAsync(DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default) => achats.CommandesAsync(page, filtre, ct);
}

public sealed class ObtenirCommande(IAchats achats)
{
    public Task<CommandeDetail?> ExecuterAsync(long id, CancellationToken ct = default) => achats.CommandeAsync(id, ct);
}

public sealed class ListerSuggestionsCommande(IAchats achats)
{
    public Task<IReadOnlyList<SuggestionCommande>> ExecuterAsync(CancellationToken ct = default) => achats.SuggestionsAsync(ct);

    /// <summary>
    /// Produits à commander, paginés et filtrés (règle des listes) : produit (sans tenir compte des majuscules ni des accents),
    /// état (rupture, critique), grossiste du dernier achat (0 : aucun achat connu). Ruptures d'abord, puis par nom.
    /// Les grossistes proposés au filtre (liste bornée) accompagnent la page.
    /// </summary>
    public async Task<PageSuggestions> PageAsync(string? recherche, string? statut, long? grossisteId, DemandePage page, CancellationToken ct = default)
    {
        var toutes = await achats.SuggestionsAsync(ct);
        var lignes = toutes
            .Where(x => TexteRecherche.Contient(recherche, x.Produit)
                && (string.IsNullOrEmpty(statut) || x.Statut == statut)
                && (grossisteId is null || (grossisteId == 0 ? x.DernierFournisseurId is null : x.DernierFournisseurId == grossisteId)))
            .ToList();
        var grossistes = toutes.Where(x => x.DernierFournisseurId is not null)
            .Select(x => new GrossisteSuggere(x.DernierFournisseurId!.Value, x.DernierFournisseur ?? "")).Distinct()
            .OrderBy(g => g.Nom, StringComparer.CurrentCulture).ToList();
        return new PageSuggestions(new Page<SuggestionCommande>(lignes.Skip(page.Decalage).Take(page.Taille).ToList(), lignes.Count, page.Page, page.Taille),
            grossistes, toutes.Any(x => x.DernierFournisseurId is null));
    }
}

public sealed record GrossisteSuggere(long Id, string Nom);

/// <summary>Page des produits à commander et grossistes des derniers achats (filtre) ; SansAchat : au moins un produit jamais acheté.</summary>
public sealed record PageSuggestions(Page<SuggestionCommande> Page, IReadOnlyList<GrossisteSuggere> Grossistes, bool SansAchat);

/// <summary>
/// Ajoute un produit au bon de commande en brouillon du grossiste (créé au besoin).
/// Sans prix saisi, le dernier prix d'achat est repris : chez ce grossiste, sinon chez n'importe lequel.
/// </summary>
public sealed class AjouterACommande(IAchats achats, IPermissionService droits)
{
    public async Task<long> ExecuterAsync(long fournisseurId, long produitId, int quantite, decimal? prixHt, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "passer une commande", ct);
        var nom = await achats.NomProduitAsync(produitId, ct) ?? throw new RegleMetierException("Produit introuvable.");
        if ((await achats.InfosAchatAsync([produitId], ct)).FirstOrDefault() is { BloqueAchat: true } bloque)
            throw new RegleMetierException($"{nom} : produit bloqué à l’achat{(bloque.MotifBlocage is { } m ? $" ({m})" : "")}. "
                + "Faites-le débloquer pour le commander ; une livraison peut être réceptionnée avec la validation du pharmacien ou de l’assistant.");
        if (await achats.FournisseurAsync(fournisseurId, ct) is not { } grossiste)
            throw new RegleMetierException("Grossiste introuvable.", "grossiste");
        if (!grossiste.Actif) throw new RegleMetierException($"{grossiste.RaisonSociale} est désactivé : réactivez-le dans l’onglet Grossistes.", "grossiste");
        var prix = prixHt ?? (await achats.DernierAchatAsync(produitId, fournisseurId, ct))?.PrixAchatHt
            ?? throw new RegleMetierException($"{nom} : aucun achat précédent, saisissez le prix d’achat.");
        RegleAchats.VerifierCommande([new LigneCommandeSaisie(produitId, nom, quantite, prix)]);

        var commandeId = await achats.BrouillonAsync(fournisseurId, utilisateurId, ct);
        await achats.AjouterLigneAsync(commandeId, produitId, quantite, prix, ct);
        return commandeId;
    }
}

/// <summary>Contrôle de concurrence des commandes : le statut lu est revérifié en base au moment d'écrire.</summary>
public static class RegleCommande
{
    /// <summary>Statut changé par un autre poste entre la lecture et l'écriture (émise, annulée, reçue).</summary>
    public const string ModifieeEntreTemps = "Commande modifiée entre-temps : rechargez.";

    /// <summary>
    /// Mêmes lignes (identifiant, produit, quantité commandée, prix) dans les deux listes, quel que soit leur ordre : la commande relue
    /// sous verrou à l'émission est bien celle qui a été contrôlée.
    /// </summary>
    public static bool MemesLignes(IReadOnlyList<LigneCommandeAchat> a, IReadOnlyList<LigneCommandeAchat> b)
    {
        static IEnumerable<(long, long, int, decimal)> Cle(IEnumerable<LigneCommandeAchat> l) =>
            l.Select(x => (x.Id, x.ProduitId, x.QteCommandee, x.PrixAchatHt)).OrderBy(x => x.Item1);
        return a.Count == b.Count && Cle(a).SequenceEqual(Cle(b));
    }
}

/// <summary>Modification d'une ligne, uniquement sur un brouillon (revérifié en base). Quantité 0 : la ligne est retirée.</summary>
public sealed class ModifierLigneCommande(IAchats achats, IPermissionService droits)
{
    public async Task ExecuterAsync(long commandeId, long ligneId, int quantite, decimal? prix, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "modifier une commande", ct);
        var c = await achats.CommandeAsync(commandeId, ct) ?? throw new RegleMetierException("Commande introuvable.");
        if (c.Entete.Statut != "brouillon")
            throw new RegleMetierException($"La commande {c.Entete.Numero} est déjà émise : elle ne se modifie plus.");
        var ligne = c.Lignes.FirstOrDefault(l => l.Id == ligneId) ?? throw new RegleMetierException("Ligne introuvable.");
        // Sans prix (employé qui ne voit pas les prix d'achat) : le prix de la ligne est gardé
        var prixHt = prix ?? ligne.PrixAchatHt;
        if (quantite == 0) { await achats.SupprimerLigneAsync(commandeId, ligneId, ct); return; }
        RegleAchats.VerifierCommande([new LigneCommandeSaisie(ligne.ProduitId, ligne.Produit, quantite, prixHt)]);
        await achats.ModifierLigneAsync(commandeId, ligneId, quantite, prixHt, ct);
    }
}

/// <summary>
/// Brouillon → envoyée ; renvoie le bon de commande PDF à transmettre au grossiste. Les lignes contrôlées sont relues sous le verrou de
/// la commande, dans la transaction qui l'émet : une ligne changée sur un autre poste entre-temps fait refuser l'émission (« rechargez ») ;
/// le bon de commande est fait à partir des lignes relues, celles qui sont réellement émises.
/// </summary>
public sealed class EmettreCommande(IAchats achats, IPermissionService droits, IDocumentsAchats documents, Administration.IPharmacie pharmacie, IJournal journal)
{
    public async Task<DocumentGenere> ExecuterAsync(long commandeId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "émettre une commande", ct);
        var c = await achats.CommandeAsync(commandeId, ct) ?? throw new RegleMetierException("Commande introuvable.");
        if (c.Entete.Statut != "brouillon")
            throw new RegleMetierException($"La commande {c.Entete.Numero} est déjà émise.");
        RegleAchats.VerifierCommande(c.Lignes.Select(l => new LigneCommandeSaisie(l.ProduitId, l.Produit, l.QteCommandee, l.PrixAchatHt)).ToList());
        var f = await achats.FournisseurAsync(c.Entete.FournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.");

        // Verrou, relecture et changement de statut dans une même transaction ; refus si les lignes ne sont plus celles contrôlées
        var emise = await achats.EmettreCommandeAsync(commandeId, c.Lignes, ct);
        await journal.EcrireAsync(utilisateurId, "commande_emise", "commande_fournisseur", commandeId,
            new Dictionary<string, object?> { ["numero"] = emise.Entete.Numero, ["montant_ht"] = emise.Entete.MontantHt, ["lignes"] = emise.Lignes.Count },
            Permissions.AchatsCommander, ct);
        var nom = (await pharmacie.LireAsync(ct)).Nom;
        return new DocumentGenere(documents.BonCommande(emise, f, nom, await droits.VoitPrixAchatAsync(utilisateurId, ct)), $"{emise.Entete.Numero}.pdf");
    }
}

/// <summary>
/// Bon de commande PDF (réimpression), quel que soit le statut. Prix et montants imprimés seulement pour qui voit les prix d'achat
/// (rapports.marges, titulaire) : sinon le bon sort sans prix (décision du 2026-11-15).
/// </summary>
public sealed class ImprimerCommande(IAchats achats, IDocumentsAchats documents, Administration.IPharmacie pharmacie, IPermissionService droits)
{
    public async Task<DocumentGenere?> ExecuterAsync(long commandeId, long utilisateurId, CancellationToken ct = default)
    {
        if (await achats.CommandeAsync(commandeId, ct) is not { } c) return null;
        var f = await achats.FournisseurAsync(c.Entete.FournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.");
        var nom = (await pharmacie.LireAsync(ct)).Nom;
        return new DocumentGenere(documents.BonCommande(c, f, nom, await droits.VoitPrixAchatAsync(utilisateurId, ct)), $"{c.Entete.Numero}.pdf");
    }
}

/// <summary>Annulation d'un brouillon ou d'une commande émise dont rien n'a encore été reçu.</summary>
public sealed class AnnulerCommande(IAchats achats, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long commandeId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "annuler une commande", ct);
        var c = await achats.CommandeAsync(commandeId, ct) ?? throw new RegleMetierException("Commande introuvable.");
        if (c.Entete.Statut is not ("brouillon" or "envoyee"))
            throw new RegleMetierException($"La commande {c.Entete.Numero} est « {c.Entete.Statut} » : annulation impossible.");
        if (c.Lignes.Any(l => l.QteRecue > 0))
            throw new RegleMetierException("Une partie de la commande a déjà été reçue.");
        if (!await achats.ChangerStatutCommandeAsync(commandeId, c.Entete.Statut, "annulee", ct))
            throw new RegleMetierException(RegleCommande.ModifieeEntreTemps);
        await journal.EcrireAsync(utilisateurId, "commande_annulee", "commande_fournisseur", commandeId,
            new Dictionary<string, object?> { ["numero"] = c.Entete.Numero }, Permissions.AchatsCommander, ct);
    }
}

// ---- Cas d'usage : réception -----------------------------------------------

public sealed record SaisieReception(
    long FournisseurId, long? CommandeId, string? NumeroBl, string? NumeroFacture, IReadOnlyList<LigneReceptionSaisie> Lignes, decimal Timbre = 0,
    Catalogue.ValidationBlocage? Blocage = null);

/// <summary>
/// Entrée en stock d'une livraison : un lot par ligne (le même lot déjà livré par ce grossiste est complété),
/// une étiquette interne créée pour chaque boîte sans Data Matrix.
/// La dette du grossiste augmente du net à payer : HT moins les remises, plus la TVA du catalogue, plus le timbre (044).
/// Le SHP de chaque ligne, s'il n'est pas saisi, suit la liste du produit (RegleShp).
/// </summary>
public sealed class Receptionner(IAchats achats, IPermissionService droits, IHorloge horloge, IJournal journal, VerificationCodeComptoir? codesComptoir = null,
    ModeReception? mode = null)
{
    public async Task<ReceptionEnregistree> ExecuterAsync(SaisieReception s, long utilisateurId, CancellationToken ct = default)
    {
        // Réception à l'aveugle : la livraison est d'abord comptée, la facture se saisit ensuite sur ce comptage
        if (mode is not null && await mode.AveugleAsync(ct))
            throw new RegleMetierException("Réception à l’aveugle : comptez d’abord les boîtes (« Compter une livraison »), la facture se saisit ensuite sur ce comptage.");
        await ExigerSaisiePrixAsync(droits, s, utilisateurId, ct);
        return await EntrerAsync(s, utilisateurId, ct);
    }

    /// <summary>
    /// Prix facturés (prix HT, remise, timbre) : saisis seulement par un ayant droit prix.modifier (un seul droit pour toute saisie de
    /// prix, titulaire compris, décision du 2026-11-15) ; la visibilité des prix d'achat reste liée à rapports.marges. Sans le droit, l'employé compte les boîtes (CompterLivraison) et la facture est saisie par un ayant droit ;
    /// le refus (DroitRefuseException) permet l'autorisation sur place par code comptoir. Exigé quel que soit le prix saisi (revue
    /// 2026-11-15) : une facture « tout à 0 » ferait entrer le stock valorisé à 0 sans contrôle.
    /// </summary>
    public static async Task ExigerSaisiePrixAsync(IPermissionService droits, SaisieReception s, long utilisateurId, CancellationToken ct = default)
    {
        if (s.Timbre == 0 && s.Lignes.Count == 0) return;
        await droits.ExigerAsync(utilisateurId, Permissions.PrixModifier, "saisir les prix de la facture d’achat", ct);
    }

    /// <summary>Livraison comptée puis facturée (FacturerComptage, DeciderEcartReception) : lignes déjà rapprochées du comptage.</summary>
    /// <param name="manquants">Boîtes facturées absentes du carton : sorties du stock dans la même transaction que l'entrée.</param>
    public Task<ReceptionEnregistree> ExecuterDepuisComptageAsync(SaisieReception s, long utilisateurId, long comptageId,
        IReadOnlyList<LigneManquante>? manquants = null, CancellationToken ct = default) =>
        EntrerAsync(s, utilisateurId, ct, comptageId, manquants);

    private async Task<ReceptionEnregistree> EntrerAsync(SaisieReception s, long utilisateurId, CancellationToken ct,
        long? comptageId = null, IReadOnlyList<LigneManquante>? manquants = null)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        if (await achats.FournisseurAsync(s.FournisseurId, ct) is not { } grossiste)
            throw new RegleMetierException("Grossiste introuvable.", "grossiste");
        if (!grossiste.Actif) throw new RegleMetierException($"{grossiste.RaisonSociale} est désactivé : réactivez-le dans l’onglet Grossistes.", "grossiste");

        var lignes = s.Lignes.Select(l => l with
        {
            NumeroLot = RegleLot.Normaliser(l.NumeroLot),
            Gtin = string.IsNullOrWhiteSpace(l.Gtin) ? null : l.Gtin.Trim(),
            NumerosSerie = l.NumerosSerie?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList()
        }).ToList();
        RegleAchats.VerifierReception(s.NumeroBl, lignes, horloge.Aujourdhui);
        RegleFactureAchat.Verifier(lignes, s.Timbre);
        if (lignes.GroupBy(l => (l.ProduitId, l.NumeroLot.ToUpperInvariant())).Any(g => g.Count() > 1))
            throw new RegleMetierException("Un même lot figure deux fois : regroupez les quantités sur une ligne.");
        // Data Matrix : le GTIN des boîtes doit être celui du produit de la ligne (sinon boîtes d'un autre produit entrées sous ce nom)
        foreach (var l in lignes.Where(l => l.Gtin is not null))
        {
            var produit = await achats.ProduitParGtinAsync(l.Gtin!, ct);
            if (produit is null)
                throw new RegleMetierException($"{l.Nom} : le Data Matrix scanné (GTIN {l.Gtin}) ne correspond à aucun produit du catalogue. Vérifiez la boîte, ou créez le produit.");
            if (produit.Value.Id != l.ProduitId)
                throw new RegleMetierException($"{l.Nom} : le Data Matrix scanné est celui de {produit.Value.Nom} (GTIN {l.Gtin}), pas de ce produit.");
        }

        if (s.CommandeId is { } cid)
        {
            var c = await achats.CommandeAsync(cid, ct) ?? throw new RegleMetierException("Commande introuvable.");
            if (c.Entete.FournisseurId != s.FournisseurId)
                throw new RegleMetierException($"La commande {c.Entete.Numero} a été passée à un autre grossiste.");
            if (c.Entete.Statut is not ("envoyee" or "recue_partielle"))
                throw new RegleMetierException($"La commande {c.Entete.Numero} est « {c.Entete.Statut} » : réception impossible.");
        }

        var tva = await achats.TauxTvaAsync(lignes.Select(l => l.ProduitId).Distinct(), ct);
        var inconnus = lignes.Where(l => !tva.ContainsKey(l.ProduitId)).Select(l => l.Nom).ToList();
        if (inconnus.Count > 0)
            throw new RegleMetierException($"Produit introuvable : {string.Join(", ", inconnus)}.");
        // Nom du catalogue (et non celui saisi) sur les lots et les étiquettes
        for (var i = 0; i < lignes.Count; i++)
            lignes[i] = lignes[i] with { Nom = await achats.NomProduitAsync(lignes[i].ProduitId, ct) ?? lignes[i].Nom };
        // SHP non saisi : selon la liste du produit
        var infos = (await achats.InfosAchatAsync(lignes.Select(l => l.ProduitId).Distinct(), ct)).ToDictionary(i => i.ProduitId);
        // Produit bloqué à l'achat : réceptionné seulement avec la validation d'une personne habilitée
        var bloques = lignes.Where(l => infos.TryGetValue(l.ProduitId, out var i) && i.BloqueAchat)
            .Select(l => infos[l.ProduitId].MotifBlocage is { } m ? $"{l.Nom} ({m})" : l.Nom).Distinct().ToList();
        var validePar = await Catalogue.AutorisationBlocage.ExigerAsync(droits, codesComptoir, utilisateurId, s.Blocage, null, "à l’achat", bloques, ct);
        for (var i = 0; i < lignes.Count; i++)
            if (lignes[i].Shp is null)
                lignes[i] = lignes[i] with { Shp = infos.TryGetValue(lignes[i].ProduitId, out var info) ? info.ShpParBoite : 0 };
        // PPA facturé qui remplace le prix public du catalogue : droit de modifier un prix de vente
        if (lignes.Any(l => l.RemplacerPrixCatalogue))
            await droits.ExigerAsync(utilisateurId, Permissions.PrixModifier, "remplacer le prix public du catalogue", ct);
        var totaux = RegleFactureAchat.Totaux(lignes, tva, s.Timbre);
        var ttc = totaux.NetAPayer;

        var r = await achats.ReceptionnerAsync(new NouvelleReception(
            s.FournisseurId, s.CommandeId, s.NumeroBl!.Trim(), string.IsNullOrWhiteSpace(s.NumeroFacture) ? null : s.NumeroFacture.Trim(),
            lignes, tva, ttc, utilisateurId, totaux, comptageId, manquants), ct);
        await journal.EcrireAsync(utilisateurId, "reception", "reception", r.ReceptionId,
            new Dictionary<string, object?>
            {
                ["bl"] = s.NumeroBl, ["lots"] = r.Lots.Count, ["montant_ht"] = totaux.MontantHt, ["remise"] = totaux.Remise,
                ["tva"] = totaux.Tva, ["timbre"] = totaux.Timbre, ["montant_ttc"] = ttc, ["shp"] = totaux.ShpTotal,
                ["ug"] = lignes.Sum(l => l.Ug)
            }, Permissions.AchatsReceptionner, ct);
        if (validePar is { } vp)
            await journal.EcrireAsync(utilisateurId, "reception_produit_bloque", "reception", r.ReceptionId,
                new Dictionary<string, object?> { ["bl"] = s.NumeroBl, ["produits"] = bloques, ["valide_par"] = vp.Id, ["validateur"] = vp.NomComplet },
                Permissions.ProduitsBloquer, ct);
        foreach (var l in lignes.Where(l => l.RemplacerPrixCatalogue))
            if (infos.TryGetValue(l.ProduitId, out var info) && info.PrixPublic != l.Ppa)
                await journal.EcrireAsync(utilisateurId, "prix_modifie", "produit", l.ProduitId,
                    new Dictionary<string, object?> { ["produit"] = l.Nom, ["avant"] = info.PrixPublic, ["apres"] = l.Ppa, ["source"] = $"réception BL {s.NumeroBl}" },
                    Permissions.PrixModifier, ct);
        return r;
    }
}

/// <summary>
/// N° de BL ou de facture saisi ou scanné (QR code de la facture) : réceptions du même grossiste qui portent déjà ce numéro,
/// pour prévenir d'une livraison enregistrée deux fois. Simple avertissement : une facture peut couvrir plusieurs livraisons.
/// </summary>
public sealed class ChercherDocumentReception(IAchats achats, Securite.IPermissionService droits)
{
    public async Task<IReadOnlyList<ReceptionResume>> ExecuterAsync(long fournisseurId, string? numero, long utilisateurId, CancellationToken ct = default)
    {
        await Securite.PermissionServiceExtensions.ExigerAsync(droits, utilisateurId, Domain.Securite.Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        var n = RegleDocumentReception.Normaliser(numero);
        return fournisseurId <= 0 || n is null ? [] : await achats.ReceptionsAvecDocumentAsync(fournisseurId, n, ct);
    }
}

/// <summary>Numéro de document lu sur une facture (saisi ou scanné).</summary>
public static class RegleDocumentReception
{
    // Douchette réglée en clavier QWERTY sur un poste AZERTY : chaque touche tapée donne le caractère AZERTY de la même touche.
    // « 26/F-015695 » arrive ainsi en « é-!F)à&(-ç( » ; on remet chaque caractère à sa valeur QWERTY.
    private static readonly Dictionary<char, char> AzertyVersQwerty = new()
    {
        ['&'] = '1', ['é'] = '2', ['"'] = '3', ['\''] = '4', ['('] = '5', ['-'] = '6', ['è'] = '7', ['_'] = '8', ['ç'] = '9', ['à'] = '0',
        [')'] = '-', ['!'] = '/', [':'] = '.', [';'] = ',', [','] = 'm', ['?'] = 'M',
        ['a'] = 'q', ['q'] = 'a', ['z'] = 'w', ['w'] = 'z', ['A'] = 'Q', ['Q'] = 'A', ['Z'] = 'W', ['W'] = 'Z', ['m'] = ';', ['M'] = ':'
    };
    private const string RangeeChiffresAzerty = "&é\"'(-è_çà";

    /// <summary>
    /// Numéro nettoyé (espaces retirés en bordure), ou null s'il est vide. Un numéro sans aucun chiffre mais avec au moins trois
    /// caractères de la rangée des chiffres AZERTY (« é-!F)à&(-ç( ») vient d'une douchette réglée en QWERTY : il est relu.
    /// </summary>
    public static string? Normaliser(string? numero)
    {
        if (string.IsNullOrWhiteSpace(numero)) return null;
        var n = numero.Trim();
        if (n.Any(char.IsAsciiDigit) || n.Count(c => RangeeChiffresAzerty.Contains(c)) < 3) return n;
        return new string(n.Select(c => AzertyVersQwerty.TryGetValue(c, out var q) ? q : c).ToArray());
    }
}

/// <summary>Infos d'achat des produits d'une réception (SHP proposé, prix public du catalogue pour contrôler le PPA facturé, TVA).</summary>
public sealed class LireInfosAchat(IAchats achats)
{
    public Task<IReadOnlyList<InfoAchatProduit>> ExecuterAsync(IEnumerable<long> produitIds, CancellationToken ct = default) =>
        achats.InfosAchatAsync(produitIds.Distinct().Take(200), ct);
}

public sealed class ListerReceptions(IAchats achats)
{
    public Task<Page<ReceptionResume>> ExecuterAsync(DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default) => achats.ReceptionsAsync(page, filtre, ct);
}

// ---- Cas d'usage : retours fournisseurs ------------------------------------

public sealed class ObtenirLotRetournable(IAchats achats)
{
    public Task<LotRetournable?> ExecuterAsync(long lotId, CancellationToken ct = default) => achats.LotRetournableAsync(lotId, ct);
}

/// <summary>Lots retournables d'un produit, par exemple tous ceux d'un numéro rappelé chez plusieurs grossistes.</summary>
public sealed class ListerLotsRetournables(IAchats achats)
{
    public async Task<IReadOnlyList<LotRetournable>> ExecuterAsync(long produitId, string? numeroLot, CancellationToken ct = default)
    {
        var liste = new List<LotRetournable>();
        foreach (var id in await achats.LotsEnStockAsync(produitId, string.IsNullOrWhiteSpace(numeroLot) ? null : numeroLot.Trim(), ct))
            if (await achats.LotRetournableAsync(id, ct) is { } lot) liste.Add(lot);
        return liste;
    }
}

public sealed class ListerRetours(IAchats achats)
{
    public Task<Page<RetourResume>> ExecuterAsync(DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default) => achats.RetoursAsync(page, filtre, ct);
}

/// <summary>
/// Prépare un retour au grossiste d'origine des lots. Les boîtes sortent du stock dès la préparation.
/// Une boîte identifiée (étiquette ou Data Matrix) doit être scannée ; les autres sont prises dans le reste du lot.
/// </summary>
public sealed class PreparerRetour(IAchats achats, IPermissionService droits, IJournal journal, Referentiels.MotifsActifs? motifs = null)
{
    public async Task<long> ExecuterAsync(MotifRetour motif, IReadOnlyList<LotARetourner> lots, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRetourFournisseur, "faire un retour fournisseur", ct);
        // Motif désactivé par le titulaire (Administration › Motifs et catégories, script 094) : refusé
        if (motifs is not null && !(await motifs.ExecuterAsync(Pharma.Domain.Referentiels.RegleMotifs.RetourFournisseur, ct)).Any(m => m.Code == motif.Code()))
            throw new RegleMetierException("Ce motif de retour a été désactivé : choisissez-en un autre.", "motif_retour");
        if (lots.Count == 0) throw new RegleMetierException("Aucun lot à retourner.");
        if (lots.Select(l => l.LotId).Distinct().Count() != lots.Count)
            throw new RegleMetierException("Un même lot figure deux fois sur le retour.");

        var lignes = new List<(LotRetournable, int, IReadOnlyList<long>)>();
        foreach (var demande in lots)
        {
            var lot = await achats.LotRetournableAsync(demande.LotId, ct) ?? throw new RegleMetierException("Lot introuvable.");
            RegleRetour.VerifierCreation(demande.Quantite, lot.Disponible, lot.NumeroLot);
            var codes = demande.CodesBoites.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (codes.Distinct().Count() != codes.Count)
                throw new RegleMetierException($"Lot {lot.NumeroLot} : une même boîte a été scannée deux fois.");
            var ids = new List<long>();
            foreach (var code in codes)
                ids.Add(lot.Boites.FirstOrDefault(b => b.Code == code)?.Id
                    ?? throw new RegleMetierException($"La boîte {code} n’est pas en stock dans le lot {lot.NumeroLot}."));
            var sansIdentite = lot.Disponible - lot.Boites.Count;
            var aScanner = demande.Quantite - Math.Max(0, sansIdentite);
            if (ids.Count > demande.Quantite)
                throw new RegleMetierException($"Lot {lot.NumeroLot} : {ids.Count} boîtes scannées pour {demande.Quantite} à retourner.");
            if (ids.Count < aScanner)
                throw new RegleMetierException($"Lot {lot.NumeroLot} : scannez au moins {aScanner} boîte(s) identifiée(s) ({ids.Count} scannée(s)).");
            lignes.Add((lot, demande.Quantite, ids));
        }

        var fournisseurs = lignes.Select(l => l.Item1.FournisseurId).Distinct().ToList();
        if (fournisseurs.Count > 1)
            throw new RegleMetierException("Ces lots viennent de grossistes différents : faites un retour par grossiste.");

        var attendu = lignes.Sum(l => Arrondi.Centime(l.Item2 * l.Item1.PrixAchatHt));
        var id = await achats.CreerRetourAsync(new NouveauRetour(fournisseurs[0], motif, lignes, attendu, utilisateurId), ct);
        await journal.EcrireAsync(utilisateurId, "retour_prepare", "retour_fournisseur", id,
            new Dictionary<string, object?> { ["motif"] = motif.Code(), ["boites"] = lignes.Sum(l => l.Item2), ["montant_attendu"] = attendu },
            Permissions.AchatsRetourFournisseur, ct);
        return id;
    }
}

/// <summary>Suivi d'un retour : envoi (bon de retour PDF), avoir reçu, ou annulation / refus avec remise en stock.</summary>
public sealed class SuivreRetour(IAchats achats, IPermissionService droits, IDocumentsAchats documents, Administration.IPharmacie pharmacie, IJournal journal,
    Surveillance.Surveiller? surveillance = null)
{
    public async Task<DocumentGenere> EnvoyerAsync(long retourId, long utilisateurId, CancellationToken ct = default)
    {
        var r = await Charger(retourId, utilisateurId, StatutRetour.Envoye, ct);
        await achats.MarquerEnvoyeAsync(retourId, ct);
        await Journaliser(utilisateurId, "retour_envoye", r, null, ct);
        var maj = await achats.RetourAsync(retourId, ct) ?? r;
        return new DocumentGenere(documents.BonRetour(maj, (await pharmacie.LireAsync(ct)).Nom, await droits.VoitPrixAchatAsync(utilisateurId, ct)),
            $"{r.Numero}.pdf");
    }

    public async Task EnregistrerAvoirAsync(long retourId, decimal montant, long utilisateurId, CancellationToken ct = default)
    {
        if (montant < 0) throw new RegleMetierException("Le montant de l’avoir ne peut pas être négatif.");
        if (!Arrondi.AuCentime(montant))
            throw new RegleMetierException("Le montant de l’avoir compte au plus deux décimales (centimes) : corrigez la saisie.");
        var r = await Charger(retourId, utilisateurId, StatutRetour.AvoirRecu, ct);
        await achats.EnregistrerAvoirAsync(retourId, montant, ct);
        await Journaliser(utilisateurId, "avoir_recu", r, montant, ct);
        // Avoir inférieur au montant attendu : anomalie au titulaire
        if (surveillance is not null && montant < r.MontantAttendu) await surveillance.ExecuterAsync(ct);
    }

    public async Task RefuserAsync(long retourId, string? motif, long utilisateurId, CancellationToken ct = default)
    {
        var r = await Charger(retourId, utilisateurId, StatutRetour.Refuse, ct);
        var commentaire = r.Statut == StatutRetour.Brouillon.Code()
            ? $"Retour {r.Numero} annulé : boîte remise en stock"
            : $"Retour {r.Numero} refusé par le grossiste : boîte remise en stock" + (string.IsNullOrWhiteSpace(motif) ? "" : $" ({motif.Trim()})");
        await achats.RefuserRetourAsync(retourId, utilisateurId, commentaire, ct);
        await Journaliser(utilisateurId, "retour_refuse", r, null, ct);
    }

    /// <summary>
    /// Bon de retour PDF : « avoir attendu » au prix d'achat imprimé seulement pour qui voit les prix d'achat (rapports.marges,
    /// titulaire) ; sinon le bon sort sans prix (décision du 2026-11-15, comme le bon de commande).
    /// </summary>
    public async Task<DocumentGenere?> ImprimerAsync(long retourId, long utilisateurId, CancellationToken ct = default) =>
        await achats.RetourAsync(retourId, ct) is { } r
            ? new DocumentGenere(documents.BonRetour(r, (await pharmacie.LireAsync(ct)).Nom, await droits.VoitPrixAchatAsync(utilisateurId, ct)),
                $"{r.Numero}.pdf")
            : null;

    private async Task<RetourResume> Charger(long retourId, long utilisateurId, StatutRetour voulu, CancellationToken ct)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRetourFournisseur, "suivre un retour fournisseur", ct);
        var r = await achats.RetourAsync(retourId, ct) ?? throw new RegleMetierException("Retour introuvable.");
        RegleRetour.VerifierTransition(CodesMetier.StatutRetourDepuisCode(r.Statut), voulu);
        return r;
    }

    private Task Journaliser(long utilisateurId, string action, RetourResume r, decimal? montant, CancellationToken ct) =>
        journal.EcrireAsync(utilisateurId, action, "retour_fournisseur", r.Id,
            new Dictionary<string, object?> { ["numero"] = r.Numero, ["montant"] = montant }, Permissions.AchatsRetourFournisseur, ct);
}

// ---- Cas d'usage : étiquettes internes -------------------------------------

public sealed class ListerLotsAEtiqueter(IAchats achats)
{
    public Task<IReadOnlyList<LotAEtiqueter>> ExecuterAsync(CancellationToken ct = default) => achats.LotsAEtiqueterAsync(ct);

    /// <summary>
    /// Lots à étiqueter filtrés et paginés par le serveur (règle des listes) : recherche sur le produit ou le n° de lot (sans majuscules
    /// ni accents), péremption jusqu'à une date, ou un lot précis (lien depuis une réception ou une alerte). Plus récents d'abord.
    /// </summary>
    public async Task<Page<LotAEtiqueter>> PageAsync(string? recherche, DateOnly? perimeAvant, long? lotId, DemandePage page, CancellationToken ct = default)
    {
        var lots = (await achats.LotsAEtiqueterAsync(ct))
            .Where(l => (lotId is null || l.LotId == lotId)
                && TexteRecherche.Contient(recherche, l.Produit, l.NumeroLot)
                && (perimeAvant is null || l.DatePeremption <= perimeAvant))
            .ToList();
        return new Page<LotAEtiqueter>(lots.Skip(page.Decalage).Take(page.Taille).ToList(), lots.Count, page.Page, page.Taille);
    }
}

public sealed record EtiquettesImprimables(IReadOnlyList<string> Codes, byte[] Pdf, string Zpl);

/// <summary>Réimpression des étiquettes d'une livraison (créées à la réception).</summary>
public sealed class ImprimerEtiquettesReception(IAchats achats, IPermissionService droits, IDocumentsAchats documents)
{
    public async Task<EtiquettesImprimables> ExecuterAsync(long receptionId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "imprimer des étiquettes", ct);
        var etiquettes = await achats.EtiquettesReceptionAsync(receptionId, ct);
        if (etiquettes.Count == 0)
            throw new RegleMetierException("Aucune boîte de cette livraison n'a d'étiquette interne en stock.");
        return new EtiquettesImprimables(etiquettes.Select(e => e.Code).ToList(), documents.PlancheEtiquettes(etiquettes), documents.EtiquettesZpl(etiquettes));
    }
}

/// <summary>Un code EAN-13 interne par boîte sans identité, en PDF (toute imprimante) et en ZPL (thermique).</summary>
public sealed class ImprimerEtiquettes(IAchats achats, IPermissionService droits, IDocumentsAchats documents)
{
    public const int MaxParLot = 500;

    public async Task<EtiquettesImprimables> ExecuterAsync(long lotId, int nombre, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "étiqueter des boîtes", ct);
        if (nombre is < 1 or > MaxParLot)
            throw new RegleMetierException($"Nombre d’étiquettes entre 1 et {MaxParLot}.");
        var lot = (await achats.LotsAEtiqueterAsync(ct)).FirstOrDefault(l => l.LotId == lotId);
        if (lot is null)
            // Lot absent de la liste : inexistant, ou toutes ses boîtes déjà identifiées
            throw new RegleMetierException(await achats.LotRetournableAsync(lotId, ct) is null
                ? "Lot introuvable."
                : "Toutes les boîtes de ce lot ont déjà une identité.");
        if (nombre > lot.BoitesSansIdentite)
            throw new RegleMetierException($"Lot {lot.NumeroLot} : {lot.BoitesSansIdentite} boîte(s) sans identité, {nombre} demandée(s).");
        var etiquettes = await achats.CreerEtiquettesAsync(lotId, nombre, ct);
        return new EtiquettesImprimables(etiquettes.Select(e => e.Code).ToList(), documents.PlancheEtiquettes(etiquettes), documents.EtiquettesZpl(etiquettes));
    }
}

// ---- Cas d'usage : lecture d'un Data Matrix à la réception ------------------

public sealed record BoiteLue(string Gtin, string? NumeroLot, DateOnly? Peremption, string? NumeroSerie, long? ProduitId, string? Produit);

/// <summary>Décode le Data Matrix GS1 d'une boîte (GTIN, lot, péremption, série) et retrouve le produit.</summary>
public sealed class LireDataMatrix(IAchats achats)
{
    public async Task<BoiteLue> ExecuterAsync(string? brut, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brut) || !Gs1DataMatrix.TryParse(brut.Trim(), out var dm) || dm is null)
            throw new RegleMetierException("Ce code n’est pas un Data Matrix de médicament (GTIN introuvable).");
        var produit = await achats.ProduitParGtinAsync(dm.Gtin, ct);
        return new BoiteLue(dm.Gtin, dm.Lot, dm.Peremption, dm.NumeroSerie, produit?.Id, produit?.Nom);
    }
}

// ---- Cas d'usage : conflits de lot à trancher à la réception ------------------------

/// <summary>Index : position de la ligne dans la saisie ; Peremptions : dates déjà en stock pour ce n° de lot.</summary>
public sealed record ConflitReception(int Index, ConflitLot Type, string Message, IReadOnlyList<DateOnly> Peremptions);

/// <summary>
/// Avant de valider : signale, ligne par ligne, une date différente d'un lot déjà en stock ou un lot bloqué,
/// pour que l'utilisateur choisisse (corriger la date ou créer un lot séparé ; mettre de côté ou entrer bloqué).
/// </summary>
public sealed class AnalyserReception(IAchats achats, IPermissionService droits)
{
    public async Task<IReadOnlyList<ConflitReception>> ExecuterAsync(long fournisseurId, IReadOnlyList<LigneReceptionSaisie> lignes,
        long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        var conflits = new List<ConflitReception>();
        for (var i = 0; i < lignes.Count; i++)
        {
            var l = lignes[i];
            var numero = RegleLot.Normaliser(l.NumeroLot);
            if (numero.Length == 0 || l.Quantite <= 0) continue;
            var memeNumero = await achats.LotsMemeNumeroAsync(l.ProduitId, fournisseurId, numero, ct);
            var dates = RegleLot.Propres(memeNumero).Select(x => x.DatePeremption).Distinct().ToList();
            foreach (var c in RegleLot.Conflits(memeNumero, l.DatePeremption))
                conflits.Add(new ConflitReception(i, c, c == ConflitLot.DateDifferente
                    ? $"{l.Nom}, lot {numero} : déjà en stock avec la péremption du {string.Join(", ", dates.Select(d => d.ToString("dd/MM/yyyy")))}, pas du {l.DatePeremption:dd/MM/yyyy}."
                    : $"{l.Nom}, lot {numero} : lot bloqué (rappel), vente interdite.", dates));
        }
        return conflits;
    }
}
