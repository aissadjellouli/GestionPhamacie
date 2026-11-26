using Pharma.Application.Commun;
using Pharma.Infrastructure.Catalogue;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Stock › Contenance manquante (2026-11-26) sur une copie jetable : produits actifs sans contenance seulement, recherche, jamais d'écrasement.</summary>
[Trait("Categorie", "Sql")]
public sealed class ContenancesManquantesSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    [FactSql]
    public async Task Liste_les_seuls_produits_actifs_sans_contenance_et_n_ecrase_jamais_une_contenance()
    {
        var sql = new ContenancesManquantesSql(copie.Source);
        long sans, avec;
        await using (var cmd = copie.Verif.CreateCommand("""
            INSERT INTO produit (nom_commercial, conditionnement, actif) VALUES ('ZZESSAI SANS CONTENANCE', 'B/30 ET B/60', true) RETURNING id
            """))
            sans = (long)(await cmd.ExecuteScalarAsync())!;
        await using (var cmd = copie.Verif.CreateCommand("""
            INSERT INTO produit (nom_commercial, conditionnement, actif, contenance) VALUES ('ZZESSAI AVEC CONTENANCE', 'B/30', true, 30) RETURNING id
            """))
            avec = (long)(await cmd.ExecuteScalarAsync())!;

        var page = await sql.ListerAsync("zzessai", new DemandePage(1, 10));
        var ligne = Assert.Single(page.Lignes);
        Assert.Equal((sans, "B/30 ET B/60", 0, 0), (ligne.ProduitId, ligne.Conditionnement, ligne.Vendues12Mois, ligne.Stock));
        Assert.Empty((await sql.ListerAsync("%", new DemandePage(1, 10))).Lignes.Where(l => l.Nom.StartsWith("ZZESSAI")).Skip(1));

        Assert.Equal("ZZESSAI SANS CONTENANCE", await sql.EnregistrerSiVideAsync(sans, 60));
        Assert.Null(await sql.EnregistrerSiVideAsync(sans, 90));     // déjà renseignée : jamais écrasée
        Assert.Null(await sql.EnregistrerSiVideAsync(avec, 90));
        await using (var cmd = copie.Verif.CreateCommand($"SELECT string_agg(contenance::text, ',' ORDER BY id) FROM produit WHERE id IN ({sans}, {avec})"))
            Assert.Equal("60,30", await cmd.ExecuteScalarAsync());
        Assert.Empty((await sql.ListerAsync("zzessai", new DemandePage(1, 10))).Lignes);

        // Correction : seulement si la valeur lue avant n'a pas changé ; « toutes » liste aussi les produits renseignés
        Assert.Equal(2, (await sql.ListerAsync("zzessai", new DemandePage(1, 10), default, toutes: true)).Lignes.Count);
        Assert.Null(await sql.CorrigerAsync(avec, 25, 12));
        Assert.Equal("ZZESSAI AVEC CONTENANCE", await sql.CorrigerAsync(avec, 30, 12));
        var identifies = await sql.ProduitsAsync();
        Assert.Equal(12, identifies.Single(p => p.Id == avec).Contenance);
        Assert.DoesNotContain((await sql.AExporterAsync()), x => x.Produit.ProduitId == avec || x.Produit.ProduitId == sans);
    }
}
