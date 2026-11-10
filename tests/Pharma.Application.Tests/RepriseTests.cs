using System.Runtime.CompilerServices;
using Pharma.Application.Commun;
using Pharma.Application.Reprise;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;

namespace Pharma.Application.Tests;

internal sealed class SourceRepriseFactice : ISourceReprise
{
    public static readonly TableSource Clients = new("CLIENTS", 2,
        [new("NUM_CLI", "VARCHAR", ["C1"]), new("NOM", "VARCHAR", ["Patient Secret"]), new("TEL", "VARCHAR", ["0550123456"]), new("CREDIT", "VARCHAR", ["1500"])]);
    public static readonly TableSource Articles = new("ARTICLES", 2,
        [new("CODE_ART", "VARCHAR", ["A1"]), new("DESIGNATION", "VARCHAR", ["SINTROM 4MG"]), new("CB", "VARCHAR", ["6130000000107"])]);
    public List<(string Table, IReadOnlyList<string> Colonnes)> Lectures { get; } = [];

    public Task<IReadOnlyList<SourceDisponible>> SourcesAsync(string? dossier, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SourceDisponible>>([]);
    public IReadOnlyList<string> Pilotes() => ["Microsoft Access Driver (*.mdb, *.accdb)"];
    public Task<InfoSource> TesterAsync(string chaine, CancellationToken ct = default) => Task.FromResult(new InfoSource("ACEODBC.DLL", null, 0));
    public Task<IReadOnlyList<TableSource>> ScannerAsync(string chaine, int exemples, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TableSource>>([Articles, Clients]);
    public async IAsyncEnumerable<string?[]> LireAsync(string chaine, string table, IReadOnlyList<string> colonnes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Lectures.Add((table, colonnes));
        await Task.Yield();
        yield return colonnes.Select(c => (string?)$"{c}-1").ToArray();
    }
}

internal sealed class RepriseEnMemoire : IRepriseImport
{
    public List<(CibleReprise Cible, IReadOnlyList<string> Champs, int Lignes)> Inserts { get; } = [];
    public Task<long> CreerSessionAsync(string logiciel, CancellationToken ct = default) => Task.FromResult(7L);
    public async Task<(int Inserees, int Doublons)> InsererAsync(long session, CibleReprise cible, IReadOnlyList<string> champs, IAsyncEnumerable<string?[]> lignes, CancellationToken ct = default)
    {
        var n = 0;
        await foreach (var _ in lignes) n++;
        Inserts.Add((cible, champs, n));
        return (n, 0);
    }
    public Task<int> RapprocherEtControlerAsync(long session, CancellationToken ct = default) => Task.FromResult(3);
    public Task<int> ControlerAsync(long session, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<SessionReprise>> SessionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionReprise>>([]);
    public Task<EtatReprise?> EtatAsync(long session, CancellationToken ct = default) => Task.FromResult<EtatReprise?>(null);
    public Task DeciderAsync(long session, string refSource, DecisionProduit decision, long? produitId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChargerAsync(long session, long utilisateurId, CancellationToken ct = default) => Task.CompletedTask;
    public Task AnnulerAsync(long session, CancellationToken ct = default) => Task.CompletedTask;
}

public class RepriseTests
{
    private static DroitsFactices Titulaire() => new() { Titulaire = true };

    [Fact]
    public async Task Scan_masque_les_donnees_des_patients_mais_propose_sur_les_vraies_valeurs()
    {
        var r = await new ScannerSourceReprise(new SourceRepriseFactice(), Titulaire()).ExecuterAsync("DSN=Ancien;", 1);
        var clients = r.Tables.Single(t => t.Nom == "CLIENTS");
        Assert.Equal([ScannerSourceReprise.Masque], clients.Colonnes.Single(c => c.Nom == "NOM").Exemples);
        Assert.Equal([ScannerSourceReprise.Masque], clients.Colonnes.Single(c => c.Nom == "TEL").Exemples);
        Assert.Equal(["SINTROM 4MG"], r.Tables.Single(t => t.Nom == "ARTICLES").Colonnes.Single(c => c.Nom == "DESIGNATION").Exemples);
        Assert.Equal("ARTICLES", r.Propositions.Single(p => p.Cible == CibleReprise.Produits).Table);
    }

    [Fact]
    public async Task Reserve_au_titulaire()
    {
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ScannerSourceReprise(new SourceRepriseFactice(), new DroitsFactices("admin.parametres")).ExecuterAsync("DSN=Ancien;", 2));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ExtraireReprise(new SourceRepriseFactice(), new RepriseEnMemoire(), new DroitsFactices("admin.parametres"), new JournalEspion())
                .ExecuterAsync("DSN=Ancien;", "Ancien 3.2", [], 2));
    }

    [Fact]
    public async Task Extraction_lit_les_colonnes_choisies_et_journalise()
    {
        var (source, import, journal) = (new SourceRepriseFactice(), new RepriseEnMemoire(), new JournalEspion());
        var r = await new ExtraireReprise(source, import, Titulaire(), journal).ExecuterAsync("DSN=Ancien;", "Ancien 3.2",
            [new(CibleReprise.Produits, "ARTICLES", new Dictionary<string, string> { ["ref_source"] = "CODE_ART", ["nom"] = "DESIGNATION", ["code_barre"] = "CB" })], 1);

        Assert.Equal((7L, 1, 3), (r.SessionId, r.Lignes[CibleReprise.Produits], r.Bloquants));
        Assert.Equal("ARTICLES", source.Lectures[0].Table);
        Assert.Equal(["CODE_ART", "DESIGNATION", "CB"], source.Lectures[0].Colonnes);
        Assert.Equal(["ref_source", "nom", "code_barre"], import.Inserts[0].Champs);
        Assert.Equal(["reprise_extraction"], journal.Actions);
    }

    [Fact]
    public async Task Extraction_refusee_sans_produits_ou_sans_colonne_obligatoire()
    {
        var cas = new ExtraireReprise(new SourceRepriseFactice(), new RepriseEnMemoire(), Titulaire(), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("DSN=Ancien;", "Ancien", [], 1));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("DSN=Ancien;", "Ancien",
            [new(CibleReprise.Produits, "ARTICLES", new Dictionary<string, string> { ["ref_source"] = "CODE_ART" })], 1));
        Assert.Contains("Désignation", ex.Message);
    }
}
