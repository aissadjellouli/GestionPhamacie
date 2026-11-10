using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Caisse;

/// <summary>VersementCloture : « manuel » (saisi en Trésorerie) ou « automatique » (compté moins le fond par défaut porté au coffre à la clôture).</summary>
public sealed record ParametresCaisse(bool ComptageAveugle, decimal FondCaisseDefaut, decimal SeuilEcart, int DeconnexionMinutes, string VersementCloture = "manuel");

/// <summary>Données d'une session pour l'écran et le rapport.</summary>
public sealed record DetailSession(
    long Id, long UtilisateurId, string Employe, string Poste, DateTime OuverteLe, decimal FondCaisse,
    DateTime? FermeeLe, decimal? VentesEspeces, decimal? Attendu, decimal? Compte, decimal? Ecart,
    int NombreVentes, decimal TotalVentes, IReadOnlyList<LigneComptage> Comptage, IReadOnlyList<string>? Vendeurs = null);

public sealed record Vendeur(long Id, string Nom);

/// <summary>
/// Une caisse ouverte par poste (un tiroir) : plusieurs employés y vendent, chaque vente garde son vendeur.
/// </summary>
public interface ISessionsCaisse
{
    Task<SessionCaisse?> SessionDuPosteAsync(string poste, CancellationToken ct = default);
    /// <param name="releveDe">Session clôturée par une relève, juste avant celle-ci.</param>
    Task<long> OuvrirAsync(long utilisateurId, string poste, decimal fondCaisse, long? releveDe = null, CancellationToken ct = default);
    /// <summary>Employés qui ont vendu pendant la session.</summary>
    Task<IReadOnlyList<Vendeur>> VendeursAsync(long sessionId, CancellationToken ct = default);
    Task<decimal> VentesEspecesAsync(long sessionId, CancellationToken ct = default);
    Task CloturerAsync(long sessionId, Comptage comptage, ResultatCloture resultat, CancellationToken ct = default);
    Task<DetailSession?> DetailAsync(long sessionId, CancellationToken ct = default);
    Task<ParametresCaisse> ParametresAsync(CancellationToken ct = default);
    Task ModifierParametresAsync(ParametresCaisse parametres, CancellationToken ct = default);
}

/// <summary>État de la caisse pour le poste. Les montants attendus ne sont donnés qu'avec le droit de voir les écarts.</summary>
public sealed record EtatCaisse(
    long? SessionId, DateTime? OuverteLe, decimal? FondCaisse, string? Poste,
    ParametresCaisse Parametres, bool PeutVoirEcart, decimal? VentesEspecesVisibles, string? OuvertePar = null);

public static class PosteCaisse
{
    /// <summary>Le poste identifie le tiroir : sans nom de poste, pas de caisse.</summary>
    public static string Exiger(string? poste) => string.IsNullOrWhiteSpace(poste)
        ? throw new RegleMetierException("Poste inconnu : le poste doit être nommé (PHARMA_POSTE) pour utiliser la caisse.")
        : poste.Trim();
}

public sealed class ObtenirEtatCaisse(ISessionsCaisse sessions, IPermissionService droits)
{
    public async Task<EtatCaisse> ExecuterAsync(long utilisateurId, string? poste, CancellationToken ct = default)
    {
        var parametres = await sessions.ParametresAsync(ct);
        var voirEcart = await droits.APermissionAsync(utilisateurId, Permissions.CaisseVoirEcart, ct);
        var session = await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct);
        if (session is null)
            return new EtatCaisse(null, null, null, null, parametres, voirEcart, null);

        // Comptage aveugle : l'attendu reste caché tant que la caisse n'est pas comptée
        decimal? especes = !parametres.ComptageAveugle && voirEcart
            ? await sessions.VentesEspecesAsync(session.Id, ct)
            : null;
        return new EtatCaisse(session.Id, session.OuverteLe, session.FondCaisse, session.Poste, parametres, voirEcart, especes, session.OuvertePar);
    }
}

public sealed class OuvrirSessionCaisse(ISessionsCaisse sessions)
{
    public async Task<long> ExecuterAsync(long utilisateurId, string? poste, decimal? fondCaisse, CancellationToken ct = default)
    {
        var nomPoste = PosteCaisse.Exiger(poste);
        if (await sessions.SessionDuPosteAsync(nomPoste, ct) is { } ouverte)
            throw new RegleMetierException(
                $"La caisse de ce poste est déjà ouverte (par {ouverte.OuvertePar ?? "un collègue"}) : vendez dessus, ou faites une relève.");
        var fond = fondCaisse ?? (await sessions.ParametresAsync(ct)).FondCaisseDefaut;
        SessionCaisse.VerifierFond(fond);
        return await sessions.OuvrirAsync(utilisateurId, nomPoste, fond, null, ct);
    }
}

/// <summary>
/// Résultat de clôture tel qu'affiché : sans le droit « caisse.voir_ecart », seul le total compté est rendu.
/// </summary>
public sealed record ClotureVue(
    long SessionId, decimal Compte, decimal? FondCaisse, decimal? VentesEspeces, decimal? Attendu,
    decimal? Ecart, bool? AuDelaDuSeuil, decimal? SeuilEcart);

public sealed class CloturerSessionCaisse(ISessionsCaisse sessions, IPermissionService droits, IJournal journal, Notifications.IAlertesTitulaire? alertes = null)
{
    public async Task<ClotureVue> ExecuterAsync(long utilisateurId, string? poste, IReadOnlyDictionary<string, decimal> saisies, CancellationToken ct = default)
    {
        var session = await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct)
                      ?? throw new RegleMetierException("Aucune caisse ouverte à clôturer sur ce poste.");
        var parametres = await sessions.ParametresAsync(ct);
        var comptage = Comptage.Depuis(saisies);
        var resultat = session.Cloturer(comptage, await sessions.VentesEspecesAsync(session.Id, ct), parametres.SeuilEcart);

        await sessions.CloturerAsync(session.Id, comptage, resultat, ct);
        if (resultat.AuDelaDuSeuil)
            await journal.EcrireAsync(utilisateurId, "ecart_caisse", "session_caisse", session.Id,
                new Dictionary<string, object?>
                {
                    ["attendu"] = resultat.Attendu, ["compte"] = resultat.Compte,
                    ["ecart"] = resultat.Ecart, ["seuil"] = parametres.SeuilEcart
                }, null, ct);

        // Alerte du titulaire (SMS au-delà du seuil d'alerte) : la clôture reste valide même si la mise en file échoue
        if (alertes is not null && resultat.Ecart != 0)
        {
            try { await alertes.EcartCaisseAsync((await sessions.DetailAsync(session.Id, ct))?.Employe ?? "employé", session.Poste, resultat.Ecart, parametres.SeuilEcart, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        if (!await droits.APermissionAsync(utilisateurId, Permissions.CaisseVoirEcart, ct))
            return new ClotureVue(session.Id, resultat.Compte, null, null, null, null, null, null);
        return new ClotureVue(session.Id, resultat.Compte, resultat.FondCaisse, resultat.VentesEspeces,
            resultat.Attendu, resultat.Ecart, resultat.AuDelaDuSeuil, parametres.SeuilEcart);
    }
}

public sealed record Releve(ClotureVue Cloture, long NouvelleSessionId);

/// <summary>
/// Relève au changement d'équipe (facultative) : le tiroir est compté, la session se clôture avec ce comptage
/// et une nouvelle s'ouvre aussitôt, avec l'argent compté comme fond. L'écart de chaque période est ainsi attribué.
/// </summary>
public sealed class ReleverCaisse(ISessionsCaisse sessions, CloturerSessionCaisse cloturer, IJournal journal)
{
    public async Task<Releve> ExecuterAsync(long utilisateurId, string? poste, IReadOnlyDictionary<string, decimal> saisies, CancellationToken ct = default)
    {
        var nomPoste = PosteCaisse.Exiger(poste);
        var precedente = await sessions.SessionDuPosteAsync(nomPoste, ct) ?? throw new RegleMetierException("Aucune caisse ouverte sur ce poste.");
        var cloture = await cloturer.ExecuterAsync(utilisateurId, nomPoste, saisies, ct);
        var nouvelle = await sessions.OuvrirAsync(utilisateurId, nomPoste, cloture.Compte, precedente.Id, ct);
        await journal.EcrireAsync(utilisateurId, "releve_caisse", "session_caisse", nouvelle,
            new Dictionary<string, object?> { ["poste"] = nomPoste, ["session_precedente"] = precedente.Id, ["fond"] = cloture.Compte }, null, ct);
        return new Releve(cloture, nouvelle);
    }
}

public interface IDocumentsPdf
{
    /// <summary>Rapport de clôture ; avec <paramref name="avecEcart"/> faux, ni attendu ni écart n'y figurent.</summary>
    byte[] RapportCloture(DetailSession session, bool avecEcart, decimal seuilEcart, string? pharmacie);
}

/// <summary>Rapport PDF d'une session : la sienne, ou n'importe laquelle avec le droit de voir les écarts.</summary>
public sealed class ObtenirRapportCloture(ISessionsCaisse sessions, IPermissionService droits, IDocumentsPdf pdf, Administration.IPharmacie pharmacie)
{
    public async Task<(byte[] Contenu, string NomFichier)?> ExecuterAsync(long sessionId, long utilisateurId, CancellationToken ct = default)
    {
        var detail = await sessions.DetailAsync(sessionId, ct);
        if (detail is null) return null;
        var voirEcart = await droits.APermissionAsync(utilisateurId, Permissions.CaisseVoirEcart, ct);
        // Caisse partagée : celui qui l'a ouverte et ceux qui y ont vendu ont accès au rapport
        var vendeurs = await sessions.VendeursAsync(sessionId, ct);
        if (detail.UtilisateurId != utilisateurId && vendeurs.All(v => v.Id != utilisateurId) && !voirEcart)
            throw new DroitRefuseException(Permissions.CaisseVoirEcart, "consulter la clôture d’une caisse où vous n’avez pas vendu");
        detail = detail with { Vendeurs = vendeurs.Select(v => v.Nom).ToList() };
        if (detail.FermeeLe is null)
            throw new RegleMetierException("La session n’est pas encore clôturée.");
        var parametres = await sessions.ParametresAsync(ct);
        var nomPharmacie = (await pharmacie.LireAsync(ct)).Nom;
        var contenu = pdf.RapportCloture(detail, voirEcart, parametres.SeuilEcart, nomPharmacie);
        return (contenu, $"cloture_{detail.FermeeLe.Value.ToLocalTime():yyyyMMdd_HHmm}_{sessionId}.pdf");
    }
}

public sealed class ObtenirParametresCaisse(ISessionsCaisse sessions)
{
    public Task<ParametresCaisse> ExecuterAsync(CancellationToken ct = default) => sessions.ParametresAsync(ct);
}

public sealed class ModifierParametresCaisse(ISessionsCaisse sessions, IJournal journal)
{
    public async Task ExecuterAsync(ParametresCaisse p, long utilisateurId, CancellationToken ct = default)
    {
        if (p.FondCaisseDefaut < 0 || p.SeuilEcart < 0)
            throw new RegleMetierException("Le fond de caisse et le seuil d’écart ne peuvent pas être négatifs.");
        if (p.DeconnexionMinutes is < 0 or > 240)
            throw new RegleMetierException("La déconnexion automatique doit être comprise entre 0 (désactivée) et 240 minutes.");
        if (p.VersementCloture is not ("manuel" or "automatique"))
            throw new RegleMetierException("Versement des caisses au coffre : « manuel » ou « automatique ».");
        var avant = await sessions.ParametresAsync(ct);
        await sessions.ModifierParametresAsync(p, ct);
        await journal.EcrireAsync(utilisateurId, "parametres_caisse", "parametre_caisse", 1,
            new Dictionary<string, object?> { ["avant"] = avant, ["apres"] = p }, Permissions.AdminParametres, ct);
    }
}

public sealed class ConsulterJournal(IJournalLecture journal)
{
    public Task<IReadOnlyList<EntreeJournal>> ExecuterAsync(CancellationToken ct = default) => journal.DernieresAsync(200, ct);
}
