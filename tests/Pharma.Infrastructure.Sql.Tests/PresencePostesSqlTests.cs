using Npgsql;
using Pharma.Application.Commun;
using Pharma.Application.SecondServeur;
using Pharma.Infrastructure.SecondServeur;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Vague 2, lot V2-1b : présence des postes (script 165) sur une copie jetable. Une base jetable n'est pas une copie répliquée : c'est un serveur principal,
/// la référence des postes connus est now(). La référence de la copie (pg_last_xact_replay_timestamp) se vérifie par la fonction pure avec une référence figée.
/// </summary>
[Trait("Categorie", "Sql")]
public sealed class PresencePostesSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    private sealed class Horloge : IHorloge
    {
        public DateTime MaintenantUtc { get; private set; } = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
        public void Avancer(double secondes) => MaintenantUtc += TimeSpan.FromSeconds(secondes);
    }

    private string Cnx() => new NpgsqlConnectionStringBuilder(OutilsPostgres.ChaineConnexion()) { Database = copie.Nom }.ConnectionString;

    /// <summary>Vide la présence ; mode de la liste des postes autorisés (script 167) : « inactif » = présence par nom, comme avant la liste.</summary>
    private async Task ViderAsync(string mode = "inactif")
    {
        await using var cmd = copie.Verif.CreateCommand("DELETE FROM presence_poste; DELETE FROM poste_autorise; UPDATE parametre_postes SET mode = @mode");
        cmd.Parameters.AddWithValue("mode", mode);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> PosteActifAsync(string nom, string statut = "actif")
    {
        await using var cmd = copie.Verif.CreateCommand("""
            INSERT INTO poste_autorise (nom, statut, cle_publique, empreinte_cle, enrole_le, revoque_le)
            VALUES (@nom, @statut, '\x3059', '0123456789ABCDEF', now(), CASE WHEN @statut = 'revoque' THEN now() END) RETURNING id
            """);
        cmd.Parameters.AddWithValue("nom", nom);
        cmd.Parameters.AddWithValue("statut", statut);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [FactSql]
    public async Task Mode_transition_seuls_les_postes_prouves_et_actifs_sont_connus_sous_leur_nom_actuel()
    {
        await ViderAsync("transition");
        var a = await PosteActifAsync("CAISSE-A");
        var r = await PosteActifAsync("CAISSE-R", "revoque");
        var sql = new PresencePostesSql(copie.Source);
        await sql.NoterAsync("CAISSE-A", a);
        await sql.NoterAsync("ANCIEN-NOM-A", a);   // même poste noté sous un ancien nom (renommé) : une seule ligne connue, nom actuel
        await sql.NoterAsync("CAISSE-R", r);       // poste révoqué : jamais connu
        await sql.NoterAsync("NON-PROUVE");        // poste non prouvé : jamais connu
        var lus = await sql.LireAsync();
        Assert.Equal(["CAISSE-A"], lus.Postes.Select(p => p.Poste).ToArray());

        await ViderAsync("inactif");
        await sql.NoterAsync("NON-PROUVE");
        Assert.Equal(["NON-PROUVE"], (await sql.LireAsync()).Postes.Select(p => p.Poste).ToArray());
    }

    private async Task InsererAsync(string poste, string ilYa)
    {
        await using var cmd = copie.Verif.CreateCommand($"INSERT INTO presence_poste (poste, vu_le) VALUES (@p, now() - interval '{ilYa}')");
        cmd.Parameters.AddWithValue("p", poste);
        await cmd.ExecuteNonQueryAsync();
    }

    [FactSql]
    public async Task La_table_existe_et_est_classee_jamais_pour_la_synchronisation()
    {
        Assert.Equal("jamais", await copie.Donnees.LireAsync<string>("SELECT sens FROM sync_table WHERE nom_table = 'presence_poste'"));
        Assert.True(await copie.Donnees.LireAsync<bool>("SELECT to_regclass('presence_poste') IS NOT NULL"));
    }

    [FactSql]
    public async Task L_upsert_garde_une_ligne_par_poste_et_avance_l_instant_vu()
    {
        await ViderAsync();
        var sql = new PresencePostesSql(copie.Source);
        await sql.NoterAsync("CAISSE-1");
        var premier = await copie.Donnees.LireAsync<DateTime>("SELECT vu_le FROM presence_poste WHERE poste = 'CAISSE-1'");
        await Task.Delay(50);
        await sql.NoterAsync("CAISSE-1");
        await sql.NoterAsync("CAISSE-2");
        Assert.Equal(2L, await copie.Donnees.LireAsync<long>("SELECT count(*) FROM presence_poste"));
        Assert.True(await copie.Donnees.LireAsync<DateTime>("SELECT vu_le FROM presence_poste WHERE poste = 'CAISSE-1'") > premier);
    }

    [FactSql]
    public async Task L_ecriture_est_limitee_a_une_par_minute_et_par_poste_meme_avec_de_nombreuses_requetes()
    {
        await ViderAsync();
        var horloge = new Horloge();
        var presence = new PresencePostes(new RegistrePostesVus(), new PresencePostesSql(copie.Source), new SuiviServeurEcarte(), new VeilleServeur(false),
            new ReleveServeursSql(Cnx(), new NpgsqlConnectionStringBuilder(Cnx()).Host), horloge);

        presence.Noter("CAISSE-1");
        await presence.Derniere;
        var premier = await copie.Donnees.LireAsync<DateTime>("SELECT vu_le FROM presence_poste WHERE poste = 'CAISSE-1'");
        for (var i = 0; i < 8; i++)   // 40 s de requêtes (healthcheck de 15 s, appels de l'écran) : aucune écriture de plus
        {
            await Task.Delay(20);
            horloge.Avancer(5);
            presence.Noter("CAISSE-1");
            await presence.Derniere;
        }
        Assert.Equal(premier, await copie.Donnees.LireAsync<DateTime>("SELECT vu_le FROM presence_poste WHERE poste = 'CAISSE-1'"));

        horloge.Avancer(21);   // 61 s depuis la première écriture
        presence.Noter("CAISSE-1");
        await presence.Derniere;
        Assert.True(await copie.Donnees.LireAsync<DateTime>("SELECT vu_le FROM presence_poste WHERE poste = 'CAISSE-1'") > premier);
        Assert.Equal(1L, await copie.Donnees.LireAsync<long>("SELECT count(*) FROM presence_poste"));
    }

    [FactSql]
    public async Task Table_vide_reference_presente_et_aucun_poste_connu()
    {
        await ViderAsync();
        var lus = await new PresencePostesSql(copie.Source).LireAsync();
        Assert.NotNull(lus.DerniereDonneeRecueUtc);   // serveur 1 (pas une copie) : référence = now()
        Assert.Empty(lus.Postes);
        Assert.Empty(PostesConnus.Calculer(lus.Postes, lus.DerniereDonneeRecueUtc, TimeSpan.FromMinutes(3))!);
    }

    [FactSql]
    public async Task Lecture_des_postes_connus_serveur_1_reference_now_fenetre_de_3_minutes()
    {
        await ViderAsync();
        await InsererAsync("A", "10 seconds");
        await InsererAsync("B", "2 minutes");
        await InsererAsync("C", "4 minutes");
        await InsererAsync("D", "2 days");
        var lus = await new PresencePostesSql(copie.Source).LireAsync();

        var baseNow = await copie.Donnees.LireAsync<DateTime>("SELECT now()");
        Assert.True(Math.Abs((lus.DerniereDonneeRecueUtc!.Value - baseNow.ToUniversalTime()).TotalSeconds) < 30);
        Assert.Equal(["A", "B", "C"], lus.Postes.Select(p => p.Poste).Order().ToArray());   // D (2 jours) hors de la fenêtre de lecture
        Assert.Equal(["A", "B"], PostesConnus.Calculer(lus.Postes, lus.DerniereDonneeRecueUtc, TimeSpan.FromMinutes(3))!.Order().ToArray());
    }

    [FactSql]
    public async Task Reference_figee_de_la_copie_les_postes_connus_ne_fondent_pas_avec_l_heure_courante()
    {
        await ViderAsync();
        await InsererAsync("A", "10 seconds");
        await InsererAsync("B", "2 minutes");
        await InsererAsync("C", "4 minutes");
        var lus = await new PresencePostesSql(copie.Source).LireAsync();

        // Une copie coupée il y a 2 min 30 s : sa dernière donnée rejouée date d'alors. Les postes dits vus APRÈS cet instant (A, B) n'existent pas pour elle ;
        // ceux vus dans les 3 minutes AVANT cet instant (C : 1 min 30 s avant) sont connus. Avec l'heure courante, tout aurait fondu.
        var figee = lus.DerniereDonneeRecueUtc!.Value.AddSeconds(-150);
        Assert.Equal(["C"], PostesConnus.Calculer(lus.Postes, figee, TimeSpan.FromMinutes(3))!.ToArray());
        // Heure courante bien plus tard (une heure) : la liste serait vide, la référence figée la garde
        Assert.Empty(PostesConnus.Calculer(lus.Postes, lus.DerniereDonneeRecueUtc.Value.AddHours(1), TimeSpan.FromMinutes(3))!);
        Assert.Null(PostesConnus.Calculer(lus.Postes, null, TimeSpan.FromMinutes(3)));   // référence inconnue : aucune décision
    }

    [FactSql]
    public async Task Copie_connectee_est_fausse_sur_une_base_sans_replique()
    {
        Assert.False(await new CopieConnecteeSql(copie.Source).ConnecteeAsync());
    }
}
