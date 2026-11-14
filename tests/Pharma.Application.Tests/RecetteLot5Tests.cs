using Pharma.Application.Achats;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class AlertesNonEnvoyeesEnMemoire : IAlertesNonEnvoyees
{
    public List<(TypeNotification Type, string Objet, string Raison)> Notees { get; } = [];
    public Task NoterAsync(TypeNotification type, string objet, string raison, CancellationToken ct = default)
    {
        Notees.Add((type, objet, raison));
        return Task.CompletedTask;
    }
    public Task<AlertesNonEnvoyeesNonVues> NonVuesAsync(int limite, CancellationToken ct = default) =>
        Task.FromResult(new AlertesNonEnvoyeesNonVues(Notees.Count,
            Notees.Select((n, i) => new AlerteNonEnvoyee(i + 1, n.Type.Code(), n.Objet, n.Raison, DateTime.UtcNow)).Reverse().Take(limite).ToList()));
    public Task<int> MarquerVuesAsync(long jusquA, long utilisateurId, CancellationToken ct = default) => Task.FromResult(Notees.Count);
}

internal sealed class TraceEspion : ITraceErreurs
{
    public List<string> Messages { get; } = [];
    public void Erreur(string message, Exception exception) => Messages.Add(message);
}

/// <summary>Service de notification en panne (base indisponible) : la mise en file échoue.</summary>
internal sealed class NotificationsEnPanne : INotificationService
{
    public Task<int> MettreEnFileAsync(ContenuNotification contenu, CancellationToken ct = default) =>
        throw new InvalidOperationException("Base indisponible");
}

/// <summary>Remise maximale propre à chaque employé.</summary>
internal sealed class RemisesMaxParEmploye(Dictionary<long, decimal> remiseMax) : IPermissionService
{
    public Task<bool> APermissionAsync(long u, string p, CancellationToken ct = default) => Task.FromResult(remiseMax.GetValueOrDefault(u) > 0);
    public Task<IReadOnlyList<string>> ListerAsync(long u, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<decimal> RemiseMaxAsync(long u, CancellationToken ct = default) => Task.FromResult(remiseMax.GetValueOrDefault(u));
    public Task<bool> EstTitulaireAsync(long u, CancellationToken ct = default) => Task.FromResult(false);
}

public class RecetteLot5Tests
{
    private static Destinataire Dest(long id, bool valide, params TypeNotification[] types) =>
        new(id, $"Destinataire {id}", $"d{id}@pharmacie.dz", "0661000000", true, true, types.ToHashSet(), valide, true);

    // ---- Alertes qui n'ont pas pu partir ----

    [Fact]
    public async Task Alerte_sans_destinataire_verifie_memorisee_pour_le_titulaire()
    {
        var depot = new NotificationsEnMemoire { Destinataires = { Dest(1, valide: false, TypeNotification.LotBloque) } };
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        await new AlertesTitulaire(depot, new NotificationService(depot), nonEnvoyees).LotBloqueAsync("Insuline", "G7710", 3, 0);
        Assert.Empty(depot.Ajoutes);
        var n = Assert.Single(nonEnvoyees.Notees);
        Assert.Equal(TypeNotification.LotBloque, n.Type);
        Assert.Equal(AlertesTitulaire.AucunDestinataire, n.Raison);
    }

    [Fact]
    public async Task Mise_en_file_impossible_tracee_memorisee_et_jamais_bloquante()
    {
        var (nonEnvoyees, trace) = (new AlertesNonEnvoyeesEnMemoire(), new TraceEspion());
        var alertes = new AlertesTitulaire(new NotificationsEnMemoire(), new NotificationsEnPanne(), nonEnvoyees, trace);
        await alertes.SauvegardeEnEchecAsync(true, "Disque E: absent");   // ne lève pas
        Assert.Single(trace.Messages);
        Assert.Contains("Base indisponible", Assert.Single(nonEnvoyees.Notees).Raison);
    }

    [Fact]
    public async Task Rappel_de_lot_reste_fait_si_l_alerte_echoue()
    {
        var (nonEnvoyees, trace) = (new AlertesNonEnvoyeesEnMemoire(), new TraceEspion());
        var rappel = new Tracabilite.RappelerLot(new RappelFactice(new Tracabilite.ResultatRappel("Insuline", "G7710", 1, 9, [])),
            new AlertesTitulaire(new NotificationsEnMemoire(), new NotificationsEnPanne(), nonEnvoyees, trace), trace);
        var r = await rappel.ExecuterAsync(9, "G7710", "rappel labo", 1);
        Assert.Equal(1, r.LotsBloques);
        Assert.Single(nonEnvoyees.Notees);
    }

    [Fact]
    public async Task Envoi_abandonne_signale_au_titulaire()
    {
        var depot = new NotificationsEnMemoire();
        var maintenant = new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
        depot.File.Add(new MessageEnFile(7, CanalNotification.Email, "Titulaire", "t@p.dz", TypeNotification.EcartCaisse, "Manque de caisse",
            "texte", null, "en_attente", 9, null, null, maintenant.AddDays(-8), null));
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        await new TraiterFileAttente(depot, new EmailEspion(enPanne: true), new SmsEspion(), new SecretsFactices(), nonEnvoyees).ExecuterAsync(maintenant);
        Assert.Null(Assert.Single(depot.Echecs).Prochaine);
        Assert.StartsWith("Envoi abandonné", Assert.Single(nonEnvoyees.Notees).Raison);
    }

    // ---- Écart de caisse : e-mail pour tout écart, SMS au-delà du seuil ----

    [Fact]
    public async Task Ecart_sous_le_seuil_notifie_par_e_mail_sans_sms()
    {
        var depot = new NotificationsEnMemoire { Destinataires = { Dest(1, true, TypeNotification.EcartCaisse) } };
        await new AlertesTitulaire(depot, new NotificationService(depot)).EcartCaisseAsync("Vendeur", "Caisse 1", -300m, seuilCaisse: 500m);
        var m = Assert.Single(depot.Ajoutes);
        Assert.Equal(CanalNotification.Email, m.Canal);
    }

    [Fact]
    public async Task Tout_ecart_non_nul_inscrit_au_journal()
    {
        var journal = new JournalEspion();
        // Attendu 5 000 + 1 000 = 6 000 ; compté 5 800 : écart −200, sous le seuil de 500
        await new CloturerSessionCaisse(new SessionsEnMemoire { Especes = 1000 }, new DroitsFactices("caisse.vendre"), journal)
            .ExecuterAsync(5, "Caisse 1", new Dictionary<string, decimal> { ["b2000"] = 2, ["b1000"] = 1, ["b200"] = 4 });
        Assert.Equal(["ecart_caisse"], journal.Actions);
        Assert.Equal(false, journal.Details.Single()["au_dela_du_seuil"]);
    }

    // ---- Clôture : coupures, saisies énormes, 0 DA compté ----

    [Fact]
    public void Coupure_inconnue_refusee()
    {
        var ex = Assert.Throws<RegleMetierException>(() => Comptage.Depuis(new Dictionary<string, decimal> { ["b5000"] = 1 }));
        Assert.Equal("Coupure inconnue : b5000.", ex.Message);
    }

    [Theory]
    [InlineData("b2000", 100_001)]
    [InlineData("p100", 1_000_000_000)]
    [InlineData("autres", 10_000_000.01)]
    public void Saisie_enorme_refusee(string coupure, double valeur) =>
        Assert.Throws<RegleMetierException>(() => Comptage.Depuis(new Dictionary<string, decimal> { [coupure] = (decimal)valeur }));

    [Fact]
    public void Limites_acceptees_et_autres_arrondies_au_centime()
    {
        var c = Comptage.Depuis(new Dictionary<string, decimal> { ["b2000"] = 100_000, ["autres"] = 12.345m });
        Assert.Equal(200_000_012.35m, c.Total);
    }

    [Fact]
    public async Task Cloture_a_zero_demande_une_confirmation()
    {
        var sessions = new SessionsEnMemoire();
        var cloturer = new CloturerSessionCaisse(sessions, new DroitsFactices("caisse.vendre"), new JournalEspion());
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cloturer.ExecuterAsync(5, "Caisse 1", new Dictionary<string, decimal>()));
        Assert.Equal(CloturerSessionCaisse.ConfirmationZero, ex.Message);
        Assert.Null(sessions.Cloture);

        var vue = await cloturer.ExecuterAsync(5, "Caisse 1", new Dictionary<string, decimal>(), confirmerZero: true);
        Assert.Equal(0m, vue.Compte);
        Assert.NotNull(sessions.Cloture);
    }

    // ---- Recherche par téléphone ----

    [Theory]
    [InlineData("0661 23 45", "06612345")]
    [InlineData("+213 661-23", "066123")]
    [InlineData("00213661", "0661")]
    [InlineData("(0661).23", "066123")]
    [InlineData("066", null)]          // moins de 4 chiffres
    [InlineData("Benali", null)]       // pas un numéro
    [InlineData("06 61 a", null)]
    [InlineData("06+61", null)]
    public void Prefixe_de_telephone_normalise(string saisie, string? attendu) => Assert.Equal(attendu, RechercheTelephone.Prefixe(saisie));

    // ---- « Faire autoriser » filtrée selon le maximum de remise ----

    [Fact]
    public async Task Valideurs_d_une_remise_couverte_par_leur_maximum_seulement()
    {
        var codes = new CodesEnMemoire();
        foreach (var (id, nom) in new[] { (1L, "Assistant 5 %"), (2L, "Assistant 20 %"), (3L, "Demandeur") })
            codes.Comptes[id] = new CompteComptoir(id, $"e{id}", nom, "hash", 0, null);
        var droits = new RemisesMaxParEmploye(new() { [1] = 5, [2] = 20, [3] = 50 });
        var liste = await new ListerValidateurs(codes, droits).ExecuterAsync(Domain.Securite.Permissions.CaisseRemise, sauf: 3, remisePct: 15);
        Assert.Equal(["Assistant 20 %"], liste.Select(v => v.NomComplet));
        Assert.Equal(2, (await new ListerValidateurs(codes, droits).ExecuterAsync(Domain.Securite.Permissions.CaisseRemise, sauf: 3)).Count);
    }

    // ---- Étiquettes : lot inexistant ----

    [Fact]
    public async Task Etiquettes_d_un_lot_inexistant_lot_introuvable()
    {
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ImprimerEtiquettes(new AchatsEnMemoire(), new DroitsFactices("achats.receptionner"), null!).ExecuterAsync(999, 1, 1));
        Assert.Equal("Lot introuvable.", ex.Message);
    }

    // ---- Reprise : « Autre » sans produit valable ----

    [Fact]
    public async Task Reprise_autre_sans_produit_valable_refusee()
    {
        var superAdmin = new SuperAdmin.SuperAdminCourant { Identifiant = "super" };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new Reprise.DeciderProduitReprise(null!, superAdmin).ExecuterAsync(1, "P1", Reprise.DecisionProduit.Autre, 0, 1));
        Assert.Contains("Choisissez le produit", ex.Message);
    }
}
