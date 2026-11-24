using Npgsql;
using Pharma.Application.SecondServeur;
using Pharma.Infrastructure.SecondServeur;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Vague 2, lot V2-4 : retrait (écart de nature « retrait » sans timeline), décisions de bascule (script 166), attente des transactions de l'API et promotion,
/// sur une base jetable seulement. Cette base est un serveur principal (pas en recovery) : aucune promotion réelle n'est possible ni tentée.
/// </summary>
[Trait("Categorie", "Sql")]
public sealed class RetraitPromotionSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    private static readonly DateTime T = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    private string Cnx(string? application = null) =>
        new NpgsqlConnectionStringBuilder(OutilsPostgres.ChaineConnexion()) { Database = copie.Nom, ApplicationName = application ?? "sql-test-retrait" }.ConnectionString;

    [FactSql]
    public async Task Ecart_de_retrait_s_ecrit_sans_timeline_du_retenu_se_relit_collant_et_une_seule_fois()
    {
        await using var source = NpgsqlDataSource.Create(Cnx());
        var depot = new EcartsSql(source);
        var ecart = new EcartDetecte(T, "SRV-2", 3, 0, Retrait: true);

        Assert.True(await depot.EnregistrerAsync(ecart, GardienServeurEcarte.Motif(ecart)));
        Assert.False(await depot.EnregistrerAsync(ecart, "second essai"));   // au plus un écart actif

        Assert.Equal("retrait", await copie.Donnees.LireAsync<string>("SELECT nature FROM serveur_ecarte WHERE leve_le IS NULL"));
        Assert.True(await copie.Donnees.LireAsync<bool>("SELECT timeline_retenue IS NULL FROM serveur_ecarte WHERE leve_le IS NULL"));
        var relu = await depot.LireActifAsync();
        Assert.NotNull(relu);
        Assert.True(relu.Retrait);
        Assert.Equal(0, relu.TimelineRetenue);
        Assert.Equal(3, relu.TimelineLocale);
        Assert.Equal("SRV-2", relu.ServeurRetenu);

        Assert.True(await depot.LeverAsync("titulaire", "Retrait annulé : essai de test"));   // levée manuelle seulement
        Assert.Null(await depot.LireActifAsync());

        // Un écart de nature « bascule » garde sa timeline obligatoire et sa nature
        var bascule = new EcartDetecte(T, "SRV-2", 1, 2);
        Assert.True(await depot.EnregistrerAsync(bascule, GardienServeurEcarte.Motif(bascule)));
        Assert.Equal("bascule", await copie.Donnees.LireAsync<string>("SELECT nature FROM serveur_ecarte WHERE leve_le IS NULL"));
        Assert.False((await depot.LireActifAsync())!.Retrait);
        Assert.True(await depot.LeverAsync("titulaire", "Nettoyage de l'essai"));
    }

    [FactSql]
    public async Task Decision_de_bascule_s_ajoute_une_fois_ajout_seul_et_cas_inconnu_refuse()
    {
        await using var source = NpgsqlDataSource.Create(Cnx());
        var depot = new DecisionsBasculeSql(source);
        var decision = new DecisionBascule(CasBascule.RetraitPromotion, "RetraitPuisPromotion", T, 4, 3, 3, 0,
            [new RapportBascule("P1", false, T, T.AddSeconds(-90), true)], new SondesBascule(true, true, null, null), "0/16B3748", T.AddSeconds(-3), 2, "cas_2_serveur1_joint_aucun_poste");

        Assert.True(await depot.AjouterAsync(decision, 3));
        Assert.False(await depot.AjouterAsync(decision, 3));   // même instant et même cas : reprise après arrêt, pas de doublon

        Assert.Equal(1L, await copie.Donnees.LireAsync<long>("SELECT count(*) FROM bascule_decision WHERE cas = 'retrait_promotion'"));
        Assert.Equal("0/16B3748", await copie.Donnees.LireAsync<string>("SELECT lsn_final FROM bascule_decision"));
        Assert.Equal(3, await copie.Donnees.LireAsync<int>("SELECT timeline_apres FROM bascule_decision"));
        Assert.Equal("P1", await copie.Donnees.LireAsync<string>("SELECT rapports -> 0 ->> 'poste' FROM bascule_decision"));

        // Ajout seul (déclencheur) : ni modification ni suppression
        await using var maj = copie.Verif.CreateCommand("UPDATE bascule_decision SET motif = 'autre'");
        await Assert.ThrowsAsync<PostgresException>(() => maj.ExecuteNonQueryAsync());
        await using var sup = copie.Verif.CreateCommand("DELETE FROM bascule_decision");
        await Assert.ThrowsAsync<PostgresException>(() => sup.ExecuteNonQueryAsync());

        await Assert.ThrowsAsync<PostgresException>(() => depot.AjouterAsync(decision with { Cas = "inconnu", DecideLeUtc = T.AddMinutes(1) }, null));
    }

    [FactSql]
    public async Task Attente_des_transactions_de_l_api_n_attend_que_les_connexions_de_ce_nom_d_application_et_lit_un_lsn()
    {
        await using var source = NpgsqlDataSource.Create(Cnx());
        var nomApi = "sql-test-api-" + Guid.NewGuid().ToString("N")[..8];
        var ecritures = new EcrituresServeurSql(source, nomApi, TimeSpan.FromMilliseconds(20));

        Assert.True(await ecritures.AttendreFinTransactionsApiAsync(TimeSpan.FromSeconds(2)));   // aucune connexion de l'API

        await using var api = new NpgsqlConnection(Cnx(nomApi));
        await api.OpenAsync();
        await using (var autre = new NpgsqlCommand("SELECT 1", api)) await autre.ExecuteScalarAsync();   // transaction terminée : rien à attendre
        Assert.True(await ecritures.AttendreFinTransactionsApiAsync(TimeSpan.FromSeconds(2)));

        await using var tx = await api.BeginTransactionAsync();
        await using (var dans = new NpgsqlCommand("SELECT 1", api, tx)) await dans.ExecuteScalarAsync();
        Assert.Equal(1L, await ecritures.TransactionsOuvertesAsync());
        Assert.False(await ecritures.AttendreFinTransactionsApiAsync(TimeSpan.FromMilliseconds(300)));   // délai dépassé : retrait impossible

        await tx.CommitAsync();
        Assert.True(await ecritures.AttendreFinTransactionsApiAsync(TimeSpan.FromSeconds(2)));

        var lsn = await ecritures.LsnCourantAsync();
        Assert.Matches("^[0-9A-F]+/[0-9A-F]+$", lsn);
    }

    [FactSql]
    public async Task Journal_systeme_ecrit_une_ligne_sans_compte_d_utilisateur()
    {
        await using var source = NpgsqlDataSource.Create(Cnx());
        await new JournalSystemeSql(source).EcrireAsync("serveur_retrait_essai", "serveur_ecarte", new Dictionary<string, object?> { ["nature"] = "retrait" });
        Assert.True(await copie.Donnees.LireAsync<bool>("SELECT utilisateur_id IS NULL FROM journal_audit WHERE action = 'serveur_retrait_essai'"));
        Assert.Equal("retrait", await copie.Donnees.LireAsync<string>("SELECT details ->> 'nature' FROM journal_audit WHERE action = 'serveur_retrait_essai'"));
    }

    [FactSql]
    public async Task Promotion_sur_une_base_qui_n_est_pas_en_recovery_echoue_proprement_sans_rien_promouvoir()
    {
        await using var source = NpgsqlDataSource.Create(Cnx());
        var promotion = new BasePromotionSql(source);

        // La base jetable est un principal : pg_promote n'est même pas appelé, aucune exception
        Assert.Equal(ResultatPromotionBase.NonPromue, await promotion.PromouvoirAsync());

        var copieEtat = await promotion.LireCopieAsync();
        Assert.NotNull(copieEtat);
        Assert.False(copieEtat.EnRecovery);
        Assert.Null(copieEtat.DerniereTransactionRecueUtc);   // aucune réplication : rien n'a été rejoué
        Assert.False(await promotion.RetraitVisibleAsync());
        Assert.False(await promotion.LsnRejoueAtteintAsync("0/16B3748"));   // pg_last_wal_replay_lsn() est NULL hors recovery : jamais « atteint »
        Assert.False(await promotion.LsnRejoueAtteintAsync("n'importe quoi; DROP TABLE x"));   // format refusé avant toute requête
        Assert.True(await copie.Donnees.LireAsync<bool>("SELECT NOT pg_is_in_recovery()"));
    }

    [FactSql]
    public async Task Retrait_visible_dans_la_copie_seulement_pour_un_ecart_actif_de_nature_retrait()
    {
        await using var source = NpgsqlDataSource.Create(Cnx());
        var promotion = new BasePromotionSql(source);
        var depot = new EcartsSql(source);

        var bascule = new EcartDetecte(T, "SRV-2", 1, 2);
        Assert.True(await depot.EnregistrerAsync(bascule, "bascule"));
        Assert.False(await promotion.RetraitVisibleAsync());
        Assert.True(await depot.LeverAsync("titulaire", "Nettoyage de l'essai"));

        var retrait = new EcartDetecte(T, "SRV-2", 1, 0, Retrait: true);
        Assert.True(await depot.EnregistrerAsync(retrait, "retrait"));
        Assert.True(await promotion.RetraitVisibleAsync());
        Assert.True(await depot.LeverAsync("titulaire", "Nettoyage de l'essai"));
        Assert.False(await promotion.RetraitVisibleAsync());   // levé : plus actif
    }
}
