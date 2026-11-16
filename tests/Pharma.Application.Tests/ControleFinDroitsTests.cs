using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Contrôle de la fin de droits Chifa (2026-11-15) : absente, bientôt expirés, expirés ; crédit renforcé ; mise à jour journalisée.</summary>
public class ControleFinDroitsTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 15);

    [Theory]
    [InlineData(null, 30, EtatDroits.Absente)]
    [InlineData("2026-11-14", 30, EtatDroits.Expires)]
    [InlineData("2026-11-15", 30, EtatDroits.BientotExpires)]   // dernier jour valable : bientôt expirés
    [InlineData("2026-12-15", 30, EtatDroits.BientotExpires)]   // dans 30 jours
    [InlineData("2026-12-16", 30, EtatDroits.Valides)]          // dans 31 jours
    [InlineData("2026-12-16", 31, EtatDroits.BientotExpires)]   // seuil réglable
    [InlineData("2026-11-20", 0, EtatDroits.Valides)]           // 0 : pas d'alerte orange
    [InlineData("2026-11-15", 0, EtatDroits.BientotExpires)]
    public void Etat_selon_la_date_et_le_seuil(string? fin, int jours, EtatDroits attendu) =>
        Assert.Equal(attendu, EtatDroitsChifa.Etat(true, fin is null ? null : DateOnly.Parse(fin), Aujourdhui, jours));

    [Fact]
    public void Hors_tiers_payant_Chifa_rien_n_est_signale()
    {
        Assert.Equal(EtatDroits.NonConcerne, EtatDroitsChifa.Etat(false, null, Aujourdhui, 30));
        Assert.Equal(EtatDroits.NonConcerne, EtatDroitsChifa.Etat(false, new DateOnly(2020, 1, 1), Aujourdhui, 30));
        Assert.True(EtatDroitsChifa.OrganismeChifa("CASNOS"));
        Assert.False(EtatDroitsChifa.OrganismeChifa("MDN"));
    }

    [Fact]
    public void Messages_orange_et_credit()
    {
        Assert.Equal("Droits Chifa jusqu’au 20/11/2026 : prévenir le patient.", EtatDroitsChifa.MessageBientot(new DateOnly(2026, 11, 20)));
        Assert.Equal("Droits expirés et vente à crédit : risque de non-paiement.", EtatDroitsChifa.MessageCredit);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task Seuil_hors_bornes_refuse(int jours)
    {
        var cas = new ModifierParametresCaisse(new SessionsEnMemoire(), new JournalEspion());
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(new ParametresCaisse(true, 5000, 500, 0, JoursAlerteFinDroit: jours), 1));
        Assert.Equal("jours_alerte_fin_droit", e.Champ);
    }

    [Fact]
    public async Task Seuil_enregistre_et_journalise()
    {
        var journal = new JournalEspion();
        await new ModifierParametresCaisse(new SessionsEnMemoire(), journal).ExecuterAsync(new ParametresCaisse(true, 5000, 500, 0, JoursAlerteFinDroit: 45), 1);
        Assert.Equal(["parametres_caisse"], journal.Actions);
        Assert.Equal(30, new ParametresCaisse(true, 0, 0, 0).JoursAlerteFinDroit);   // défaut
    }

    private static PatientsEnMemoire Patient(DateOnly? fin) =>
        new(new Pharma.Domain.Ventes.Patient { Id = 12, Nom = "Benali", OrganismeId = 1, PlafondCredit = 1_000_000 }) { Fiche = new FichePatient(12, "Benali", "Karim", null, null, null, "690755008888/02", 1, false, 0, DateFinDroit: fin) };

    [Fact]
    public async Task Vente_a_credit_droits_expires_journal_renforce()
    {
        var journal = new JournalEspion();
        await DroitsChifa.JournaliserSiExpiresAsync(Patient(Aujourdhui.AddDays(-3)), journal, 12, true, Aujourdhui, 5, 1, "V1", CancellationToken.None, credit: true);
        Assert.Equal([DroitsChifa.ActionJournal, DroitsChifa.ActionJournalCredit], journal.Actions);
        Assert.Equal("credit", journal.Details[1]["mode"]);
        Assert.Equal(Aujourdhui.AddDays(-3), journal.Details[1]["date_fin_droit"]);
    }

    [Fact]
    public async Task Vente_a_credit_droits_valables_aucun_journal()
    {
        var journal = new JournalEspion();
        await DroitsChifa.JournaliserSiExpiresAsync(Patient(Aujourdhui), journal, 12, true, Aujourdhui, 5, 1, "V1", CancellationToken.None, credit: true);
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public async Task Encaissement_a_credit_CNAS_droits_expires_non_bloque_et_journalise()
    {
        var ventes = new VentesEspion();
        var journal = new JournalEspion();
        var cas = new Encaisser(new CatalogueEnMemoire(), Patient(CatalogueEnMemoire.Aujourdhui.AddDays(-1)), ventes, new SessionsEnMemoire(),
            new DroitsFactices("caisse.vendre", "credit.accorder", "credit.depasser_plafond"), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui),
            new ScansEnMemoire(), journal: journal, voletsChifa: new VoletsChifaEnMemoire());
        await cas.ExecuterAsync(new CommandeVente([new LigneCommande(2, 20, 1)], true, 1, 80, 12,
            new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, null), 0, ModePaiement.Credit, null, MotifSansVoletCode: "carte_absente"), 5, "Caisse 1");
        Assert.NotNull(ventes.Recu);
        Assert.Contains(DroitsChifa.ActionJournalCredit, journal.Actions);
    }

    [Fact]
    public async Task Mise_a_jour_de_la_date_enregistree_et_journalisee()
    {
        var patients = Patient(Aujourdhui.AddDays(-10));
        var journal = new JournalEspion();
        var nouvelle = new DateOnly(2027, 6, 30);
        await new MettreAJourFinDroit(patients, journal).ExecuterAsync(12, nouvelle, 5);
        Assert.Equal(nouvelle, patients.Modifiee!.DateFinDroit);
        Assert.Equal("Benali", patients.Modifiee.Nom);   // le reste de la fiche est inchangé
        Assert.Equal(["patient_modifie"], journal.Actions);
        Assert.True(journal.Details[0].ContainsKey("date_fin_droit"));
    }

    [Fact]
    public async Task Mise_a_jour_identique_rien_n_est_journalise_patient_inconnu_refuse()
    {
        var patients = Patient(Aujourdhui);
        var journal = new JournalEspion();
        var cas = new MettreAJourFinDroit(patients, journal);
        await cas.ExecuterAsync(12, Aujourdhui, 5);
        Assert.Empty(journal.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(99, Aujourdhui, 5));
    }

    [Theory]
    [InlineData(-3, "expires")]
    [InlineData(10, "bientot")]   // dans les 30 jours
    public async Task Credit_sans_tiers_payant_droits_expires_ou_bientot_journal_renforce(int jours, string etat)
    {
        var journal = new JournalEspion();
        await DroitsChifa.JournaliserCreditSansTiersPayantAsync(Patient(Aujourdhui.AddDays(jours)), journal, 12, Aujourdhui, 30, 5, 1, "V1", CancellationToken.None);
        Assert.Equal([DroitsChifa.ActionJournalCredit], journal.Actions);
        Assert.Equal((etat, false), ((string)journal.Details[0]["etat"]!, (bool)journal.Details[0]["tiers_payant"]!));
        Assert.Equal(EtatDroitsChifa.MessageCredit, "Droits expirés et vente à crédit : risque de non-paiement.");
        Assert.Equal(etat, EtatDroitsChifa.EtatCreditSansTiersPayant(Aujourdhui.AddDays(jours), Aujourdhui, 30));
    }

    [Fact]
    public async Task Credit_sans_tiers_payant_droits_valables_ou_absents_aucun_journal()
    {
        var journal = new JournalEspion();
        await DroitsChifa.JournaliserCreditSansTiersPayantAsync(Patient(Aujourdhui.AddDays(60)), journal, 12, Aujourdhui, 30, 5, 1, "V1", CancellationToken.None);
        await DroitsChifa.JournaliserCreditSansTiersPayantAsync(Patient(null), journal, 12, Aujourdhui, 30, 5, 1, "V1", CancellationToken.None);
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public async Task Encaissement_a_credit_sans_tiers_payant_droits_expires_non_bloque_et_journalise()
    {
        var ventes = new VentesEspion();
        var journal = new JournalEspion();
        var cas = new Encaisser(new CatalogueEnMemoire(), Patient(CatalogueEnMemoire.Aujourdhui.AddDays(-1)), ventes, new SessionsEnMemoire(),
            new DroitsFactices("caisse.vendre", "credit.accorder", "credit.depasser_plafond"), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui),
            new ScansEnMemoire(), journal: journal);
        await cas.ExecuterAsync(new CommandeVente([new LigneCommande(2, 20, 1)], false, null, null, 12,
            new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, null), 0, ModePaiement.Credit, null), 5, "Caisse 1");
        Assert.NotNull(ventes.Recu);
        Assert.Contains(DroitsChifa.ActionJournalCredit, journal.Actions);
        Assert.DoesNotContain(DroitsChifa.ActionJournal, journal.Actions);
    }

    [Fact]
    public void Message_credit_selon_la_date_expires_ou_bientot()
    {
        var jour = new DateOnly(2026, 11, 15);
        Assert.Equal("Droits Chifa expirés le 14/11/2026 et vente à crédit : risque de non-paiement.", EtatDroitsChifa.MessageCreditSelon(jour.AddDays(-1), jour));
        Assert.Equal("Droits Chifa bientôt expirés (jusqu’au 15/11/2026) et vente à crédit : risque de non-paiement.", EtatDroitsChifa.MessageCreditSelon(jour, jour));
        Assert.Equal("Droits Chifa bientôt expirés (jusqu’au 25/11/2026) et vente à crédit : risque de non-paiement.", EtatDroitsChifa.MessageCreditSelon(jour.AddDays(10), jour));
        Assert.Equal(EtatDroitsChifa.MessageCredit, EtatDroitsChifa.MessageCreditSelon(null, jour));
    }
}
