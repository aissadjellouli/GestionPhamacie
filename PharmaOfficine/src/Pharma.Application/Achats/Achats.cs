using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

// ---- Modèles ----------------------------------------------------------------

public sealed record Fournisseur(long Id, string RaisonSociale, string? Telephone, decimal Solde, int? DelaiRepriseJours);

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
    DateTime Date, int NombreLots, int NombreBoites, decimal MontantTtc);

/// <summary>Lot créé à la réception, avec le nombre de boîtes encore sans identité (à étiqueter).</summary>
public sealed record LotRecu(long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite, int BoitesSansIdentite);

public sealed record ReceptionEnregistree(long ReceptionId, decimal MontantTtc, IReadOnlyList<LotRecu> Lots);

public sealed record NouvelleReception(
    long FournisseurId, long? CommandeId, string NumeroBl, string? NumeroFacture,
    IReadOnlyList<LigneReceptionSaisie> Lignes, IReadOnlyDictionary<long, decimal> TauxTva,
    decimal MontantTtc, long UtilisateurId);

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

// ---- Ports ------------------------------------------------------------------

public interface IAchats
{
    Task<IReadOnlyList<Fournisseur>> FournisseursAsync(CancellationToken ct = default);
    Task<Fournisseur?> FournisseurAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<CommandeResume>> CommandesAsync(int limite, CancellationToken ct = default);
    Task<CommandeDetail?> CommandeAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<SuggestionCommande>> SuggestionsAsync(CancellationToken ct = default);
    Task<DernierAchat?> DernierAchatAsync(long produitId, long fournisseurId, CancellationToken ct = default);
    Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default);
    /// <summary>Produit dont le code-barres correspond au GTIN-14 (EAN-13 précédé de 0).</summary>
    Task<(long Id, string Nom)?> ProduitParGtinAsync(string gtin, CancellationToken ct = default);

    /// <summary>Brouillon du grossiste, créé s'il n'existe pas. Renvoie son identifiant.</summary>
    Task<long> BrouillonAsync(long fournisseurId, long utilisateurId, CancellationToken ct = default);
    /// <summary>Ajoute la quantité si le produit est déjà sur le brouillon, sinon crée la ligne.</summary>
    Task AjouterLigneAsync(long commandeId, long produitId, int quantite, decimal prixHt, CancellationToken ct = default);
    Task ModifierLigneAsync(long commandeId, long ligneId, int quantite, decimal prixHt, CancellationToken ct = default);
    Task SupprimerLigneAsync(long commandeId, long ligneId, CancellationToken ct = default);
    Task ChangerStatutCommandeAsync(long commandeId, string statut, CancellationToken ct = default);

    Task<IReadOnlyDictionary<long, decimal>> TauxTvaAsync(IEnumerable<long> produitIds, CancellationToken ct = default);
    /// <summary>Réception, lots, boîtes sérialisées, mouvements, quantités reçues et dette du grossiste : une seule transaction.</summary>
    Task<ReceptionEnregistree> ReceptionnerAsync(NouvelleReception reception, CancellationToken ct = default);
    Task<IReadOnlyList<ReceptionResume>> ReceptionsAsync(int limite, CancellationToken ct = default);

    Task<LotRetournable?> LotRetournableAsync(long lotId, CancellationToken ct = default);
    /// <summary>Lots encore en stock d'un produit (tous grossistes), éventuellement d'un numéro de lot donné.</summary>
    Task<IReadOnlyList<long>> LotsEnStockAsync(long produitId, string? numeroLot, CancellationToken ct = default);
    /// <summary>Retour en brouillon : le stock du lot et les boîtes désignées sortent tout de suite (mis de côté).</summary>
    Task<long> CreerRetourAsync(NouveauRetour retour, CancellationToken ct = default);
    Task<RetourResume?> RetourAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<RetourResume>> RetoursAsync(int limite, CancellationToken ct = default);
    Task MarquerEnvoyeAsync(long retourId, CancellationToken ct = default);
    /// <summary>Avoir reçu : enregistré sur le retour et déduit de la dette du grossiste.</summary>
    Task EnregistrerAvoirAsync(long retourId, decimal montant, CancellationToken ct = default);
    /// <summary>Annulé ou refusé : le stock et les boîtes reviennent en stock.</summary>
    Task RefuserRetourAsync(long retourId, long utilisateurId, string commentaire, CancellationToken ct = default);

    Task<IReadOnlyList<LotAEtiqueter>> LotsAEtiqueterAsync(CancellationToken ct = default);
    /// <summary>Crée les boîtes et leurs codes EAN-13 internes (fonction creer_boites_internes).</summary>
    Task<IReadOnlyList<EtiquetteBoite>> CreerEtiquettesAsync(long lotId, int nombre, CancellationToken ct = default);
}

public interface IDocumentsAchats
{
    byte[] BonCommande(CommandeDetail commande, Fournisseur fournisseur, string? pharmacie);
    byte[] BonRetour(RetourResume retour, string? pharmacie);
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
    public Task<IReadOnlyList<CommandeResume>> ExecuterAsync(CancellationToken ct = default) => achats.CommandesAsync(100, ct);
}

public sealed class ObtenirCommande(IAchats achats)
{
    public Task<CommandeDetail?> ExecuterAsync(long id, CancellationToken ct = default) => achats.CommandeAsync(id, ct);
}

public sealed class ListerSuggestionsCommande(IAchats achats)
{
    public Task<IReadOnlyList<SuggestionCommande>> ExecuterAsync(CancellationToken ct = default) => achats.SuggestionsAsync(ct);
}

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
        if (await achats.FournisseurAsync(fournisseurId, ct) is null)
            throw new RegleMetierException("Grossiste introuvable.");
        var prix = prixHt ?? (await achats.DernierAchatAsync(produitId, fournisseurId, ct))?.PrixAchatHt
            ?? throw new RegleMetierException($"{nom} : aucun achat précédent, saisissez le prix d’achat.");
        RegleAchats.VerifierCommande([new LigneCommandeSaisie(produitId, nom, quantite, prix)]);

        var commandeId = await achats.BrouillonAsync(fournisseurId, utilisateurId, ct);
        await achats.AjouterLigneAsync(commandeId, produitId, quantite, prix, ct);
        return commandeId;
    }
}

/// <summary>Modification d'une ligne, uniquement sur un brouillon. Quantité 0 : la ligne est retirée.</summary>
public sealed class ModifierLigneCommande(IAchats achats, IPermissionService droits)
{
    public async Task ExecuterAsync(long commandeId, long ligneId, int quantite, decimal prixHt, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsCommander, "modifier une commande", ct);
        var c = await achats.CommandeAsync(commandeId, ct) ?? throw new RegleMetierException("Commande introuvable.");
        if (c.Entete.Statut != "brouillon")
            throw new RegleMetierException($"La commande {c.Entete.Numero} est déjà émise : elle ne se modifie plus.");
        var ligne = c.Lignes.FirstOrDefault(l => l.Id == ligneId) ?? throw new RegleMetierException("Ligne introuvable.");
        if (quantite == 0) { await achats.SupprimerLigneAsync(commandeId, ligneId, ct); return; }
        RegleAchats.VerifierCommande([new LigneCommandeSaisie(ligne.ProduitId, ligne.Produit, quantite, prixHt)]);
        await achats.ModifierLigneAsync(commandeId, ligneId, quantite, prixHt, ct);
    }
}

/// <summary>Brouillon → envoyée ; renvoie le bon de commande PDF à transmettre au grossiste.</summary>
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

        await achats.ChangerStatutCommandeAsync(commandeId, "envoyee", ct);
        await journal.EcrireAsync(utilisateurId, "commande_emise", "commande_fournisseur", commandeId,
            new Dictionary<string, object?> { ["numero"] = c.Entete.Numero, ["montant_ht"] = c.Entete.MontantHt }, Permissions.AchatsCommander, ct);
        var detail = c with { Entete = c.Entete with { Statut = "envoyee" } };
        var nom = (await pharmacie.LireAsync(ct)).Nom;
        return new DocumentGenere(documents.BonCommande(detail, f, nom), $"{c.Entete.Numero}.pdf");
    }
}

/// <summary>Bon de commande PDF (réimpression), quel que soit le statut.</summary>
public sealed class ImprimerCommande(IAchats achats, IDocumentsAchats documents, Administration.IPharmacie pharmacie)
{
    public async Task<DocumentGenere?> ExecuterAsync(long commandeId, CancellationToken ct = default)
    {
        if (await achats.CommandeAsync(commandeId, ct) is not { } c) return null;
        var f = await achats.FournisseurAsync(c.Entete.FournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.");
        var nom = (await pharmacie.LireAsync(ct)).Nom;
        return new DocumentGenere(documents.BonCommande(c, f, nom), $"{c.Entete.Numero}.pdf");
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
        await achats.ChangerStatutCommandeAsync(commandeId, "annulee", ct);
        await journal.EcrireAsync(utilisateurId, "commande_annulee", "commande_fournisseur", commandeId,
            new Dictionary<string, object?> { ["numero"] = c.Entete.Numero }, Permissions.AchatsCommander, ct);
    }
}

// ---- Cas d'usage : réception -----------------------------------------------

public sealed record SaisieReception(
    long FournisseurId, long? CommandeId, string? NumeroBl, string? NumeroFacture, IReadOnlyList<LigneReceptionSaisie> Lignes);

/// <summary>
/// Entrée en stock d'une livraison : un lot par ligne, rattaché à la réception et au grossiste.
/// La dette du grossiste augmente du total TTC (TVA du catalogue).
/// </summary>
public sealed class Receptionner(IAchats achats, IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public async Task<ReceptionEnregistree> ExecuterAsync(SaisieReception s, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "réceptionner une livraison", ct);
        if (await achats.FournisseurAsync(s.FournisseurId, ct) is null)
            throw new RegleMetierException("Grossiste introuvable.");

        var lignes = s.Lignes.Select(l => l with
        {
            NumeroLot = l.NumeroLot?.Trim() ?? "",
            Gtin = string.IsNullOrWhiteSpace(l.Gtin) ? null : l.Gtin.Trim(),
            NumerosSerie = l.NumerosSerie?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList()
        }).ToList();
        RegleAchats.VerifierReception(s.NumeroBl, lignes, horloge.Aujourdhui);
        if (lignes.GroupBy(l => (l.ProduitId, l.NumeroLot.ToUpperInvariant())).Any(g => g.Count() > 1))
            throw new RegleMetierException("Un même lot figure deux fois : regroupez les quantités sur une ligne.");

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
        var ttc = lignes.Sum(l => RegleAchats.MontantTtc(l.Quantite, l.PrixAchatHt, tva[l.ProduitId]));

        var r = await achats.ReceptionnerAsync(new NouvelleReception(
            s.FournisseurId, s.CommandeId, s.NumeroBl!.Trim(), string.IsNullOrWhiteSpace(s.NumeroFacture) ? null : s.NumeroFacture.Trim(),
            lignes, tva, ttc, utilisateurId), ct);
        await journal.EcrireAsync(utilisateurId, "reception", "reception", r.ReceptionId,
            new Dictionary<string, object?> { ["bl"] = s.NumeroBl, ["lots"] = r.Lots.Count, ["montant_ttc"] = ttc }, Permissions.AchatsReceptionner, ct);
        return r;
    }
}

public sealed class ListerReceptions(IAchats achats)
{
    public Task<IReadOnlyList<ReceptionResume>> ExecuterAsync(CancellationToken ct = default) => achats.ReceptionsAsync(100, ct);
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
    public Task<IReadOnlyList<RetourResume>> ExecuterAsync(CancellationToken ct = default) => achats.RetoursAsync(100, ct);
}

/// <summary>
/// Prépare un retour au grossiste d'origine des lots. Les boîtes sortent du stock dès la préparation.
/// Une boîte identifiée (étiquette ou Data Matrix) doit être scannée ; les autres sont prises dans le reste du lot.
/// </summary>
public sealed class PreparerRetour(IAchats achats, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(MotifRetour motif, IReadOnlyList<LotARetourner> lots, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRetourFournisseur, "faire un retour fournisseur", ct);
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

        var attendu = lignes.Sum(l => Math.Round(l.Item2 * l.Item1.PrixAchatHt, 2));
        var id = await achats.CreerRetourAsync(new NouveauRetour(fournisseurs[0], motif, lignes, attendu, utilisateurId), ct);
        await journal.EcrireAsync(utilisateurId, "retour_prepare", "retour_fournisseur", id,
            new Dictionary<string, object?> { ["motif"] = motif.Code(), ["boites"] = lignes.Sum(l => l.Item2), ["montant_attendu"] = attendu },
            Permissions.AchatsRetourFournisseur, ct);
        return id;
    }
}

/// <summary>Suivi d'un retour : envoi (bon de retour PDF), avoir reçu, ou annulation / refus avec remise en stock.</summary>
public sealed class SuivreRetour(IAchats achats, IPermissionService droits, IDocumentsAchats documents, Administration.IPharmacie pharmacie, IJournal journal)
{
    public async Task<DocumentGenere> EnvoyerAsync(long retourId, long utilisateurId, CancellationToken ct = default)
    {
        var r = await Charger(retourId, utilisateurId, StatutRetour.Envoye, ct);
        await achats.MarquerEnvoyeAsync(retourId, ct);
        await Journaliser(utilisateurId, "retour_envoye", r, null, ct);
        var maj = await achats.RetourAsync(retourId, ct) ?? r;
        return new DocumentGenere(documents.BonRetour(maj, (await pharmacie.LireAsync(ct)).Nom), $"{r.Numero}.pdf");
    }

    public async Task EnregistrerAvoirAsync(long retourId, decimal montant, long utilisateurId, CancellationToken ct = default)
    {
        if (montant < 0) throw new RegleMetierException("Le montant de l’avoir ne peut pas être négatif.");
        var r = await Charger(retourId, utilisateurId, StatutRetour.AvoirRecu, ct);
        await achats.EnregistrerAvoirAsync(retourId, montant, ct);
        await Journaliser(utilisateurId, "avoir_recu", r, montant, ct);
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

    public async Task<DocumentGenere?> ImprimerAsync(long retourId, CancellationToken ct = default) =>
        await achats.RetourAsync(retourId, ct) is { } r
            ? new DocumentGenere(documents.BonRetour(r, (await pharmacie.LireAsync(ct)).Nom), $"{r.Numero}.pdf")
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
}

public sealed record EtiquettesImprimables(IReadOnlyList<string> Codes, byte[] Pdf, string Zpl);

/// <summary>Un code EAN-13 interne par boîte sans identité, en PDF (toute imprimante) et en ZPL (thermique).</summary>
public sealed class ImprimerEtiquettes(IAchats achats, IPermissionService droits, IDocumentsAchats documents)
{
    public const int MaxParLot = 500;

    public async Task<EtiquettesImprimables> ExecuterAsync(long lotId, int nombre, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "étiqueter des boîtes", ct);
        if (nombre is < 1 or > MaxParLot)
            throw new RegleMetierException($"Nombre d’étiquettes entre 1 et {MaxParLot}.");
        var lot = (await achats.LotsAEtiqueterAsync(ct)).FirstOrDefault(l => l.LotId == lotId)
            ?? throw new RegleMetierException("Toutes les boîtes de ce lot ont déjà une identité.");
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
