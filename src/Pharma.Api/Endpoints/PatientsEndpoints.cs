using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Patients;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class PatientsEndpoints
{
    public static void MapPatients(this RouteGroupBuilder api)
    {
        // Recherche rapide (suggestions pendant la frappe, décision du titulaire 2026-11-18) : droit de vendre ou Factures ; sans le
        // droit de vendre, ni organisme, ni fin de droits, ni dette, ni plafond dans la réponse (champs nuls, masqués par l'Application)
        api.MapGet("/patients", async (string? q, RechercherPatients rechercher, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await rechercher.PourEmployeAsync(q, user.UtilisateurId(), ct)).Select(p => new PatientDto(p.Id, p.Nom, p.Prenom,
                    p.Telephone, p.NumAssure, p.OrganismeId, p.Organisme, p.PlafondCredit, p.SoldeCredit, p.DateFinDroit)).ToList()))
           .ExigerPermission(DroitsRecherche);

        api.MapPost("/patients", async (NouveauPatientDto req, CreerPatient creer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await creer.ExecuterAsync(new NouveauPatient(
                    req.Nom, req.Prenom, req.Telephone, req.NumAssure, req.OrganismeId, req.PlafondCredit, req.Email), user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapGet("/patients/{id:long}/fiche", async (long id, LireFichePatient lire, CancellationToken ct) =>
            {
                var f = await lire.ExecuterAsync(id, ct);
                var proches = await lire.ProchesAsync(id, ct);
                // Assuré principal possible : l'actuel, et les fiches de même n° de base qui peuvent l'être (pas ses propres ayants droit)
                var candidats = proches.Where(p => p.Id == f.AssurePrincipalId
                        || (FamillePatient.PeutEtrePrincipal(p.Lien, p.AssurePrincipalId) && p.AssurePrincipalId != id
                            && FamillePatient.NumeroBase(p.NumAssure) is { } b && b == FamillePatient.NumeroBase(f.NumAssure)))
                    .Select(Dto).ToList();
                var proposition = FamillePatient.Proposition(f.Id, f.NumAssure, f.Lien, f.AssurePrincipalId, proches);
                return Results.Ok(new FichePatientDto(f.Id, f.Nom, f.Prenom, f.DateNaissance, f.Telephone, f.Email, f.NumAssure, f.OrganismeId,
                    f.Chronique, f.PlafondCredit, f.SoldeCredit, f.Lien, f.AssurePrincipalId, candidats, proposition is null ? null : Dto(proposition), f.DateFinDroit));
                static ProchePatientDto Dto(ProchePatient p) => new(p.Id, p.Nom, p.NumAssure, p.Lien);
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Avoir du patient (script 106) : solde (jamais négatif) et mouvements, séparés de sa dette
        api.MapGet("/patients/{id:long}/avoir", async (long id, int? page, int? taille, Pharma.Application.Caisse.ConsulterAvoir consulter, CancellationToken ct) =>
            {
                var s = await consulter.ExecuterAsync(id, Pharma.Application.Commun.DemandePage.Lire(page, taille), ct);
                return Results.Ok(new AvoirPatientDto(s.Solde, new PageDto<AvoirMouvementDto>(s.Mouvements.Lignes.Select(m => new AvoirMouvementDto(
                        m.Id, m.Sens, m.Montant, m.SoldeApres, m.VenteId, m.NumeroVente, m.Motif, new DateTimeOffset(DateTime.SpecifyKind(m.CreeLe, DateTimeKind.Utc)),
                        m.CreePar, m.Origine, m.BonSortie)).ToList(),
                    s.Mouvements.Total, s.Mouvements.Numero, s.Mouvements.Taille)));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Remboursement d'un avoir (D-10) : sortie du tiroir « Remboursement d'un client », même droit et même seuil que les sorties
        api.MapPost("/patients/{id:long}/avoir/remboursement", async (long id, RemboursementAvoirRequete req, Pharma.Application.Caisse.RembourserAvoir rembourser,
                ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var bon = await rembourser.ExecuterAsync(id, req.Montant, req.Motif, user.UtilisateurId(), requete.Poste(), ct);
                return Results.Created($"/api/caisse/tiroir/mouvements/{bon.Id}", new OuvertureTiroirDto(bon.Id, bon.Numero));
            })
           .ExigerPermission(Permissions.CaisseOuvrirTiroir)
           .GererErreursMetier();

        // Membres de la famille (script 090) proposés en priorité pour « Remis à » / « Pour » à la caisse (2026-11-15)
        api.MapGet("/patients/{id:long}/proches", async (long id, LireFichePatient lire, CancellationToken ct) =>
                Results.Ok((await lire.ProchesAsync(id, ct)).Where(p => p.Id != id).Select(p => new ProchePatientDto(p.Id, p.Nom, p.NumAssure, p.Lien)).ToList()))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapPut("/patients/{id:long}", async (long id, FichePatientDto d, ModifierPatient modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new FichePatient(id, d.Nom, d.Prenom, d.DateNaissance, d.Telephone, d.Email, d.NumAssure, d.OrganismeId,
                    d.Chronique, d.PlafondCredit, 0, d.Lien, d.AssurePrincipalId, d.DateFinDroit), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Contrôle de fin de droits Chifa (2026-11-15) : « Mettre à jour la date » (caisse, liste de suivi), journalisé (patient_modifie)
        api.MapPut("/patients/{id:long}/fin-droit", async (long id, FinDroitDto d, MettreAJourFinDroit maj, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await maj.ExecuterAsync(id, d.DateFinDroit, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Patients › « Droits expirés ou bientôt expirés » : paginée, filtrée par état, organisme et période (date de fin)
        api.MapGet("/patients/droits-chifa", async (string? etat, string? organisme, DateOnly? du, DateOnly? au, int? page, int? taille,
                ListerSuiviDroitsChifa lister, Pharma.Application.Commun.IHorloge horloge, CancellationToken ct) =>
            {
                var p = await lister.ExecuterAsync(etat, organisme, du, au, Pharma.Application.Commun.DemandePage.Lire(page, taille), ct);
                return Results.Ok(new PageDto<SuiviDroitChifaDto>(p.Lignes.Select(l => new SuiviDroitChifaDto(l.PatientId, l.Nom, l.NumAssure,
                    RangAssure.Extraire(l.NumAssure), l.Organisme, l.DateFinDroit, DroitsChifa.Expires(l.DateFinDroit, horloge.Aujourdhui) ? "expires" : "bientot",
                    l.DerniereVente)).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Patients › « Fichier des patients » (2026-11-18) : liste paginée et filtrée par le serveur (la recherche rapide GET /patients?q= reste
        // inchangée). organisme : id ou « aucun » ; droits : expires_ou_bientot, expires, bientot ; dette : selon le droit qui la montre déjà
        api.MapGet("/patients/fichier", async (string? texte, string? organisme, bool? chronique, bool? credit, string? droits, bool? sms,
                int? page, int? taille, ListerFichierPatients lister, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await lister.ExecuterAsync(new SaisieFiltreFichierPatients(texte, organisme, chronique, credit, droits, sms),
                    user.UtilisateurId(), Pharma.Application.Commun.DemandePage.Lire(page, taille), ct);
                return Results.Ok(new PageDto<PatientFichierDto>(p.Lignes.Select(l => new PatientFichierDto(l.Id, l.Nom, l.Prenom, l.Telephone,
                    l.NumAssure, l.Organisme, l.DateFinDroit, l.EtatDroits, l.Chronique, l.ConsentementSms, l.SoldeCredit)).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // sansWilaya : filtre « wilaya à renseigner » de l'écran des médecins (2026-11-16)
        api.MapGet("/medecins", async (string? q, bool? sansWilaya, RechercherMedecins rechercher, CancellationToken ct) =>
                Results.Ok((sansWilaya == true ? await rechercher.SansWilayaAsync(q, ct) : await rechercher.ExecuterAsync(q, ct))
                    .Select(m => new MedecinDto(m.Id, m.Nom, m.Specialite, m.Telephone, m.Adresse, m.Email, m.NumeroOrdre, m.CodeCnas, m.Wilaya))))
           .ExigerPermission(DroitsRechercheMedecins);

        // Fiche d'un médecin par son identifiant (même droit que la recherche) : relue avant une correction ciblée (wilaya de la caisse)
        api.MapGet("/medecins/{id:long}", async (long id, RechercherMedecins rechercher, CancellationToken ct) =>
                await rechercher.LireAsync(id, ct) is { } m
                    ? Results.Ok(new MedecinDto(m.Id, m.Nom, m.Specialite, m.Telephone, m.Adresse, m.Email, m.NumeroOrdre, m.CodeCnas, m.Wilaya))
                    : Results.NotFound())
           .ExigerPermission(DroitsRechercheMedecins);

        // Référentiel des wilayas (liste bornée : fiche médecin, zone de l'officine)
        api.MapGet("/wilayas", async (Pharma.Application.Psychotropes.IZonePsychotropesDepot zone, CancellationToken ct) =>
                Results.Ok((await zone.WilayasAsync(ct)).Select(w => new WilayaDto(w.Code, w.Nom)).ToList()))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapPost("/medecins", async (NouveauMedecinDto req, CreerMedecin creer, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await creer.ExecuterAsync(VersSaisie(req), ct))))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapPut("/medecins/{id:long}", async (long id, NouveauMedecinDto req, ModifierMedecin modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(id, VersSaisie(req), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapGet("/organismes", async (ListerOrganismes lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(o => new OrganismeDto(o.Id, o.Code, o.Nom, o.DelaiPaiementJours))))
           .ExigerPermission(Permissions.CaisseVendre);
    }

    /// <summary>
    /// « Droit de l'écran » (décision du titulaire, 2026-11-18) : un patient se cherche avec le droit de vendre ou depuis Factures ;
    /// un médecin, aussi depuis le registre des psychotropes (prescripteur).
    /// </summary>
    private static readonly string[] DroitsRecherche = [.. RechercherPatients.DroitsRecherche];
    private static readonly string[] DroitsRechercheMedecins = [.. RechercherPatients.DroitsRechercheMedecins];

    private static Pharma.Application.Patients.SaisieMedecin VersSaisie(NouveauMedecinDto d) =>
        new(d.Nom, d.Specialite, d.Telephone, d.Adresse, d.Email, d.NumeroOrdre, d.CodeCnas, d.Wilaya);
}
