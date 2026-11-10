using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Tresorerie;

namespace Pharma.Application.Tresorerie;

/// <summary>Type : coffre ou banque. Solde = solde de départ + mouvements depuis la date de départ.</summary>
public sealed record CompteTresorerie(long Id, string Type, string Nom, string? Banque, string? NumeroCompte, bool RecoitCarte, bool Actif,
    decimal SoldeInitial, DateOnly DateInitiale, decimal Solde);

/// <summary>Origine : saisi (à la main) ou calcule (cartes, chèques remis, grossistes, remboursements par carte).</summary>
public sealed record MouvementTresorerie(DateOnly Date, string Sens, decimal Montant, string Nature, string? Categorie, string Libelle,
    string? Reference, string? Employe, string Origine);

public sealed record ChequeARemettre(long PaiementId, DateTime Date, decimal Montant, string Origine, string? Patient);

/// <summary>Caisse clôturée dont l'argent n'est pas encore au coffre ; Propose = compté moins le fond par défaut.</summary>
public sealed record CaisseAVerser(long SessionId, string Poste, DateTime FermeeLe, string OuvertePar, decimal Compte, decimal Propose);

public sealed record BordereauEnvoye(long Id, long OrganismeId, string Organisme, string Numero, decimal Montant);

public sealed record AVerser(IReadOnlyList<ChequeARemettre> Cheques, IReadOnlyList<CaisseAVerser> Caisses, IReadOnlyList<BordereauEnvoye> Bordereaux);

public sealed record CompteAEcrire(long? Id, string Nom, string? Banque, string? NumeroCompte, bool RecoitCarte, decimal SoldeInitial,
    DateOnly DateInitiale, bool Actif);

/// <summary>StatutBordereau : paye ou rejet_partiel, appliqué au bordereau rattaché.</summary>
public sealed record MouvementAEcrire(long CompteId, DateOnly Date, string Sens, decimal Montant, string Nature, string? Categorie, string? Libelle,
    string? Reference, long? OrganismeId, long? BordereauId, string? StatutBordereau, long UtilisateurId,
    IReadOnlyList<(long FactureId, decimal Montant)>? Factures = null);

public interface ITresorerie
{
    Task<IReadOnlyList<CompteTresorerie>> ComptesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MouvementTresorerie>> MouvementsAsync(long compteId, DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<AVerser> AVerserAsync(CancellationToken ct = default);
    /// <summary>Crée une banque (Id null) ou modifie un compte ; une seule banque reçoit les cartes.</summary>
    Task<long> EnregistrerCompteAsync(CompteAEcrire c, CancellationToken ct = default);
    Task<long> AjouterMouvementAsync(MouvementAEcrire m, CancellationToken ct = default);
    Task<long> TransfererAsync(long de, long vers, DateOnly date, decimal montant, string libelle, string? reference, long utilisateurId, CancellationToken ct = default);
    /// <summary>Refuse un chèque déjà remis (deux postes en même temps).</summary>
    Task<long> RemettreChequesAsync(long compteId, DateOnly date, string? reference, IReadOnlyList<long> paiements, long utilisateurId, CancellationToken ct = default);
    Task<long> VerserCaisseAsync(long sessionId, long coffreId, decimal montant, DateOnly date, bool pendantSession, bool automatique, long utilisateurId,
        CancellationToken ct = default);
}

public sealed class ConsulterTresorerie(ITresorerie tresorerie, IPermissionService droits)
{
    public async Task<IReadOnlyList<CompteTresorerie>> ComptesAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "consulter la trésorerie", ct);
        return await tresorerie.ComptesAsync(ct);
    }

    public async Task<IReadOnlyList<MouvementTresorerie>> MouvementsAsync(long compteId, DateOnly du, DateOnly au, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "consulter la trésorerie", ct);
        if (au < du) throw new RegleMetierException("La date de fin précède la date de début.");
        return await tresorerie.MouvementsAsync(compteId, du, au, ct);
    }

    public async Task<AVerser> AVerserAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "consulter la trésorerie", ct);
        return await tresorerie.AVerserAsync(ct);
    }
}

/// <summary>Banques déclarées par le titulaire (ou un employé qui a le droit) ; le coffre existe toujours, seul son départ se règle.</summary>
public sealed class EnregistrerCompteTresorerie(ITresorerie tresorerie, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(CompteAEcrire c, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "gérer les comptes de trésorerie", ct);
        if (string.IsNullOrWhiteSpace(c.Nom)) throw new RegleMetierException("Indiquez le nom du compte.");
        var existant = c.Id is { } id ? (await tresorerie.ComptesAsync(ct)).FirstOrDefault(x => x.Id == id) ?? throw new RegleMetierException("Compte introuvable.") : null;
        if (existant?.Type == "coffre" && (c.RecoitCarte || !c.Actif))
            throw new RegleMetierException("Le coffre ne reçoit pas les cartes et reste actif.");
        if (c.RecoitCarte && !c.Actif) throw new RegleMetierException("La banque qui reçoit les cartes doit rester active.");
        var nettoye = c with { Nom = c.Nom.Trim(), Banque = Vide(c.Banque), NumeroCompte = Vide(c.NumeroCompte) };
        var nouveau = await tresorerie.EnregistrerCompteAsync(nettoye, ct);
        await journal.EcrireAsync(utilisateurId, c.Id is null ? "compte_tresorerie_cree" : "compte_tresorerie_modifie", "compte_tresorerie", nouveau,
            new Dictionary<string, object?> { ["nom"] = nettoye.Nom, ["solde_initial"] = c.SoldeInitial, ["date_initiale"] = c.DateInitiale, ["carte"] = c.RecoitCarte },
            Permissions.TresorerieGerer, ct);
        return nouveau;
    }

    internal static string? Vide(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Dépense, encaissement divers ou versement d'un organisme (bordereau facultatif, marqué payé ou rejet partiel).</summary>
public sealed class SaisirMouvementTresorerie(ITresorerie tresorerie, IPermissionService droits, IHorloge horloge, IJournal journal,
    Factures.IFactures? factures = null)
{
    public async Task<long> ExecuterAsync(long compteId, DateOnly? date, string? nature, decimal montant, string? categorie, string? libelle,
        string? reference, long? organismeId, long? bordereauId, string? statutBordereau, long utilisateurId,
        IReadOnlyList<long>? facturesASolder = null, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "saisir un mouvement de trésorerie", ct);
        var n = RegleTresorerie.Nature(nature);
        var compte = await CompteActif(tresorerie, compteId, ct);
        var jour = date ?? horloge.Aujourdhui;
        RegleTresorerie.VerifierMouvement(n, montant, libelle, categorie, organismeId, jour, horloge.Aujourdhui, compte.DateInitiale);
        if (n != NatureMouvement.VersementOrganisme) (organismeId, bordereauId) = (null, null);
        if (bordereauId is not null && statutBordereau is not ("paye" or "rejet_partiel"))
            throw new RegleMetierException("Bordereau : indiquez s’il est payé en entier ou en rejet partiel.");
        if (n == NatureMouvement.Depense && compte.Solde < montant)
            throw new RegleMetierException($"Solde insuffisant sur {compte.Nom} ({compte.Solde:N2} DA).");

        // Factures clients soldées par cette entrée (versement d'organisme, encaissement) : dans l'ordre choisi, au plus leur reste
        IReadOnlyList<(long, decimal)>? imputations = null;
        if (facturesASolder is { Count: > 0 })
        {
            if (n == NatureMouvement.Depense) throw new RegleMetierException("Une dépense ne solde pas de facture client.");
            if (factures is null) throw new InvalidOperationException("Service des factures absent.");
            var ouvertes = (await factures.ListerAsync(new Factures.FiltreFactures(null, null, "a_encaisser", null, null), ct)).ToDictionary(f => f.Id);
            var choisies = facturesASolder.Distinct().Select(i => ouvertes.TryGetValue(i, out var f) ? f
                : throw new RegleMetierException("Une des factures choisies n’est plus à encaisser : rechargez la liste.")).ToList();
            if (n == NatureMouvement.VersementOrganisme && choisies.Any(f => f.OrganismeId != organismeId))
                throw new RegleMetierException("Ces factures ne sont pas toutes adressées à cet organisme.");
            imputations = Pharma.Domain.Factures.RegleFacture.Imputer(montant, choisies.Select(f => (f.Id, f.Reste)));
        }

        var id = await tresorerie.AjouterMouvementAsync(new MouvementAEcrire(compteId, jour, n == NatureMouvement.Depense ? "sortie" : "entree", montant,
            n.Code(), n == NatureMouvement.Depense ? categorie : null, EnregistrerCompteTresorerie.Vide(libelle), EnregistrerCompteTresorerie.Vide(reference),
            organismeId, bordereauId, bordereauId is null ? null : statutBordereau, utilisateurId, imputations), ct);
        await journal.EcrireAsync(utilisateurId, "mouvement_tresorerie", "mouvement_tresorerie", id,
            new Dictionary<string, object?> { ["compte"] = compte.Nom, ["nature"] = n.Code(), ["montant"] = montant, ["categorie"] = categorie, ["libelle"] = libelle },
            Permissions.TresorerieGerer, ct);
        return id;
    }

    internal static async Task<CompteTresorerie> CompteActif(ITresorerie tresorerie, long id, CancellationToken ct) =>
        (await tresorerie.ComptesAsync(ct)).FirstOrDefault(c => c.Id == id && c.Actif) ?? throw new RegleMetierException("Compte introuvable ou inactif.");
}

/// <summary>Transfert entre comptes : dépôt d'espèces du coffre à la banque, retrait, virement entre banques.</summary>
public sealed class TransfererTresorerie(ITresorerie tresorerie, IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public async Task<long> ExecuterAsync(long de, long vers, DateOnly? date, decimal montant, string? reference, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "transférer entre comptes", ct);
        RegleTresorerie.VerifierTransfert(de, vers);
        var source = await SaisirMouvementTresorerie.CompteActif(tresorerie, de, ct);
        var cible = await SaisirMouvementTresorerie.CompteActif(tresorerie, vers, ct);
        var jour = date ?? horloge.Aujourdhui;
        RegleTresorerie.VerifierMontantEtDate(montant, jour, horloge.Aujourdhui, source.DateInitiale > cible.DateInitiale ? source.DateInitiale : cible.DateInitiale);
        if (source.Solde < montant) throw new RegleMetierException($"Solde insuffisant sur {source.Nom} ({source.Solde:N2} DA).");
        var id = await tresorerie.TransfererAsync(de, vers, jour, montant, $"{source.Nom} → {cible.Nom}", EnregistrerCompteTresorerie.Vide(reference), utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "transfert_tresorerie", "mouvement_tresorerie", id,
            new Dictionary<string, object?> { ["de"] = source.Nom, ["vers"] = cible.Nom, ["montant"] = montant }, Permissions.TresorerieGerer, ct);
        return id;
    }
}

/// <summary>Remise en banque de chèques reçus au comptoir.</summary>
public sealed class RemettreCheques(ITresorerie tresorerie, IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public async Task<long> ExecuterAsync(long compteId, DateOnly? date, string? reference, IReadOnlyList<long> paiements, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "remettre des chèques en banque", ct);
        if (paiements.Count == 0) throw new RegleMetierException("Cochez les chèques remis.");
        var compte = await SaisirMouvementTresorerie.CompteActif(tresorerie, compteId, ct);
        if (compte.Type != "banque") throw new RegleMetierException("Les chèques se remettent sur un compte bancaire.");
        var jour = date ?? horloge.Aujourdhui;
        if (jour > horloge.Aujourdhui) throw new RegleMetierException("La date ne peut pas être dans le futur.");
        var id = await tresorerie.RemettreChequesAsync(compteId, jour, EnregistrerCompteTresorerie.Vide(reference), paiements.Distinct().ToList(), utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "remise_cheques", "remise_cheques", id,
            new Dictionary<string, object?> { ["banque"] = compte.Nom, ["cheques"] = paiements.Count }, Permissions.TresorerieGerer, ct);
        return id;
    }
}

/// <summary>
/// Argent d'une caisse porté au coffre : après la clôture (caisse à verser), ou pendant la journée depuis la caisse ouverte
/// du poste (l'attendu de cette caisse diminue d'autant).
/// </summary>
public sealed class VerserCaisseAuCoffre(ITresorerie tresorerie, ISessionsCaisse sessions, IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public async Task<long> ApresClotureAsync(long sessionId, decimal montant, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "verser une caisse au coffre", ct);
        var caisse = (await tresorerie.AVerserAsync(ct)).Caisses.FirstOrDefault(c => c.SessionId == sessionId)
                     ?? throw new RegleMetierException("Cette caisse est déjà versée au coffre (ou n’est pas clôturée).");
        RegleTresorerie.VerifierVersementCaisse(montant, caisse.Compte);
        return await VerserAsync(sessionId, montant, pendant: false, automatique: false, utilisateurId, caisse.Poste, ct);
    }

    public async Task<long> PendantJourneeAsync(decimal montant, string? poste, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "porter l’argent de la caisse au coffre", ct);
        var nom = PosteCaisse.Exiger(poste);
        var session = await sessions.SessionDuPosteAsync(nom, ct) ?? throw new RegleMetierException("Aucune caisse ouverte sur ce poste.");
        var dansTiroir = session.FondCaisse + await sessions.VentesEspecesAsync(session.Id, ct);
        if (montant <= 0) throw new RegleMetierException("Le montant doit être supérieur à 0.");
        if (montant > dansTiroir) throw new RegleMetierException($"Le tiroir ne contient en principe que {dansTiroir:N2} DA.");
        return await VerserAsync(session.Id, montant, pendant: true, automatique: false, utilisateurId, nom, ct);
    }

    private async Task<long> VerserAsync(long sessionId, decimal montant, bool pendant, bool automatique, long utilisateurId, string poste, CancellationToken ct)
    {
        var coffre = (await tresorerie.ComptesAsync(ct)).First(c => c.Type == "coffre");
        var id = await tresorerie.VerserCaisseAsync(sessionId, coffre.Id, montant, horloge.Aujourdhui, pendant, automatique, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "versement_caisse_coffre", "mouvement_tresorerie", id,
            new Dictionary<string, object?> { ["poste"] = poste, ["session"] = sessionId, ["montant"] = montant, ["pendant_session"] = pendant, ["automatique"] = automatique },
            automatique ? null : Permissions.TresorerieGerer, ct);
        return id;
    }

    /// <summary>
    /// Après une clôture (pas une relève) : si le titulaire a choisi « automatique », le compté moins le fond par défaut part au coffre.
    /// Rien à faire en « manuel ». Retourne le montant versé.
    /// </summary>
    public async Task<decimal> ApresClotureAutomatiqueAsync(long sessionId, long utilisateurId, CancellationToken ct = default)
    {
        var parametres = await sessions.ParametresAsync(ct);
        if (parametres.VersementCloture != "automatique") return 0;
        var caisse = (await tresorerie.AVerserAsync(ct)).Caisses.FirstOrDefault(c => c.SessionId == sessionId);
        if (caisse is null) return 0;
        var montant = RegleTresorerie.VersementAutomatique(caisse.Compte, parametres.FondCaisseDefaut);
        if (montant <= 0) return 0;
        await VerserAsync(sessionId, montant, pendant: false, automatique: true, utilisateurId, caisse.Poste, ct);
        return montant;
    }
}
