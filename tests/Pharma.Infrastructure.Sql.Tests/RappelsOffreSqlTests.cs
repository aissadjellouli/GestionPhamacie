using Pharma.Infrastructure.Offre;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Rappels de fin d'offre (script 172, décision Q5) sur une copie jetable : une seule note par étape et par date de fin.</summary>
[Trait("Categorie", "Sql")]
public sealed class RappelsOffreSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    [FactSql]
    public async Task Un_rappel_n_est_note_qu_une_fois_par_etape_et_par_date_de_fin()
    {
        var sql = new ActivationsOffreSql(copie.Source);
        var fin = new DateOnly(2031, 3, 31);
        Assert.False(await sql.RappelNoteAsync("30j", fin));
        Assert.True(await sql.NoterRappelAsync("30j", fin));
        Assert.True(await sql.RappelNoteAsync("30j", fin));
        Assert.False(await sql.NoterRappelAsync("30j", fin));
        Assert.True(await sql.NoterRappelAsync("7j", fin));
        Assert.True(await sql.NoterRappelAsync("30j", fin.AddYears(1)));
        // Simultanés : un seul passe
        var essais = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sql.NoterRappelAsync("grace", fin)));
        Assert.Equal(1, essais.Count(e => e));
    }
}
