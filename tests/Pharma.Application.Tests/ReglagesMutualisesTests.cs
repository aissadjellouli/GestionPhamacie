using System.Text;
using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Referentiels;
using Pharma.Application.Reglages;
using Pharma.Application.Surveillance;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Reglages;
using Pharma.Shared;

namespace Pharma.Application.Tests;

// ---- Mutualisation des réglages, lot M4 : application des modèles, adoption, garde des cas d'usage (docs/conception-synchronisation.md § 16 bis) ----

/// <summary>Dépôt en mémoire : états des 11 tables, valeur locale par table, journal et écritures observables.</summary>
internal sealed class DepotReglagesEnMemoire : IReglagesMutualisesDepot
{
    public bool Possible { get; set; } = true;
    public Dictionary<string, EtatReglage> Etats { get; } = TablesMutualisables.Toutes.ToDictionary(t => t,
        t => new EtatReglage(t, "local", true, null, null, null, null, null, "aucun", null, null, "locale", null));
    /// <summary>Valeur locale de chaque table, au format de la valeur du modèle.</summary>
    public Dictionary<string, string> Locales { get; } = [];
    public Dictionary<string, List<string>> ErreursBase { get; } = [];
    public Dictionary<string, ResultatEcriture> ArretsRemises { get; } = [];
    public List<string> Ecritures { get; } = [];
    public List<(string Action, string Table, string ModeAvant, string ModeApres, string? Avant, string? Apres, long? Utilisateur)> Journal { get; } = [];
    public HashSet<string> Valides { get; } = [];

    public Task<bool> ApplicationPossibleAsync(CancellationToken ct = default) => Task.FromResult(Possible);

    public Task<(string Mode, bool AjoutsLocaux)> ModeEffectifAsync(string table, CancellationToken ct = default) =>
        Task.FromResult((Possible ? Etats[table].Mode : "local", Etats[table].AjoutsLocaux));

    public Task<IReadOnlyList<EtatReglage>> EtatsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EtatReglage>>(Etats.Values.ToList());

    public Task<ITransactionReglage> OuvrirAsync(string table, CancellationToken ct = default) => Task.FromResult<ITransactionReglage>(new Tx(this, table));

    public Task JournaliserAsync(string action, string table, string modeAvant, string modeApres, long? version, string? auteur, long? utilisateurId,
        CancellationToken ct = default)
    {
        Journal.Add((action, table, modeAvant, modeApres, null, null, utilisateurId));
        return Task.CompletedTask;
    }

    private sealed class Tx(DepotReglagesEnMemoire d, string table) : ITransactionReglage
    {
        public Task<EtatReglage> EtatAsync(CancellationToken ct = default) => Task.FromResult(d.Etats[table]);
        public Task<string> LireValeurAsync(string valeur, CancellationToken ct = default) => Task.FromResult(d.Locales.GetValueOrDefault(table, "{}"));
        public Task<IReadOnlyList<string>> ControlerAsync(string valeur, bool impose, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(d.ErreursBase.GetValueOrDefault(table, []));

        public Task<ResultatEcriture> EcrireAsync(string valeur, bool impose, bool identique, CancellationToken ct = default)
        {
            d.Ecritures.Add($"{table}|{(impose ? "impose" : "propose")}|{(identique ? "identique" : "differe")}");
            return Task.FromResult(d.ArretsRemises.GetValueOrDefault(table, new ResultatEcriture(0, null)));
        }

        public Task EnregistrerEtatAsync(MiseAJourEtat e, CancellationToken ct = default)
        {
            var avant = d.Etats[table];
            d.Etats[table] = avant with
            {
                Mode = e.Mode ?? avant.Mode, AjoutsLocaux = e.AjoutsLocaux ?? avant.AjoutsLocaux, Etat = e.Etat,
                VersionAppliquee = e.VersionAppliquee ?? avant.VersionAppliquee, AppliqueLe = e.Applique ? DateTime.UtcNow : avant.AppliqueLe,
                MotifRejet = e.MotifRejet, VersionRejetee = e.VersionRejetee ?? avant.VersionRejetee, Origine = e.Origine ?? avant.Origine
            };
            return Task.CompletedTask;
        }

        public Task JournaliserAsync(string action, string modeAvant, string modeApres, long? version, string? auteur, string? avant, string? apres,
            long? utilisateurId, CancellationToken ct = default)
        {
            d.Journal.Add((action, table, modeAvant, modeApres, avant, apres, utilisateurId));
            return Task.CompletedTask;
        }

        public Task ValiderAsync(CancellationToken ct = default) { d.Valides.Add(table); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class AlertesReglagesEspion : IAlertesTitulaire
{
    public List<IReadOnlyList<ReglageImpose>> Messages { get; } = [];
    public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
    public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
    public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
    public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
    public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
    public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReglagesImposesAsync(IReadOnlyList<ReglageImpose> tables, CancellationToken ct = default) { Messages.Add(tables); return Task.CompletedTask; }
}

public class ReglagesMutualisesTests
{
    private const string Inventaire = TablesMutualisables.Inventaire;
    private const string LocaleDeuxMille = "{\"seuil_boites\":2,\"seuil_montant\":2000.00}";

    private static string Element(string table, string mode, int version, ValeurMutuDto? valeur = null, bool? ajouts = null) =>
        Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser(table, mode == "local"
            ? ElementMutuDto.Local(version)
            : new ElementMutuDto(mode, version, ajouts, new DateTime(2026, 11, 20, 9, 12, 0, DateTimeKind.Utc), "admin-central-1", valeur)));

    private static DepotReglagesEnMemoire Depot(string table, string element, int version, string mode, string? localeJson = null, string origine = "locale",
        string modeEnVigueur = "local")
    {
        var d = new DepotReglagesEnMemoire();
        d.Etats[table] = d.Etats[table] with { Mode = modeEnVigueur, ModeRecu = mode, VersionRecue = version, ModeleJson = element, Origine = origine };
        if (localeJson is not null) d.Locales[table] = localeJson;
        return d;
    }

    private static (AppliquerReglagesMutualises Cas, AlertesReglagesEspion Alertes, MoteurEspion Moteur) Cas(DepotReglagesEnMemoire d)
    {
        var (alertes, moteur) = (new AlertesReglagesEspion(), new MoteurEspion());
        return (new AppliquerReglagesMutualises(d, moteur, alertes), alertes, moteur);
    }

    // ---- Imposé ----

    [Fact]
    public async Task Impose_remplace_la_valeur_locale_journalise_avant_apres_et_previent_le_titulaire()
    {
        var d = Depot(Inventaire, Element(Inventaire, "impose", 3, new MutuInventaireDto(5, 4000m)), 3, "impose", LocaleDeuxMille);
        var (cas, alertes, _) = Cas(d);

        var bilan = await cas.ExecuterAsync();

        Assert.Equal([Inventaire], bilan.Appliquees);
        Assert.Equal([$"{Inventaire}|impose|differe"], d.Ecritures);
        var e = d.Etats[Inventaire];
        Assert.Equal(("impose", "applique", 3L, "central"), (e.Mode, e.Etat, e.VersionAppliquee, e.Origine));
        var j = Assert.Single(d.Journal);
        Assert.Equal(ActionsReglage.Remplace, j.Action);
        Assert.Equal(("local", "impose"), (j.ModeAvant, j.ModeApres));
        Assert.Equal(LocaleDeuxMille, j.Avant);                        // l'ancienne valeur est au journal
        Assert.Contains("\"seuil_boites\":5", j.Apres);                  // et la nouvelle
        var message = Assert.Single(Assert.Single(alertes.Messages));
        Assert.Equal((Inventaire, true, 0), (message.Table, message.ValeurRemplacee, message.RemisesLocalesArretees));
        Assert.Contains("inventaire", message.Libelle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Impose_avec_valeur_identique_ne_remplace_rien_mais_previent_que_la_table_est_imposee()
    {
        var d = Depot(Inventaire, Element(Inventaire, "impose", 1, new MutuInventaireDto(2, 2000m)), 1, "impose", LocaleDeuxMille);
        var (cas, alertes, _) = Cas(d);
        await cas.ExecuterAsync();
        Assert.Equal(ActionsReglage.Applique, Assert.Single(d.Journal).Action);
        Assert.False(Assert.Single(Assert.Single(alertes.Messages)).ValeurRemplacee);
    }

    [Fact]
    public async Task Un_seul_message_par_passage_pour_toutes_les_tables_imposees()
    {
        var d = Depot(Inventaire, Element(Inventaire, "impose", 1, new MutuInventaireDto(5, 4000m)), 1, "impose", LocaleDeuxMille);
        var sub = TablesMutualisables.Substitution;
        d.Etats[sub] = d.Etats[sub] with { ModeRecu = "impose", VersionRecue = 1, ModeleJson = Element(sub, "impose", 1, new MutuSubstitutionDto(false)) };
        d.Locales[sub] = "{\"equivalent_exact_seulement\":true}";
        var (cas, alertes, _) = Cas(d);
        await cas.ExecuterAsync();
        Assert.Equal(2, Assert.Single(alertes.Messages).Count);
    }

    [Fact]
    public async Task Table_deja_appliquee_et_inchangee_n_est_pas_reexaminee()
    {
        var d = Depot(Inventaire, Element(Inventaire, "impose", 3, new MutuInventaireDto(5, 4000m)), 3, "impose", LocaleDeuxMille);
        var (cas, alertes, _) = Cas(d);
        await cas.ExecuterAsync();
        d.Ecritures.Clear();
        d.Journal.Clear();
        alertes.Messages.Clear();
        var bilan = await cas.ExecuterAsync();
        Assert.Empty(bilan.Appliquees);
        Assert.Empty(d.Ecritures);
        Assert.Empty(d.Journal);
        Assert.Empty(alertes.Messages);
    }

    [Fact]
    public async Task Impose_modifiee_localement_depuis_l_application_est_remise_au_modele()
    {
        // Désactivation puis réactivation, restauration d'une sauvegarde : la valeur imposée modifiée entre-temps est remise au modèle (§ 16 bis.5 et 16 bis.7)
        var d = Depot(Inventaire, Element(Inventaire, "impose", 3, new MutuInventaireDto(5, 4000m)), 3, "impose", "{\"seuil_boites\":9,\"seuil_montant\":1}", "central", "impose");
        d.Etats[Inventaire] = d.Etats[Inventaire] with
        {
            VersionAppliquee = 3, Etat = "applique", AppliqueLe = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc),
            ModifieLocalementLe = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc)
        };
        var (cas, alertes, _) = Cas(d);
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([Inventaire], bilan.Appliquees);
        Assert.Equal(ActionsReglage.Remplace, Assert.Single(d.Journal).Action);
        Assert.True(Assert.Single(Assert.Single(alertes.Messages)).ValeurRemplacee);
    }

    [Fact]
    public async Task Seuils_d_alerte_imposes_recalculent_les_alertes()
    {
        var t = TablesMutualisables.Alertes;
        var valeur = new MutuAlertesDto(120, 60, 365, 2, 7, 30, 90, 5, 10);
        var d = Depot(t, Element(t, "impose", 1, valeur), 1, "impose", "{\"jours_orange\":180}");
        var (cas, _, moteur) = Cas(d);
        await cas.ExecuterAsync();
        Assert.Equal(1, moteur.Appels);
    }

    // ---- Proposé (D2) ----

    [Fact]
    public async Task Propose_une_valeur_jamais_modifiee_localement_suit_le_modele()
    {
        var d = Depot(Inventaire, Element(Inventaire, "propose", 2, new MutuInventaireDto(5, 4000m)), 2, "propose", LocaleDeuxMille, origine: "central");
        var (cas, alertes, _) = Cas(d);
        await cas.ExecuterAsync();
        Assert.Equal([$"{Inventaire}|propose|differe"], d.Ecritures);
        Assert.Equal(("propose", "applique"), (d.Etats[Inventaire].Mode, d.Etats[Inventaire].Etat));
        Assert.Empty(alertes.Messages);                                  // pas de message : rien n'est imposé
    }

    [Fact]
    public async Task Propose_une_valeur_modifiee_localement_est_gardee_et_le_modele_reste_a_adopter()
    {
        var d = Depot(Inventaire, Element(Inventaire, "propose", 2, new MutuInventaireDto(5, 4000m)), 2, "propose", LocaleDeuxMille);
        d.Etats[Inventaire] = d.Etats[Inventaire] with { ModifieLocalementLe = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc) };
        var (cas, alertes, _) = Cas(d);
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([Inventaire], bilan.NonAdoptees);
        Assert.Empty(d.Ecritures);                                       // la valeur locale n'est pas touchée
        Assert.Empty(d.Journal);
        Assert.Empty(alertes.Messages);
        Assert.Equal(("propose", "non_adopte", "locale"), (d.Etats[Inventaire].Mode, d.Etats[Inventaire].Etat, d.Etats[Inventaire].Origine));
        // Réexamen au passage suivant : toujours non adopté, aucune écriture de plus
        await cas.ExecuterAsync();
        Assert.Empty(d.Ecritures);
    }

    [Fact]
    public async Task Propose_premiere_reception_sur_une_table_jamais_modifiee_sur_place_applique_la_valeur_du_central()
    {
        // Origine « locale » sans date de modification locale : état d'installation (script 142), rien n'a été modifié sur place
        var d = Depot(Inventaire, Element(Inventaire, "propose", 2, new MutuInventaireDto(5, 4000m)), 2, "propose", LocaleDeuxMille);
        Assert.Null(d.Etats[Inventaire].ModifieLocalementLe);
        var (cas, alertes, _) = Cas(d);
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([Inventaire], bilan.Appliquees);
        Assert.Empty(bilan.NonAdoptees);
        Assert.Equal([$"{Inventaire}|propose|differe"], d.Ecritures);
        Assert.Equal(("propose", "applique", "central", 2L), (d.Etats[Inventaire].Mode, d.Etats[Inventaire].Etat, d.Etats[Inventaire].Origine, d.Etats[Inventaire].VersionAppliquee));
        Assert.Equal(ActionsReglage.Applique, Assert.Single(d.Journal).Action);
        Assert.Empty(alertes.Messages);                                  // pas de message : rien n'est imposé
    }

    [Fact]
    public async Task Propose_premiere_reception_sur_une_table_modifiee_avant_ou_apres_le_script_141_garde_la_valeur_locale()
    {
        // La date de modification locale vient de la garde (après 141) ou de la reprise du journal d'audit (avant 141) : même état pour le code
        foreach (var modifieLe in new[] { new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 16, 17, 30, 0, DateTimeKind.Utc) })
        {
            var d = Depot(Inventaire, Element(Inventaire, "propose", 1, new MutuInventaireDto(5, 4000m)), 1, "propose", LocaleDeuxMille);
            d.Etats[Inventaire] = d.Etats[Inventaire] with { ModifieLocalementLe = modifieLe };
            var (cas, _, _) = Cas(d);
            var bilan = await cas.ExecuterAsync();
            Assert.Equal([Inventaire], bilan.NonAdoptees);
            Assert.Empty(d.Ecritures);
            Assert.Empty(d.Journal);
            Assert.Equal(("propose", "non_adopte", "locale", (long?)null), (d.Etats[Inventaire].Mode, d.Etats[Inventaire].Etat, d.Etats[Inventaire].Origine, d.Etats[Inventaire].VersionAppliquee));
        }
    }

    [Fact]
    public async Task Propose_deuxieme_reception_suit_D2_la_valeur_du_central_suit_le_modele_puis_la_modification_locale_est_gardee()
    {
        var d = Depot(Inventaire, Element(Inventaire, "propose", 1, new MutuInventaireDto(5, 4000m)), 1, "propose", LocaleDeuxMille);
        var (cas, _, _) = Cas(d);
        await cas.ExecuterAsync();                                       // 1re réception : appliquée (table jamais modifiée)
        Assert.Equal("central", d.Etats[Inventaire].Origine);
        // 2e réception, valeur jamais modifiée depuis : suit le central
        d.Locales[Inventaire] = "{\"seuil_boites\":5,\"seuil_montant\":4000.00}";
        d.Etats[Inventaire] = d.Etats[Inventaire] with { VersionRecue = 2, ModeleJson = Element(Inventaire, "propose", 2, new MutuInventaireDto(6, 4500m)) };
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([Inventaire], bilan.Appliquees);
        Assert.Equal(2L, d.Etats[Inventaire].VersionAppliquee);
        // Modification locale (la garde de la base note origine « locale » et la date), 3e réception : gardée
        d.Locales[Inventaire] = "{\"seuil_boites\":9,\"seuil_montant\":4500.00}";
        d.Etats[Inventaire] = d.Etats[Inventaire] with
        {
            Origine = "locale", ModifieLocalementLe = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc), VersionRecue = 3,
            ModeleJson = Element(Inventaire, "propose", 3, new MutuInventaireDto(7, 4800m))
        };
        bilan = await cas.ExecuterAsync();
        Assert.Equal([Inventaire], bilan.NonAdoptees);
        Assert.Equal(2L, d.Etats[Inventaire].VersionAppliquee);
    }

    [Fact]
    public async Task Impose_est_applique_a_la_premiere_reception_meme_sur_une_table_modifiee_sur_place()
    {
        var d = Depot(Inventaire, Element(Inventaire, "impose", 1, new MutuInventaireDto(5, 4000m)), 1, "impose", LocaleDeuxMille);
        d.Etats[Inventaire] = d.Etats[Inventaire] with { ModifieLocalementLe = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc) };
        var (cas, _, _) = Cas(d);
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([Inventaire], bilan.Appliquees);
        Assert.Equal([$"{Inventaire}|impose|differe"], d.Ecritures);
        Assert.Equal(("impose", "applique", "central"), (d.Etats[Inventaire].Mode, d.Etats[Inventaire].Etat, d.Etats[Inventaire].Origine));
    }

    [Fact]
    public async Task Propose_avec_valeur_identique_est_appliquee_sans_toucher_la_valeur()
    {
        var d = Depot(Inventaire, Element(Inventaire, "propose", 2, new MutuInventaireDto(2, 2000m)), 2, "propose", LocaleDeuxMille);
        var (cas, _, _) = Cas(d);
        await cas.ExecuterAsync();
        Assert.Equal([$"{Inventaire}|propose|identique"], d.Ecritures);
        Assert.Equal(("applique", "central"), (d.Etats[Inventaire].Etat, d.Etats[Inventaire].Origine));
    }

    // ---- Rejet (D9) ----

    [Fact]
    public async Task Valeur_invalide_la_table_entiere_est_rejetee_sans_message_ni_ecriture_et_n_est_pas_reexaminee()
    {
        // 4 psychotropes sur 5 : le domaine refuse (les 5 règles sont obligatoires)
        var t = TablesMutualisables.AlertesPsychotropes;
        var modele = Element(t, "impose", 4, new MutuAlertesPsychotropesDto([new("patient_repete", true, 3, 30, false, null)]));
        var d = Depot(t, modele, 4, "impose", "{\"regles\":[]}", modeEnVigueur: "propose");
        var (cas, alertes, _) = Cas(d);

        var bilan = await cas.ExecuterAsync();

        Assert.Equal([t], bilan.Rejetees);
        Assert.Empty(d.Ecritures);
        Assert.Empty(alertes.Messages);                                  // pas de message au titulaire (D9)
        var e = d.Etats[t];
        Assert.Equal(("rejete", 4L, "propose"), (e.Etat, e.VersionRejetee, e.Mode));   // le mode précédent reste
        Assert.Contains("absente", e.MotifRejet);
        Assert.Equal(ActionsReglage.Rejete, Assert.Single(d.Journal).Action);
        // Pas de réexamen de la même version
        d.Journal.Clear();
        Assert.Empty((await cas.ExecuterAsync()).Rejetees);
        Assert.Empty(d.Journal);
    }

    [Fact]
    public async Task Controle_de_la_base_refuse_la_table_entiere()
    {
        var t = TablesMutualisables.Remises;
        var remise = new MutuRemiseDto("c_abcdefghijkl", "inexistante", 10, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), null);
        var d = Depot(t, Element(t, "impose", 1, new MutuRemisesDto([remise]), ajouts: true), 1, "impose", "{\"remises\":[]}");
        d.ErreursBase[t] = ["Remise « c_abcdefghijkl » : la catégorie « inexistante » n’existe pas dans l’officine."];
        var (cas, alertes, _) = Cas(d);
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([t], bilan.Rejetees);
        Assert.Empty(d.Ecritures);
        Assert.Contains("inexistante", d.Etats[t].MotifRejet);
        Assert.Empty(alertes.Messages);
    }

    [Fact]
    public async Task Une_valeur_rejetee_n_arrete_pas_les_autres_tables()
    {
        var t = TablesMutualisables.AlertesPsychotropes;
        var d = Depot(t, Element(t, "impose", 1, new MutuAlertesPsychotropesDto([new("patient_repete", true, 3, 30, false, null)])), 1, "impose");
        d.Etats[Inventaire] = d.Etats[Inventaire] with { ModeRecu = "impose", VersionRecue = 1, ModeleJson = Element(Inventaire, "impose", 1, new MutuInventaireDto(5, 4000m)) };
        d.Locales[Inventaire] = LocaleDeuxMille;
        var (cas, _, _) = Cas(d);
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([t], bilan.Rejetees);
        Assert.Equal([Inventaire], bilan.Appliquees);
    }

    // ---- Libération (D3) ----

    [Fact]
    public async Task Mode_local_recu_libere_la_table_sans_toucher_la_valeur()
    {
        var d = Depot(Inventaire, Element(Inventaire, "local", 5), 5, "local", modeEnVigueur: "impose");
        d.Etats[Inventaire] = d.Etats[Inventaire] with { VersionAppliquee = 4, Etat = "applique", Origine = "central" };
        var (cas, alertes, _) = Cas(d);
        var bilan = await cas.ExecuterAsync();
        Assert.Equal([Inventaire], bilan.Liberees);
        Assert.Empty(d.Ecritures);
        Assert.Empty(alertes.Messages);
        var e = d.Etats[Inventaire];
        Assert.Equal(("local", "aucun", 5L), (e.Mode, e.Etat, e.VersionAppliquee));
        var j = Assert.Single(d.Journal);
        Assert.Equal((ActionsReglage.Libere, "impose", "local"), (j.Action, j.ModeAvant, j.ModeApres));
    }

    [Fact]
    public async Task Liberation_puis_retablissement_avec_la_meme_version_que_celle_appliquee_sont_executes()
    {
        // Constat M8 (essai réel) : le central republie la MÊME version_modele avec le mode « local » (libération), puis de nouveau « impose » (retour)
        var d = Depot(Inventaire, Element(Inventaire, "local", 4), 4, "local", modeEnVigueur: "impose");
        d.Etats[Inventaire] = d.Etats[Inventaire] with { VersionAppliquee = 4, Etat = "applique", Origine = "central" };
        var (cas, alertes, _) = Cas(d);

        var liberation = await cas.ExecuterAsync();

        Assert.Equal([Inventaire], liberation.Liberees);
        Assert.Equal(("local", "aucun", 4L), (d.Etats[Inventaire].Mode, d.Etats[Inventaire].Etat, d.Etats[Inventaire].VersionAppliquee));
        Assert.Empty((await cas.ExecuterAsync()).Liberees);       // pas réexaminée tant que rien ne change

        // Retour : même version 4, mode « impose » ; la valeur locale (modifiée entre-temps) est remplacée
        d.Etats[Inventaire] = d.Etats[Inventaire] with
        {
            ModeRecu = "impose", ModeleJson = Element(Inventaire, "impose", 4, new MutuInventaireDto(5, 4000m))
        };
        d.Locales[Inventaire] = LocaleDeuxMille;
        var retour = await cas.ExecuterAsync();

        Assert.Equal([Inventaire], retour.Appliquees);
        Assert.Equal(("impose", "applique"), (d.Etats[Inventaire].Mode, d.Etats[Inventaire].Etat));
        Assert.Equal([$"{Inventaire}|impose|differe"], d.Ecritures);
        Assert.Single(Assert.Single(alertes.Messages));
        Assert.Empty((await cas.ExecuterAsync()).Appliquees);                                    // puis plus rien à faire
    }

    [Fact]
    public async Task Desactiver_la_synchronisation_journalise_la_liberation_des_tables_imposees()
    {
        var d = new DepotReglagesEnMemoire();
        d.Etats[Inventaire] = d.Etats[Inventaire] with { Mode = "impose" };
        d.Etats[TablesMutualisables.Substitution] = d.Etats[TablesMutualisables.Substitution] with { Mode = "propose" };
        await new LibererReglagesMutualises(d).ExecuterAsync(utilisateurId: 7);
        Assert.Equal(2, d.Journal.Count);
        Assert.All(d.Journal, j => Assert.Equal((ActionsReglage.Libere, "local", 7L), (j.Action, j.ModeApres, j.Utilisateur)));
    }

    [Fact]
    public async Task Rien_n_est_applique_sans_synchronisation_active()
    {
        var d = Depot(Inventaire, Element(Inventaire, "impose", 3, new MutuInventaireDto(5, 4000m)), 3, "impose", LocaleDeuxMille);
        d.Possible = false;
        var (cas, alertes, _) = Cas(d);
        Assert.Empty((await cas.ExecuterAsync()).Appliquees);
        Assert.Empty(d.Ecritures);
        Assert.Empty(alertes.Messages);
    }

    // ---- Adopter ----

    [Fact]
    public async Task Adopter_applique_la_valeur_du_modele_avec_trace_au_journal_et_l_employe()
    {
        var d = Depot(Inventaire, Element(Inventaire, "propose", 2, new MutuInventaireDto(5, 4000m)), 2, "propose", LocaleDeuxMille, modeEnVigueur: "propose");
        d.Etats[Inventaire] = d.Etats[Inventaire] with { Etat = "non_adopte" };
        var (cas, alertes, _) = Cas(d);
        var adopter = new AdopterReglageCentral(cas, new DroitsFactices { Titulaire = true });

        await adopter.ExecuterAsync(Inventaire, 2, utilisateurId: 9);

        Assert.Equal([$"{Inventaire}|propose|differe"], d.Ecritures);
        var j = Assert.Single(d.Journal);
        Assert.Equal((ActionsReglage.Adopte, 9L), (j.Action, j.Utilisateur));
        Assert.Equal(LocaleDeuxMille, j.Avant);
        var e = d.Etats[Inventaire];
        Assert.Equal(("applique", "central", 2L), (e.Etat, e.Origine, e.VersionAppliquee));
        Assert.Empty(alertes.Messages);
    }

    [Fact]
    public async Task Adopter_refuse_une_version_qui_a_change_ou_sans_le_droit()
    {
        var d = Depot(Inventaire, Element(Inventaire, "propose", 3, new MutuInventaireDto(5, 4000m)), 3, "propose", LocaleDeuxMille, modeEnVigueur: "propose");
        var (cas, _, _) = Cas(d);
        await Assert.ThrowsAsync<ModeleReglageChangeException>(() =>
            new AdopterReglageCentral(cas, new DroitsFactices { Titulaire = true }).ExecuterAsync(Inventaire, 2, 9));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new AdopterReglageCentral(cas, new DroitsFactices("caisse.vendre")).ExecuterAsync(Inventaire, 3, 9));
        Assert.Empty(d.Ecritures);
        // Les motifs demandent le droit de les régler localement
        Assert.Equal("admin.parametres", AdopterReglageCentral.Permission(TablesMutualisables.Motifs));
        Assert.Equal("caisse.remises_auto", AdopterReglageCentral.Permission(TablesMutualisables.Remises));
        Assert.Null(AdopterReglageCentral.Permission(Inventaire));   // titulaire par défaut
    }

    [Fact]
    public async Task Adopter_un_modele_devenu_invalide_est_refuse_sans_rien_ecrire()
    {
        var d = Depot(Inventaire, Element(Inventaire, "propose", 2, new MutuInventaireDto(5, 4000m)), 2, "propose", LocaleDeuxMille, modeEnVigueur: "propose");
        d.ErreursBase[Inventaire] = ["contrôle de la base"];
        var (cas, _, _) = Cas(d);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new AdopterReglageCentral(cas, new DroitsFactices { Titulaire = true }).ExecuterAsync(Inventaire, 2, 9));
        Assert.Empty(d.Ecritures);
        Assert.Empty(d.Journal);
    }

    // ---- Garde des cas d'usage de saisie locale (409, avant d'écrire) ----

    private static IGardeReglagesCentraux Garde(string table, string mode, bool ajouts = true)
    {
        var d = new DepotReglagesEnMemoire();
        d.Etats[table] = d.Etats[table] with { Mode = mode, AjoutsLocaux = ajouts };
        return new GardeReglagesCentraux(d);
    }

    [Fact]
    public void Le_refus_a_le_message_et_le_champ_attendus_par_le_poste()
    {
        var e = new ReglageImposeException(Inventaire);
        Assert.Equal("Ce réglage est fixé par le serveur central.", e.Message);
        Assert.Equal("reglage_impose", ReglageImposeException.Champ);
    }

    [Fact]
    public async Task Seuils_imposes_refus_sur_la_partie_generale_seulement_pas_sur_les_grossistes()
    {
        var seuils = new SeuilsEnMemoire();
        var garde = Garde(TablesMutualisables.Alertes, "impose");
        var cas = new EnregistrerSeuils(seuils, new MoteurEspion(), new JournalEspion(), garde);
        var general = seuils.Actuels.General with { JoursOrange = 200 };
        await Assert.ThrowsAsync<ReglageImposeException>(() => cas.ExecuterAsync(new ReglagesSeuils(general, [], []), 1));
        Assert.Null(seuils.Enregistres);                                  // refusé AVANT d'écrire
        await cas.ExecuterAsync(new ReglagesSeuils(seuils.Actuels.General, [new SeuilGrossiste(1, "Grossiste A", 3, null, null)], []), 1);
        Assert.NotNull(seuils.Enregistres);
    }

    [Fact]
    public async Task Surveillance_imposee_refus_sur_un_seuil_mais_les_horaires_restent_locaux()
    {
        var depot = new SurveillanceEnMemoire();
        var regler = new ReglerSurveillance(depot, new DroitsFactices { Titulaire = true }, new JournalEspion(), Garde(TablesMutualisables.Surveillance, "impose"));
        await Assert.ThrowsAsync<ReglageImposeException>(() => regler.EnregistrerAsync(depot.Parametres with { SeuilTiroir = 9 }, 1));
        Assert.Equal(3, depot.Parametres.SeuilTiroir);
        // D7 : l'horaire d'ouverture se règle toujours
        await regler.EnregistrerAsync(depot.Parametres with { HeureOuverture = new TimeOnly(8, 0), HeureFermeture = new TimeOnly(20, 0) }, 1);
        Assert.Equal(new TimeOnly(8, 0), depot.Parametres.HeureOuverture);
    }

    [Fact]
    public async Task Surveillance_proposee_ou_locale_reste_modifiable()
    {
        foreach (var mode in new[] { "propose", "local" })
        {
            var depot = new SurveillanceEnMemoire();
            await new ReglerSurveillance(depot, new DroitsFactices { Titulaire = true }, new JournalEspion(), Garde(TablesMutualisables.Surveillance, mode))
                .EnregistrerAsync(depot.Parametres with { SeuilTiroir = 9 }, 1);
            Assert.Equal(9, depot.Parametres.SeuilTiroir);
        }
    }

    [Fact]
    public async Task Substitution_imposee_refus_avant_d_ecrire()
    {
        var journal = new JournalEspion();
        var cas = new Pharma.Application.Catalogue.ModifierParametreSubstitution(new SubstitutionEnMemoire(true), new DroitsFactices("admin.parametres"), journal,
            Garde(TablesMutualisables.Substitution, "impose"));
        await Assert.ThrowsAsync<ReglageImposeException>(() => cas.ExecuterAsync(false, 1));
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public async Task Substitution_modifiee_localement_le_journal_porte_avant_et_apres()
    {
        var journal = new JournalEspion();
        var cas = new Pharma.Application.Catalogue.ModifierParametreSubstitution(new SubstitutionEnMemoire(true), new DroitsFactices("admin.parametres"), journal,
            Garde(TablesMutualisables.Substitution, "local"));
        await cas.ExecuterAsync(false, 1);
        Assert.Contains("avant", journal.Details[0].Keys);
        Assert.Contains("apres", journal.Details[0].Keys);
    }

    [Fact]
    public async Task Motifs_imposes_ajout_local_selon_ajouts_locaux_et_motif_du_central_verrouille()
    {
        // D5 : ajouts locaux permis par défaut ; refusés si le modèle ne les accorde pas ; un motif du central ne se modifie pas
        var depot = new MotifsEnMemoire();
        depot.Motifs.Add(new MotifReglable(20, "tiroir", "c_abcdefghijkl", "Motif central", true, false, 30, ModeleCodeCentral: true));
        var droits = new DroitsFactices("admin.parametres");
        var avecAjouts = new EnregistrerMotifReglable(depot, droits, new JournalEspion(), Garde(TablesMutualisables.Motifs, "impose", ajouts: true));
        var sansAjouts = new EnregistrerMotifReglable(depot, droits, new JournalEspion(), Garde(TablesMutualisables.Motifs, "impose", ajouts: false));

        await avecAjouts.ExecuterAsync(null, "tiroir", "Motif local", true, 1, effet: "aucun", detail: "aucun");
        var ex = await Assert.ThrowsAsync<ReglageImposeException>(() => sansAjouts.ExecuterAsync(null, "tiroir", "Autre motif local", true, 1, effet: "aucun", detail: "aucun"));
        Assert.Equal(1, depot.Motifs.Count(m => m.Libelle is "Motif local" or "Autre motif local"));
        Assert.Equal("Ce réglage est fixé par le serveur central.", ex.Message);
        await Assert.ThrowsAsync<ReglageImposeException>(() => avecAjouts.ExecuterAsync(20, "tiroir", "Renommé", true, 1));
        Assert.Equal("Motif central", depot.Motifs.Single(m => m.Id == 20).Libelle);
        // Un motif local reste modifiable
        await avecAjouts.ExecuterAsync(1, "tiroir", "Faire la monnaie", true, 1);
        Assert.Equal("Faire la monnaie", depot.Motifs.Single(m => m.Id == 1).Libelle);
    }

    [Fact]
    public async Task Remises_imposees_aucune_creation_locale_et_remise_du_central_verrouillee()
    {
        var garde = Garde(TablesMutualisables.Remises, "impose");
        await Assert.ThrowsAsync<ReglageImposeException>(() => garde.ExigerAjoutLocalAsync(TablesMutualisables.Remises));
        await Assert.ThrowsAsync<ReglageImposeException>(() => garde.ExigerElementLocalAsync(TablesMutualisables.Remises, venuDuCentral: true));
        await garde.ExigerElementLocalAsync(TablesMutualisables.Remises, venuDuCentral: false);   // arrêt d'une remise locale : permis
        var propose = Garde(TablesMutualisables.Remises, "propose");
        await propose.ExigerAjoutLocalAsync(TablesMutualisables.Remises);
    }

    [Fact]
    public async Task Table_locale_ou_proposee_jamais_refusee()
    {
        foreach (var mode in new[] { "local", "propose" })
            await Garde(Inventaire, mode).ExigerModifiableAsync(Inventaire);
        await Assert.ThrowsAsync<ReglageImposeException>(() => Garde(Inventaire, "impose").ExigerModifiableAsync(Inventaire));
    }

    // ---- Comparaison de valeurs ----

    [Fact]
    public void Comparaison_ignore_l_ordre_des_cles_des_listes_et_l_echelle_des_nombres()
    {
        Assert.Equal(JsonCanonique.Normaliser("{\"a\":5000.00,\"l\":[{\"x\":1},{\"x\":2}]}"), JsonCanonique.Normaliser("{\"l\":[{\"x\":2},{\"x\":1}],\"a\":5000}"));
        Assert.NotEqual(JsonCanonique.Normaliser("{\"a\":1}"), JsonCanonique.Normaliser("{\"a\":2}"));
    }

    // ---- Sujet de notification ----

    [Fact]
    public async Task Le_sujet_reglages_centraux_est_abonnable_et_le_message_nomme_les_tables_sans_donnee_personnelle()
    {
        Assert.Contains(TypeNotification.ReglagesCentraux, CodesNotification.Abonnables);
        Assert.Equal("reglages_centraux", TypeNotification.ReglagesCentraux.Code());
        Assert.Equal(TypeNotification.ReglagesCentraux, CodesNotification.TypeDepuisCode("reglages_centraux"));

        var depot = new NotificationsEnMemoire
        {
            Destinataires = { new Destinataire(1, "Titulaire", "t@pharmacie.dz", "0661000000", true, true, new HashSet<TypeNotification> { TypeNotification.ReglagesCentraux }, true, true) }
        };
        await new AlertesTitulaire(depot, new NotificationService(depot)).ReglagesImposesAsync(
            [new ReglageImpose(Inventaire, "Seuils d’inventaire", true, 0), new ReglageImpose(TablesMutualisables.Remises, "Remises automatiques", false, 2)]);
        var m = Assert.Single(depot.Ajoutes, x => x.Canal == CanalNotification.Email);
        Assert.Contains("Seuils d’inventaire", m.Corps);
        Assert.Contains("votre valeur a été remplacée, l’ancienne est au journal", m.Corps);
        Assert.Contains("2 remise(s) locale(s) arrêtée(s)", m.Corps);
    }

    // ---- Voie descendante ----

    [Fact]
    public void Le_statut_invalide_ne_bloque_pas_l_acquittement()
    {
        Assert.Equal("invalide", StatutsDescendant.Invalide);
        Assert.DoesNotContain(StatutsDescendant.Invalide, new[] { StatutsDescendant.TableInconnue, StatutsDescendant.Refuse });
    }

    [Fact]
    public void Examen_d_une_table_selon_son_etat()
    {
        EtatReglage Etat(long? recue, long? appliquee, long? rejetee, string etat = "applique", string mode = "propose", DateTime? modifie = null, DateTime? applique = null) =>
            new("x", mode, true, "propose", recue, "{}", appliquee, applique, etat, null, rejetee, "locale", modifie);
        Assert.False(AppliquerReglagesMutualises.AAppliquer(Etat(null, null, null)));                       // rien reçu
        Assert.True(AppliquerReglagesMutualises.AAppliquer(Etat(2, 1, null)));                              // version nouvelle
        Assert.False(AppliquerReglagesMutualises.AAppliquer(Etat(2, 2, null)));                             // déjà appliquée
        Assert.False(AppliquerReglagesMutualises.AAppliquer(Etat(2, 1, 2, "rejete")));                      // déjà rejetée
        Assert.True(AppliquerReglagesMutualises.AAppliquer(Etat(2, 1, 1)));                                 // une version plus ancienne rejetée n'empêche pas la nouvelle
        Assert.True(AppliquerReglagesMutualises.AAppliquer(Etat(2, 1, null, "non_adopte")));
        var avant = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(AppliquerReglagesMutualises.AAppliquer(Etat(2, 2, null, mode: "impose", modifie: avant.AddHours(1), applique: avant)));
        Assert.False(AppliquerReglagesMutualises.AAppliquer(Etat(2, 2, null, mode: "propose", modifie: avant.AddHours(1), applique: avant)));
        Assert.True(AppliquerReglagesMutualises.AAppliquer(Etat(2, 2, null, mode: "impose")));              // mode reçu (propose) différent du mode en vigueur : libération ou retour à la même version
        Assert.False(AppliquerReglagesMutualises.AAppliquer(Etat(2, 2, 2, "rejete", mode: "impose")));      // une version rejetée n'est jamais réexaminée, même si les modes diffèrent
    }

    [Fact]
    public void Ce_que_le_format_ecrit_passe_la_validation_du_domaine()
    {
        // Le lot M4 ne rejette jamais un modèle conforme au format mutu-v1
        var e = Element(Inventaire, "impose", 1, new MutuInventaireDto(2, 2000m));
        Assert.Empty(ValidationModele.ValiderElement(Inventaire, e));
    }
}
