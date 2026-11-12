using Pharma.Application.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class ReceptionsEnAttenteEnMemoire : IReceptionsEnAttente
{
    private readonly List<(ReceptionEnAttente R, string Contenu, IReadOnlyList<long> Scans)> liste = [];
    private long suivant = 1;

    public Task<IReadOnlyList<ReceptionEnAttente>> ListerAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReceptionEnAttente>>(liste.Select(x => x.R).ToList());

    public Task<long> EnregistrerAsync(string poste, long utilisateurId, string libelle, string mode, int lignes, string contenu, IReadOnlyList<long> scans,
        CancellationToken ct = default)
    {
        var id = suivant++;
        liste.Add((new ReceptionEnAttente(id, libelle, mode, lignes, utilisateurId, $"Employé {utilisateurId}", poste, DateTime.Now), contenu, scans));
        return Task.FromResult(id);
    }

    public Task<ReceptionEnAttente?> TrouverAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(liste.Where(x => x.R.Id == id).Select(x => x.R).FirstOrDefault());

    public Task<ReceptionReprise?> ReprendreAsync(long id, CancellationToken ct = default)
    {
        var i = liste.FindIndex(x => x.R.Id == id);
        if (i < 0) return Task.FromResult<ReceptionReprise?>(null);
        var x = liste[i];
        liste.RemoveAt(i);
        return Task.FromResult<ReceptionReprise?>(new ReceptionReprise(x.Contenu, x.R.Mode, x.R.EmployeId));
    }

    public Task<(string Libelle, IReadOnlyList<long> Scans)?> SupprimerAsync(long id, CancellationToken ct = default)
    {
        var i = liste.FindIndex(x => x.R.Id == id);
        if (i < 0) return Task.FromResult<(string, IReadOnlyList<long>)?>(null);
        var x = liste[i];
        liste.RemoveAt(i);
        return Task.FromResult<(string, IReadOnlyList<long>)?>((x.R.Libelle, x.Scans));
    }
}

public class ReceptionsEnAttenteTests
{
    private static (GererReceptionsEnAttente Cas, MobileEnMemoire Mobile, JournalEspion Journal) Creer(params string[] droits)
    {
        var mobile = new MobileEnMemoire();
        var journal = new JournalEspion();
        return (new GererReceptionsEnAttente(new ReceptionsEnAttenteEnMemoire(), mobile, journal, new DroitsFactices(droits)), mobile, journal);
    }

    [Fact]
    public async Task Reprise_depuis_un_autre_poste_par_un_collegue_et_une_seule_fois()
    {
        var (cas, _, _) = Creer();
        var id = await cas.MettreEnAttenteAsync("Caisse 1", 7, "BL 123 · Grossiste A", "reception", 3, """{"lignes":[]}""", [10, 11]);
        var r = await cas.ReprendreAsync(id, 8);
        Assert.Equal("""{"lignes":[]}""", r.Contenu);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ReprendreAsync(id, 8));   // déjà reprise
    }

    [Fact]
    public async Task Comptage_a_l_aveugle_repris_et_abandonne_par_le_seul_employe_qui_a_compte()
    {
        var (cas, _, _) = Creer();
        var id = await cas.MettreEnAttenteAsync("Réserve", 7, "Comptage BL 9", "comptage", 2, "{}", null);
        var refus = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ReprendreAsync(id, 8));
        Assert.Contains("Employé 7", refus.Message);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.SupprimerAsync(id, 8));
        Assert.Equal("{}", (await cas.ReprendreAsync(id, 7)).Contenu);
    }

    [Fact]
    public async Task Comptage_d_un_employe_absent_repris_ou_abandonne_par_qui_decide_des_ecarts()
    {
        var (cas, mobile, journal) = Creer("achats.valider_ecart");
        var id = await cas.MettreEnAttenteAsync("Réserve", 7, "Comptage BL 9", "comptage", 2, "{}", [3]);
        var autre = await cas.MettreEnAttenteAsync("Réserve", 7, "Recompte BL 10", "recompte", 1, "{}", null);
        Assert.Equal("{}", (await cas.ReprendreAsync(id, 8)).Contenu);
        await cas.SupprimerAsync(autre, 8);
        Assert.Equal("reception_attente_abandonnee", Assert.Single(journal.Actions));
    }

    [Fact]
    public async Task Abandon_rend_les_scans_du_mobile_et_s_inscrit_au_journal()
    {
        var (cas, mobile, journal) = Creer();
        var id = await cas.MettreEnAttenteAsync(null, 7, null, "reception", 1, "{}", [4, 5, 5]);
        await cas.SupprimerAsync(id, 8);
        Assert.Equal([4L, 5L], mobile.Remis);
        Assert.Equal("reception_attente_abandonnee", Assert.Single(journal.Actions));
        Assert.Equal("Réception en attente", journal.Details[0]["libelle"]);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.SupprimerAsync(id, 8));
    }

    [Fact]
    public async Task Au_plus_vingt_receptions_en_attente_et_jamais_vide()
    {
        var (cas, _, _) = Creer();
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.MettreEnAttenteAsync("P", 7, "x", "reception", 1, " ", null));
        for (var i = 0; i < GererReceptionsEnAttente.Max; i++) await cas.MettreEnAttenteAsync("P", 7, $"BL {i}", "reception", 1, "{}", null);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.MettreEnAttenteAsync("P", 7, "BL 21", "reception", 1, "{}", null));
    }

    [Fact]
    public async Task Libelle_tronque_et_mode_inconnu_ramene_a_reception()
    {
        var attente = new ReceptionsEnAttenteEnMemoire();
        var c = new GererReceptionsEnAttente(attente, new MobileEnMemoire(), new JournalEspion(), new DroitsFactices());
        await c.MettreEnAttenteAsync("P", 7, new string('x', 120), "nimporte", 1, "{}", null);
        var r = Assert.Single(await attente.ListerAsync());
        Assert.Equal(80, r.Libelle.Length);
        Assert.Equal("reception", r.Mode);
    }
}
