using Pharma.Application.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>
/// « Remis à » / « Pour » (règle du titulaire, 2026-11-15, script 091) : pas de dette familiale, la vente et sa dette restent au nom
/// d'un seul patient. Karim (3) : patient de l'ordonnance ; Ali (4) : son père, de la même famille.
/// </summary>
public class RemiseVenteTests
{
    private static readonly DateOnly Jour = CatalogueEnMemoire.Aujourdhui;

    private static Patient Karim() => new() { Id = 3, Nom = "Karim Fictif", PlafondCredit = 5000 };
    private static Patient Ali() => new() { Id = 4, Nom = "Ali Fictif", PlafondCredit = 5000 };

    private static (Encaisser Cas, VentesEspion Ventes) Caisse(params Patient[] patients)
    {
        var ventes = new VentesEspion();
        return (new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(patients), ventes, new SessionsEnMemoire(),
            new DroitsFactices("caisse.vendre", "credit.accorder", "psychotropes.delivrer"), new HorlogeFixe(Jour), new ScansEnMemoire()), ventes);
    }

    private static CommandeVente AvecOrdonnance(long patientId, PersonneVente? remisA = null, PersonneVente? pour = null,
        ModePaiement mode = ModePaiement.Especes, long produit = 2, long lot = 20, PorteurOrdonnance? porteur = null) =>
        new([new LigneCommande(produit, lot, 1)], false, null, null, patientId, new OrdonnanceSaisie(1, Jour, "ORD-1", Porteur: porteur, DureeTraitementJours: 30), 0, mode, null,
            RemisA: remisA, Pour: pour);

    private static CommandeVente SansOrdonnance(long? patientId, PersonneVente? remisA = null, PersonneVente? pour = null,
        ModePaiement mode = ModePaiement.Especes) =>
        new([new LigneCommande(11, 110, 1)], false, null, null, patientId, null, 0, mode, null, RemisA: remisA, Pour: pour);

    [Fact]
    public async Task Avec_ordonnance_remis_a_un_membre_de_la_famille_enregistre()
    {
        var (cas, ventes) = Caisse(Karim(), Ali());
        await cas.ExecuterAsync(AvecOrdonnance(3, remisA: new PersonneVente(4, "ignoré")), 5, "Caisse 1");
        Assert.Equal(3, ventes.Recu!.Vente.PatientId);
        Assert.Equal(new PersonneVente(4, null), ventes.Recu.RemisA);   // fiche choisie : seule la référence est gardée
        Assert.Null(ventes.Recu.Pour);
    }

    [Fact]
    public async Task Sans_ordonnance_pour_un_nom_libre_enregistre()
    {
        var (cas, ventes) = Caisse(Ali());
        await cas.ExecuterAsync(SansOrdonnance(4, pour: new PersonneVente(null, "  Karim Fictif ")), 5, "Caisse 1");
        Assert.Equal(4, ventes.Recu!.Vente.PatientId);
        Assert.Equal(new PersonneVente(null, "Karim Fictif"), ventes.Recu.Pour);
        Assert.Null(ventes.Recu.RemisA);
    }

    [Fact]
    public async Task Pour_refuse_sur_une_vente_avec_ordonnance()
    {
        var (cas, ventes) = Caisse(Karim(), Ali());
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(AvecOrdonnance(3, pour: new PersonneVente(4, null)), 5, "Caisse 1"));
        Assert.Equal("pour", ex.Champ);
        Assert.Null(ventes.Recu);
    }

    [Fact]
    public async Task Remis_a_refuse_sur_une_vente_sans_ordonnance()
    {
        var (cas, ventes) = Caisse(Ali());
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(SansOrdonnance(4, remisA: new PersonneVente(null, "Karim")), 5, "Caisse 1"));
        Assert.Equal("remis_a", ex.Champ);
        Assert.Null(ventes.Recu);
    }

    [Fact]
    public async Task Pour_refuse_sans_patient_de_la_vente()
    {
        var (cas, ventes) = Caisse();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(SansOrdonnance(null, pour: new PersonneVente(null, "Karim")), 5, "Caisse 1"));
        Assert.Contains("patient au nom de qui", ex.Message);
        Assert.Null(ventes.Recu);
    }

    [Fact]
    public async Task Refus_si_la_personne_est_le_patient_lui_meme_ou_introuvable()
    {
        var (cas, _) = Caisse(Karim());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(AvecOrdonnance(3, remisA: new PersonneVente(3, null)), 5, "Caisse 1"));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(AvecOrdonnance(3, remisA: new PersonneVente(99, null)), 5, "Caisse 1"));
        Assert.Contains("introuvable", ex.Message);
    }

    [Fact]
    public async Task Nom_libre_de_plus_de_200_caracteres_refuse()
    {
        var (cas, _) = Caisse(Karim());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(AvecOrdonnance(3, remisA: new PersonneVente(null, new string('a', 201))), 5, "Caisse 1"));
    }

    [Fact]
    public async Task Vide_rien_n_est_enregistre()
    {
        var (cas, ventes) = Caisse(Karim());
        await cas.ExecuterAsync(AvecOrdonnance(3, remisA: new PersonneVente(null, "   ")), 5, "Caisse 1");
        Assert.Null(ventes.Recu!.RemisA);
    }

    [Fact]
    public async Task Dette_au_nom_du_patient_de_la_vente_jamais_de_celui_qui_recupere()
    {
        var (karim, ali) = (Karim(), Ali());
        var (cas, ventes) = Caisse(karim, ali);
        await cas.ExecuterAsync(AvecOrdonnance(3, remisA: new PersonneVente(4, null), mode: ModePaiement.Credit), 5, "Caisse 1");
        Assert.True(ventes.Recu!.CreditAccorde);
        Assert.Equal(3, ventes.Recu.Vente.PatientId);
        Assert.Equal(350m, karim.SoldeCredit);
        Assert.Equal(0m, ali.SoldeCredit);
    }

    [Fact]
    public async Task Dette_sans_ordonnance_au_nom_de_celui_qui_commande()
    {
        var (karim, ali) = (Karim(), Ali());
        var (cas, ventes) = Caisse(karim, ali);
        await cas.ExecuterAsync(SansOrdonnance(4, pour: new PersonneVente(3, null), mode: ModePaiement.Credit), 5, "Caisse 1");
        Assert.Equal(4, ventes.Recu!.Vente.PatientId);
        Assert.True(ali.SoldeCredit > 0);
        Assert.Equal(0m, karim.SoldeCredit);
    }

    [Fact]
    public async Task Psychotrope_le_porteur_saisi_vaut_remis_a()
    {
        var (cas, ventes) = Caisse(Karim());
        await cas.ExecuterAsync(AvecOrdonnance(3, produit: 7, lot: 70, porteur: new PorteurOrdonnance("Ali Fictif", "12 rue des Fictifs", "CNI 000000")), 5, "Caisse 1");
        Assert.Equal(new PersonneVente(null, "Ali Fictif"), ventes.Recu!.RemisA);
    }

    [Fact]
    public async Task Psychotrope_remis_a_saisi_garde_plutot_que_le_porteur()
    {
        var (cas, ventes) = Caisse(Karim(), Ali());
        await cas.ExecuterAsync(AvecOrdonnance(3, remisA: new PersonneVente(4, null), produit: 7, lot: 70,
            porteur: new PorteurOrdonnance("Ali Fictif", "12 rue des Fictifs", "CNI 000000")), 5, "Caisse 1");
        Assert.Equal(new PersonneVente(4, null), ventes.Recu!.RemisA);
    }

    // ---- Affichage (ticket, relevé, fiche de la vente, historique des deux patients) ----

    [Theory]
    [InlineData("Karim", "Ali", null, "Karim · remis à Ali")]
    [InlineData("Ali", null, "Karim", "Ali · pour Karim")]
    [InlineData("Karim", null, null, "Karim")]
    [InlineData(null, "Ali", null, "remis à Ali")]
    public void Libelle_de_la_vente(string? titulaire, string? remisA, string? pour, string attendu) =>
        Assert.Equal(attendu, RemiseVente.Libelle(titulaire, remisA, pour));

    [Fact]
    public void Sans_complement_pas_de_ligne_supplementaire() => Assert.Null(RemiseVente.Complement(null, "  "));

    [Fact]
    public async Task Ticket_porte_le_client_remis_a()
    {
        var vente = new TicketVente("T-000001", DateTime.UtcNow, "Vendeur", "Caisse 1", "validee", null, null,
            [new LigneTicket("Produit fictif", 1, 100, 100, 0)], 100, 0, 0, 100, "especes", null, null, null, null, Client: "Karim · remis à Ali");
        var t = await new ObtenirTicket(new TicketsEnMemoire(vente), new PharmacieFactice()).ExecuterAsync(1);
        Assert.Equal("Karim · remis à Ali", t.Client);
    }
}
