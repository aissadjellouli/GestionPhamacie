using System.Runtime.CompilerServices;
using Pharma.Application.Commun;
using Pharma.Application.Reprise;
using Pharma.Application.SuperAdmin;
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
    public string? LancePar { get; private set; }
    public (long Utilisateur, string Par)? Charge { get; private set; }
    public Task<long> CreerSessionAsync(string logiciel, string lancePar, CancellationToken ct = default) { LancePar = lancePar; return Task.FromResult(7L); }
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
    // Après chargement : état minimal (relu par ChargerReprise pour le journal)
    public Task<EtatReprise?> EtatAsync(long session, CancellationToken ct = default) => Task.FromResult(Charge is { } c
        ? new EtatReprise(new SessionReprise(session, "Ancien 3.2", DateTime.UtcNow, "charge", LancePar, DateTime.UtcNow, c.Par), new Dictionary<string, int>(), new Dictionary<string, int>(), [], [], [])
        : null);
    public Task DeciderAsync(long session, string refSource, DecisionProduit decision, long? produitId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChargerAsync(long session, long utilisateurId, string chargePar, CancellationToken ct = default) { Charge = (utilisateurId, chargePar); return Task.CompletedTask; }
    public Task AnnulerAsync(long session, CancellationToken ct = default) => Task.CompletedTask;
}

public class RepriseTests
{
    /// <summary>Super admin connecté, avec la validation par code de l'action donnée (ou sans validation).</summary>
    private static SuperAdminCourant SuperAdmin(string? validee = null) => new()
    {
        Identifiant = "admin.pharma",
        Validation = validee is null ? null : new ValidationAcceptee(validee, "SMS", "06•••••512")
    };

    [Fact]
    public async Task Scan_masque_les_donnees_des_patients_mais_propose_sur_les_vraies_valeurs()
    {
        var r = await new ScannerSourceReprise(new SourceRepriseFactice(), SuperAdmin()).ExecuterAsync("DSN=Ancien;", 1);
        var clients = r.Tables.Single(t => t.Nom == "CLIENTS");
        Assert.Equal([ScannerSourceReprise.Masque], clients.Colonnes.Single(c => c.Nom == "NOM").Exemples);
        Assert.Equal([ScannerSourceReprise.Masque], clients.Colonnes.Single(c => c.Nom == "TEL").Exemples);
        Assert.Equal(["SINTROM 4MG"], r.Tables.Single(t => t.Nom == "ARTICLES").Colonnes.Single(c => c.Nom == "DESIGNATION").Exemples);
        Assert.Equal("ARTICLES", r.Propositions.Single(p => p.Cible == CibleReprise.Produits).Table);
    }

    [Fact]
    public async Task Reserve_au_super_admin_et_action_validee_par_code()
    {
        // Employé (même titulaire) : pas de super admin connecté
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ScannerSourceReprise(new SourceRepriseFactice(), new SuperAdminCourant()).ExecuterAsync("DSN=Ancien;", 2));
        // Super admin sans code validé, ou code validé pour une autre action
        var import = new RepriseEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ExtraireReprise(new SourceRepriseFactice(), import, SuperAdmin(), new JournalEspion()).ExecuterAsync("DSN=Ancien;", "Ancien 3.2", [], 2));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ChargerReprise(import, SuperAdmin(ActionsSuperAdmin.RepriseExtraire), new JournalEspion()).ExecuterAsync(7, 2));
        Assert.Null(import.Charge);
    }

    [Fact]
    public async Task Extraction_lit_les_colonnes_choisies_et_journalise()
    {
        var (source, import, journal) = (new SourceRepriseFactice(), new RepriseEnMemoire(), new JournalEspion());
        var r = await new ExtraireReprise(source, import, SuperAdmin(ActionsSuperAdmin.RepriseExtraire), journal).ExecuterAsync("DSN=Ancien;", "Ancien 3.2",
            [new(CibleReprise.Produits, "ARTICLES", new Dictionary<string, string> { ["ref_source"] = "CODE_ART", ["nom"] = "DESIGNATION", ["code_barre"] = "CB" })], 1);

        Assert.Equal((7L, 1, 3), (r.SessionId, r.Lignes[CibleReprise.Produits], r.Bloquants));
        Assert.Equal("ARTICLES", source.Lectures[0].Table);
        Assert.Equal(["CODE_ART", "DESIGNATION", "CB"], source.Lectures[0].Colonnes);
        Assert.Equal(["ref_source", "nom", "code_barre"], import.Inserts[0].Champs);
        Assert.Equal(["reprise_extraction"], journal.Actions);
        Assert.Equal("super admin admin.pharma", import.LancePar);
        Assert.Equal("admin.pharma", journal.Details[0]["super_admin"]);
        Assert.Equal("code envoyé par SMS au 06•••••512", journal.Details[0]["validation"]);
    }

    [Fact]
    public async Task Chargement_attribue_au_titulaire_et_trace_le_super_admin()
    {
        var (import, journal) = (new RepriseEnMemoire(), new JournalEspion());
        await new ChargerReprise(import, SuperAdmin(ActionsSuperAdmin.RepriseCharger), journal).ExecuterAsync(7, utilisateurId: 1);

        Assert.Equal((1L, "super admin admin.pharma"), import.Charge);   // 1 : identifiant du titulaire passé par l'API
        Assert.Equal(["reprise_chargement"], journal.Actions);
        Assert.Equal("admin.pharma", journal.Details[0]["super_admin"]);
    }

    [Fact]
    public async Task Extraction_refusee_sans_produits_ou_sans_colonne_obligatoire()
    {
        var cas = new ExtraireReprise(new SourceRepriseFactice(), new RepriseEnMemoire(), SuperAdmin(ActionsSuperAdmin.RepriseExtraire), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("DSN=Ancien;", "Ancien", [], 1));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("DSN=Ancien;", "Ancien",
            [new(CibleReprise.Produits, "ARTICLES", new Dictionary<string, string> { ["ref_source"] = "CODE_ART" })], 1));
        Assert.Contains("Désignation", ex.Message);
    }

    // ---- Option du titulaire : fichier des produits pour l'historique des organismes seul (2026-11-16) ----

    private static readonly Dictionary<string, string> ColonnesChifa = new() { ["num_facture"] = "CODE_ART", ["date_facture"] = "DESIGNATION" };

    private static ExtraireReprise Extraction(bool exiger, RepriseEnMemoire import) =>
        new(new SourceRepriseFactice(), import, SuperAdmin(ActionsSuperAdmin.RepriseExtraire), new JournalEspion(),
            new SessionsEnMemoire { Parametres = new Pharma.Application.Caisse.ParametresCaisse(true, 0, 0, 0, ExigerProduitsRepriseHistorique: exiger) });

    [Fact]
    public async Task Historique_seul_refuse_quand_le_titulaire_exige_les_produits_et_cite_l_option()
    {
        var import = new RepriseEnMemoire();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Extraction(true, import).ExecuterAsync("DSN=Ancien;", "Ancien",
            [new(CibleReprise.HistoriqueChifa, "ARTICLES", ColonnesChifa)], 1));
        Assert.Contains("Le titulaire exige", ex.Message);
        Assert.Contains(ExigenceProduitsReprise.Option, ex.Message);
        Assert.Contains("Caisse et contrôle", ex.Message);
        Assert.Empty(import.Inserts);
    }

    [Fact]
    public async Task Historique_Chifa_et_MDN_seuls_acceptes_quand_le_titulaire_n_exige_pas_les_produits()
    {
        var import = new RepriseEnMemoire();
        var r = await Extraction(false, import).ExecuterAsync("DSN=Ancien;", "Ancien",
            [new(CibleReprise.HistoriqueChifa, "ARTICLES", ColonnesChifa), new(CibleReprise.HistoriqueMdn, "CLIENTS", new Dictionary<string, string> { ["num_facture"] = "NUM_CLI", ["date_facture"] = "NOM" })], 1);
        Assert.Equal(1, r.Lignes[CibleReprise.HistoriqueChifa]);
        Assert.Equal(1, r.Lignes[CibleReprise.HistoriqueMdn]);
    }

    [Theory]
    [InlineData(CibleReprise.Stock, "ref_produit")]
    [InlineData(CibleReprise.Clients, "ref_source")]
    public async Task Stock_ou_patients_sans_produits_toujours_refuses_meme_si_l_option_n_exige_pas(CibleReprise cible, string champ)
    {
        var colonnes = DetectionReprise.Champs[cible].Where(f => f.Obligatoire).ToDictionary(f => f.Code, _ => "CODE_ART");
        colonnes[champ] = "CODE_ART";
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Extraction(false, new RepriseEnMemoire()).ExecuterAsync("DSN=Ancien;", "Ancien",
            [new(cible, "ARTICLES", colonnes), new(CibleReprise.HistoriqueChifa, "CLIENTS", ColonnesChifa)], 1));
        Assert.Contains("indispensable dès que", ex.Message);
    }

    [Fact]
    public async Task Le_scan_informe_l_administrateur_de_l_etat_de_l_option()
    {
        Assert.Equal("Le titulaire exige le fichier des produits pour un import de l’historique des organismes seul (Chifa, MDN).", ExigenceProduitsReprise.Information(true));
        Assert.StartsWith("Le titulaire n’exige pas", ExigenceProduitsReprise.Information(false));
        var scan = await new ScannerSourceReprise(new SourceRepriseFactice(), SuperAdmin(),
            new SessionsEnMemoire { Parametres = new Pharma.Application.Caisse.ParametresCaisse(true, 0, 0, 0, ExigerProduitsRepriseHistorique: false) }).ExecuterAsync("DSN=Ancien;", 1);
        Assert.False(scan.ExigerProduitsHistorique);
        // Sans réglage lu : exigé (comportement d'origine)
        Assert.True((await new ScannerSourceReprise(new SourceRepriseFactice(), SuperAdmin()).ExecuterAsync("DSN=Ancien;", 1)).ExigerProduitsHistorique);
    }

    [Fact]
    public void Verification_des_cibles_sans_produits()
    {
        ExigenceProduitsReprise.Verifier([CibleReprise.HistoriqueMdn], exiger: false);
        ExigenceProduitsReprise.Verifier([CibleReprise.Produits, CibleReprise.Stock], exiger: true);
        Assert.Throws<RegleMetierException>(() => ExigenceProduitsReprise.Verifier([CibleReprise.HistoriqueMdn], exiger: true));
        Assert.Throws<RegleMetierException>(() => ExigenceProduitsReprise.Verifier([CibleReprise.Fournisseurs], exiger: false));
        Assert.Throws<RegleMetierException>(() => ExigenceProduitsReprise.Verifier([], exiger: false));
    }
}
