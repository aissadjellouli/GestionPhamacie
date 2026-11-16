using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Integrite;
using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Écran « 2e serveur » (Administration, titulaire) : réplication, réglages par serveur, export de la clé de scellement.</summary>
public static class SecondServeurEndpoints
{
    public static void MapSecondServeur(this RouteGroupBuilder api)
    {
        var s = api.MapGroup("/admin/second-serveur").ExigerTitulaire();

        s.MapGet("", async (ObtenirSecondServeur obtenir, CeServeur ce, IScelleurIntegrite scelleur, CancellationToken ct) =>
        {
            string? empreinte;
            try { empreinte = scelleur.EmpreinteCle; }
            catch (Exception x) when (x is InvalidOperationException or PlatformNotSupportedException) { empreinte = null; }
            var t = await obtenir.ExecuterAsync(ce.Nom, ce.VersionApi, empreinte, ct);
            var e = t.Replication;
            return Results.Ok(new SecondServeurDto(ce.Nom, ce.VersionApi, empreinte, e.EnRecovery, e.Connecte, e.Etat, e.Client, e.RetardOctets,
                e.RetardSecondes, e.SlotExiste, e.SlotActif, e.DetailsVisibles, t.Reglages.SeuilSecondes, (int)(t.Reglages.SeuilOctets / 1024 / 1024),
                t.Reglages.DerniereAlerteUtc, t.Probleme,
                t.Serveurs.Select(v => VersDto(v, ce)).ToList()));
        });

        s.MapPut("/serveurs", async (ModifierServeurOfficineDto d, ModifierServeurOfficine modifier, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await modifier.ExecuterAsync(d.Nom, new ModificationServeur(d.DossierChifa, d.DossierMdn, d.ChifaInstalle, d.LecteurToken, d.MdnInstalle,
                d.DouchetteTestee), user.UtilisateurId(), ct);
            return Results.NoContent();
        });

        s.MapPut("/seuils", async (SeuilsReplicationDto d, ModifierSeuilsReplication modifier, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await modifier.ExecuterAsync(d.SeuilSecondes, d.SeuilMo, user.UtilisateurId(), ct);
            return Results.NoContent();
        });

        // Export de la clé de scellement : fichier chiffré par le mot de passe du titulaire (jamais la clé en clair)
        s.MapPost("/cle/exporter", async (ExportCleDto d, ExporterCleScellement exporter, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var f = await exporter.ExecuterAsync(d.MotDePasse, d.Confirmation, user.UtilisateurId(), ct);
            return Results.Ok(new FichierCleDto(f.NomFichier, Convert.ToBase64String(f.Contenu), f.Empreinte));
        });
    }

    // Ce serveur : dossiers vérifiés en direct (l'autre serveur : relevé du script de vérification)
    private static ServeurOfficineDto VersDto(ServeurOfficine v, CeServeur ce)
    {
        var lui = v.Nom == ce.Nom;
        return new ServeurOfficineDto(v.Nom, v.Role, v.DossierChifa, v.DossierMdn, v.ChifaInstalle, v.LecteurToken, v.MdnInstalle, v.DouchetteTestee,
            v.VersionApi, v.ClePresente, v.EmpreinteCle, v.Imprimantes,
            lui ? Joignable(ce.DossierChifa) : v.DossierChifaJoignable, lui ? Joignable(ce.DossierMdn) : v.DossierMdnJoignable, v.ReleveLe);
    }

    internal static bool? Joignable(string? dossier)
    {
        if (string.IsNullOrWhiteSpace(dossier)) return null;
        try { return Directory.Exists(dossier); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
