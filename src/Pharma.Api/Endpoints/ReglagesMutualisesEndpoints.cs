using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Reglages;
using Pharma.Application.Securite;
using Pharma.Domain.Reglages;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Mutualisation des réglages (lot M5, docs/conception-synchronisation.md § 16 bis.5) : état des 11 tables, valeur proposée à côté de la valeur de l'officine,
/// « Adopter », et saisie locale du réglage fiscal et de la traçabilité (D8, sans écran jusqu'ici). Tout se lit dans la base de l'officine : le serveur
/// central en panne ne bloque rien. Refus d'une table imposée : 409, champ « reglage_impose ».
/// </summary>
internal static class ReglagesMutualisesEndpoints
{
    public static void MapReglagesMutualises(this RouteGroupBuilder api)
    {
        var r = api.MapGroup("/reglages-mutualises");

        // État des 11 tables : mode, versions, état, motif de rejet, dates ; écarts pour une valeur proposée non adoptée (titulaire et droit admin.parametres)
        r.MapGet("", async (ConsulterReglagesMutualises consulter, CancellationToken ct) =>
                Results.Ok((await consulter.ListerAsync(ct)).Select(Dto).ToList()))
           .ExigerPermission(Permissions.AdminParametres);

        // Une table (écran de réglage correspondant) : même droit que celui d'adopter la valeur de cette table
        r.MapGet("/{table}", async (string table, ConsulterReglagesMutualises consulter, IPermissionService droits, ClaimsPrincipal user, CancellationToken ct) =>
            {
                if (!TablesMutualisables.Connue(table)) return Results.NotFound(new ErreurDto("Réglage inconnu."));
                await ExigerDroitAsync(droits, user.UtilisateurId(), table, ct);
                return Results.Ok(Dto(await consulter.LireAsync(table, ct)));
            })
           .ExigerConnexion();

        // Adopter la valeur proposée : 409 si le serveur central a publié une autre version ou si la table est imposée, 422 si la valeur est rejetée
        r.MapPost("/{table}/adopter", async (string table, AdopterReglageRequete d, AdopterReglageCentral adopter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await adopter.ExecuterAsync(table, d.VersionModele, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerConnexion();

        // Réglage fiscal et traçabilité (titulaire) : règles du domaine, garde des tables imposées, journal avant / après
        api.MapGet("/fiscal/parametres", async (ConsulterParametreFiscal c, CancellationToken ct) =>
            {
                var p = await c.ExecuterAsync(ct);
                return Results.Ok(new ParametreFiscalDto(p.ConservationAnnees, p.FormatExportDgi, p.SeparateurCsv, p.Fuseau));
            })
           .ExigerTitulaire()
           .AvecEtatReglageCentral(TablesMutualisables.Fiscal);

        api.MapPut("/fiscal/parametres", async (ParametreFiscalDto d, ModifierParametreFiscal m, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await m.ExecuterAsync(new ParametreFiscal(d.ConservationAnnees, d.FormatExportDgi, d.SeparateurCsv, d.Fuseau), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        // Taux de TVA réglables (script 171) : liste lue par tout employé connecté (formulaires), réglée par le titulaire
        api.MapGet("/fiscal/taux-tva", async (Pharma.Application.Fiscal.GererTauxTva g, CancellationToken ct) =>
            {
                var l = await g.ListerAsync(ct);
                return Results.Ok(new ListeTauxTvaDto(l.Taux.Select(t => new TauxTvaDto(t.Id, t.Taux, t.Libelle, t.Actif, t.Produits)).ToList(),
                    l.Categories.Select(c => new TauxTvaCategorieDto(c.Code, c.Libelle, c.Taux)).ToList(),
                    l.HorsListe.Select(h => new TauxHorsListeDto(h.Taux, h.Produits)).ToList()));
            })
           .ExigerConnexion();
        api.MapPost("/fiscal/taux-tva", async (TauxTvaRequete r, Pharma.Application.Fiscal.GererTauxTva g, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await g.AjouterAsync(user.UtilisateurId(), r.Taux, r.Libelle, ct))))
           .ExigerTitulaire();
        api.MapPut("/fiscal/taux-tva/{id:long}", async (long id, TauxTvaModificationRequete r, Pharma.Application.Fiscal.GererTauxTva g, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                await g.ModifierAsync(user.UtilisateurId(), id, r.Libelle, r.Actif, ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();
        api.MapPut("/fiscal/taux-tva/categories/{code}", async (string code, TauxTvaDefautRequete r, Pharma.Application.Fiscal.GererTauxTva g,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                await g.DefinirDefautAsync(user.UtilisateurId(), code, r.Taux, ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        // Offre de base et options (script 172) : écran « Mon offre » du titulaire, saisie d'un code d'activation signé par l'éditeur
        api.MapGet("/offre", async (Pharma.Application.Offre.GererOffre g, Pharma.Application.Postes.GererPostesAutorises postes, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var (e, id, historique, comptes) = await g.LireAsync(user.UtilisateurId(), postes.NombreComptesAsync, ct);
                return Results.Ok(new OffreDto(e.Etat, e.Packs.Select(p => new PackOffreDto(p, Pharma.Application.Offre.PacksOffre.Libelle(p))).ToList(),
                    e.Postes, comptes, e.ScannersMobiles, e.Fin, e.Transition, e.EmiseLeUtc, e.Alerte, id.Officine, id.Nif,
                    historique.Select(h => new ActivationOffreDto(h.Id, h.EmiseLeUtc, h.Source, h.SaisieLeUtc, h.SaisiePar, h.RefuseeLeUtc, h.MotifRefus)).ToList(),
                    Pharma.Application.Offre.PacksOffre.Tous.Select(p => new PackOffreDto(p.Code, p.Libelle)).ToList()));
            })
           .ExigerTitulaire();
        api.MapPost("/offre/activation", async (ActivationOffreRequete r, Pharma.Application.Offre.GererOffre g, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await g.SaisirAsync(user.UtilisateurId(), r.Code, ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        api.MapGet("/tracabilite/parametres", async (ConsulterParametreTracabilite c, CancellationToken ct) =>
            {
                var p = await c.ExecuterAsync(ct);
                return Results.Ok(new ParametreTracabiliteDto(p.BoiteObligatoire, p.PatientObligatoire));
            })
           .ExigerTitulaire()
           .AvecEtatReglageCentral(TablesMutualisables.Tracabilite);

        api.MapPut("/tracabilite/parametres", async (ParametreTracabiliteDto d, ModifierParametreTracabilite m, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await m.ExecuterAsync(new ParametreTracabilite(d.BoiteObligatoire, d.PatientObligatoire), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();
    }

    /// <summary>Droit de saisir la table localement (le même que pour l'adopter) : droit de la table, sinon titulaire.</summary>
    private static async Task ExigerDroitAsync(IPermissionService droits, long utilisateurId, string table, CancellationToken ct)
    {
        const string quoi = "consulter le réglage du serveur central";
        if (AdopterReglageCentral.Permission(table) is { } p) await droits.ExigerAsync(utilisateurId, p, quoi, ct);
        else await droits.ExigerTitulaireAsync(utilisateurId, quoi, ct);
    }

    private static ReglageMutualiseDto Dto(VueReglage v) => new(v.Etat.Table, v.Libelle, v.ModeEnVigueur, v.Etat.ModeRecu, v.Etat.AjoutsLocaux, v.Etat.VersionRecue,
        v.Etat.VersionAppliquee, v.Etat.RecuLe, v.Etat.AppliqueLe, v.Etat.Etat, v.Etat.MotifRejet, v.Etat.Origine, v.Auteur,
        v.Ecarts.Select(e => new EcartReglageDto(e.Libelle, e.Local, e.Central)).ToList());
}
