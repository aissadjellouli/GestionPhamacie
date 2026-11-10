using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class NomenclatureEnMemoire : INomenclature
{
    public NomenclatureLue? Recue { get; private set; }
    public (long Produit, string Classe)? Changement { get; private set; }

    public Task<long> RemplacerAsync(NomenclatureLue n, string fichier, long u, CancellationToken ct = default) { Recue = n; return Task.FromResult(1L); }
    public Task<VersionNomenclature?> VersionAsync(CancellationToken ct = default) =>
        Task.FromResult<VersionNomenclature?>(Recue is null ? null : new VersionNomenclature(Recue.Libelle, "f.xlsx", Recue.Lignes.Count, 0, 0, DateTime.UtcNow, "Titulaire"));
    public Task<IReadOnlyList<MedicamentNomenclature>> RechercherAsync(string t, int l, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MedicamentNomenclature>>([]);
    public Task<IReadOnlyList<ProduitAVerifier>> AVerifierAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProduitAVerifier>>([]);
    public Task<string?> ChangerClasseAsync(long p, string c, CancellationToken ct = default)
    {
        Changement = (p, c);
        return Task.FromResult<string?>(p == 7 ? "liste_i" : null);
    }
}

internal sealed class LecteurFactice(int lignes, bool illisible = false) : ILecteurNomenclature
{
    public NomenclatureLue Lire(Stream contenu) => illisible
        ? throw new FormatException("Le fichier n'est pas un classeur Excel (.xlsx).")
        : new NomenclatureLue("Nomenclature Juin 2026", Enumerable.Range(0, lignes).Select(i =>
            new LigneNomenclature(SituationNomenclature.EnVigueur, $"{i}/01", null, "DCI", $"MARQUE {i}", null, null, null,
                null, null, null, null, null, null, null, null, null, null, null)).ToList());
}

public class NomenclatureApplicationTests
{
    [Fact]
    public async Task Import_reserve_au_titulaire()
    {
        var cas = new ImporterNomenclature(new LecteurFactice(200), new NomenclatureEnMemoire(), new DroitsFactices("admin.parametres"), new JournalEspion());
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync(new MemoryStream(), "n.xlsx", 5));
    }

    [Fact]
    public async Task Import_remplace_la_version_et_ecrit_au_journal()
    {
        var stock = new NomenclatureEnMemoire();
        var journal = new JournalEspion();
        var v = await new ImporterNomenclature(new LecteurFactice(200), stock, new DroitsFactices { Titulaire = true }, journal)
            .ExecuterAsync(new MemoryStream(), @"C:\Téléchargements\NOMENCLATURE.xlsx", 1);
        Assert.Equal((200, "Nomenclature Juin 2026"), (v.EnVigueur, v.Libelle));
        Assert.Contains("nomenclature_importee", journal.Actions);
    }

    [Fact]
    public async Task Fichier_illisible_ou_trop_petit_refuse_avec_un_message()
    {
        var titulaire = new DroitsFactices { Titulaire = true };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ImporterNomenclature(new LecteurFactice(0, illisible: true), new NomenclatureEnMemoire(), titulaire, new JournalEspion())
                .ExecuterAsync(new MemoryStream(), "x.csv", 1));
        Assert.Contains("Excel", ex.Message);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ImporterNomenclature(new LecteurFactice(10), new NomenclatureEnMemoire(), titulaire, new JournalEspion())
                .ExecuterAsync(new MemoryStream(), "x.xlsx", 1));
    }

    [Fact]
    public async Task Classe_proposee_appliquee_par_le_titulaire_et_journalisee()
    {
        var stock = new NomenclatureEnMemoire();
        var journal = new JournalEspion();
        await new AppliquerClasseNomenclature(stock, new DroitsFactices { Titulaire = true }, journal).ExecuterAsync(7, "psychotrope", 1);
        Assert.Equal((7L, "psychotrope"), stock.Changement);
        Assert.Contains("classe_produit", journal.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new AppliquerClasseNomenclature(stock, new DroitsFactices { Titulaire = true }, journal).ExecuterAsync(7, "inconnue", 1));
    }
}
