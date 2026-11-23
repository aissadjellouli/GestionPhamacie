using Pharma.Infrastructure.Lecture;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>R1 et R2 : balance mensuelle du registre des psychotropes (RegistreSql.SyntheseAsync) sur une vraie base.</summary>
[Trait("Categorie", "Sql")]
public sealed class RegistreSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    /// <summary>Instant « heure locale du poste » (fuseau de RegistreSql) converti en instant absolu.</summary>
    private static DateTimeOffset Local(int annee, int mois, int jour, int heure = 10, int minute = 0)
        => new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(annee, mois, jour, heure, minute, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);

    [FactSql]
    public async Task R1_balance_sur_trois_mois_le_report_est_le_solde_de_fin_du_mois_precedent()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var produit = await d.ProduitAsync(psychotrope: true);
        var lot = await d.LotAsync(produit, quantite: 0);
        await d.InscrireRegistreAsync(lot, utilisateur, "ouverture", 10, 10, Local(2031, 3, 2));
        await d.InscrireRegistreAsync(lot, utilisateur, "entree", 5, 15, Local(2031, 3, 10));
        await d.InscrireRegistreAsync(lot, utilisateur, "sortie", 3, 12, Local(2031, 3, 20));
        await d.InscrireRegistreAsync(lot, utilisateur, "sortie", 2, 10, Local(2031, 4, 5));
        await d.InscrireRegistreAsync(lot, utilisateur, "entree", 4, 14, Local(2031, 5, 3));
        await d.InscrireRegistreAsync(lot, utilisateur, "sortie", 1, 13, Local(2031, 5, 28));

        var (inscriptions, balance) = await new RegistreSql(copie.Source).SyntheseAsync(produit, new DateOnly(2031, 3, 1), new DateOnly(2031, 5, 31));

        Assert.Equal(6, inscriptions);
        Assert.Equal(3, balance.Count);
        var (mars, avril, mai) = (balance[0], balance[1], balance[2]);
        Assert.Equal((3, 5, 3, 12, 10), (mars.Mois, mars.Entrees, mars.Sorties, mars.SoldeFin, mars.SoldeReporte));   // ouverture exclue des entrées
        Assert.Equal((4, 0, 2, 10, 12), (avril.Mois, avril.Entrees, avril.Sorties, avril.SoldeFin, avril.SoldeReporte));
        Assert.Equal((5, 4, 1, 13, 10), (mai.Mois, mai.Entrees, mai.Sorties, mai.SoldeFin, mai.SoldeReporte));
        foreach (var m in balance) Assert.Equal(m.SoldeFin, m.SoldeReporte + m.Entrees - m.Sorties);
    }

    [FactSql]
    public async Task R1_les_inscriptions_d_un_autre_produit_sont_exclues_par_le_filtre_produit()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var produit = await d.ProduitAsync(psychotrope: true);
        var autre = await d.ProduitAsync(psychotrope: true);
        var lot = await d.LotAsync(produit, quantite: 0);
        var lotAutre = await d.LotAsync(autre, quantite: 0);
        await d.InscrireRegistreAsync(lot, utilisateur, "ouverture", 10, 10, Local(2032, 3, 2));
        await d.InscrireRegistreAsync(lotAutre, utilisateur, "ouverture", 7, 7, Local(2032, 3, 2));

        var (inscriptions, balance) = await new RegistreSql(copie.Source).SyntheseAsync(produit, new DateOnly(2032, 3, 1), new DateOnly(2032, 3, 31));

        Assert.Equal(1, inscriptions);
        Assert.Equal(produit, Assert.Single(balance).ProduitId);
    }

    [FactSql]
    public async Task R2_un_mouvement_a_23h30_heure_locale_le_dernier_jour_est_compte_dans_ce_mois()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var produit = await d.ProduitAsync(psychotrope: true);
        var lot = await d.LotAsync(produit, quantite: 0);
        await d.InscrireRegistreAsync(lot, utilisateur, "ouverture", 10, 10, Local(2033, 1, 15));
        await d.InscrireRegistreAsync(lot, utilisateur, "entree", 5, 15, Local(2033, 1, 31, 23, 30));
        await d.InscrireRegistreAsync(lot, utilisateur, "sortie", 2, 13, Local(2033, 2, 1, 0, 30));

        var (inscriptions, balance) = await new RegistreSql(copie.Source).SyntheseAsync(produit, new DateOnly(2033, 1, 1), new DateOnly(2033, 2, 28));

        Assert.Equal(3, inscriptions);
        Assert.Equal(2, balance.Count);
        Assert.Equal((1, 5, 0, 15, 10), (balance[0].Mois, balance[0].Entrees, balance[0].Sorties, balance[0].SoldeFin, balance[0].SoldeReporte));
        Assert.Equal((2, 0, 2, 13, 15), (balance[1].Mois, balance[1].Entrees, balance[1].Sorties, balance[1].SoldeFin, balance[1].SoldeReporte));
    }

    [FactSql]
    public async Task R2_la_periode_se_termine_au_dernier_jour_local_inclus()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var produit = await d.ProduitAsync(psychotrope: true);
        var lot = await d.LotAsync(produit, quantite: 0);
        await d.InscrireRegistreAsync(lot, utilisateur, "ouverture", 10, 10, Local(2034, 1, 15));
        await d.InscrireRegistreAsync(lot, utilisateur, "entree", 5, 15, Local(2034, 1, 31, 23, 30));
        await d.InscrireRegistreAsync(lot, utilisateur, "sortie", 2, 13, Local(2034, 2, 1, 0, 30));

        var (inscriptions, balance) = await new RegistreSql(copie.Source).SyntheseAsync(produit, new DateOnly(2034, 1, 1), new DateOnly(2034, 1, 31));

        Assert.Equal(2, inscriptions);
        var janvier = Assert.Single(balance);
        Assert.Equal((5, 0, 15), (janvier.Entrees, janvier.Sorties, janvier.SoldeFin));
    }
}
