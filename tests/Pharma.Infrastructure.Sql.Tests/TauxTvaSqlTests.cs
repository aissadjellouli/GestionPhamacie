using Pharma.Infrastructure.Fiscal;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Taux de TVA réglables (script 171) sur une copie jetable : liste de départ, ajout, désactivation, défaut d'une catégorie.</summary>
[Trait("Categorie", "Sql")]
public sealed class TauxTvaSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    [FactSql]
    public async Task Liste_de_depart_puis_ajout_desactivation_et_defaut_de_categorie()
    {
        var sql = new TauxTvaSql(copie.Source);
        var l = await sql.ListerAsync();
        Assert.Contains(l.Taux, t => t.Taux == 0 && t.Libelle == "Exonéré" && t.Actif);
        Assert.Contains(l.Taux, t => t.Taux == 19 && t.Libelle == "Taux normal");
        Assert.Equal((decimal?)0, l.Categories.Single(c => c.Code == "medicament").Taux);
        Assert.Equal((decimal?)19, l.Categories.Single(c => c.Code == "parapharmacie").Taux);

        var id = await sql.AjouterAsync(9.5m, "Essai 171");
        Assert.NotNull(id);
        Assert.Null(await sql.AjouterAsync(9.5m, "Doublon"));
        Assert.True(await sql.DefinirDefautAsync("hygiene", 9.5m));
        Assert.Equal((decimal?)9.5m, (await sql.ListerAsync()).Categories.Single(c => c.Code == "hygiene").Taux);
        Assert.True(await sql.DefinirDefautAsync("hygiene", null));
        Assert.Null((await sql.ListerAsync()).Categories.Single(c => c.Code == "hygiene").Taux);
        Assert.False(await sql.DefinirDefautAsync("categorie_inconnue", 9.5m));
        Assert.True(await sql.ModifierAsync(id!.Value, "Essai 171 bis", false));
        Assert.Contains((await sql.ListerAsync()).Taux, t => t.Id == id && !t.Actif && t.Libelle == "Essai 171 bis");
    }
}
