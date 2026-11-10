using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class CodesEnMemoire : ICodesComptoir
{
    public Dictionary<long, CompteComptoir> Comptes { get; } = [];

    public Task<CompteComptoir?> TrouverActifAsync(string identifiant, CancellationToken ct = default) =>
        Task.FromResult(Comptes.Values.FirstOrDefault(c => c.Identifiant == identifiant));
    public Task<CompteComptoir?> ParIdAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Comptes.GetValueOrDefault(id));
    public Task EnregistrerEssaiAsync(long id, int essais, DateTime? bloqueJusqu, CancellationToken ct = default)
    {
        Comptes[id] = Comptes[id] with { Essais = essais, BloqueJusqu = bloqueJusqu };
        return Task.CompletedTask;
    }
    public Task<int?> ReserverEssaiAsync(long id, DateTime maintenant, int max, DateTime bloqueJusqu, CancellationToken ct = default)
    {
        var c = Comptes[id];
        if (c.BloqueJusqu is { } b && b > maintenant) return Task.FromResult<int?>(null);
        var n = c.BloqueJusqu is not null ? 1 : c.Essais + 1;
        Comptes[id] = c with { Essais = n, BloqueJusqu = n >= max ? bloqueJusqu : null };
        return Task.FromResult<int?>(n);
    }
    public Task DefinirAsync(long id, string? hash, CancellationToken ct = default)
    {
        Comptes[id] = Comptes[id] with { CodeHash = hash, Essais = 0, BloqueJusqu = null };
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<CompteComptoir>> AvecCodeAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CompteComptoir>>(Comptes.Values.Where(c => c.CodeHash is not null).ToList());
}

public class ConnexionComptoirTests
{
    private static readonly DateOnly Jour = new(2026, 11, 9);

    private static (SeConnecterParCode Cas, CodesEnMemoire Codes, JetonsFactices Jetons, JournalEspion Journal) Monter(
        SessionsEnMemoire? sessions = null, DroitsFactices? droits = null)
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var jetons = new JetonsFactices();
        var journal = new JournalEspion();
        var cas = new SeConnecterParCode(codes, new VerificateurFactice(), jetons, sessions ?? new SessionsEnMemoire(),
            droits ?? new DroitsFactices("caisse.vendre"), new ComptesEnMemoire(), new HorlogeFixe(Jour), journal);
        return (cas, codes, jetons, journal);
    }

    [Fact]
    public async Task Bon_code_sur_un_poste_ouvert_donne_un_jeton_limite_au_comptoir()
    {
        var (cas, _, jetons, _) = Monter();
        var session = await cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1");
        Assert.Equal("Vendeur B", session.NomComplet);
        Assert.True(jetons.Comptoir.Single());
    }

    [Fact]
    public async Task Code_refuse_si_la_caisse_du_poste_est_fermee()
    {
        var (cas, _, _, _) = Monter(new SessionsEnMemoire { Ouverte = null });
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1"));
        Assert.Contains("mot de passe", ex.Message);
    }

    [Fact]
    public async Task Cinq_codes_faux_bloquent_meme_le_bon_code()
    {
        var (cas, codes, _, journal) = Monter();
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "1111", "Caisse 1"));
        Assert.NotNull(codes.Comptes[9].BloqueJusqu);
        Assert.Contains("code_comptoir_bloque", journal.Actions);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1"));
        Assert.Contains("bloqué", ex.Message);
    }

    [Fact]
    public async Task Un_bon_code_remet_les_essais_a_zero()
    {
        var (cas, codes, _, _) = Monter();
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "1111", "Caisse 1"));
        Assert.Equal(1, codes.Comptes[9].Essais);
        await cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1");
        Assert.Equal(0, codes.Comptes[9].Essais);
    }

    [Fact]
    public async Task Sans_droit_de_vendre_pas_de_reprise_de_caisse()
    {
        var (cas, _, _, _) = Monter(droits: new DroitsFactices("stock.consulter"));
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1"));
    }

    [Fact]
    public async Task Employe_sans_code_refuse_avec_le_meme_message()
    {
        var (cas, codes, _, _) = Monter();
        codes.Comptes[9] = codes.Comptes[9] with { CodeHash = null };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1"));
        Assert.Equal(SeConnecterParCode.Refus, ex.Message);
    }

    [Fact]
    public async Task Changer_son_code_exige_le_mot_de_passe()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", null, 0, null);
        var definir = new DefinirCodeComptoir(codes, new HacheurFactice(), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => definir.ExecuterAsync(9, connexionParCode: true, "2580"));
        await definir.ExecuterAsync(9, connexionParCode: false, "2580");
        Assert.Equal("hash:2580", codes.Comptes[9].CodeHash);
    }

    [Fact]
    public async Task Effacer_un_code_exige_l_administration_des_utilisateurs()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new EffacerCodeComptoir(codes, new DroitsFactices("caisse.vendre"), new JournalEspion()).ExecuterAsync(9, 5));
        await new EffacerCodeComptoir(codes, new DroitsFactices("admin.utilisateurs"), new JournalEspion()).ExecuterAsync(9, 5);
        Assert.Null(codes.Comptes[9].CodeHash);
    }
}
