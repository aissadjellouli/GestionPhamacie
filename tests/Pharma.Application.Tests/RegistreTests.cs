using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class RegistreEnMemoire(params LigneRegistre[] lignes) : IRegistrePsychotropes
{
    public Task<IReadOnlyList<LigneRegistre>> LireAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LigneRegistre>>(lignes.Where(l => produitId is null || l.ProduitId == produitId).ToList());
    public Task<Page<LigneRegistre>> LirePageAsync(long? produitId, DateOnly du, DateOnly au, string? type, DemandePage page, CancellationToken ct = default)
    {
        var choisies = lignes.Where(l => (produitId is null || l.ProduitId == produitId) && (type is null || l.Type == type)).ToList();
        return Task.FromResult(new Page<LigneRegistre>(choisies.Skip(page.Decalage).Take(page.Taille).ToList(), choisies.Count, page.Page, page.Taille));
    }
    public Task<IReadOnlyList<string>> TypesAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(lignes.Select(l => l.Type).Distinct().ToList());
    public Task<(int Inscriptions, IReadOnlyList<BalanceMensuelle> Balance)> SyntheseAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        var choisies = lignes.Where(l => produitId is null || l.ProduitId == produitId).ToList();
        return Task.FromResult<(int, IReadOnlyList<BalanceMensuelle>)>((choisies.Count, ConsulterRegistre.Balance(choisies)));
    }
    public Task<IReadOnlyList<ProduitRegistre>> ProduitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProduitRegistre>>([new ProduitRegistre(7, "Alprazolam 0,25 mg")]);
}

public class RegistreTests
{
    private static readonly DateOnly Jour = CatalogueEnMemoire.Aujourdhui;

    private static LigneRegistre Ligne(long n, DateTime date, string type, int? entree, int? sortie, int? solde, string? patient = null,
        string? porteur = null) =>
        new(n, date, 7, "Alprazolam 0,25 mg", "AL25", type, entree, sortie, solde, solde ?? 0, patient, null, null, null, null,
            porteur, porteur is null ? null : "12 rue des Fictifs", porteur is null ? null : "CNI 000000", "Employé");

    private static readonly LigneRegistre[] Lignes =
    [
        Ligne(1, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), "ouverture", 13, null, 13),
        Ligne(2, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), "sortie", null, 1, 12, "Patient Fictif", "Porteur Fictif Un"),
        Ligne(3, new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc), "entree", 5, null, 17)
    ];

    [Fact]
    public async Task Registre_exige_le_droit_de_consultation() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ConsulterRegistre(new RegistreEnMemoire(Lignes), new DroitsFactices("psychotropes.delivrer")).ExecuterAsync(5, null, null, null, Jour));

    [Fact]
    public async Task Noms_en_clair_pour_le_titulaire_et_abreges_pour_les_autres()
    {
        var titulaire = await new ConsulterRegistre(new RegistreEnMemoire(Lignes), new DroitsFactices("psychotropes.registre") { Titulaire = true })
            .ExecuterAsync(1, null, null, null, Jour);
        Assert.False(titulaire.NomsMasques);
        Assert.Equal(("Patient Fictif", "Porteur Fictif Un", "CNI 000000"), (titulaire.Lignes[1].Patient, titulaire.Lignes[1].PorteurNom, titulaire.Lignes[1].PorteurPiece));

        var employe = await new ConsulterRegistre(new RegistreEnMemoire(Lignes), new DroitsFactices("psychotropes.registre"))
            .ExecuterAsync(5, null, null, null, Jour);
        Assert.True(employe.NomsMasques);
        Assert.Equal(("P. F.", "P. F. U.", "•••", "•••"),
            (employe.Lignes[1].Patient, employe.Lignes[1].PorteurNom, employe.Lignes[1].PorteurAdresse, employe.Lignes[1].PorteurPiece));
    }

    [Fact]
    public void Balance_mensuelle_entrees_sorties_et_solde_de_fin_de_mois()
    {
        var b = ConsulterRegistre.Balance(Lignes);
        Assert.Equal(2, b.Count);
        // Le solde d'ouverture est un solde reporté, pas une entrée
        Assert.Equal((10, 13, 0, 1, (int?)12), (b[0].Mois, b[0].SoldeReporte, b[0].Entrees, b[0].Sorties, b[0].SoldeFin));
        Assert.Equal((11, 0, 5, 0, (int?)17), (b[1].Mois, b[1].SoldeReporte, b[1].Entrees, b[1].Sorties, b[1].SoldeFin));
    }

    [Fact]
    public void Balance_mensuelle_sans_les_lignes_anterieures_a_l_ouverture()
    {
        // Délivrance inscrite avant le solde d'ouverture (solde du produit non calculé) : hors balance, sinon comptée en plus
        LigneRegistre[] lignes =
        [
            Ligne(1, new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc), "sortie", null, 2, null),
            Ligne(2, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), "sortie", null, 1, null),
            .. Lignes.Select(l => l with { Numero = l.Numero + 2 })
        ];
        var b = ConsulterRegistre.Balance(lignes);
        Assert.Equal(2, b.Count);   // septembre, sans ligne après l'ouverture, n'apparaît pas
        Assert.Equal((10, 13, 0, 1, (int?)12), (b[0].Mois, b[0].SoldeReporte, b[0].Entrees, b[0].Sorties, b[0].SoldeFin));
        // Solde reporté + entrées − sorties = solde de fin de mois
        Assert.All(b, m => Assert.Equal(m.SoldeFin, (b.TakeWhile(x => x != m).LastOrDefault()?.SoldeFin ?? 0) + m.SoldeReporte + m.Entrees - m.Sorties));
    }

    [Fact]
    public async Task Periode_par_defaut_du_debut_du_mois_a_aujourd_hui_et_export_limite_a_un_an()
    {
        var cas = new ConsulterRegistre(new RegistreEnMemoire(Lignes), new DroitsFactices("psychotropes.registre"));
        var r = await cas.ExecuterAsync(5, null, null, null, new DateOnly(2026, 11, 9));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 9)), (r.Du, r.Au));
        // Registre complet (exports) : un an au plus
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(5, null, new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 9), Jour));
        Assert.Contains("Export", ex.Message);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(5, null, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 1), Jour));
    }

    [Fact]
    public async Task Consultation_a_l_ecran_sans_limite_de_duree()
    {
        var cas = new ConsulterRegistre(new RegistreEnMemoire(Lignes), new DroitsFactices("psychotropes.registre"));
        var synthese = await cas.SyntheseAsync(5, null, new DateOnly(2015, 1, 1), new DateOnly(2026, 11, 9), Jour);
        Assert.Equal((3, 2), (synthese.Inscriptions, synthese.Balance.Count));
        var page = await cas.InscriptionsAsync(5, null, new DateOnly(2015, 1, 1), new DateOnly(2026, 11, 9), null, new DemandePage(1, 10), Jour);
        Assert.Equal(3, page.Page.Total);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.SyntheseAsync(5, null, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 1), Jour));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ConsulterRegistre(new RegistreEnMemoire(Lignes), new DroitsFactices("psychotropes.delivrer")).SyntheseAsync(5, null, null, null, Jour));
    }

    [Fact]
    public async Task Page_d_inscriptions_filtree_par_operation_et_noms_abreges_hors_titulaire()
    {
        var cas = new ConsulterRegistre(new RegistreEnMemoire(Lignes), new DroitsFactices("psychotropes.registre"));
        var r = await cas.InscriptionsAsync(5, null, null, null, "sortie", new DemandePage(1, 10), Jour);
        Assert.Equal(1, r.Page.Total);
        Assert.True(r.NomsMasques);
        Assert.Equal(("P. F.", "•••"), (r.Page.Lignes[0].Patient, r.Page.Lignes[0].PorteurPiece));
        Assert.Equal(3, r.Operations.Count);   // opérations de toute la période, pour le filtre
    }

    [Fact]
    public void Csv_neutralise_les_formules_et_libelle_les_operations()
    {
        var r = new Registre(Jour, Jour, null, [Ligne(1, DateTime.UtcNow, "ecart_moins", null, 2, 3, "=CMD()")], [], false);
        var texte = System.Text.Encoding.UTF8.GetString(RegistreCsv.Ecrire(r));
        Assert.Contains("'=CMD()", texte);
        Assert.Contains("Écart d'inventaire (−)", texte);
    }

    // ---- Porteur de l'ordonnance à la caisse ----

    private static (Encaisser Cas, VentesEspion Ventes) Caisse()
    {
        var ventes = new VentesEspion();
        return (new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(),
            new DroitsFactices("caisse.vendre", "psychotropes.delivrer"), new HorlogeFixe(Jour), new ScansEnMemoire()), ventes);
    }

    private static CommandeVente Commande(long produit, long lot, PorteurOrdonnance? porteur) =>
        new([new LigneCommande(produit, lot, 1)], false, null, null, PatientId: 3,
            new OrdonnanceSaisie(1, Jour, "ORD-1", Porteur: porteur), 0, ModePaiement.Especes, null);

    [Fact]
    public async Task Porteur_complet_transmis_au_registre()
    {
        var (cas, ventes) = Caisse();
        await cas.ExecuterAsync(Commande(7, 70, new PorteurOrdonnance(" Porteur Fictif ", "12 rue des Fictifs", "CNI 000000")), 5, "Caisse 1");
        Assert.Equal(new PorteurOrdonnance("Porteur Fictif", "12 rue des Fictifs", "CNI 000000"), ventes.Recu!.Ordonnance!.Porteur);
    }

    [Fact]
    public async Task Porteur_incomplet_refuse()
    {
        var (cas, _) = Caisse();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(Commande(7, 70, new PorteurOrdonnance("Porteur Fictif", " ", "CNI 000000")), 5, "Caisse 1"));
        Assert.Contains("adresse", ex.Message);
    }

    [Fact]
    public async Task Porteur_ignore_hors_psychotrope()
    {
        var (cas, ventes) = Caisse();
        await cas.ExecuterAsync(Commande(2, 20, new PorteurOrdonnance("Porteur Fictif", null, null)), 5, "Caisse 1");
        Assert.Null(ventes.Recu!.Ordonnance!.Porteur);
    }

    [Fact]
    public async Task Export_par_prescripteur_filtre_sur_l_identifiant_du_medecin_et_non_sur_une_partie_du_nom()
    {
        // « Dr Ali » (id 1) et « Dr Alice » (id 2) : le nom de l'un est contenu dans l'autre (revue 2026-11-18)
        LigneRegistre Sortie(long n, long? medecinId, string? medecin) =>
            Ligne(n, new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc), "sortie", null, 1, 10) with { MedecinId = medecinId, Medecin = medecin };
        var depot = new RegistreEnMemoire(Sortie(10, 1, "Dr Ali"), Sortie(11, 2, "Dr Alice"), Sortie(12, null, null));
        var consulter = new ConsulterRegistre(depot, new DroitsFactices("psychotropes.registre"));
        var parId = await consulter.ExecuterAsync(5, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 8), Jour, medecinId: 1);
        Assert.Equal([10L], parId.Lignes.Select(l => l.Numero));
        Assert.Empty(parId.Balance);   // extrait : pas de balance
        // L'identifiant prime sur l'ancien texte ; l'ancien paramètre texte reste accepté seul (compatibilité)
        Assert.Equal([11L], (await consulter.ExecuterAsync(5, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 8), Jour, prescripteur: "Dr Ali", medecinId: 2))
            .Lignes.Select(l => l.Numero));
        Assert.Equal([10L, 11L], (await consulter.ExecuterAsync(5, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 8), Jour, prescripteur: "dr ali"))
            .Lignes.Select(l => l.Numero));
    }
}
