using Npgsql;
using Pharma.Cloud.Api.Comptes;
using Pharma.Shared;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Lot S7.3 (script central 034) : comptes distants rattachés vus par le poste, droits (dont « ordres »), retrait. Officine du
/// certificat seulement (jamais un champ de la demande), rôle pharma_cloud_api sur base jetable, retrait immédiat.
/// </summary>
public class AccesDistantsPosteTests
{
    [Fact]
    public void Statuts_de_la_base_vers_reponses_http()
    {
        Assert.Null(AccesDistantsPoste.Reponse("ok"));
        Assert.Equal((422, AccesDistantsPoste.RefusDroits), AccesDistantsPoste.Reponse("droits_invalides"));
        foreach (var s in new[] { "inconnu", "autre" })
            Assert.Equal((422, AccesDistantsPoste.RefusInconnu), AccesDistantsPoste.Reponse(s));
        Assert.Equal(["consulter", "journal_acces", "ordres"], DroitsCompteDistant.Tous);
    }

    [Fact]
    public void Officine_jamais_dans_la_demande_routes_authentifiees_comme_l_echange()
    {
        Assert.Equal(["Compte", "Droits"], typeof(DemandeDroitsCompteDistantDto).GetProperties().Select(p => p.Name));
        Assert.Equal(["Compte"], typeof(DemandeRetraitCompteDistantDto).GetProperties().Select(p => p.Name));
        var source = File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Api", "PointsEntree.cs"));
        foreach (var route in new[] { "v1.MapGet(\"/acces-distants\"", "v1.MapPost(\"/acces-distants/droits\"", "v1.MapPost(\"/acces-distants/retrait\"" })
        {
            var debut = source.IndexOf(route, StringComparison.Ordinal);
            Assert.True(debut > 0, $"route {route} introuvable");
            var bloc = source[debut..source.IndexOf(".RequireRateLimiting(\"officine\");", debut, StringComparison.Ordinal)];
            Assert.Contains("await AppelantAsync(ctx, ct)", bloc);   // certificat client + jeton lié, comme /echange
            Assert.Contains("appelant.Officine, appelant.EmpreinteCertificat", bloc);
        }
    }

    [Fact]
    public async Task Comptes_droits_et_retrait_de_l_officine_du_certificat_sur_une_base_jetable()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var proprietaire = NpgsqlDataSource.Create(base_.Connexion);
        Guid a, b;
        await using (var cmd = proprietaire.CreateCommand("INSERT INTO officine (libelle) VALUES ('Poste A'), ('Poste B') RETURNING id"))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            await r.ReadAsync(); a = r.GetGuid(0);
            await r.ReadAsync(); b = r.GetGuid(0);
        }
        foreach (var sql in new[]
        {
            "INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance) VALUES (repeat('a', 64), $1, 'principal', 'p1', now() + interval '1 year'), (repeat('b', 64), $2, 'principal', 'p2', now() + interval '1 year')",
            "SELECT cloud_compte_distant_creer('titulaire.groupe', 'Titulaire groupe', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'g@exemple.invalid', 'tests') WHERE $1::uuid IS NOT NULL AND $2::uuid IS NOT NULL",
            "SELECT cloud_compte_distant_creer('titulaire.seul', 'Titulaire seul', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 's@exemple.invalid', 'tests') WHERE $1::uuid IS NOT NULL AND $2::uuid IS NOT NULL",
            """
            INSERT INTO compte_officine (compte_id, officine_id, accorde_par)
            SELECT c.id, o.id, 'tests' FROM compte_distant c, officine o
            WHERE (c.identifiant = 'titulaire.groupe' AND o.id IN ($1, $2)) OR (c.identifiant = 'titulaire.seul' AND o.id = $2)
            """,
        })
        {
            await using var cmd = proprietaire.CreateCommand(sql);
            cmd.Parameters.AddWithValue(a);
            cmd.Parameters.AddWithValue(b);
            await cmd.ExecuteNonQueryAsync();
        }
        // Connexion au rôle de l'API centrale (pharma_cloud_api), comme en service
        await using var api = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(base_.Connexion) { Options = "-c role=pharma_cloud_api", Pooling = false }.ConnectionString);
        var ct = CancellationToken.None;

        // A ne voit que ses comptes, jamais ceux de B
        var comptesA = await AccesDistantsPoste.ListerAsync(api, a, new string('a', 64), ct);
        Assert.Equal(["titulaire.groupe"], comptesA.Select(c => c.Identifiant));
        Assert.Equal(["consulter", "journal_acces"], comptesA[0].Droits);
        Assert.Equal(["titulaire.groupe", "titulaire.seul"], (await AccesDistantsPoste.ListerAsync(api, b, new string('b', 64), ct)).Select(c => c.Identifiant));

        // Droits : « ordres » accordé ; compte d'une autre officine ou droits invalides refusés
        Assert.Equal(("ok", 0), await AccesDistantsPoste.DroitsAsync(api, a, new string('a', 64), "titulaire.groupe", ["consulter", "ordres"], ct));
        Assert.Equal("inconnu", (await AccesDistantsPoste.DroitsAsync(api, a, new string('a', 64), "titulaire.seul", ["consulter"], ct)).Statut);
        Assert.Equal("droits_invalides", (await AccesDistantsPoste.DroitsAsync(api, a, new string('a', 64), "titulaire.groupe", ["admin"], ct)).Statut);
        Assert.Equal("droits_invalides", (await AccesDistantsPoste.DroitsAsync(api, a, new string('a', 64), "titulaire.groupe", null, ct)).Statut);
        Assert.Equal(["consulter", "ordres"], (await AccesDistantsPoste.ListerAsync(api, a, new string('a', 64), ct))[0].Droits);

        // Certificat d'une autre officine que celle posée : refus de la base
        var e = await Assert.ThrowsAsync<PostgresException>(() => AccesDistantsPoste.RetirerAsync(api, a, new string('b', 64), "titulaire.groupe", ct));
        Assert.Equal("42501", e.SqlState);

        // Retrait : effet immédiat pour A seulement ; B garde le compte
        Assert.Equal(("ok", 0), await AccesDistantsPoste.RetirerAsync(api, a, new string('a', 64), "titulaire.groupe", ct));
        Assert.Equal("inconnu", (await AccesDistantsPoste.RetirerAsync(api, a, new string('a', 64), "titulaire.groupe", ct)).Statut);
        Assert.Empty(await AccesDistantsPoste.ListerAsync(api, a, new string('a', 64), ct));
        Assert.Equal(2, (await AccesDistantsPoste.ListerAsync(api, b, new string('b', 64), ct)).Count);
    }
}
