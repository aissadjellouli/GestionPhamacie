using System.Text;
using System.Text.Json;
using Npgsql;
using Pharma.Application.Alertes;
using Pharma.Application.Notifications;
using Pharma.Application.Reglages;
using Pharma.Domain.Reglages;
using Pharma.Infrastructure.Reglages;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Mutualisation des réglages, lot M4 : application des modèles du serveur central sur une COPIE JETABLE (« pharmacie_jetable_m4_… », base neuve
/// database/init, script 141 compris) avec la vraie voie de réception (sync_recevoir_descendant), la vraie garde de la base (zz_reglage_garde) et le
/// vrai dépôt (ReglagesMutualisesSql). Sans serveur PostgreSQL joignable, les tests ne font rien.
/// </summary>
public class ReglagesMutualisesSqlTests(BaseJetableM4 base_) : IClassFixture<BaseJetableM4>
{
    private sealed class MoteurEspion : IMoteurAlertes
    {
        public int Appels;
        public Task<int> GenererAsync(CancellationToken ct = default) { Appels++; return Task.FromResult(0); }
    }

    private sealed class AlertesEspion : IAlertesTitulaire
    {
        public readonly List<IReadOnlyList<ReglageImpose>> Messages = [];
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReglagesImposesAsync(IReadOnlyList<ReglageImpose> tables, CancellationToken ct = default) { Messages.Add(tables); return Task.CompletedTask; }
    }

    private static readonly DateTime Publie = new(2026, 11, 20, 9, 12, 0, DateTimeKind.Utc);
    private long version;
    private string? categorie;
    private long utilisateur;

    // ---- Outils ----

    private async Task<bool> PreparerAsync()
    {
        if (!base_.Disponible) return false;
        await using var cx = await base_.Source!.OpenConnectionAsync();
        // Synchronisation active et officine inscrite : sans cela le mode en vigueur reste « local » (reglage_mode_effectif)
        await Exec(cx, "UPDATE sync_parametre SET active = true, inscrite_le = now(), adresse_serveur = 'https://central.exemple:5090' WHERE id = 1");
        await Exec(cx, "SELECT set_config('pharma.reglage_central', 'on', false)");
        // État neuf : toutes les tables locales, aucun modèle, valeurs de départ
        await Exec(cx, """
            UPDATE reglage_mutualise SET mode = 'local', ajouts_locaux = true, mode_recu = NULL, version_modele_recue = NULL, recu_le = NULL, modele = NULL,
                   version_modele_appliquee = NULL, applique_le = NULL, etat = 'aucun', motif_rejet = NULL, version_modele_rejetee = NULL,
                   origine = 'locale', modifie_localement_le = NULL
            """);
        await Exec(cx, "DELETE FROM reglage_central WHERE cle LIKE 'mutu\\_%'");
        await Exec(cx, "DELETE FROM remise_automatique");
        await Exec(cx, "DELETE FROM motif_reglable WHERE code LIKE 'c\\_%' OR libelle LIKE 'Local %'");
        await Exec(cx, "UPDATE motif_reglable SET modele_code_central = false");
        await Exec(cx, "UPDATE parametre_inventaire SET seuil_boites = 2, seuil_montant = 2000");
        await Exec(cx, "UPDATE parametre_surveillance SET seuil_tiroir = 3, heure_ouverture = NULL, heure_fermeture = NULL");
        await Exec(cx, "UPDATE parametre_alerte SET jours_orange = 180, jours_rouge = 90");
        categorie = (string)(await Scalar(cx, "SELECT code FROM categorie_produit ORDER BY code LIMIT 1"))!;
        utilisateur = (long)(await Scalar(cx, "SELECT min(id) FROM utilisateur"))!;
        await Exec(cx, "RESET pharma.reglage_central");
        return true;
    }

    private static async Task Exec(NpgsqlConnection cx, string sql, params object[] valeurs)
    {
        await using var cmd = new NpgsqlCommand(sql, cx);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection cx, string sql, params object[] valeurs)
    {
        await using var cmd = new NpgsqlCommand(sql, cx);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        var r = await cmd.ExecuteScalarAsync();
        return r is DBNull ? null : r;
    }

    private async Task<T?> Lire<T>(string sql, params object[] valeurs)
    {
        await using var cx = await base_.Source!.OpenConnectionAsync();
        return (T?)await Scalar(cx, sql, valeurs);
    }

    private async Task Ecrire(string sql, params object[] valeurs)
    {
        await using var cx = await base_.Source!.OpenConnectionAsync();
        await Exec(cx, sql, valeurs);
    }

    /// <summary>Réception d'un modèle par la vraie voie descendante du script 141 (sync_recevoir_descendant).</summary>
    private async Task<string> RecevoirAsync(string table, string mode, ValeurMutuDto? valeur = null, bool? ajouts = null)
    {
        var json = Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser(table, mode == "local"
            ? ElementMutuDto.Local((int)++version)
            : new ElementMutuDto(mode, (int)++version, ajouts, Publie, "admin-central-1", valeur)));
        await RecevoirBrutAsync(table, json);
        return json;
    }

    private async Task RecevoirBrutAsync(string table, string json)
    {
        await using var cx = await base_.Source!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT sync_recevoir_descendant('reglage_central', $1, $2, $3::jsonb, $2)", cx);
        cmd.Parameters.AddWithValue("mutu_" + table);
        cmd.Parameters.AddWithValue(version);
        cmd.Parameters.AddWithValue(json);
        Assert.Equal("applique", await cmd.ExecuteScalarAsync());
    }

    private (AppliquerReglagesMutualises Cas, AlertesEspion Alertes, MoteurEspion Moteur, ReglagesMutualisesSql Depot) Cas()
    {
        var (depot, alertes, moteur) = (new ReglagesMutualisesSql(base_.Source!), new AlertesEspion(), new MoteurEspion());
        return (new AppliquerReglagesMutualises(depot, moteur, alertes), alertes, moteur, depot);
    }

    private async Task<long> NombreJournalAsync(string action, string table) =>
        (await Lire<long?>("SELECT count(*) FROM journal_audit WHERE action = $1 AND table_cible = $2", action, table)) ?? 0;

    private async Task<JsonElement> DernierJournalAsync(string action, string table)
    {
        var json = await Lire<string>("SELECT details::text FROM journal_audit WHERE action = $1 AND table_cible = $2 ORDER BY id DESC LIMIT 1", action, table);
        Assert.NotNull(json);
        return JsonDocument.Parse(json!).RootElement.Clone();
    }

    private async Task<PostgresException> RefusBaseAsync(string sql, params object[] valeurs)
    {
        await using var cx = await base_.Source!.OpenConnectionAsync();
        return await Assert.ThrowsAsync<PostgresException>(() => Exec(cx, sql, valeurs));
    }

    // ---- Imposé : les 11 tables ----

    private MutuSurveillanceDto Surveillance(int tiroir = 9) =>
        new(6, tiroir, 4, 6, 4, 6, 4, 4, 8, false, 31, 5001m, 8, 20001m, 61, 11, 6, 11, 4, 2, 6);

    [Fact]
    public async Task Les_onze_tables_s_appliquent_en_imposee_puis_ne_changent_plus_au_passage_suivant()
    {
        if (!await PreparerAsync()) return;
        var motif = await Lire<string>("SELECT code FROM motif_reglable WHERE liste = 'tiroir' AND NOT systeme AND effet = 'aucun' ORDER BY id LIMIT 1");
        var (cas, alertes, moteur, _) = Cas();
        await RecevoirAsync(TablesMutualisables.Fiscal, "impose", new MutuFiscalDto(7, "csv-v1", ",", "Africa/Algiers"));
        await RecevoirAsync(TablesMutualisables.AlertesPsychotropes, "impose", new MutuAlertesPsychotropesDto(
            new[] { "patient_repete", "ordonnance_reutilisee", "quantite_anormale", "prescripteur_inhabituel", "prescripteur_hors_zone" }
                .Select(c => new MutuRegleAlertePsychotropeDto(c, true, 4, 60, true, c == "quantite_anormale" ? 20 : null)).ToList()));
        await RecevoirAsync(TablesMutualisables.Tracabilite, "impose", new MutuTracabiliteDto(true, "toujours"));
        await RecevoirAsync(TablesMutualisables.Serialisation, "impose", new MutuSerialisationDto(false, true));
        await RecevoirAsync(TablesMutualisables.Substitution, "impose", new MutuSubstitutionDto(false));
        await RecevoirAsync(TablesMutualisables.Motifs, "impose", new MutuMotifsDto(
        [
            new("tiroir", motif!, "Libellé du central", true, 15, "aucun", "aucun"),
            new("tiroir", "c_abcdefghijkl", "Nouveau motif central", true, 25, "aucun", "aucun")
        ]), ajouts: true);
        await RecevoirAsync(TablesMutualisables.FormulaireSms, "impose", new MutuFormulaireSmsDto("Texte du serveur central"));
        await RecevoirAsync(TablesMutualisables.Surveillance, "impose", Surveillance());
        await RecevoirAsync(TablesMutualisables.Alertes, "impose", new MutuAlertesDto(120, 60, 365, 2, 7, 30, 90, 5, 10));
        await RecevoirAsync(TablesMutualisables.Inventaire, "impose", new MutuInventaireDto(5, 4000.5m));
        await RecevoirAsync(TablesMutualisables.Remises, "impose", new MutuRemisesDto(
            [new MutuRemiseDto("c_remise000001", categorie!, 15, DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), DateOnly.FromDateTime(DateTime.Today.AddDays(60)), "Promotion centrale")]), ajouts: true);

        var bilan = await cas.ExecuterAsync();

        Assert.Equal(TablesMutualisables.Toutes.Order().ToList(), bilan.Appliquees.Order().ToList());
        Assert.Empty(bilan.Rejetees);
        Assert.Equal(11, Assert.Single(alertes.Messages).Count);                      // un seul message pour toutes les tables
        Assert.Equal(1, moteur.Appels);                                              // les seuils d'alerte recalculent les alertes
        Assert.Equal(7, await Lire<short>("SELECT conservation_annees FROM parametre_fiscal WHERE id = 1"));
        Assert.Equal("toujours", await Lire<string>("SELECT patient_obligatoire FROM parametre_tracabilite WHERE id = 1"));
        Assert.False(await Lire<bool>("SELECT actif FROM parametre_serialisation WHERE id = 1"));
        Assert.False(await Lire<bool>("SELECT equivalent_exact_seulement FROM parametre_substitution WHERE id = 1"));
        Assert.Equal("Texte du serveur central", await Lire<string>("SELECT texte FROM parametre_formulaire_sms WHERE id = 1"));
        Assert.Equal(9, await Lire<int>("SELECT seuil_tiroir FROM parametre_surveillance WHERE id = 1"));
        Assert.Equal(120, await Lire<int>("SELECT jours_orange FROM parametre_alerte WHERE id = 1"));
        Assert.Equal(4000.5m, await Lire<decimal>("SELECT seuil_montant FROM parametre_inventaire WHERE id = 1"));
        Assert.Equal(60, await Lire<int>("SELECT periode_jours FROM regle_alerte_psychotrope WHERE code = 'patient_repete'"));
        Assert.Equal("Libellé du central", await Lire<string>("SELECT libelle FROM motif_reglable WHERE liste = 'tiroir' AND code = $1", motif!));
        Assert.True(await Lire<bool>("SELECT modele_code_central FROM motif_reglable WHERE code = 'c_abcdefghijkl'"));
        Assert.Equal(15m, await Lire<decimal>("SELECT pourcentage FROM remise_automatique WHERE modele_cle = 'c_remise000001'"));
        Assert.Equal(11L, await Lire<long>("SELECT count(*) FROM reglage_mutualise WHERE etat = 'applique' AND mode = 'impose' AND origine = 'central'"));

        // Deuxième passage : rien à faire, rien n'est écrit ni journalisé de plus
        var journal = await Lire<long>("SELECT count(*) FROM journal_audit WHERE action LIKE 'reglage_central_%'");
        alertes.Messages.Clear();
        var encore = await cas.ExecuterAsync();
        Assert.Empty(encore.Appliquees);
        Assert.Empty(alertes.Messages);
        Assert.Equal(journal, await Lire<long>("SELECT count(*) FROM journal_audit WHERE action LIKE 'reglage_central_%'"));
    }

    [Fact]
    public async Task Imposee_la_valeur_remplacee_est_au_journal_avant_et_apres_le_message_le_dit()
    {
        if (!await PreparerAsync()) return;
        var (cas, alertes, _, _) = Cas();
        await RecevoirAsync(TablesMutualisables.Inventaire, "impose", new MutuInventaireDto(5, 4000m));
        await cas.ExecuterAsync();
        var j = await DernierJournalAsync("reglage_central_remplace", "parametre_inventaire");
        Assert.Equal(2, j.GetProperty("valeur").GetProperty("avant").GetProperty("seuil_boites").GetInt32());
        Assert.Equal(5, j.GetProperty("valeur").GetProperty("apres").GetProperty("seuil_boites").GetInt32());
        Assert.Equal("local", j.GetProperty("mode").GetProperty("avant").GetString());
        Assert.Equal("impose", j.GetProperty("mode").GetProperty("apres").GetString());
        Assert.Equal("admin-central-1", j.GetProperty("auteur_central").GetString());
        var message = Assert.Single(Assert.Single(alertes.Messages));
        Assert.True(message.ValeurRemplacee);
    }

    // ---- Garde de la base (second rempart) et refus 409 ----

    [Fact]
    public async Task Table_imposee_ecriture_refusee_par_la_base_et_par_la_garde_de_l_application_sauf_marqueur()
    {
        if (!await PreparerAsync()) return;
        var (cas, _, _, depot) = Cas();
        await RecevoirAsync(TablesMutualisables.Inventaire, "impose", new MutuInventaireDto(5, 4000m));
        await cas.ExecuterAsync();

        // Application : refus au format habituel de l'API locale (ErreurDto, 409, champ reglage_impose), AVANT d'écrire
        var ex = await Assert.ThrowsAsync<ReglageImposeException>(() => new GardeReglagesCentraux(depot).ExigerModifiableAsync(TablesMutualisables.Inventaire));
        Assert.Equal("Ce réglage est fixé par le serveur central.", ex.Message);
        // Base : second rempart
        var refus = await RefusBaseAsync("UPDATE parametre_inventaire SET seuil_boites = 9");
        Assert.Equal("P0001", refus.SqlState);
        Assert.Contains("Réglage fixé par le serveur central", refus.MessageText);
        Assert.Equal(5, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));
        // Les scripts qui rejouent la suite posent le marqueur : écriture permise
        await using var cx = await base_.Source!.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        await Exec2(cx, tx, "SELECT set_config('pharma.reglage_central', 'on', true)");
        await Exec2(cx, tx, "UPDATE parametre_inventaire SET seuil_boites = 6");
        await tx.RollbackAsync();
    }

    private static async Task Exec2(NpgsqlConnection cx, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Sans_synchronisation_active_les_tables_imposees_redeviennent_modifiables_D3()
    {
        if (!await PreparerAsync()) return;
        var (cas, _, _, depot) = Cas();
        await RecevoirAsync(TablesMutualisables.Inventaire, "impose", new MutuInventaireDto(5, 4000m));
        await cas.ExecuterAsync();
        await RefusBaseAsync("UPDATE parametre_inventaire SET seuil_boites = 9");

        await Ecrire("UPDATE sync_parametre SET active = false WHERE id = 1");      // désactivation : mode en vigueur « local », valeurs gardées
        await Ecrire("UPDATE parametre_inventaire SET seuil_boites = 9");
        Assert.Equal(9, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));
        await new GardeReglagesCentraux(depot).ExigerModifiableAsync(TablesMutualisables.Inventaire);   // plus de refus
        Assert.Equal("impose", await Lire<string>("SELECT mode FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'"));   // rien n'est effacé

        // Désactivée : rien n'est appliqué, même un nouveau modèle
        await RecevoirAsync(TablesMutualisables.Inventaire, "impose", new MutuInventaireDto(7, 100m));
        Assert.Empty((await cas.ExecuterAsync()).Appliquees);
        Assert.Equal(9, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));

        // Réactivation : la valeur imposée modifiée entre-temps est remise au modèle, journalisée, titulaire prévenu
        await Ecrire("UPDATE sync_parametre SET active = true WHERE id = 1");
        var (cas2, alertes, _, _) = Cas();
        await cas2.ExecuterAsync();
        Assert.Equal(7, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));
        Assert.True(Assert.Single(Assert.Single(alertes.Messages)).ValeurRemplacee);
    }

    [Fact]
    public async Task Liberation_par_le_serveur_central_la_table_redevient_locale_sans_rien_effacer()
    {
        if (!await PreparerAsync()) return;
        var (cas, alertes, _, _) = Cas();
        await RecevoirAsync(TablesMutualisables.Inventaire, "impose", new MutuInventaireDto(5, 4000m));
        await cas.ExecuterAsync();
        alertes.Messages.Clear();

        await RecevoirAsync(TablesMutualisables.Inventaire, "local");
        var bilan = await cas.ExecuterAsync();

        Assert.Equal([TablesMutualisables.Inventaire], bilan.Liberees);
        Assert.Equal("local", await Lire<string>("SELECT mode FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'"));
        Assert.Equal(5, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));   // valeur gardée
        await Ecrire("UPDATE parametre_inventaire SET seuil_boites = 3");                                // modifiable de nouveau
        Assert.Equal(1L, await NombreJournalAsync("reglage_central_libere", "parametre_inventaire"));
        Assert.Empty(alertes.Messages);
    }

    // ---- Proposé, adopter (D2) ----

    [Fact]
    public async Task Propose_valeur_modifiee_localement_gardee_puis_adoptee_a_la_demande()
    {
        if (!await PreparerAsync()) return;
        var (cas, alertes, _, _) = Cas();
        await Ecrire("UPDATE parametre_inventaire SET seuil_boites = 3");      // valeur locale (origine « locale »)
        await RecevoirAsync(TablesMutualisables.Inventaire, "propose", new MutuInventaireDto(5, 4000m));

        var bilan = await cas.ExecuterAsync();

        Assert.Equal([TablesMutualisables.Inventaire], bilan.NonAdoptees);
        Assert.Equal(3, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));   // valeur locale gardée
        Assert.Equal("non_adopte", await Lire<string>("SELECT etat FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'"));
        Assert.Empty(alertes.Messages);
        await Ecrire("UPDATE parametre_inventaire SET seuil_boites = 4");                                // une table proposée reste modifiable

        // Adopter : droit (titulaire), version attendue, trace au journal avec l'employé
        var droits = new Pharma.Infrastructure.Securite.PermissionService(base_.Source!);
        var adopter = new AdopterReglageCentral(cas, droits);
        await Assert.ThrowsAsync<ModeleReglageChangeException>(() => adopter.ExecuterAsync(TablesMutualisables.Inventaire, version + 1, utilisateur));
        await adopter.ExecuterAsync(TablesMutualisables.Inventaire, version, utilisateur);

        Assert.Equal(5, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));
        Assert.Equal(("applique", "central"), (await Lire<string>("SELECT etat FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'"),
            await Lire<string>("SELECT origine FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'")));
        var j = await DernierJournalAsync("reglage_central_adopte", "parametre_inventaire");
        Assert.Equal(4, j.GetProperty("valeur").GetProperty("avant").GetProperty("seuil_boites").GetInt32());
        Assert.Equal(utilisateur, await Lire<long>("SELECT utilisateur_id FROM journal_audit WHERE action = 'reglage_central_adopte' ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public async Task Propose_valeur_jamais_modifiee_suit_le_modele_et_une_modification_locale_la_garde_ensuite()
    {
        if (!await PreparerAsync()) return;
        var (cas, _, _, _) = Cas();
        // Première réception : valeur identique → appliquée, « origine central »
        await RecevoirAsync(TablesMutualisables.Inventaire, "propose", new MutuInventaireDto(2, 2000m));
        Assert.Equal([TablesMutualisables.Inventaire], (await cas.ExecuterAsync()).Appliquees);
        Assert.Equal("central", await Lire<string>("SELECT origine FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'"));
        // Nouvelle version : la valeur n'a pas été modifiée ici, elle suit
        await RecevoirAsync(TablesMutualisables.Inventaire, "propose", new MutuInventaireDto(8, 9000m));
        Assert.Equal([TablesMutualisables.Inventaire], (await cas.ExecuterAsync()).Appliquees);
        Assert.Equal(8, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));
        // Modification locale (la base note « origine locale »), puis nouvelle version : gardée
        await Ecrire("UPDATE parametre_inventaire SET seuil_boites = 3");
        Assert.Equal("locale", await Lire<string>("SELECT origine FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'"));
        await RecevoirAsync(TablesMutualisables.Inventaire, "propose", new MutuInventaireDto(1, 1m));
        Assert.Equal([TablesMutualisables.Inventaire], (await cas.ExecuterAsync()).NonAdoptees);
        Assert.Equal(3, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));
    }

    // ---- Rejet (D9) ----

    [Fact]
    public async Task Valeur_invalide_rejetee_en_entier_avec_motif_sans_message_et_sans_changement()
    {
        if (!await PreparerAsync()) return;
        var (cas, alertes, _, _) = Cas();
        // Seuil de péremption incohérent (rouge >= orange) : refusé par les règles du domaine
        await RecevoirAsync(TablesMutualisables.Alertes, "impose", new MutuAlertesDto(50, 90, 365, 2, 7, 30, 90, 5, 10));
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([TablesMutualisables.Alertes], bilan.Rejetees);
        Assert.Equal(180, await Lire<int>("SELECT jours_orange FROM parametre_alerte WHERE id = 1"));
        Assert.Equal(("rejete", "local"), (await Lire<string>("SELECT etat FROM reglage_mutualise WHERE nom_table = 'parametre_alerte'"),
            await Lire<string>("SELECT mode FROM reglage_mutualise WHERE nom_table = 'parametre_alerte'")));
        Assert.False(string.IsNullOrWhiteSpace(await Lire<string>("SELECT motif_rejet FROM reglage_mutualise WHERE nom_table = 'parametre_alerte'")));
        Assert.Equal(version, await Lire<long>("SELECT version_modele_rejetee FROM reglage_mutualise WHERE nom_table = 'parametre_alerte'"));
        Assert.Empty(alertes.Messages);
        Assert.Equal(1L, await NombreJournalAsync("reglage_central_rejete", "parametre_alerte"));
        // Pas de réexamen de la même version
        await cas.ExecuterAsync();
        Assert.Equal(1L, await NombreJournalAsync("reglage_central_rejete", "parametre_alerte"));
        await Ecrire("UPDATE parametre_alerte SET jours_orange = 170");        // rien n'est imposé : modifiable
    }

    [Fact]
    public async Task Controle_de_la_base_categorie_inconnue_rejette_toute_la_table_des_remises()
    {
        if (!await PreparerAsync()) return;
        var (cas, _, _, _) = Cas();
        var debut = DateOnly.FromDateTime(DateTime.Today);
        await RecevoirAsync(TablesMutualisables.Remises, "impose", new MutuRemisesDto(
        [
            new MutuRemiseDto("c_remise000001", categorie!, 10, debut, debut.AddDays(30), null),
            new MutuRemiseDto("c_remise000002", "categorie_inexistante", 10, debut, debut.AddDays(30), null)
        ]), ajouts: true);
        Assert.Equal([TablesMutualisables.Remises], (await cas.ExecuterAsync()).Rejetees);
        Assert.Equal(0L, await Lire<long>("SELECT count(*) FROM remise_automatique"));                 // tout ou rien
        Assert.Contains("n’existe pas", await Lire<string>("SELECT motif_rejet FROM reglage_mutualise WHERE nom_table = 'remise_automatique'"));
    }

    [Fact]
    public async Task Element_mal_forme_est_rejete_par_la_base_et_l_application_ne_le_reexamine_pas()
    {
        if (!await PreparerAsync()) return;
        var (cas, _, _, _) = Cas();
        version++;
        await RecevoirBrutAsync(TablesMutualisables.Inventaire, "{\"format\":\"mutu-v9\",\"mode\":\"impose\",\"version_modele\":1}");
        Assert.Equal("rejete", await Lire<string>("SELECT etat FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'"));
        Assert.Empty((await cas.ExecuterAsync()).Appliquees);
        Assert.Equal(2, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));
    }

    // ---- Motifs (D5) ----

    [Fact]
    public async Task Motifs_imposes_ajouts_locaux_selon_le_modele_motif_central_verrouille_motif_retire_redevient_local()
    {
        if (!await PreparerAsync()) return;
        var (cas, _, _, _) = Cas();
        var existant = (await Lire<string>("SELECT code FROM motif_reglable WHERE liste = 'tiroir' AND NOT systeme AND effet = 'aucun' ORDER BY id LIMIT 1"))!;
        MutuMotifsDto Motifs(bool avecCentral) => new(avecCentral
            ? [new("tiroir", "c_abcdefghijkl", "Motif du central", true, 25, "aucun", "aucun"), new("tiroir", existant, "Réglé par le central", true, 15, "aucun", "aucun")]
            : [new("tiroir", existant, "Réglé par le central", true, 15, "aucun", "aucun")]);

        // ajouts_locaux = faux : plus d'ajout local, motif du central verrouillé
        await RecevoirAsync(TablesMutualisables.Motifs, "impose", Motifs(true), ajouts: false);
        await cas.ExecuterAsync();
        var refus = await RefusBaseAsync("INSERT INTO motif_reglable (liste, code, libelle, ordre) VALUES ('tiroir', 'local_1', 'Local un', 40)");
        Assert.Contains("ajout de motifs n'est pas permis", refus.MessageText);
        await RefusBaseAsync("UPDATE motif_reglable SET libelle = 'Autre nom' WHERE code = 'c_abcdefghijkl'");
        // ajouts_locaux = vrai : un motif local s'ajoute, il reste modifiable, le motif du central non
        await RecevoirAsync(TablesMutualisables.Motifs, "impose", Motifs(true), ajouts: true);
        await cas.ExecuterAsync();
        await Ecrire("INSERT INTO motif_reglable (liste, code, libelle, ordre) VALUES ('tiroir', 'local_1', 'Local un', 40)");
        await Ecrire("UPDATE motif_reglable SET libelle = 'Local un bis' WHERE code = 'local_1'");
        await RefusBaseAsync("UPDATE motif_reglable SET libelle = 'Autre nom' WHERE code = 'c_abcdefghijkl'");
        await RefusBaseAsync("UPDATE motif_reglable SET libelle = 'Autre nom' WHERE code = $1", existant);

        // Motif retiré du modèle : jamais supprimé ni désactivé, il redevient local et inchangé
        await RecevoirAsync(TablesMutualisables.Motifs, "impose", Motifs(false), ajouts: true);
        await cas.ExecuterAsync();
        Assert.False(await Lire<bool>("SELECT modele_code_central FROM motif_reglable WHERE code = 'c_abcdefghijkl'"));
        Assert.True(await Lire<bool>("SELECT actif FROM motif_reglable WHERE code = 'c_abcdefghijkl'"));
        Assert.Equal("Motif du central", await Lire<string>("SELECT libelle FROM motif_reglable WHERE code = 'c_abcdefghijkl'"));
        await Ecrire("UPDATE motif_reglable SET libelle = 'Désormais local' WHERE code = 'c_abcdefghijkl'");
    }

    [Fact]
    public async Task Motifs_libelle_deja_pris_par_un_motif_local_ou_systeme_non_desactivable_rejettent_la_table()
    {
        if (!await PreparerAsync()) return;
        var (cas, _, _, _) = Cas();
        await Ecrire("INSERT INTO motif_reglable (liste, code, libelle, ordre) VALUES ('tiroir', 'local_2', 'Local pris', 40)");
        await RecevoirAsync(TablesMutualisables.Motifs, "impose", new MutuMotifsDto([new("tiroir", "c_abcdefghijkl", "Local pris", true, 25, "aucun", "aucun")]), ajouts: true);
        Assert.Equal([TablesMutualisables.Motifs], (await cas.ExecuterAsync()).Rejetees);
        Assert.Contains("déjà pris", await Lire<string>("SELECT motif_rejet FROM reglage_mutualise WHERE nom_table = 'motif_reglable'"));
        Assert.Equal(0L, await Lire<long>("SELECT count(*) FROM motif_reglable WHERE code = 'c_abcdefghijkl'"));

        var systeme = (await Lire<string>("SELECT code FROM motif_reglable WHERE liste = 'tiroir' AND systeme AND effet = 'aucun' LIMIT 1"))!;
        await RecevoirAsync(TablesMutualisables.Motifs, "impose", new MutuMotifsDto([new("tiroir", systeme, "Autre (préciser)", false, 999, "aucun", "aucun")]), ajouts: true);
        Assert.Equal([TablesMutualisables.Motifs], (await cas.ExecuterAsync()).Rejetees);
        Assert.Contains("indispensable", await Lire<string>("SELECT motif_rejet FROM reglage_mutualise WHERE nom_table = 'motif_reglable'"));
        Assert.True(await Lire<bool>("SELECT actif FROM motif_reglable WHERE code = $1 AND liste = 'tiroir'", systeme));
    }

    // ---- Remises (D6) ----

    [Fact]
    public async Task Remises_imposees_les_remises_locales_sont_arretees_jamais_supprimees_et_le_titulaire_est_prevenu()
    {
        if (!await PreparerAsync()) return;
        var (cas, alertes, _, _) = Cas();
        await Ecrire("""
            INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, motif, cree_par)
            VALUES ($1, 10, current_date - 1, current_date + 30, 'locale en cours', $2), ($1, 20, current_date - 100, current_date - 50, 'locale terminée', $2)
            """, categorie!, utilisateur);
        var debut = DateOnly.FromDateTime(DateTime.Today);
        await RecevoirAsync(TablesMutualisables.Remises, "impose", new MutuRemisesDto([new MutuRemiseDto("c_remise000001", categorie!, 15, debut, debut.AddDays(60), "Centrale")]), ajouts: true);

        await cas.ExecuterAsync();

        Assert.Equal(3L, await Lire<long>("SELECT count(*) FROM remise_automatique"));                            // rien n'est supprimé
        Assert.NotNull(await Lire<object>("SELECT arretee_le FROM remise_automatique WHERE motif = 'locale en cours'"));   // arrêtée
        Assert.Null(await Lire<object>("SELECT arretee_le FROM remise_automatique WHERE modele_cle = 'c_remise000001'"));
        Assert.Null(await Lire<object>("SELECT arretee_par FROM remise_automatique WHERE motif = 'locale en cours'"));
        var message = Assert.Single(Assert.Single(alertes.Messages));
        Assert.Equal(1, message.RemisesLocalesArretees);
        var j = await DernierJournalAsync("reglage_central_remplace", "remise_automatique");
        Assert.Equal(1, j.GetProperty("valeur").GetProperty("avant").GetProperty("remises_locales_arretees").GetArrayLength());
        // Plus de création locale, remise du central verrouillée, arrêt d'une remise locale encore permis par la base
        await RefusBaseAsync("INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, cree_par) VALUES ($1, 5, current_date, current_date + 1, $2)", categorie!, utilisateur);
        await RefusBaseAsync("UPDATE remise_automatique SET pourcentage = 50 WHERE modele_cle = 'c_remise000001'");
        // Retrait de la remise du central du modèle : arrêtée, jamais supprimée
        await RecevoirAsync(TablesMutualisables.Remises, "impose", new MutuRemisesDto([]), ajouts: true);
        await cas.ExecuterAsync();
        Assert.NotNull(await Lire<object>("SELECT arretee_le FROM remise_automatique WHERE modele_cle = 'c_remise000001'"));
        Assert.Equal(3L, await Lire<long>("SELECT count(*) FROM remise_automatique"));
    }

    [Fact]
    public async Task Remises_proposees_une_remise_locale_qui_chevauche_fait_rejeter_la_table()
    {
        if (!await PreparerAsync()) return;
        var (cas, _, _, _) = Cas();
        await Ecrire("""
            INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, cree_par) VALUES ($1, 10, current_date - 1, current_date + 30, $2)
            """, categorie!, utilisateur);
        var debut = DateOnly.FromDateTime(DateTime.Today);
        await RecevoirAsync(TablesMutualisables.Remises, "propose", new MutuRemisesDto([new MutuRemiseDto("c_remise000001", categorie!, 15, debut, debut.AddDays(60), null)]), ajouts: true);
        Assert.Equal([TablesMutualisables.Remises], (await cas.ExecuterAsync()).Rejetees);
        Assert.Contains("couvre déjà", await Lire<string>("SELECT motif_rejet FROM reglage_mutualise WHERE nom_table = 'remise_automatique'"));
        Assert.Equal(1L, await Lire<long>("SELECT count(*) FROM remise_automatique"));
        Assert.Null(await Lire<object>("SELECT arretee_le FROM remise_automatique"));                // la remise locale n'est pas touchée
    }

    // ---- Horaires (D7) ----

    [Fact]
    public async Task Surveillance_imposee_les_seuils_sont_fixes_les_horaires_restent_locaux_et_ne_remettent_rien_en_cause()
    {
        if (!await PreparerAsync()) return;
        var (cas, alertes, _, _) = Cas();
        await RecevoirAsync(TablesMutualisables.Surveillance, "impose", Surveillance());
        await cas.ExecuterAsync();
        alertes.Messages.Clear();

        await RefusBaseAsync("UPDATE parametre_surveillance SET seuil_tiroir = 4");
        await Ecrire("UPDATE parametre_surveillance SET heure_ouverture = '08:00', heure_fermeture = '20:00'");
        Assert.Equal(new TimeOnly(8, 0), await Lire<TimeOnly>("SELECT heure_ouverture FROM parametre_surveillance WHERE id = 1"));
        // L'horaire n'est pas dans le modèle : le passage suivant ne le touche pas et ne prévient personne
        await cas.ExecuterAsync();
        Assert.Equal(new TimeOnly(8, 0), await Lire<TimeOnly>("SELECT heure_ouverture FROM parametre_surveillance WHERE id = 1"));
        Assert.Equal(9, await Lire<int>("SELECT seuil_tiroir FROM parametre_surveillance WHERE id = 1"));
        Assert.Empty(alertes.Messages);
    }

    // ---- Valeur imposée modifiée malgré tout ----

    [Fact]
    public async Task Valeur_imposee_modifiee_par_un_script_est_remise_au_modele_au_passage_suivant()
    {
        if (!await PreparerAsync()) return;
        var (cas, alertes, _, _) = Cas();
        await RecevoirAsync(TablesMutualisables.Inventaire, "impose", new MutuInventaireDto(5, 4000m));
        await cas.ExecuterAsync();
        alertes.Messages.Clear();
        // Écriture hors garde (script sans marqueur…) puis note de modification locale
        await using (var cx = await base_.Source!.OpenConnectionAsync())
        {
            await Exec(cx, "SELECT set_config('pharma.reglage_central', 'on', false)");
            await Exec(cx, "UPDATE parametre_inventaire SET seuil_boites = 9");
            await Exec(cx, "UPDATE reglage_mutualise SET origine = 'locale', modifie_localement_le = now() + interval '1 minute' WHERE nom_table = 'parametre_inventaire'");
            await Exec(cx, "RESET pharma.reglage_central");
        }
        Assert.Equal([TablesMutualisables.Inventaire], (await cas.ExecuterAsync()).Appliquees);
        Assert.Equal(5, await Lire<int>("SELECT seuil_boites FROM parametre_inventaire WHERE id = 1"));
        Assert.True(Assert.Single(Assert.Single(alertes.Messages)).ValeurRemplacee);
    }

    // ---- Journal et sujet ----

    [Fact]
    public async Task Le_sujet_reglages_centraux_est_accepte_par_la_base()
    {
        if (!await PreparerAsync()) return;
        await Ecrire("INSERT INTO destinataire_notification (nom, email, types) VALUES ('Essai', 'essai@exemple.dz', ARRAY['reglages_centraux'])");
        Assert.Equal(1L, await Lire<long>("SELECT count(*) FROM destinataire_notification WHERE 'reglages_centraux' = ANY (types)"));
    }
}
