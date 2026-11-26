using Pharma.Application.Commun;
using Pharma.Application.Fiscal;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Taux de TVA réglables (décision du titulaire du 2026-11-26, script 171) : liste, défauts des catégories, contrôle de la saisie.</summary>
public class TauxTvaTests
{
    private sealed class Depot : ITauxTva
    {
        public List<TauxTvaLu> Taux { get; } = [new(1, 0, "Exonéré", true, 9472), new(2, 19, "Taux normal", true, 35)];
        public Dictionary<string, decimal?> Defauts { get; } = new() { ["medicament"] = 0, ["parapharmacie"] = 19, ["hygiene"] = null };

        public Task<ListeTauxTva> ListerAsync(CancellationToken ct = default) => Task.FromResult(new ListeTauxTva(Taux.ToList(),
            Defauts.Select(d => new TauxTvaCategorie(d.Key, d.Key, d.Value)).ToList(), []));

        public Task<long?> AjouterAsync(decimal taux, string libelle, CancellationToken ct = default)
        {
            if (Taux.Any(t => t.Taux == taux)) return Task.FromResult<long?>(null);
            Taux.Add(new(Taux.Count + 1, taux, libelle, true, 0));
            return Task.FromResult<long?>(Taux.Count);
        }

        public Task<bool> ModifierAsync(long id, string libelle, bool actif, CancellationToken ct = default)
        {
            var i = Taux.FindIndex(t => t.Id == id);
            if (i < 0) return Task.FromResult(false);
            Taux[i] = Taux[i] with { Libelle = libelle, Actif = actif };
            return Task.FromResult(true);
        }

        public Task<bool> DefinirDefautAsync(string categorie, decimal? taux, CancellationToken ct = default)
        {
            if (!Defauts.ContainsKey(categorie)) return Task.FromResult(false);
            Defauts[categorie] = taux;
            return Task.FromResult(true);
        }
    }

    private static (Depot D, JournalEspion J, GererTauxTva G) Banc(bool titulaire = true)
    {
        var d = new Depot();
        var j = new JournalEspion();
        return (d, j, new GererTauxTva(d, new DroitsFactices { Titulaire = titulaire }, j));
    }

    [Fact]
    public async Task Ajouter_modifier_et_definir_un_defaut_titulaire_seul_et_au_journal()
    {
        var (d, j, g) = Banc();
        await g.AjouterAsync(1, 9, "Taux réduit");
        Assert.Equal("taux", (await Assert.ThrowsAsync<RegleMetierException>(() => g.AjouterAsync(1, 9, "Doublon"))).Champ);
        Assert.Equal("taux", (await Assert.ThrowsAsync<RegleMetierException>(() => g.AjouterAsync(1, 100.5m, "Trop"))).Champ);
        Assert.Equal("taux", (await Assert.ThrowsAsync<RegleMetierException>(() => g.AjouterAsync(1, 9.555m, "Décimales"))).Champ);
        Assert.Equal("libelle", (await Assert.ThrowsAsync<RegleMetierException>(() => g.AjouterAsync(1, 7, "  "))).Champ);

        await g.DefinirDefautAsync(1, "hygiene", 9);
        Assert.Equal(9, d.Defauts["hygiene"]);
        // Un taux inactif ou absent ne peut pas être un défaut ; le défaut d'une catégorie ne se désactive pas
        Assert.Equal("taux", (await Assert.ThrowsAsync<RegleMetierException>(() => g.DefinirDefautAsync(1, "hygiene", 7))).Champ);
        Assert.Equal("actif", (await Assert.ThrowsAsync<RegleMetierException>(() => g.ModifierAsync(1, 3, "Taux réduit", false))).Champ);
        await g.DefinirDefautAsync(1, "hygiene", null);
        await g.ModifierAsync(1, 3, "Taux réduit", false);
        Assert.False(d.Taux.Single(t => t.Taux == 9).Actif);
        Assert.Equal(["taux_tva_ajoute", "taux_tva_categorie", "taux_tva_categorie", "taux_tva_modifie"], j.Actions);

        await Assert.ThrowsAsync<DroitRefuseException>(() => Banc(titulaire: false).G.AjouterAsync(2, 9, "Taux réduit"));
    }

    [Fact]
    public async Task La_saisie_prend_un_taux_actif_de_la_liste_un_taux_existant_hors_liste_est_garde_tel_quel()
    {
        var d = new Depot();
        var autorises = new TauxTvaAutorises(d);
        await autorises.ExigerAsync(19, null, "tva");
        var refus = await Assert.ThrowsAsync<RegleMetierException>(() => autorises.ExigerAsync(7, null, "tva"));
        Assert.Equal("tva", refus.Champ);
        Assert.Contains("0 % ou 19 %", refus.Message);
        await autorises.ExigerAsync(7, 7, "tva");                 // déjà enregistré, inchangé : gardé
        await Assert.ThrowsAsync<RegleMetierException>(() => autorises.ExigerAsync(7, 0, "tva"));
        d.Taux[1] = d.Taux[1] with { Actif = false };
        await Assert.ThrowsAsync<RegleMetierException>(() => autorises.ExigerAsync(19, 0, "taux_tva"));
    }
}
