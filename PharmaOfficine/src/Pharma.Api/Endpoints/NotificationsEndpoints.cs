using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Notifications;
using Pharma.Application.Sauvegardes;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Notifications : réservées au pharmacien titulaire (lui seul renseigne les destinataires).
/// Sauvegardes : droit admin.sauvegardes.
/// </summary>
internal static class NotificationsEndpoints
{
    public static void MapNotifications(this RouteGroupBuilder api)
    {
        var n = api.MapGroup("/admin/notifications");

        n.MapGet("/reglages", async (ObtenirReglagesNotification obtenir, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await obtenir.ExecuterAsync(user.UtilisateurId(), ct))))
           .ExigerTitulaire();

        n.MapPut("/reglages", async (ModificationReglagesNotificationDto d, ModifierReglagesNotification modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new ModificationReglagesNotification(d.SmtpServeur, d.SmtpPort, d.SmtpTls, d.SmtpExpediteur,
                    d.SmtpIdentifiant, d.NouveauMotDePasse, d.ModemPort, d.HeureRapport, d.SmsSeuilEcart, d.SmsLotBloque, d.SmsSauvegarde),
                    user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        n.MapGet("/destinataires", async (ListerDestinataires lister, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(user.UtilisateurId(), ct)).Select(VersDto)))
           .ExigerTitulaire();

        n.MapPost("/destinataires", async (SaisieDestinataireDto d, EnregistrerDestinataire enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(null, VersSaisie(d), user.UtilisateurId(), ct))))
           .ExigerTitulaire();

        n.MapPut("/destinataires/{id:long}", async (long id, SaisieDestinataireDto d, EnregistrerDestinataire enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(id, VersSaisie(d), user.UtilisateurId(), ct))))
           .ExigerTitulaire();

        n.MapPost("/destinataires/{id:long}/validation", async (long id, ValidationDestinataireDto d, ValiderDestinataire valider, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await valider.ExecuterAsync(id, d.Valide, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        n.MapPost("/destinataires/{id:long}/test", async (long id, EnvoyerMessageTest test, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new NombreDto(await test.ExecuterAsync(id, user.UtilisateurId(), ct))))
           .ExigerTitulaire();

        // Rapport du jour envoyé tout de suite (vérification des réglages) ; n'empêche pas l'envoi automatique du soir
        n.MapPost("/rapport", async (EnvoyerRapportQuotidien rapport, CancellationToken ct) =>
                Results.Ok(new NombreDto(await rapport.EnvoyerAsync(DateOnly.FromDateTime(DateTime.Now), ct))))
           .ExigerTitulaire();

        n.MapGet("/file", async (ConsulterFileMessages consulter, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await consulter.ExecuterAsync(user.UtilisateurId(), ct)).Select(m => new MessageSortantDto(
                    m.Id, m.Canal.Code(), m.Destinataire, m.Adresse, m.Type.Code(), m.Objet, m.Corps, m.Statut, m.Tentatives,
                    m.ProchaineTentative, m.DerniereErreur, m.CreeLe, m.EnvoyeLe))))
           .ExigerTitulaire();

        // ---- Sauvegardes ----
        var s = api.MapGroup("/admin/sauvegardes");

        s.MapGet("", async (ObtenirSauvegardes obtenir, CancellationToken ct) =>
            {
                var (r, journal) = await obtenir.ExecuterAsync(ct);
                return Results.Ok(new EtatSauvegardesDto(
                    new ReglagesSauvegardeDto(r.Actif, r.Dossier, r.Heure, r.Conservation, r.DerniereExecution),
                    journal.Select(j => new EntreeSauvegardeDto(j.Id, j.Debut, j.Fin, j.Statut, j.Declenchement, j.Fichier, j.TailleOctets, j.Message, j.Utilisateur)).ToList()));
            })
           .ExigerPermission(Permissions.AdminSauvegardes);

        s.MapPut("/reglages", async (ReglagesSauvegardeDto d, ModifierReglagesSauvegarde modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(d.Actif, d.Dossier, d.Heure, d.Conservation, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminSauvegardes);

        s.MapPost("/lancer", async (LancerSauvegarde lancer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await lancer.ExecuterAsync(false, user.UtilisateurId(), DateTime.Now, ct);
                return Results.Ok(new ResultatSauvegardeDto(r.Statut, r.Fichier, r.TailleOctets, r.Message));
            })
           .ExigerPermission(Permissions.AdminSauvegardes);
    }

    private static ReglagesNotificationDto VersDto(ReglagesNotification r) =>
        new(r.SmtpServeur, r.SmtpPort, r.SmtpTls, r.SmtpExpediteur, r.SmtpIdentifiant, r.MotDePasseEnregistre,
            r.ModemPort, r.HeureRapport, r.DernierRapport, r.SmsSeuilEcart, r.SmsLotBloque, r.SmsSauvegarde);

    private static DestinataireDto VersDto(Destinataire d) =>
        new(d.Id, d.Nom, d.Email, d.Telephone, d.CanalEmail, d.CanalSms,
            CodesNotification.Abonnables.Where(d.Types.Contains).Select(t => t.Code()).ToList(), d.Valide, d.Actif);

    private static SaisieDestinataire VersSaisie(SaisieDestinataireDto d)
    {
        TypeNotification[] types;
        try { types = (d.Types ?? []).Select(CodesNotification.TypeDepuisCode).ToArray(); }
        catch (ArgumentException) { throw new RegleMetierException("Type de message inconnu."); }
        return new SaisieDestinataire(d.Nom, d.Email, d.Telephone, d.CanalEmail, d.CanalSms, types, d.Actif);
    }
}
