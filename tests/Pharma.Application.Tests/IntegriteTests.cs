using System.Security.Cryptography;
using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Integrite;
using Pharma.Application.Notifications;

namespace Pharma.Application.Tests;

/// <summary>Intégrité des journaux (lot 2) : sceau HMAC, vérification des chaînes et des sceaux, alerte et journal en cas de rupture.</summary>
public class IntegriteTests
{
    private const string Journal = "integrite_essai";
    private static readonly DateTime Le = new(2026, 11, 16, 22, 30, 15, DateTimeKind.Utc);

    [Fact]
    public async Task Le_sceau_est_un_HMAC_SHA256_du_message_canonique_et_couvre_la_derniere_ligne()
    {
        var depot = new DepotFactice();
        depot.Chaine("officine", 3, Hash(3));
        var scelleur = new ScelleurFactice();
        var s = await new ScellerJournal(depot, scelleur, new Horloge(Le.AddTicks(7))).ExecuterAsync(Journal, new DateOnly(2026, 11, 16),
            new DateOnly(2026, 11, 16), MotifSceau.ClotureJournaliere, auteurId: 1);

        var attendu = "sceau-v1|integrite_essai|officine|cloture_journaliere|2026-11-16|2026-11-16|2026-11-16T22:30:15.000000Z|3|30|"
                      + Convert.ToHexStringLower(Hash(3));
        Assert.Equal(attendu, Encoding.UTF8.GetString(MessageSceau.Canonique(s)));
        Assert.Equal(HMACSHA256.HashData(ScelleurFactice.Cle, Encoding.UTF8.GetBytes(attendu)), s.Sceau);
        Assert.Equal(3, s.DernierRang);
        Assert.Single(depot.Sceaux);
        Assert.Equal("HMAC-SHA256", depot.Sceaux[0].Algorithme);
    }

    [Fact]
    public async Task Un_motif_inconnu_ou_un_journal_non_chaine_est_refuse()
    {
        var depot = new DepotFactice();
        var sceller = new ScellerJournal(depot, new ScelleurFactice(), new Horloge(Le));
        await Assert.ThrowsAsync<Domain.Commun.RegleMetierException>(() =>
            sceller.ExecuterAsync(Journal, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), "autre", null));
        await Assert.ThrowsAsync<Domain.Commun.RegleMetierException>(() =>
            sceller.ExecuterAsync("vente", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), MotifSceau.Manuel, null));
    }

    [Fact]
    public async Task Une_chaine_intacte_avec_un_sceau_valide_est_ok_et_enregistree_sans_alerte()
    {
        var (depot, alertes, verifier) = await PrepareAsync();
        var etats = await verifier.ExecuterAsync(null);
        Assert.Equal(VerifierIntegrite.Ok, Assert.Single(etats).Statut);
        Assert.Equal(VerifierIntegrite.Ok, Assert.Single(depot.Verifications).Statut);
        Assert.Empty(alertes.Messages);
        Assert.Empty(depot.RupturesSysteme);
    }

    [Fact]
    public async Task Une_ligne_modifiee_signalee_par_la_base_alerte_le_titulaire_et_va_au_journal()
    {
        var (depot, alertes, verifier) = await PrepareAsync();
        depot.Rupture = new ResultatChaine("officine", 3, 1, Hash(1), 2, 20, "ligne modifiée (empreinte recalculée différente)", "aa", "bb");
        var etat = Assert.Single(await verifier.ExecuterAsync(null));
        Assert.Equal(VerifierIntegrite.Rupture, etat.Statut);
        Assert.Equal(2, etat.RangRupture);
        Assert.Single(alertes.Messages);
        Assert.True(alertes.Messages[0].Urgent);
        Assert.Single(depot.RupturesSysteme);   // tâche planifiée : journal sans employé
    }

    [Fact]
    public async Task Une_derniere_ligne_supprimee_est_detectee_par_le_sceau()
    {
        var (depot, _, _) = await PrepareAsync();
        depot.Chaine("officine", 2, Hash(2));   // la ligne 3, scellée, a disparu : la chaîne restante est intacte
        var (alertes, journal) = (new AlertesFactices(), new JournalFactice());
        var etat = Assert.Single(await new VerifierIntegrite(depot, new ScelleurFactice(), alertes, journal).ExecuterAsync(5));
        Assert.Equal(VerifierIntegrite.Rupture, etat.Statut);
        Assert.Contains("dernière ligne scellée absente", etat.Motif);
        Assert.Equal("integrite_rupture", Assert.Single(journal.Actions));   // vérification du titulaire : journal à son nom
        Assert.Single(alertes.Messages);
    }

    [Fact]
    public async Task Un_sceau_falsifie_est_detecte()
    {
        var (depot, _, verifier) = await PrepareAsync();
        var s = depot.Sceaux[0];
        depot.Sceaux[0] = s with { DernierRang = 2, DernierHash = Hash(2) };   // données du sceau changées, HMAC inchangé
        var etat = Assert.Single(await verifier.ExecuterAsync(null));
        Assert.Equal(VerifierIntegrite.Rupture, etat.Statut);
        Assert.Contains("invalide", etat.Motif);
    }

    [Fact]
    public async Task Un_sceau_fait_avec_une_autre_cle_est_signale()
    {
        var (depot, _, _) = await PrepareAsync();
        var verifier = new VerifierIntegrite(depot, new ScelleurFactice("autre-cle-de-test-32-octets-xxxx"), new AlertesFactices(), new JournalFactice());
        var etat = Assert.Single(await verifier.ExecuterAsync(null));
        Assert.Contains("autre clé", etat.Motif);
    }

    [Fact]
    public void Le_sceau_s_exporte_en_hexadecimal_ou_en_base64()
    {
        byte[] sceau = [0xAB, 0x01];
        Assert.Equal("ab01", MessageSceau.Exporter(sceau, "hex"));
        Assert.Equal("qwE=", MessageSceau.Exporter(sceau, "base64"));
    }

    private static async Task<(DepotFactice, AlertesFactices, VerifierIntegrite)> PrepareAsync()
    {
        var depot = new DepotFactice();
        depot.Chaine("officine", 3, Hash(3));
        var scelleur = new ScelleurFactice();
        await new ScellerJournal(depot, scelleur, new Horloge(Le)).ExecuterAsync(Journal, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 16),
            MotifSceau.ClotureJournaliere, 1);
        var alertes = new AlertesFactices();
        return (depot, alertes, new VerifierIntegrite(depot, scelleur, alertes, new JournalFactice()));
    }

    private static byte[] Hash(long rang) => SHA256.HashData(BitConverter.GetBytes(rang));

    private sealed class Horloge(DateTime utc) : IHorloge
    {
        public DateTime MaintenantUtc => utc;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(utc);
    }

    private sealed class ScelleurFactice(string cle = "cle-de-test-de-32-octets-exactem") : IScelleurIntegrite
    {
        public static readonly byte[] Cle = Encoding.ASCII.GetBytes("cle-de-test-de-32-octets-exactem");
        private readonly byte[] octets = Encoding.ASCII.GetBytes(cle);
        public string Algorithme => "HMAC-SHA256";
        public string EmpreinteCle => Convert.ToHexStringLower(SHA256.HashData(octets))[..16];
        public byte[] Sceller(byte[] message) => HMACSHA256.HashData(octets, message);
        public bool Verifier(byte[] message, byte[] sceau) => CryptographicOperations.FixedTimeEquals(Sceller(message), sceau);
    }

    private sealed class AlertesFactices : IAlertesTitulaire
    {
        public List<(string Message, bool Urgent)> Messages { get; } = [];
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) { Messages.Add((message, urgent)); return Task.CompletedTask; }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class JournalFactice : IJournal
    {
        public List<string> Actions { get; } = [];
        public Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId,
            IReadOnlyDictionary<string, object?> details, string? permissionCode, CancellationToken ct = default)
        { Actions.Add(action); return Task.CompletedTask; }
    }

    private sealed class DepotFactice : IIntegriteDepot
    {
        private readonly Dictionary<string, (long Rang, byte[] Hash)> chaines = [];
        public List<SceauEnregistre> Sceaux { get; } = [];
        public List<NouvelleVerification> Verifications { get; } = [];
        public List<IReadOnlyDictionary<string, object?>> RupturesSysteme { get; } = [];
        public ResultatChaine? Rupture { get; set; }

        public void Chaine(string cle, long rang, byte[] hash) => chaines[cle] = (rang, hash);

        public Task<IReadOnlyList<JournalChaine>> JournauxAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JournalChaine>>([new JournalChaine(Journal, "Essai", "sha256", Le)]);

        public Task<IReadOnlyList<ResultatChaine>> VerifierChaineAsync(string journal, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ResultatChaine>>(Rupture is { } r ? [r]
                : chaines.Select(c => new ResultatChaine(c.Key, c.Value.Rang, c.Value.Rang, c.Value.Hash, null, null, null, null, null)).ToList());

        public Task<MaillonChaine> DernierMaillonAsync(string journal, string cleChaine, CancellationToken ct = default) =>
            Task.FromResult(new MaillonChaine(chaines[cleChaine].Rang, chaines[cleChaine].Rang * 10, chaines[cleChaine].Hash));

        public Task<byte[]?> HashAuRangAsync(string journal, string cleChaine, long rang, CancellationToken ct = default) =>
            Task.FromResult(chaines.TryGetValue(cleChaine, out var c) && rang <= c.Rang ? Hash(rang) : null);

        public Task<long> EnregistrerSceauAsync(NouveauSceau s, CancellationToken ct = default)
        {
            Sceaux.Add(new SceauEnregistre(Sceaux.Count + 1, s.Journal, s.CleChaine, s.Motif, s.PeriodeDu, s.PeriodeAu, s.ScelleLeUtc, s.DernierRang,
                s.DernierId, s.DernierHash, s.Algorithme, s.EmpreinteCle, s.Sceau, s.AuteurId));
            return Task.FromResult((long)Sceaux.Count);
        }

        public Task<IReadOnlyList<SceauEnregistre>> SceauxAsync(string journal, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SceauEnregistre>>(Sceaux.ToList());

        public Task EnregistrerVerificationAsync(NouvelleVerification v, CancellationToken ct = default) { Verifications.Add(v); return Task.CompletedTask; }
        public Task<IReadOnlyList<DerniereVerification>> DernieresVerificationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DerniereVerification>>([]);
        public Task<ParametresIntegrite> ParametresAsync(CancellationToken ct = default) => Task.FromResult(new ParametresIntegrite("sha256", 3, "hex"));
        public Task JournaliserRuptureSystemeAsync(string journal, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default)
        { RupturesSysteme.Add(details); return Task.CompletedTask; }
    }
}
