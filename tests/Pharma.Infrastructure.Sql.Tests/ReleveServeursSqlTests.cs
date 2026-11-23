using Pharma.Infrastructure.SecondServeur;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Bascule du 2e serveur, lot 1 : relevé (recovery, timeline, identifiant système) sur une base jetable, en lecture seule.</summary>
[Trait("Categorie", "Sql")]
public sealed class ReleveServeursSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    // La chaîne d'une NpgsqlDataSource n'a plus son mot de passe : on repart de celle du harnais, sur la base jetable
    private string Cnx() => new Npgsql.NpgsqlConnectionStringBuilder(OutilsPostgres.ChaineConnexion()) { Database = copie.Nom }.ConnectionString;

    [FactSql]
    public async Task Releve_local_lit_timeline_et_identifiant_systeme_d_une_base_en_ecriture()
    {
        var releves = new ReleveServeursSql(Cnx(), null);

        var r = await releves.LocalAsync();

        Assert.NotNull(r);
        Assert.False(r.EnRecovery);
        Assert.True(r.Timeline >= 1);
        // Identifiant lisible si le compte a le droit d'exécuter pg_control_system ; sinon null (lignée inconnue : jamais de décision)
        var droit = await copie.Donnees.LireAsync<bool>("SELECT has_function_privilege('pg_control_system()', 'EXECUTE')");
        Assert.Equal(droit, r.IdentifiantSysteme is not null);
        Assert.False(releves.AutreServeurRegle);
        Assert.Null(await releves.AutreAsync());
    }

    [FactSql]
    public async Task Releve_de_l_autre_serveur_meme_instance_donne_la_meme_lignee_et_serveur_injoignable_donne_null()
    {
        var hote = new Npgsql.NpgsqlConnectionStringBuilder(Cnx()).Host;
        var releves = new ReleveServeursSql(Cnx(), hote);

        var local = await releves.LocalAsync();
        var autre = await releves.AutreAsync();

        Assert.True(releves.AutreServeurRegle);
        Assert.Equal(local, autre);   // même instance : mêmes timeline et identifiant, donc jamais un écart

        var injoignable = new ReleveServeursSql(Cnx(), "serveur-introuvable.invalid");
        Assert.Null(await injoignable.AutreAsync());
    }
}
