using System.Security.Cryptography.X509Certificates;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Commun;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Synchronisation, lot S2 : envoi des lots, accusés, reprise, activation et réglages (sans réseau ni base).</summary>
public class EnvoiSynchronisationTests
{
    private static readonly Guid Officine = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Maintenant = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    private sealed class FormatV1 : IFormatLotSync
    {
        public string VersionContrat => ContratSync.VersionV1;
        public int TailleOctets(LigneSortieSync l) => SerialisationSyncV1.TailleLigne(Dto(l));
        public LotFormule Formuler(EnTeteLotSync e, IReadOnlyList<LigneSortieSync> lignes)
        {
            var lot = SerialisationSyncV1.AvecEmpreinte(new LotSyncDto(
                new EnTeteLotSyncDto(e.VersionContrat, e.OfficineId, e.Epoque, e.NumeroLot, lignes.Count, ""), lignes.Select(Dto).ToList()));
            return new LotFormule(SerialisationSyncV1.Serialiser(lot), lot.EnTete.Empreinte);
        }
        private static LigneSyncDto Dto(LigneSortieSync l) => new(l.Numero, l.Table, l.IdLocal, l.Operation, l.SurvenuLeUtc, l.DonneesJson);
    }

    /// <summary>File, lots et réglages en mémoire (sync_sortie, sync_lot, sync_parametre).</summary>
    private sealed class Base : ISortieSyncDepot, IEnvoiSyncDepot
    {
        public ParametresEnvoiSync P = new(true, Officine, 1, ContratSync.VersionV1, 0, 0, "https://central:5090", Maintenant.AddDays(-1),
            null, null, null, null, "filtres", false, 0, 0);
        public readonly List<LigneSortieSync> Sortie = [];
        public readonly Dictionary<long, long> Rattachement = [];
        public readonly List<(LotEnregistre Lot, bool Accuse)> Lots = [];
        public readonly List<string?> Envois = [];

        public void Ajouter(int n) => Sortie.AddRange(Enumerable.Range(Sortie.Count + 1, n).Select(i =>
            new LigneSortieSync(i, "vente", i.ToString(), ContratSync.Creation, Maintenant, $$"""{"id":{{i}}}""")));

        public Task<EtatSync> EtatAsync(CancellationToken ct = default) => Task.FromResult(new EtatSync(P.Active, P.OfficineId, P.Epoque,
            P.VersionContrat, P.DernierLot, P.DernierAccuse, 2, 1_048_576, Sortie.Count(l => !Rattachement.ContainsKey(l.Numero))));
        public Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => !Rattachement.ContainsKey(l.Numero)).Take(maximum).ToList());
        public Task<bool> EnregistrerLotAsync(LotConstitue lot, CancellationToken ct = default)
        {
            foreach (var l in lot.Lignes) Rattachement[l] = lot.Numero;
            Lots.Add((new LotEnregistre(P.Epoque, lot.Numero, P.VersionContrat, lot.Empreinte), false));
            P = P with { DernierLot = lot.Numero };
            return Task.FromResult(true);
        }

        public Task<ParametresEnvoiSync> ParametresAsync(CancellationToken ct = default) => Task.FromResult(P);
        public Task<IReadOnlyList<LotEnregistre>> LotsNonAccusesAsync(int maximum, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LotEnregistre>>(Lots.Where(l => !l.Accuse).Select(l => l.Lot).OrderBy(l => l.Numero).Take(maximum).ToList());
        public Task<IReadOnlyList<LigneSortieSync>> LignesDuLotAsync(int epoque, long numero, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => Rattachement.TryGetValue(l.Numero, out var n) && n == numero).ToList());
        public Task EnregistrerAccuseAsync(int epoque, long numero, CancellationToken ct = default)
        {
            for (var i = 0; i < Lots.Count; i++) if (Lots[i].Lot.Numero <= numero) Lots[i] = (Lots[i].Lot, true);
            P = P with { DernierAccuse = Math.Max(P.DernierAccuse, numero) };
            return Task.CompletedTask;
        }
        public Task NoterEnvoiAsync(string? erreur, CancellationToken ct = default) { Envois.Add(erreur); P = P with { DerniereErreur = erreur }; return Task.CompletedTask; }
        public Task EnregistrerInscriptionAsync(Guid officine, CancellationToken ct = default) { P = P with { InscriteLe = Maintenant }; return Task.CompletedTask; }
        public Task ReglerAsync(string? a, string t, bool m, bool active, CancellationToken ct = default)
        {
            P = P with { Adresse = a, TextesLibres = t, EnvoyerMedecins = m, Active = active };
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<LotEnregistre>> LotsEntreAsync(int epoque, long du, long au, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LotEnregistre>>(Lots.Select(l => l.Lot).Where(l => l.Numero >= du && l.Numero <= au).OrderBy(l => l.Numero).ToList());
    }

    private sealed class Certificat : ICertificatSync
    {
        public CertificatSyncInfo? Info = new("ab".PadRight(64, '0'), Maintenant.AddYears(3), "cd".PadRight(64, '0'));
        public int Demandes;
        public string? Installe, Epinglage;
        public CertificatSyncInfo? Lire() => Info;
        public string NouvelleDemande() { Demandes++; return "CSR"; }
        public void Installer(string certificatPem, string autoritePem, string? epinglage)
        {
            (Installe, Epinglage) = (certificatPem, epinglage);
            Info = new("ef".PadRight(64, '0'), Maintenant.AddYears(3), "cd".PadRight(64, '0'));
        }
        public X509Certificate2? CertificatClient() => null;
        public X509Certificate2? Autorite() => null;
    }

    /// <summary>Serveur central simulé : n° = dernier + 1 par époque, idempotent, vérifie l'empreinte.</summary>
    private sealed class Central : IClientServeurCentral
    {
        public long Dernier;
        public readonly Dictionary<long, string> Recus = [];
        public bool Coupure;
        public Exception? Panne;
        public long? NumeroAccuseFaux;
        public Guid OfficineRendue = Officine;
        public int Renouvellements;

        public Task<ReponseActivationSync> ActiverAsync(string adresse, string code, string demandePem, Guid officine, CancellationToken ct = default) =>
            Task.FromResult(new ReponseActivationSync(OfficineRendue, "principal", "CERT", "AC", Maintenant.AddYears(3), "0123"));

        public Task<AccuseLotSync> DeposerAsync(string adresse, byte[] contenu, CancellationToken ct = default)
        {
            if (Coupure) throw new HttpRequestException("Connexion impossible");
            if (Panne is not null) throw Panne;
            var lot = SerialisationSyncV1.Lire(contenu);
            Assert.True(SerialisationSyncV1.EmpreinteValide(lot));
            var n = lot.EnTete.NumeroLot;
            if (n <= Dernier) return Task.FromResult(new AccuseLotSync(1, n, Recus[n] == lot.EnTete.Empreinte ? StatutsLotSync.DejaRecu : StatutsLotSync.EmpreinteDifferente, Dernier));
            if (n > Dernier + 1) return Task.FromResult(new AccuseLotSync(1, n, StatutsLotSync.Trou, Dernier));
            Recus[n] = lot.EnTete.Empreinte;
            Dernier = n;
            return Task.FromResult(new AccuseLotSync(1, NumeroAccuseFaux ?? n, StatutsLotSync.Accepte, Dernier));
        }

        public Task<ReponseActivationSync> RenouvelerAsync(string adresse, string demandePem, CancellationToken ct = default)
        {
            Renouvellements++;
            return Task.FromResult(new ReponseActivationSync(Officine, "principal", "CERT2", "AC", Maintenant.AddYears(3), null));
        }
    }

    private static EnvoyerLotsSync Envoi(Base b, Certificat c, Central s, VerrouSync? v = null)
    {
        var f = new FormatV1();
        return new EnvoyerLotsSync(b, new ConstitutionLotsSync(b, f), f, c, s, v ?? new VerrouSync());
    }

    [Fact]
    public async Task Rien_ne_part_si_desactivee_sans_adresse_ou_non_activee()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(3);
        b.P = b.P with { Active = false };
        Assert.Equal(EnvoyerLotsSync.ArretDesactivee, (await Envoi(b, c, s).ExecuterAsync(Maintenant)).Arret);
        b.P = b.P with { Active = true, Adresse = null };
        Assert.Equal(EnvoyerLotsSync.ArretSansAdresse, (await Envoi(b, c, s).ExecuterAsync(Maintenant)).Arret);
        b.P = b.P with { Adresse = "https://central:5090" };
        c.Info = null;
        Assert.Equal(EnvoyerLotsSync.ArretNonActivee, (await Envoi(b, c, s).ExecuterAsync(Maintenant)).Arret);
        Assert.Empty(b.Lots);
        Assert.Equal(0, s.Dernier);
    }

    [Fact]
    public async Task Les_lots_partent_dans_l_ordre_et_sont_accuses()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(5); // lots de 2 lignes : 3 lots
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(3, r.LotsAccuses);
        Assert.Equal(3, s.Dernier);
        Assert.Equal(3, b.P.DernierAccuse);
        Assert.All(b.Lots, l => Assert.True(l.Accuse));
        Assert.Null(b.Envois[^1]);
    }

    [Fact]
    public async Task Apres_une_coupure_rien_n_est_perdu_et_le_lot_renvoye_est_identique()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(4);
        s.Coupure = true;
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.NotNull(r.Erreur);
        Assert.Equal(2, b.Lots.Count);
        Assert.All(b.Lots, l => Assert.False(l.Accuse));
        Assert.Contains("Connexion impossible", b.P.DerniereErreur);
        // Le serveur central a reçu le lot 1 mais l'accusé s'est perdu : renvoi à l'identique, accusé « déjà reçu »
        s.Coupure = false;
        s.Recus[1] = b.Lots[0].Lot.Empreinte;
        s.Dernier = 1;
        b.Ajouter(1);
        r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(3, r.LotsAccuses);
        Assert.Equal(3, s.Dernier);
        Assert.Equal(5, b.Sortie.Count(l => b.Rattachement.ContainsKey(l.Numero)));
    }

    [Fact]
    public async Task Un_trou_cote_central_est_repris_automatiquement()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(4);
        await new ConstitutionLotsSync(b, new FormatV1()).ConstituerAsync();
        b.Lots[0] = (b.Lots[0].Lot, true); // lot 1 cru accusé, mais le serveur central ne l'a pas (restauration du serveur central)
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);          // reprise automatique : lot 1 renvoyé depuis sync_lot, puis lot 2
        Assert.Equal(2, s.Dernier);
        Assert.True(b.Lots[1].Accuse);
    }

    [Fact]
    public async Task Un_trou_dont_les_lots_ne_sont_plus_disponibles_arrete_l_envoi_avec_la_procedure()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(4);
        await new ConstitutionLotsSync(b, new FormatV1()).ConstituerAsync();
        b.Lots.RemoveAt(0); // lot 1 accusé puis purgé ici, absent du serveur central
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Contains("procédure", r.Erreur);
        Assert.True(r.Bloquant);
        Assert.Equal(0, s.Dernier);
    }

    [Fact]
    public async Task Un_accuse_pour_un_autre_lot_n_est_pas_enregistre()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(2);
        s.NumeroAccuseFaux = 9;
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Contains("incohérent", r.Erreur);
        Assert.All(b.Lots, l => Assert.False(l.Accuse));
    }

    [Fact]
    public async Task Un_seul_passage_a_la_fois_et_rien_sur_une_base_en_lecture_seule()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(2);
        var v = new VerrouSync();
        Assert.True(v.Prendre());
        Assert.Equal(EnvoyerLotsSync.ArretEnCours, (await Envoi(b, c, s, v).ExecuterAsync(Maintenant)).Arret);
        await Assert.ThrowsAsync<RegleMetierException>(() => new ActiverSync(b, c, s, new JournalEspion(), v).ExecuterAsync("CODE", 1));
        v.Rendre();
        b.P = b.P with { BaseEnLectureSeule = true };
        Assert.Equal(EnvoyerLotsSync.ArretLectureSeule, (await Envoi(b, c, s, v).ExecuterAsync(Maintenant)).Arret);
        Assert.Empty(b.Lots);
    }

    [Fact]
    public async Task Une_panne_imprevue_est_notee_sans_detail_et_le_lot_reste_en_attente()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(2);
        s.Panne = new InvalidOperationException("valeur sensible 0550123456");
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Contains("InvalidOperationException", r.Erreur);
        Assert.DoesNotContain("0550123456", r.Erreur);
        Assert.All(b.Lots, l => Assert.False(l.Accuse));
        s.Panne = new ServeurCentralException("refusé", 422);
        Assert.True((await Envoi(b, c, s).ExecuterAsync(Maintenant)).Bloquant);
    }

    [Fact]
    public async Task Un_lot_dont_le_contenu_ne_correspond_plus_a_son_empreinte_n_est_pas_envoye()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(2);
        await new ConstitutionLotsSync(b, new FormatV1()).ConstituerAsync();
        b.Lots[0] = (b.Lots[0].Lot with { Empreinte = new string('0', 64) }, false);
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Contains("empreinte", r.Erreur);
        Assert.Equal(0, s.Dernier);
    }

    [Fact]
    public async Task Certificat_proche_de_l_echeance_renouvele_et_certificat_echu_refuse()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        c.Info = c.Info! with { EcheanceUtc = Maintenant.AddDays(30) };
        Assert.Null((await Envoi(b, c, s).ExecuterAsync(Maintenant)).Erreur);
        Assert.Equal(1, s.Renouvellements);
        Assert.Equal("CERT2", c.Installe);
        Assert.Null(c.Epinglage);
        c.Info = c.Info! with { EcheanceUtc = Maintenant.AddDays(-1) };
        Assert.Contains("échu", (await Envoi(b, c, s).ExecuterAsync(Maintenant)).Erreur);
    }

    [Fact]
    public async Task Activation_installe_le_certificat_et_refuse_une_autre_officine()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.P = b.P with { InscriteLe = null };
        var journal = new JournalEspion();
        var info = await new ActiverSync(b, c, s, journal, new VerrouSync()).ExecuterAsync("CODE", 7);
        Assert.Equal("CERT", c.Installe);
        Assert.Equal("0123", c.Epinglage);
        Assert.NotNull(b.P.InscriteLe);
        Assert.Equal("ef", info.Empreinte[..2]);
        Assert.Contains("synchronisation_activee", journal.Actions);

        s.OfficineRendue = Guid.NewGuid();
        c.Installe = null;
        await Assert.ThrowsAsync<RegleMetierException>(() => new ActiverSync(b, c, s, journal, new VerrouSync()).ExecuterAsync("CODE", 7));
        Assert.Null(c.Installe);
        b.P = b.P with { Adresse = null };
        await Assert.ThrowsAsync<RegleMetierException>(() => new ActiverSync(b, c, s, journal, new VerrouSync()).ExecuterAsync("CODE", 7));
    }

    [Theory]
    [InlineData("https://central.exemple.dz", true)]
    [InlineData("https://127.0.0.1:5090/", true)]
    [InlineData("", true)]
    [InlineData("http://central.exemple.dz", false)]
    [InlineData("https://central.exemple.dz/chemin", false)]
    [InlineData("https://a:b@central.exemple.dz", false)]
    [InlineData("central", false)]
    public void Adresse_du_serveur_central_en_https_seulement(string adresse, bool valide) =>
        Assert.Equal(valide, ReglerSync.AdresseValide(adresse));

    [Fact]
    public async Task Reglages_appliques_et_journalises()
    {
        var b = new Base();
        b.P = b.P with { Active = false, Adresse = null };
        var journal = new JournalEspion();
        await new ReglerSync(b, journal).ExecuterAsync(new ReglagesSync(true, "https://central:5090/", "retires", true), 3);
        Assert.True(b.P.Active);
        Assert.Equal("https://central:5090", b.P.Adresse);
        Assert.Equal("retires", b.P.TextesLibres);
        Assert.True(b.P.EnvoyerMedecins);
        Assert.Equal(["reglages_synchronisation"], journal.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() => new ReglerSync(b, journal).ExecuterAsync(new ReglagesSync(true, "http://x", "filtres", false), 3));
        await Assert.ThrowsAsync<RegleMetierException>(() => new ReglerSync(b, journal).ExecuterAsync(new ReglagesSync(true, null, "tout", false), 3));
    }
}
