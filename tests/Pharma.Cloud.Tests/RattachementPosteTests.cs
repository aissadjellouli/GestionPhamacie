using Npgsql;
using Pharma.Cloud.Api.Comptes;
using Pharma.Shared;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Lot S7.2b (script central 033) : code de rattachement demandé par le poste (POST /api/v1/rattachement). Officine du certificat
/// seulement, réponse neutre (aucune énumération des comptes), 5 demandes par heure et par officine, même tirage que la console.
/// </summary>
public class RattachementPosteTests
{
    [Fact]
    public void Statuts_de_la_base_vers_reponses_http_neutres()
    {
        Assert.Null(RattachementPoste.Reponse("ok"));
        Assert.Equal((429, RattachementPoste.RefusLimite), RattachementPoste.Reponse("trop_de_codes"));
        // Tout refus (et tout statut inattendu) reçoit LA même réponse : rien ne distingue un compte inconnu d'un éditeur ou d'un compte désactivé
        foreach (var s in new[] { "refus", "compte_inconnu", "pas_titulaire", "compte_inactif", "autre" })
            Assert.Equal((422, RattachementPoste.RefusNeutre), RattachementPoste.Reponse(s));
    }

    [Fact]
    public void Officine_jamais_dans_la_demande_route_authentifiee_comme_l_echange()
    {
        Assert.Equal(["Compte"], typeof(DemandeCodeRattachementDto).GetProperties().Select(p => p.Name));
        var source = File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Api", "PointsEntree.cs"));
        var debut = source.IndexOf("v1.MapPost(\"/rattachement\"", StringComparison.Ordinal);
        Assert.True(debut > 0, "route /api/v1/rattachement introuvable");
        var bloc = source[debut..source.IndexOf(".RequireRateLimiting(\"officine\");", debut, StringComparison.Ordinal)];
        Assert.Contains("await AppelantAsync(ctx, ct)", bloc);   // certificat client + jeton lié, comme /echange
        Assert.Contains("appelant.Officine, appelant.EmpreinteCertificat", bloc);
        Assert.Contains("CacheControl = \"no-store\"", bloc);
        Assert.DoesNotContain("code}", bloc.Replace("code!", ""));   // le code n'est jamais écrit au journal
        // Même tirage que la console
        var poste = File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Api", "Comptes", "RattachementPoste.cs"));
        var console = File.ReadAllText(Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Api", "Comptes", "ComptesDistantsAdministration.cs"));
        Assert.Contains("ComptesDistants.TirerCodeRattachement()", poste);
        Assert.Contains("ComptesDistants.TirerCodeRattachement()", console);
        Assert.DoesNotContain("ComptesDistants.CodeRattachement()", poste + console);
    }

    [Fact]
    public void Code_tire_et_empreinte_ensemble()
    {
        var (code, empreinte) = ComptesDistants.TirerCodeRattachement();
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        Assert.Equal(ComptesDistants.EmpreinteCodeRattachement(code), empreinte);
        Assert.NotEqual(code, ComptesDistants.TirerCodeRattachement().Code);
    }

    [Fact]
    public async Task Officine_du_certificat_reponse_neutre_et_limite_sur_une_base_jetable()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var proprietaire = NpgsqlDataSource.Create(base_.Connexion);
        Guid a, b;
        await using (var cmd = proprietaire.CreateCommand("""
            INSERT INTO officine (libelle) VALUES ('Poste A'), ('Poste B') RETURNING id
            """))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            await r.ReadAsync(); a = r.GetGuid(0);
            await r.ReadAsync(); b = r.GetGuid(0);
        }
        await using (var cmd = proprietaire.CreateCommand("""
            INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance)
            VALUES (repeat('a', 64), $1, 'principal', 'p1', now() + interval '1 year'), (repeat('b', 64), $2, 'principal', 'p2', now() + interval '1 year')
            """))
        {
            cmd.Parameters.AddWithValue(a);
            cmd.Parameters.AddWithValue(b);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var (ident, role) in new[] { ("titulaire.poste", "titulaire"), ("editeur.poste", "editeur") })
        {
            await using var cmd = proprietaire.CreateCommand("SELECT cloud_compte_distant_creer($1, 'Compte poste', $2, $3, NULL, 'p@exemple.invalid', 'tests')");
            cmd.Parameters.AddWithValue(ident);
            cmd.Parameters.AddWithValue(role);
            cmd.Parameters.AddWithValue("$2a$12$" + new string('a', 53));
            await cmd.ExecuteScalarAsync();
        }
        // Connexion au rôle de l'API centrale (pharma_cloud_api), comme en service
        await using var api = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(base_.Connexion) { Options = "-c role=pharma_cloud_api", Pooling = false }.ConnectionString);
        var ct = CancellationToken.None;

        var (statut, code, expire) = await RattachementPoste.DemanderAsync(api, a, new string('a', 64), " Titulaire.Poste ", ct);
        Assert.Equal("ok", statut);
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        Assert.InRange(expire!.Value, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
        await using (var cmd = proprietaire.CreateCommand("SELECT officine_id::text || '|' || empreinte_code FROM code_rattachement"))
            Assert.Equal($"{a}|{ComptesDistants.EmpreinteCodeRattachement(code)}", await cmd.ExecuteScalarAsync());

        // Réponse neutre : éditeur, inconnu, mal formé (aucun code rendu, aucun code créé)
        foreach (var compte in new[] { "editeur.poste", "inconnu.poste", "pas valide", null })
        {
            var refus = await RattachementPoste.DemanderAsync(api, a, new string('a', 64), compte, ct);
            Assert.Equal(("refus", (string?)null, (DateTimeOffset?)null), refus);
        }
        // 5 demandes comptées pour A : la suivante est refusée, même pour le titulaire ; B n'est pas touchée
        Assert.Equal("trop_de_codes", (await RattachementPoste.DemanderAsync(api, a, new string('a', 64), "titulaire.poste", ct)).Statut);
        Assert.Equal("ok", (await RattachementPoste.DemanderAsync(api, b, new string('b', 64), "titulaire.poste", ct)).Statut);
        // Officine posée différente de celle du certificat : refus de la base
        var e = await Assert.ThrowsAsync<PostgresException>(() => RattachementPoste.DemanderAsync(api, a, new string('b', 64), "titulaire.poste", ct));
        Assert.Equal("42501", e.SqlState);
        await using (var cmd = proprietaire.CreateCommand("SELECT count(*) FROM code_rattachement"))
            Assert.Equal(2L, await cmd.ExecuteScalarAsync());
        // Le code n'apparaît dans aucun journal
        await using (var cmd = proprietaire.CreateCommand("SELECT count(*) FROM journal_securite WHERE detail LIKE '%' || $1 || '%' OR detail LIKE '%' || replace($1, '-', '') || '%'"))
        {
            cmd.Parameters.AddWithValue(code!);
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        }
    }
}
