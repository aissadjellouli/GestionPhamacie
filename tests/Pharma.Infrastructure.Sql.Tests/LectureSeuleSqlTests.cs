using Npgsql;
using Pharma.Application.SecondServeur;
using Pharma.Infrastructure.SecondServeur;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Bascule du 2e serveur, lot 3 : une base jetable. Le verrou de lecture seule refuse toute écriture (25006) sur chaque connexion du
/// pool, y compris réutilisée ; l'écart gardé en base (script 163) s'écrit et se lève par sa propre source, même verrouillée.
/// </summary>
[Trait("Categorie", "Sql")]
public sealed class LectureSeuleSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    private string Cnx() => new NpgsqlConnectionStringBuilder(OutilsPostgres.ChaineConnexion()) { Database = copie.Nom }.ConnectionString;

    private static async Task EcrireAsync(NpgsqlDataSource source)
    {
        await using var cmd = source.CreateCommand("INSERT INTO essai_lecture_seule (id) VALUES (1)");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CompterAsync(NpgsqlDataSource source)
    {
        await using var cmd = source.CreateCommand("SELECT count(*) FROM essai_lecture_seule");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [FactSql]
    public async Task Ecarte_toute_ecriture_est_refusee_en_base_25006_sur_chaque_connexion_meme_reutilisee_et_la_levee_rend_l_ecriture()
    {
        await using (var cmd = copie.Verif.CreateCommand("CREATE TABLE IF NOT EXISTS essai_lecture_seule (id int)"))
            await cmd.ExecuteNonQueryAsync();
        var verrou = new ConnexionsLectureSeule();
        await using var source = verrou.CreerSource(Cnx());

        await EcrireAsync(source);   // serveur normal : l'écriture passe, la connexion revient au pool
        Assert.Equal(1, await CompterAsync(source));

        verrou.Appliquer(true);      // écarté : pool vidé, nouvelles connexions en lecture seule
        // Plusieurs écritures de suite : la même connexion physique est réutilisée, le réglage doit survivre à la remise au pool
        for (var i = 0; i < 6; i++)
        {
            var refus = await Assert.ThrowsAsync<PostgresException>(() => EcrireAsync(source));
            Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, refus.SqlState);   // 25006
        }
        Assert.Equal(1, await CompterAsync(source));   // la consultation reste possible côté base (c'est l'intergiciel qui la refuse aux postes)

        verrou.Appliquer(false);     // levée : de nouveau en écriture
        await EcrireAsync(source);
        Assert.Equal(2, await CompterAsync(source));
    }

    [FactSql]
    public async Task Une_connexion_deja_ouverte_dans_le_pool_au_passage_en_ecarte_est_ecartee()
    {
        await using (var cmd = copie.Verif.CreateCommand("CREATE TABLE IF NOT EXISTS essai_lecture_seule (id int)"))
            await cmd.ExecuteNonQueryAsync();
        var verrou = new ConnexionsLectureSeule();
        await using var source = verrou.CreerSource(Cnx());
        await using (var ouverte = await source.OpenConnectionAsync()) { }   // une connexion libre dans le pool, ouverte en écriture

        verrou.Appliquer(true);

        var refus = await Assert.ThrowsAsync<PostgresException>(() => EcrireAsync(source));
        Assert.Equal("25006", refus.SqlState);
    }

    [FactSql]
    public async Task Ecart_gardé_en_base_s_ecrit_une_fois_se_relit_et_se_leve_une_fois_meme_quand_les_connexions_de_l_api_sont_verrouillees()
    {
        var verrou = new ConnexionsLectureSeule();
        await using var sourceApi = verrou.CreerSource(Cnx());
        await using var sourceEcart = NpgsqlDataSource.Create(Cnx());
        var depot = new EcartsSql(sourceEcart);
        var ecart = new EcartDetecte(new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc), "SRV-2", 1, 2);
        verrou.Appliquer(true);

        Assert.Null(await depot.LireActifAsync());
        Assert.True(await depot.EnregistrerAsync(ecart, GardienServeurEcarte.Motif(ecart)));
        Assert.False(await depot.EnregistrerAsync(ecart, "second essai"));   // au plus un écart actif
        var relu = await depot.LireActifAsync();
        Assert.Equal(ecart with { DepuisUtc = relu!.DepuisUtc }, relu);
        Assert.Equal(ecart.DepuisUtc, relu.DepuisUtc.ToUniversalTime());

        Assert.True(await depot.LeverAsync("titulaire", "Faux positif : l'autre serveur était arrêté"));
        Assert.False(await depot.LeverAsync("titulaire", "deuxième levée"));   // levée une seule fois
        Assert.Null(await depot.LireActifAsync());
        Assert.Equal("titulaire", await copie.Donnees.LireAsync<string>("SELECT leve_par FROM serveur_ecarte"));
        Assert.True(await depot.EnregistrerAsync(ecart, "nouvel écart après la levée"));   // un nouvel écart reste possible
        Assert.Equal(2L, await copie.Donnees.LireAsync<long>("SELECT count(*) FROM serveur_ecarte"));   // ajout seul : l'historique est gardé

        // La source verrouillée de l'API, elle, ne peut pas écrire dans serveur_ecarte
        await using var cmd = sourceApi.CreateCommand("UPDATE serveur_ecarte SET leve_le = now(), leve_par = 'x', leve_motif = 'y' WHERE leve_le IS NULL");
        var refus = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("25006", refus.SqlState);
    }
}
