using Npgsql;
using Pharma.Application.Factures;
using Pharma.Application.SecondServeur;
using Pharma.Domain.Commun;
using Pharma.Domain.Factures;
using Pharma.Infrastructure.Factures;
using Pharma.Infrastructure.SecondServeur;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Bascule du 2e serveur, lot 7 (script 164) : saut des compteurs à la promotion, une seule fois par timeline, numérotation continue après le saut
/// (tickets, factures, avoirs, bons de commande). La timeline est passée à la fonction (le serveur de test n'est jamais promu).
/// Scénario complet sur sa propre copie : première note, promotion, numérotation après le saut, relances, seconde promotion, ajout seul.
/// </summary>
[Trait("Categorie", "Sql")]
public sealed class SautCompteursBasculeScenarioSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    private Donnees D => copie.Donnees;

    private async Task<(int Timeline, bool Saut, int Compteurs)> AppelerAsync(int timeline)
    {
        await using var cmd = copie.Source.CreateCommand("SELECT timeline_notee, saut_fait, compteurs_sautes FROM bascule_saut_promotion($1)");
        cmd.Parameters.AddWithValue(timeline);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt32(0), r.GetBoolean(1), r.GetInt32(2));
    }

    /// <summary>Émet une facture (1 000 DA) ou un avoir (100 DA, sur la facture d'origine donnée) et rend (id, n°).</summary>
    private async Task<(long Id, string Numero)> EmettreAsync(string type, long utilisateur, long patient, DateOnly date, long? origine = null)
    {
        var ttc = type == "avoir" ? 100m : 1000m;
        var f = new FactureAEcrire(type, Destinataire.Patient, patient, null, null, "Patient Essai", null, date, null, null, new Totaux(ttc, 0m, ttc), 0m,
            [new LigneFacture(null, null, "libre", "Ligne d'essai", 1m, ttc, 0m, ttc)], origine, "Essai", null, utilisateur, type == "avoir" ? null : date);
        return await new FacturesSql(copie.Source).EmettreAsync(f, null);
    }

    [FactSql]
    public async Task Promotion_saute_les_compteurs_une_seule_fois_et_la_numerotation_continue()
    {
        var annee = await D.LireAsync<int>("SELECT extract(year FROM (now() AT TIME ZONE 'Africa/Algiers'))::int");
        var jour = new DateOnly(annee, 6, 15);
        var u = await D.UtilisateurAsync();
        var patient = await D.PatientAsync();
        var ticketAvant = await D.LireAsync<long>("SELECT dernier_numero FROM compteur_ticket WHERE id = 1");

        // Première note : aucune ligne, la timeline est gardée sans saut
        Assert.Equal((1, false, 0), await AppelerAsync(1));
        Assert.Equal(ticketAvant, await D.LireAsync<long>("SELECT dernier_numero FROM compteur_ticket WHERE id = 1"));

        // Une facture, un avoir et un bon de commande avant la promotion
        var facture = await EmettreAsync("facture", u, patient, jour);
        Assert.Equal($"F-{annee}-0001", facture.Numero);
        Assert.Equal($"AV-{annee}-0001", (await EmettreAsync("avoir", u, patient, jour, facture.Id)).Numero);
        var commandeAvant = await D.LireAsync<string>("SELECT prochain_numero_commande()");
        var rangCommande = long.Parse(commandeAvant[2..]);

        // Promotion (timeline 2) : tout saute
        var saut = await AppelerAsync(2);
        Assert.Equal(2, saut.Timeline);
        Assert.True(saut.Saut);
        Assert.Equal(4, saut.Compteurs);   // tickets, factures, avoirs, bons de commande
        Assert.Equal(ticketAvant + 100_000, await D.LireAsync<long>("SELECT dernier_numero FROM compteur_ticket WHERE id = 1"));
        Assert.Equal(1, await D.LireAsync<long>("SELECT count(*) FROM compteur_ticket_saut WHERE motif = 'bascule' AND du = @du AND au = @au",
            ("du", ticketAvant + 1), ("au", ticketAvant + 100_000)));
        Assert.Equal(4, await D.LireAsync<long>("SELECT count(*) FROM bascule_saut"));
        Assert.Equal(1, await D.LireAsync<long>("SELECT count(*) FROM journal_vente WHERE evenement = 'saut_compteur'"));
        Assert.Equal(4, await D.LireAsync<long>("SELECT count(*) FROM journal_audit WHERE action = 'saut_compteur_bascule' AND details ->> 'motif' = 'bascule'"));

        // Numérotation continue après le saut : la réserve est franchie, puis +1 à la fois
        var apres = await EmettreAsync("facture", u, patient, jour);
        Assert.Equal($"F-{annee}-1002", apres.Numero);
        Assert.Equal($"AV-{annee}-1002", (await EmettreAsync("avoir", u, patient, jour, apres.Id)).Numero);
        Assert.Equal($"C-{rangCommande + 1_001:000000}", await D.LireAsync<string>("SELECT prochain_numero_commande()"));
        Assert.Equal($"T-{ticketAvant + 100_001:000000}", await D.LireAsync<string>("SELECT prochain_numero_vente()"));
        Assert.Equal($"T-{ticketAvant + 100_002:000000}", await D.LireAsync<string>("SELECT prochain_numero_vente()"));
        // La plage sautée n'est pas une série de n° manquants
        Assert.Equal(0, await D.LireAsync<long>("SELECT count(*) FROM journal_vente_continuite() WHERE anomalie = 'numero_manquant' AND numero IN (@a, @b)",
            ("a", $"T-{ticketAvant + 1:000000}"), ("b", $"T-{ticketAvant + 100_000:000000}")));

        // Relancer (même timeline, plus ancienne) ne resaute pas
        Assert.False((await AppelerAsync(2)).Saut);
        Assert.False((await AppelerAsync(1)).Saut);
        Assert.Equal(4, await D.LireAsync<long>("SELECT count(*) FROM bascule_saut"));
        Assert.Equal(ticketAvant + 100_002, await D.LireAsync<long>("SELECT dernier_numero FROM compteur_ticket WHERE id = 1"));

        // Une seconde promotion saute de nouveau, une seule fois
        Assert.True((await AppelerAsync(3)).Saut);
        Assert.False((await AppelerAsync(3)).Saut);
        Assert.Equal(ticketAvant + 200_002, await D.LireAsync<long>("SELECT dernier_numero FROM compteur_ticket WHERE id = 1"));
        Assert.Equal(8, await D.LireAsync<long>("SELECT count(*) FROM bascule_saut"));

        // Les deux tables sont en ajout seul (modification, suppression, vidage refusés)
        await using var cx = await copie.Source.OpenConnectionAsync();
        foreach (var sql in new[] { "UPDATE bascule_promotion SET motif = 'x'", "DELETE FROM bascule_promotion", "TRUNCATE bascule_promotion",
                                    "UPDATE bascule_saut SET reserve = reserve", "DELETE FROM bascule_saut" })
        {
            await using var cmd = new NpgsqlCommand(sql, cx);
            await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
    }
}

/// <summary>Appels simultanés sur une copie vierge : verrou consultatif et clé unique, une seule ligne par timeline.</summary>
[Trait("Categorie", "Sql")]
public sealed class SautCompteursBasculeSimultaneSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    [FactSql]
    public async Task Appels_simultanes_ne_notent_qu_une_fois_la_timeline()
    {
        var appels = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var cmd = copie.Source.CreateCommand("SELECT timeline_notee FROM bascule_saut_promotion($1)");
            cmd.Parameters.AddWithValue(7);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }));
        Assert.All(appels, t => Assert.Equal(7, t));
        Assert.Equal(1, await copie.Donnees.LireAsync<long>("SELECT count(*) FROM bascule_promotion"));
    }
}

/// <summary>Le service appelé par l'API au démarrage : timeline lue dans la base, une seule note, aucun saut sur un serveur qui n'a jamais été promu.</summary>
[Trait("Categorie", "Sql")]
public sealed class SautCompteursBasculeServiceSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    [FactSql]
    public async Task Le_service_note_la_timeline_courante_sans_saut_puis_ne_fait_plus_rien()
    {
        var service = new SautCompteursBasculeSql(copie.Source);
        var ticketAvant = await copie.Donnees.LireAsync<long>("SELECT dernier_numero FROM compteur_ticket WHERE id = 1");

        var premier = await service.AppliquerAsync();
        var second = await service.AppliquerAsync();

        Assert.True(premier.Timeline >= 1);
        Assert.False(premier.SautFait);
        Assert.Equal(new ResultatSautCompteurs(premier.Timeline, false, 0), second);
        Assert.Equal(1, await copie.Donnees.LireAsync<long>("SELECT count(*) FROM bascule_promotion"));
        Assert.Equal(0, await copie.Donnees.LireAsync<long>("SELECT count(*) FROM bascule_saut"));
        Assert.Equal(ticketAvant, await copie.Donnees.LireAsync<long>("SELECT dernier_numero FROM compteur_ticket WHERE id = 1"));
    }
}
