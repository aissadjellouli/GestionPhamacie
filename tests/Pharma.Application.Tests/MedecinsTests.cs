using Pharma.Application.Patients;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class MedecinsEnMemoire : IMedecins
{
    public List<Medecin> Liste { get; } = [];
    public Task<IReadOnlyList<Medecin>> RechercherAsync(string texte, int limite, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Medecin>>(Liste.Where(m => m.Nom.Contains(texte, StringComparison.OrdinalIgnoreCase)).Take(limite).ToList());
    public Task<long> CreerAsync(Medecin medecin, CancellationToken ct = default)
    {
        var id = Liste.Count + 1L;
        Liste.Add(medecin with { Id = id });
        return Task.FromResult(id);
    }
    public Task<bool> ModifierAsync(Medecin medecin, CancellationToken ct = default)
    {
        var i = Liste.FindIndex(m => m.Id == medecin.Id);
        if (i < 0) return Task.FromResult(false);
        Liste[i] = medecin;
        return Task.FromResult(true);
    }
}

/// <summary>Coordonnées d'un médecin prescripteur (2026-11-15) : nom obligatoire, le reste facultatif.</summary>
public class MedecinsTests
{
    [Fact]
    public async Task Un_medecin_se_cree_avec_le_nom_seul()
    {
        var depot = new MedecinsEnMemoire();
        await new CreerMedecin(depot).ExecuterAsync(new SaisieMedecin("  Dr Recette  ", " "));
        var m = Assert.Single(depot.Liste);
        Assert.Equal("Dr Recette", m.Nom);
        Assert.Null(m.Specialite);
        Assert.Null(m.Telephone);
        Assert.Null(m.NumeroOrdre);
        Assert.Null(m.CodeCnas);
    }

    [Fact]
    public async Task Les_coordonnees_facultatives_sont_gardees_et_nettoyees()
    {
        var depot = new MedecinsEnMemoire();
        await new CreerMedecin(depot).ExecuterAsync(new SaisieMedecin("Dr Recette", "Médecine générale", " 0550  12 34 56 ", " 2 rue de la Paix, Alger ",
            " dr.recette@exemple.dz ", " 16/1234 ", " CNAS-16-0042 "));
        var m = Assert.Single(depot.Liste);
        Assert.Equal("0550 12 34 56", m.Telephone);
        Assert.Equal("2 rue de la Paix, Alger", m.Adresse);
        Assert.Equal("dr.recette@exemple.dz", m.Email);
        Assert.Equal("16/1234", m.NumeroOrdre);
        Assert.Equal("CNAS-16-0042", m.CodeCnas);
    }

    [Theory]
    [InlineData(null, null, null, "nom_medecin")]
    [InlineData("Dr Recette", "pas-un-mail", null, "email")]
    [InlineData("Dr Recette", null, "12", "telephone")]
    public async Task Une_saisie_incorrecte_est_refusee_sur_son_champ(string? nom, string? email, string? telephone, string champ)
    {
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new CreerMedecin(new MedecinsEnMemoire()).ExecuterAsync(new SaisieMedecin(nom, null, telephone, null, email)));
        Assert.Equal(champ, e.Champ);
    }

    [Fact]
    public async Task Un_numero_trop_long_est_refuse()
    {
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new CreerMedecin(new MedecinsEnMemoire()).ExecuterAsync(new SaisieMedecin("Dr Recette", null, CodeCnas: new string('1', 31))));
        Assert.Equal("code_cnas", e.Champ);
    }

    [Fact]
    public async Task La_correction_garde_les_coordonnees_et_va_au_journal()
    {
        var depot = new MedecinsEnMemoire();
        var id = await new CreerMedecin(depot).ExecuterAsync(new SaisieMedecin("Dr Recete", null));
        var journal = new JournalEspion();

        await new ModifierMedecin(depot, journal).ExecuterAsync(id, new SaisieMedecin("Dr Recette", null, NumeroOrdre: "16/1234"), 1);

        Assert.Equal("Dr Recette", depot.Liste[0].Nom);
        Assert.Equal("16/1234", depot.Liste[0].NumeroOrdre);
        Assert.Equal("medecin_modifie", Assert.Single(journal.Actions));
        Assert.Equal("16/1234", journal.Details[0]["numero_ordre"]);
    }
}
