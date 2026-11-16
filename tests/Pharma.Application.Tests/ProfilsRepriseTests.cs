using System.Runtime.CompilerServices;
using Pharma.Application.Commun;
using Pharma.Application.Reprise;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>Source qui rend des valeurs fixées, et dépôt qui garde les valeurs insérées en transit.</summary>
internal sealed class SourceValeurs(params string?[][] lignes) : ISourceReprise
{
    public Task<IReadOnlyList<SourceDisponible>> SourcesAsync(string? dossier, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SourceDisponible>>([]);
    public IReadOnlyList<string> Pilotes() => [];
    public Task<InfoSource> TesterAsync(string chaine, CancellationToken ct = default) => Task.FromResult(new InfoSource("x", null, 0));
    public Task<IReadOnlyList<TableSource>> ScannerAsync(string chaine, int exemples, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableSource>>([]);
    public async IAsyncEnumerable<string?[]> LireAsync(string chaine, string table, IReadOnlyList<string> colonnes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        foreach (var l in lignes) yield return (string?[])l.Clone();
    }
}

internal sealed class TransitEspion : IRepriseImport
{
    public List<(CibleReprise Cible, IReadOnlyList<string> Champs, List<string?[]> Lignes)> Inserts { get; } = [];
    public Task<long> CreerSessionAsync(string logiciel, string lancePar, CancellationToken ct = default) => Task.FromResult(9L);
    public async Task<(int Inserees, int Doublons)> InsererAsync(long session, CibleReprise cible, IReadOnlyList<string> champs, IAsyncEnumerable<string?[]> lignes, CancellationToken ct = default)
    {
        var l = new List<string?[]>();
        await foreach (var x in lignes) l.Add(x);
        Inserts.Add((cible, champs, l));
        return (l.Count, 0);
    }
    public Task<int> RapprocherEtControlerAsync(long session, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> ControlerAsync(long session, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<SessionReprise>> SessionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionReprise>>([]);
    public Task<EtatReprise?> EtatAsync(long session, CancellationToken ct = default) => Task.FromResult<EtatReprise?>(null);
    public Task DeciderAsync(long session, string refSource, DecisionProduit decision, long? produitId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChargerAsync(long session, long utilisateurId, string chargePar, CancellationToken ct = default) => Task.CompletedTask;
    public Task AnnulerAsync(long session, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class ProfilsEnMemoire : IProfilsReprise
{
    public List<ProfilReprise> Profils { get; } = [new(1, "Logiciel A", "", "a_completer", null, [], TransformationsReprise.Aucune)];
    public Task<Page<ProfilReprise>> ListerAsync(FiltreProfilsReprise filtre, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(new Page<ProfilReprise>(Profils, Profils.Count, page.Page, page.Taille));
    public Task<ProfilReprise?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult(Profils.FirstOrDefault(p => p.Id == id));
    public Task<long> EnregistrerAsync(ProfilReprise profil, string auteur, CancellationToken ct = default)
    {
        Profils.RemoveAll(p => p.Id == profil.Id);
        var id = profil.Id == 0 ? Profils.Count + 10 : profil.Id;
        Profils.Add(profil with { Id = id, ModifiePar = auteur });
        return Task.FromResult(id);
    }
}

internal sealed class HistoriqueEspion : IHistoriqueChifa
{
    public FiltreHistoriqueChifa? Filtre { get; private set; }
    public Task<Page<LigneHistoriqueChifa>> ListerAsync(FiltreHistoriqueChifa filtre, DemandePage page, CancellationToken ct = default)
    {
        Filtre = filtre;
        return Task.FromResult(new Page<LigneHistoriqueChifa>([], 0, page.Page, page.Taille));
    }
}

public class ProfilsRepriseTests
{
    private static SuperAdminCourant SuperAdmin(string? validee = null) => new()
    {
        Identifiant = "admin.pharma",
        Validation = validee is null ? null : new ValidationAcceptee(validee, "SMS", "06•••••512")
    };

    [Fact]
    public async Task Extraction_applique_les_transformations_du_profil_avant_le_transit()
    {
        var import = new TransitEspion();
        var journal = new JournalEspion();
        var t = new TransformationsReprise(SeparateurDecimal.Virgule, "dd/MM/yyyy", new Dictionary<string, string> { ["P"] = "psychotrope" });
        await new ExtraireReprise(new SourceValeurs(["A1", "DOLIPRANE", "12.500", "p"]), import, SuperAdmin(ActionsSuperAdmin.RepriseExtraire), journal)
            .ExecuterAsync("x", "Logiciel A", [new(CibleReprise.Produits, "Feuil1",
                new Dictionary<string, string> { ["ref_source"] = "C", ["nom"] = "N", ["prix_vente"] = "PV", ["classe"] = "CL" })], t, "Logiciel A", 1);

        Assert.Equal(["A1", "DOLIPRANE", "12500", "psychotrope"], import.Inserts[0].Lignes[0]);
        Assert.Equal("Logiciel A", journal.Details[0]["profil"]);
    }

    [Fact]
    public async Task Historique_chifa_extrait_vers_son_transit()
    {
        var import = new TransitEspion();
        await new ExtraireReprise(new SourceValeurs(["F1", "12/03/2024", "1 200,00"]), import, SuperAdmin(ActionsSuperAdmin.RepriseExtraire), new JournalEspion())
            .ExecuterAsync("x", "Ancien", [
                new(CibleReprise.Produits, "P", new Dictionary<string, string> { ["ref_source"] = "C", ["nom"] = "N" }),
                new(CibleReprise.HistoriqueChifa, "Factures", new Dictionary<string, string> { ["num_facture"] = "NF", ["date_facture"] = "D", ["montant_total"] = "T" })],
                new TransformationsReprise(SeparateurDecimal.Virgule, "dd/MM/yyyy", new Dictionary<string, string>()), null, 1);

        var h = import.Inserts.Single(i => i.Cible == CibleReprise.HistoriqueChifa);
        Assert.Equal(["F1", "2024-03-12", "1200"], h.Lignes[0]);
    }

    [Fact]
    public async Task Enregistrement_reserve_au_super_admin_valide_et_journalise()
    {
        var profils = new ProfilsEnMemoire();
        var journal = new JournalEspion();
        var profil = new ProfilReprise(1, "Logiciel A", "", "verifie", null,
            [new(CibleReprise.Produits, "Articles", new Dictionary<string, string> { ["ref_source"] = "CODE" })], TransformationsReprise.Aucune);

        await Assert.ThrowsAsync<DroitRefuseException>(() => new EnregistrerProfilReprise(profils, new SuperAdminCourant(), journal).ExecuterAsync(profil, 1));
        var id = await new EnregistrerProfilReprise(profils, SuperAdmin(), journal).ExecuterAsync(profil, 1);

        Assert.Equal(1, id);
        Assert.Equal("verifie", profils.Profils.Single().Statut);
        Assert.Equal("super admin admin.pharma", profils.Profils.Single().ModifiePar);
        Assert.Equal(["reprise_profil"], journal.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new EnregistrerProfilReprise(profils, SuperAdmin(), journal).ExecuterAsync(profil with { Id = 99 }, 1));
    }

    [Fact]
    public async Task Historique_chifa_reserve_au_droit_bordereaux_et_periode_controlee()
    {
        var h = new HistoriqueEspion();
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ConsulterHistoriqueChifa(h, new DroitsFactices()).ExecuterAsync(new(), new DemandePage(), 3));
        var droits = new DroitsFactices(Permissions.ChifaBordereaux);
        await Assert.ThrowsAsync<RegleMetierException>(() => new ConsulterHistoriqueChifa(h, droits)
            .ExecuterAsync(new(null, new DateOnly(2024, 5, 1), new DateOnly(2024, 4, 1)), new DemandePage(), 3));
        await new ConsulterHistoriqueChifa(h, droits).ExecuterAsync(new("  F1 "), new DemandePage(), 3);
        Assert.Equal("F1", h.Filtre!.Recherche);
    }
}
