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
        api.MapGet("/patients", async (string? q, RechercherPatients rechercher, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(q, ct)).Select(p => new PatientDto(
                    p.Id, p.Nom, p.Prenom, p.Telephone, p.NumAssure, p.OrganismeId, p.Organisme, p.PlafondCredit, p.SoldeCredit))))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapPost("/patients", async (NouveauPatientDto req, CreerPatient creer, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await creer.ExecuterAsync(new NouveauPatient(
                    req.Nom, req.Prenom, req.Telephone, req.NumAssure, req.OrganismeId, req.PlafondCredit, req.Email), ct))))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapGet("/patients/{id:long}/fiche", async (long id, LireFichePatient lire, CancellationToken ct) =>
            {
                var f = await lire.ExecuterAsync(id, ct);
                return Results.Ok(new FichePatientDto(f.Id, f.Nom, f.Prenom, f.DateNaissance, f.Telephone, f.Email, f.NumAssure, f.OrganismeId,
                    f.Chronique, f.PlafondCredit, f.SoldeCredit));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapPut("/patients/{id:long}", async (long id, FichePatientDto d, ModifierPatient modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new FichePatient(id, d.Nom, d.Prenom, d.DateNaissance, d.Telephone, d.Email, d.NumAssure, d.OrganismeId,
                    d.Chronique, d.PlafondCredit), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapGet("/medecins", async (string? q, RechercherMedecins rechercher, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(q, ct)).Select(m => new MedecinDto(m.Id, m.Nom, m.Specialite))))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapPost("/medecins", async (NouveauMedecinDto req, CreerMedecin creer, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await creer.ExecuterAsync(req.Nom, req.Specialite, ct))))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapPut("/medecins/{id:long}", async (long id, NouveauMedecinDto req, ModifierMedecin modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(id, req.Nom, req.Specialite, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapGet("/organismes", async (ListerOrganismes lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(o => new OrganismeDto(o.Id, o.Code, o.Nom, o.DelaiPaiementJours))))
           .ExigerPermission(Permissions.CaisseVendre);
    }
}
