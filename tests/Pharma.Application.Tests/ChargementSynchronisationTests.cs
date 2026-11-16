using System.Security.Cryptography.X509Certificates;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Commun;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Synchronisation, lot S6 : chargement complet (ordre d'envoi, étapes, lots manquants, demande) sans réseau ni base.</summary>
public class ChargementSynchronisationTests
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
        private static LigneSyncDto Dto(LigneSortieSync l) => new(l.NumeroEnvoye, l.Table, l.IdLocal, l.Operation, l.SurvenuLeUtc, l.DonneesJson);
    }

    private static LigneSortieSync Ligne(long n, string op = ContratSync.Modification, long? ordre = null) =>
        new(n, "vente", n.ToString(), op, Maintenant, $$"""{"id":{{n}}}""", ordre);

    /// <summary>File, lots, réglages et chargements en mémoire.</summary>
    private sealed class Base : ISortieSyncDepot, IEnvoiSyncDepot, IChargementSyncDepot
    {
        public ParametresEnvoiSync P = new(true, Officine, 1, ContratSync.VersionV1, 0, 0, "https://central:5090", Maintenant.AddDays(-1),
            null, null, null, null, "filtres", false, 0, 0);
        public readonly List<LigneSortieSync> Sortie = [];
        public readonly Dictionary<long, long> Rattachement = [];
        public readonly List<(LotEnregistre Lot, bool Accuse)> Lots = [];
        public readonly List<string?> Envois = [];
        public readonly List<ChargementSync> Chargements = [];
        public readonly List<string> Demandes = [];
        public int TablesInstantane = 2;
        public Exception? PanneInstantane;

        // ISortieSyncDepot
        public Task<EtatSync> EtatAsync(CancellationToken ct = default) => Task.FromResult(new EtatSync(P.Active, P.OfficineId, P.Epoque,
            P.VersionContrat, P.DernierLot, P.DernierAccuse, 3, 1_048_576, Sortie.Count(l => !Rattachement.ContainsKey(l.Numero))));
        public Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, IReadOnlyList<string> familles, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => !Rattachement.ContainsKey(l.Numero))
                .OrderBy(l => l.NumeroEnvoye).ThenBy(l => l.Numero).Take(maximum).ToList());
        public Task<bool> EnregistrerLotAsync(LotConstitue lot, CancellationToken ct = default)
        {
            if (Chargements.Any(c => c.Etat == EtatsChargement.Constitution)) return Task.FromResult(false);
            foreach (var l in lot.Lignes) Rattachement[l] = lot.Numero;
            Lots.Add((new LotEnregistre(P.Epoque, lot.Numero, P.VersionContrat, lot.Empreinte), false));
            P = P with { DernierLot = lot.Numero };
            return Task.FromResult(true);
        }

        // IEnvoiSyncDepot
        public Task<ParametresEnvoiSync> ParametresAsync(CancellationToken ct = default) => Task.FromResult(P);
        public Task<IReadOnlyList<LotEnregistre>> LotsNonAccusesAsync(int maximum, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LotEnregistre>>(Lots.Where(l => !l.Accuse).Select(l => l.Lot).OrderBy(l => l.Numero).Take(maximum).ToList());
        public Task<IReadOnlyList<LotEnregistre>> LotsEntreAsync(int epoque, long du, long au, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LotEnregistre>>(Lots.Select(l => l.Lot).Where(l => l.Numero >= du && l.Numero <= au).OrderBy(l => l.Numero).ToList());
        public Task<IReadOnlyList<LigneSortieSync>> LignesDuLotAsync(int epoque, long numero, IReadOnlyList<string>? familles, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => Rattachement.TryGetValue(l.Numero, out var n) && n == numero)
                .OrderBy(l => l.NumeroEnvoye).ThenBy(l => l.Numero).ToList());
        public Task<IReadOnlyList<string>> AppliquerDescendantsAsync(IReadOnlyList<ChangementDescendantSync> changements, long versionMax, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(changements.Select(_ => StatutsDescendant.Ancien).ToList());
        public Task NoterVersionDescendanteAsync(long version, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReformerLotAsync(int epoque, long numero, string empreinte, int octets, IReadOnlyList<string> familles, CancellationToken ct = default) =>
            throw new InvalidOperationException("aucune famille nominative dans ces essais");
        public Task EnregistrerAccuseAsync(int epoque, long numero, CancellationToken ct = default)
        {
            for (var i = 0; i < Lots.Count; i++) if (Lots[i].Lot.Numero <= numero) Lots[i] = (Lots[i].Lot, true);
            P = P with { DernierAccuse = Math.Max(P.DernierAccuse, numero) };
            return Task.CompletedTask;
        }
        public Task NoterEnvoiAsync(string? erreur, CancellationToken ct = default) { Envois.Add(erreur); P = P with { DerniereErreur = erreur }; return Task.CompletedTask; }
        public readonly List<string?> Passages = [];
        public Task NoterPassageAsync(string? natureErreur, CancellationToken ct = default) { Passages.Add(natureErreur); return Task.CompletedTask; }
        public Task EnregistrerInscriptionAsync(Guid officine, CancellationToken ct = default) { P = P with { InscriteLe = Maintenant }; return Task.CompletedTask; }
        public Task ReglerAsync(string? a, string t, bool m, bool active, CancellationToken ct = default)
        {
            P = P with { Adresse = a, TextesLibres = t, EnvoyerMedecins = m, Active = active };
            return Task.CompletedTask;
        }

        // IChargementSyncDepot
        private int Index(int n) => Chargements.FindIndex(c => c.Numero == n);
        private bool Accuse(long id) => Rattachement.TryGetValue(id, out var lot) && Lots.Any(l => l.Lot.Numero == lot && l.Accuse);
        public Task<ChargementSync?> DernierAsync(CancellationToken ct = default)
        {
            var c = Chargements.Where(c => EtatsChargement.EnCours(c.Etat)).Concat(Chargements.AsEnumerable().Reverse()).FirstOrDefault();
            return Task.FromResult(c is null ? null : c with { LignesAccusees = Sortie.Count(l => l.Ordre is not null && l.Ordre == c.Repere && Accuse(l.Numero)) });
        }
        public Task<int?> DemanderAsync(string motif, long? utilisateurId, CancellationToken ct = default)
        {
            Demandes.Add(motif);
            var c = Chargements.FirstOrDefault(c => EtatsChargement.EnCours(c.Etat));
            if (c is not null)
            {
                if (c.Etat is EtatsChargement.Demande or EtatsChargement.Constitution) return Task.FromResult<int?>(c.Numero);
                if (motif is MotifsChargement.Demande or MotifsChargement.Activation) return Task.FromResult<int?>(null);
                Chargements[Index(c.Numero)] = c with { Etat = EtatsChargement.Remplace };
            }
            var n = Chargements.Count + 1;
            Chargements.Add(new ChargementSync(n, motif, EtatsChargement.Demande, Maintenant, null, null, new Dictionary<string, long>(), 0, 0, null, null));
            return Task.FromResult<int?>(n);
        }
        public Task ConstituerAsync(int numero, Action<int, int>? progression, CancellationToken ct = default)
        {
            if (PanneInstantane is not null) throw PanneInstantane;
            var repere = Sortie.Count == 0 ? 0 : Sortie.Max(l => l.Numero);
            // Deux ventes existantes (100 et 101) : lignes « instantane », ordre = repère
            var lignes = new Dictionary<string, long> { ["patient"] = 0, ["vente"] = 2 };
            foreach (var id in new long[] { 100, 101 })
                Sortie.Add(new LigneSortieSync(Sortie.Count == 0 ? 1 : Sortie.Max(l => l.Numero) + 1, "vente", id.ToString(), ContratSync.Instantane,
                    Maintenant, $$"""{"id":{{id}}}""", repere));
            for (var i = 1; i <= TablesInstantane; i++) progression?.Invoke(i, TablesInstantane);
            Chargements[Index(numero)] = Chargements[Index(numero)] with { Etat = EtatsChargement.Pret, Epoque = P.Epoque, Repere = repere, Lignes = lignes, LignesTotal = 2 };
            return Task.CompletedTask;
        }
        public Task MarquerAnnonceAsync(int numero, long reprendreApres, CancellationToken ct = default)
        {
            Chargements[Index(numero)] = Chargements[Index(numero)] with { Etat = EtatsChargement.Annonce };
            return Task.CompletedTask;
        }
        public Task<bool> ToutAccuseAsync(int numero, CancellationToken ct = default)
        {
            var c = Chargements[Index(numero)];
            return Task.FromResult(Sortie.Where(l => l.Operation == ContratSync.Instantane && l.Ordre == c.Repere).All(l => Accuse(l.Numero)));
        }
        public Task TerminerAsync(int numero, long absentes, IReadOnlyDictionary<string, long> parTable, CancellationToken ct = default)
        {
            Chargements[Index(numero)] = Chargements[Index(numero)] with { Etat = EtatsChargement.Termine, TermineLe = Maintenant, AbsentesCentral = absentes };
            return Task.CompletedTask;
        }
    }

    private sealed class Certificat : ICertificatSync
    {
        public CertificatSyncInfo? Lire() => new("ab".PadRight(64, '0'), Maintenant.AddYears(3), "cd".PadRight(64, '0'));
        public string NouvelleDemande() => "CSR";
        public void Installer(string certificatPem, string autoritePem, string? epinglage) { }
        public X509Certificate2? CertificatClient() => null;
        public X509Certificate2? Autorite() => null;
    }

    /// <summary>Serveur central simulé : lots dans l'ordre, état final par (n° le plus grand), chargement (début, saut, fin).</summary>
    private sealed class Central : IClientServeurCentral, IClientChargementCentral
    {
        public long Dernier;
        public readonly Dictionary<long, string> Recus = [];
        public readonly Dictionary<string, (long N, string Op)> Etat = [];
        public readonly List<string> Ordre = [];
        public readonly List<DebutChargementSync> Debuts = [];
        public readonly List<int> Fins = [];
        public long AbsentesARendre;
        public ServeurCentralException? Panne;

        public Task<ReponseActivationSync> ActiverAsync(string adresse, string code, string demandePem, Guid officine, CancellationToken ct = default) =>
            Task.FromResult(new ReponseActivationSync(Officine, "principal", "CERT", "AC", Maintenant.AddYears(3), "0123"));
        public Task<DescendantsRecus> EchangerAsync(string adresse, long versionAppliquee, CancellationToken ct = default) => Task.FromResult(DescendantsRecus.Aucun);

        public Task<ReponseActivationSync> RenouvelerAsync(string adresse, string demandePem, CancellationToken ct = default) =>
            Task.FromResult(new ReponseActivationSync(Officine, "principal", "CERT", "AC", Maintenant.AddYears(3), null));

        public Task<AccuseLotSync> DeposerAsync(string adresse, byte[] contenu, long versionDescendante, CancellationToken ct = default)
        {
            if (Panne is not null) throw Panne;
            var lot = SerialisationSyncV1.Lire(contenu);
            var n = lot.EnTete.NumeroLot;
            if (n <= Dernier) return Task.FromResult(new AccuseLotSync(1, n, StatutsLotSync.DejaRecu, Dernier));
            if (n > Dernier + 1) return Task.FromResult(new AccuseLotSync(1, n, StatutsLotSync.Trou, Dernier));
            foreach (var l in lot.Lignes)
            {
                Assert.True(l.Operation != ContratSync.Instantane || Debuts.Count > 0, "ligne d'instantané reçue avant l'annonce du chargement");
                Ordre.Add($"{l.Operation}:{l.Id}:{l.Numero}");
                if (!Etat.TryGetValue(l.Id, out var e) || e.N < l.Numero) Etat[l.Id] = (l.Numero, l.Operation);
            }
            Recus[n] = lot.EnTete.Empreinte;
            Dernier = n;
            return Task.FromResult(new AccuseLotSync(1, n, StatutsLotSync.Accepte, Dernier));
        }

        public Task<ReponseDebutChargementSync> DebuterAsync(string adresse, DebutChargementSync d, CancellationToken ct = default)
        {
            Debuts.Add(d);
            var abandonnes = Math.Max(0, d.ReprendreApres - Dernier);
            Dernier = Math.Max(Dernier, d.ReprendreApres);
            return Task.FromResult(new ReponseDebutChargementSync(d.Numero, Dernier, abandonnes));
        }

        public Task<ReponseFinChargementSync> TerminerAsync(string adresse, int numero, IReadOnlyDictionary<string, long> lignes, CancellationToken ct = default)
        {
            Fins.Add(numero);
            return Task.FromResult(new ReponseFinChargementSync(numero, AbsentesARendre, new Dictionary<string, long> { ["vente"] = AbsentesARendre }));
        }
    }

    private static (EnvoyerLotsSync Envoi, SuiviChargementSync Suivi) Envoi(Base b, Central s)
    {
        var f = new FormatV1();
        var suivi = new SuiviChargementSync();
        return (new EnvoyerLotsSync(b, new ConstitutionLotsSync(b, f), f, new Certificat(), s, new VerrouSync(),
            new ChargementCompletSync(b, s, suivi)), suivi);
    }

    [Fact]
    public void Les_lots_sont_formes_par_ordre_puis_par_numero()
    {
        var c = new ConstitutionLotsSync(new Base(), new FormatV1());
        // Modification 8 (antérieure au repère 8), instantané inscrit en n° 10 (ordre 8), modification 9 (postérieure)
        var choisies = c.Borner([Ligne(10, ContratSync.Instantane, 8), Ligne(9), Ligne(8)], 10, 1_048_576);
        Assert.Equal([8L, 10L, 9L], choisies.Select(l => l.Numero));
        Assert.Equal([8L, 8L, 9L], choisies.Select(l => l.NumeroEnvoye));
    }

    [Fact]
    public async Task Chargement_complet_annonce_puis_envoye_dans_l_ordre_puis_termine()
    {
        var (b, s) = (new Base(), new Central());
        b.Sortie.Add(Ligne(1)); // modification antérieure, en attente
        await b.DemanderAsync(MotifsChargement.Activation, 7);
        s.AbsentesARendre = 3;
        var (envoi, _) = Envoi(b, s);
        var r = await envoi.ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Single(s.Debuts);
        Assert.Equal((1, 1L, 0L), (s.Debuts[0].Numero, s.Debuts[0].Repere, s.Debuts[0].ReprendreApres));
        Assert.Equal(["patient", "vente"], s.Debuts[0].Tables);
        Assert.Equal(["modification:1:1", "instantane:100:1", "instantane:101:1"], s.Ordre);
        Assert.Equal([1], s.Fins);
        var c = (await b.DernierAsync())!;
        Assert.Equal((EtatsChargement.Termine, 3L), (c.Etat, c.AbsentesCentral));
        // Un passage suivant ne réannonce ni ne termine de nouveau
        b.Sortie.Add(Ligne(b.Sortie.Max(l => l.Numero) + 1));
        Assert.Null((await envoi.ExecuterAsync(Maintenant)).Erreur);
        Assert.Single(s.Debuts);
        Assert.Single(s.Fins);
    }

    [Fact]
    public async Task Une_modification_posterieure_gagne_sur_l_instantane_au_serveur_central()
    {
        var (b, s) = (new Base(), new Central());
        await b.DemanderAsync(MotifsChargement.Demande, 1);
        await b.ConstituerAsync(1, null);                       // repère 0 : instantané de 100 et 101 (n° envoyé 0)
        b.Sortie.Add(new LigneSortieSync(3, "vente", "100", ContratSync.Suppression, Maintenant, null)); // supprimée ensuite
        var (envoi, _) = Envoi(b, s);
        Assert.Null((await envoi.ExecuterAsync(Maintenant)).Erreur);
        Assert.Equal(ContratSync.Suppression, s.Etat["100"].Op);  // pas de résurrection
        Assert.Equal(ContratSync.Instantane, s.Etat["101"].Op);
        Assert.True(s.Ordre.IndexOf("instantane:100:0") < s.Ordre.IndexOf("suppression:100:3"));
    }

    [Fact]
    public async Task Pendant_la_constitution_aucun_lot_n_est_forme()
    {
        var b = new Base();
        b.Sortie.Add(Ligne(1));
        b.Chargements.Add(new ChargementSync(1, MotifsChargement.Demande, EtatsChargement.Constitution, Maintenant, null, null,
            new Dictionary<string, long>(), 0, 0, null, null));
        var r = await new ConstitutionLotsSync(b, new FormatV1()).ConstituerAsync();
        Assert.Equal(ConstitutionLotsSync.MotifSimultanee, r.Motif);
        Assert.Empty(b.Lots);
    }

    [Fact]
    public async Task Base_occupee_l_instantane_est_reporte_sans_erreur_bloquante()
    {
        var (b, s) = (new Base(), new Central());
        await b.DemanderAsync(MotifsChargement.Demande, 1);
        b.PanneInstantane = new ChargementReporteException("Instantané reporté (base occupée).");
        var r = await Envoi(b, s).Envoi.ExecuterAsync(Maintenant);
        Assert.Contains("reporté", r.Erreur);
        Assert.False(r.Bloquant);
        Assert.Empty(s.Debuts);
        b.PanneInstantane = null;
        Assert.Null((await Envoi(b, s).Envoi.ExecuterAsync(Maintenant)).Erreur);
        Assert.Equal(EtatsChargement.Termine, (await b.DernierAsync())!.Etat);
    }

    [Fact]
    public async Task Lots_perdus_par_le_central_et_absents_ici_rechargement_complet_puis_reprise()
    {
        var (b, s) = (new Base(), new Central());
        for (var i = 1; i <= 6; i++) b.Sortie.Add(Ligne(i));
        var (envoi, _) = Envoi(b, s);
        Assert.Null((await envoi.ExecuterAsync(Maintenant)).Erreur);     // lots 1 et 2 accusés
        b.Lots.RemoveAt(0);                                              // lot 1 purgé ici
        s.Dernier = 0;                                                   // le central a tout perdu
        b.Sortie.Add(Ligne(7));
        var r = await envoi.ExecuterAsync(Maintenant);
        Assert.Contains("rechargement complet", r.Erreur);
        Assert.False(r.Bloquant);
        Assert.Equal([MotifsChargement.LotsManquants], b.Demandes);
        // Passage suivant : instantané, annonce avec reprise après le dernier lot accusé ici (saut), lots suivants acceptés
        r = await envoi.ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(2L, s.Debuts.Single().ReprendreApres);
        Assert.Equal(EtatsChargement.Termine, (await b.DernierAsync())!.Etat);
        Assert.Equal(b.P.DernierLot, s.Dernier);
    }

    [Fact]
    public async Task Lot_purge_apres_le_delai_et_perdu_par_le_central_rechargement_complet_sans_renvoi_du_lot()
    {
        var (b, s) = (new Base(), new Central());
        for (var i = 1; i <= 6; i++) b.Sortie.Add(Ligne(i));
        var (envoi, _) = Envoi(b, s);
        Assert.Null((await envoi.ExecuterAsync(Maintenant)).Erreur);     // lots 1 et 2 accusés
        // Lot 1 purgé (lignes retirées de la file, sa ligne de lot reste) ; le central a tout perdu
        b.Lots[0] = (b.Lots[0].Lot with { Purge = true }, true);
        foreach (var l in b.Rattachement.Where(x => x.Value == 1).Select(x => x.Key).ToList()) b.Sortie.RemoveAll(x => x.Numero == l);
        s.Dernier = 0;
        s.Recus.Clear();
        b.Sortie.Add(Ligne(7));
        var r = await envoi.ExecuterAsync(Maintenant);
        Assert.Contains("rechargement complet", r.Erreur);
        Assert.False(r.Bloquant);
        Assert.Equal([MotifsChargement.LotsManquants], b.Demandes);
        Assert.Empty(s.Recus);                                           // le lot purgé n'a jamais été renvoyé (ni un lot incomplet)
    }

    [Fact]
    public async Task Lot_accuse_encore_conserve_est_renvoye_a_l_identique_apres_une_restauration_du_central()
    {
        var (b, s) = (new Base(), new Central());
        for (var i = 1; i <= 6; i++) b.Sortie.Add(Ligne(i));
        var (envoi, _) = Envoi(b, s);
        Assert.Null((await envoi.ExecuterAsync(Maintenant)).Erreur);     // lots 1 et 2 accusés, lignes encore là (délai non écoulé)
        var empreintes = s.Recus.ToDictionary(x => x.Key, x => x.Value);
        s.Dernier = 0;
        s.Recus.Clear();
        b.Sortie.Add(Ligne(7));
        Assert.Null((await envoi.ExecuterAsync(Maintenant)).Erreur);
        Assert.Empty(b.Demandes);
        Assert.Equal(empreintes[1], s.Recus[1]);
        Assert.Equal(empreintes[2], s.Recus[2]);
    }

    [Fact]
    public async Task Passage_note_a_la_supervision_succes_erreur_ordinaire_et_quota()
    {
        var (b, s) = (new Base(), new Central());
        b.Sortie.Add(Ligne(1));
        var (envoi, _) = Envoi(b, s);
        s.Panne = new ServeurCentralException("serveur central : 429 Quota journalier atteint", 429, quota: true);
        Assert.NotNull((await envoi.ExecuterAsync(Maintenant)).Erreur);
        s.Panne = new ServeurCentralException("serveur central : 503", 503);
        Assert.NotNull((await envoi.ExecuterAsync(Maintenant)).Erreur);
        s.Panne = null;
        Assert.Null((await envoi.ExecuterAsync(Maintenant)).Erreur);
        Assert.Equal([NaturesErreurSync.Quota, NaturesErreurSync.Autre, null], b.Passages);
    }

    [Fact]
    public async Task Lots_manquants_malgre_un_rechargement_annonce_l_envoi_s_arrete_avec_la_procedure()
    {
        var (b, s) = (new Base(), new Central());
        b.Chargements.Add(new ChargementSync(1, MotifsChargement.LotsManquants, EtatsChargement.Annonce, Maintenant, 1, 0,
            new Dictionary<string, long> { ["vente"] = 1 }, 1, 0, null, null));
        b.Sortie.Add(new LigneSortieSync(1, "vente", "1", ContratSync.Instantane, Maintenant, """{"id":1}""", 0));
        await new ConstitutionLotsSync(b, new FormatV1()).ConstituerAsync();
        b.Sortie.Add(Ligne(2));
        await new ConstitutionLotsSync(b, new FormatV1()).ConstituerAsync();
        b.Lots.RemoveAt(0);
        var r = await Envoi(b, s).Envoi.ExecuterAsync(Maintenant);
        Assert.Contains("procédure", r.Erreur);
        Assert.True(r.Bloquant);
    }

    [Fact]
    public async Task Demande_du_titulaire_controlee_et_journalisee()
    {
        var b = new Base();
        var journal = new JournalEspion();
        var recharger = new RechargerSync(b, b, journal);
        b.P = b.P with { Active = false };
        await Assert.ThrowsAsync<RegleMetierException>(() => recharger.ExecuterAsync(5));
        b.P = b.P with { Active = true, InscriteLe = null };
        await Assert.ThrowsAsync<RegleMetierException>(() => recharger.ExecuterAsync(5));
        b.P = b.P with { InscriteLe = Maintenant };
        Assert.Equal(1, await recharger.ExecuterAsync(5));
        Assert.Contains("synchronisation_rechargement", journal.Actions);
        Assert.Equal(1, await recharger.ExecuterAsync(5)); // pas encore d'instantané : même chargement
        await b.ConstituerAsync(1, null);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => recharger.ExecuterAsync(5));
        Assert.Contains("déjà en cours", e.Message);
    }

    [Fact]
    public async Task Premiere_activation_et_reactivation_demandent_un_chargement()
    {
        var (b, s) = (new Base(), new Central());
        b.P = b.P with { InscriteLe = null };
        await new ActiverSync(b, new Certificat(), s, new JournalEspion(), new VerrouSync(), b).ExecuterAsync("CODE", 7);
        Assert.Equal([MotifsChargement.Activation], b.Demandes);
        await new ActiverSync(b, new Certificat(), s, new JournalEspion(), new VerrouSync(), b).ExecuterAsync("CODE", 7);
        Assert.Single(b.Demandes);                                    // nouveau certificat : pas de rechargement
        b.P = b.P with { Active = false };
        await new ReglerSync(b, new JournalEspion(), b).ExecuterAsync(new ReglagesSync(true, "https://central:5090", "filtres", false), 3);
        Assert.Equal(MotifsChargement.Reactivation, b.Demandes[^1]);
        await new ReglerSync(b, new JournalEspion(), b).ExecuterAsync(new ReglagesSync(true, "https://central:5090", "retires", false), 3);
        Assert.Equal(2, b.Demandes.Count);                            // déjà active : rien
    }

    [Fact]
    public async Task La_progression_de_la_constitution_est_suivie()
    {
        var (b, s) = (new Base(), new Central());
        await b.DemanderAsync(MotifsChargement.Demande, 1);
        var vus = new List<(int, int)>();
        var suivi = new SuiviChargementSync();
        var c = new ChargementCompletSync(new SuiviBase(b, (f, t) => vus.Add((f, t))), s, suivi);
        await c.PreparerAsync("https://central:5090", 0);
        Assert.Equal([(1, 2), (2, 2)], vus);
        Assert.Equal(0, suivi.Lire().Numero); // fini
    }

    /// <summary>Espionne la progression transmise au dépôt.</summary>
    private sealed class SuiviBase(Base b, Action<int, int> espion) : IChargementSyncDepot
    {
        public Task<ChargementSync?> DernierAsync(CancellationToken ct = default) => b.DernierAsync(ct);
        public Task<int?> DemanderAsync(string motif, long? u, CancellationToken ct = default) => b.DemanderAsync(motif, u, ct);
        public Task ConstituerAsync(int numero, Action<int, int>? progression, CancellationToken ct = default) =>
            b.ConstituerAsync(numero, (f, t) => { progression?.Invoke(f, t); espion(f, t); }, ct);
        public Task MarquerAnnonceAsync(int numero, long r, CancellationToken ct = default) => b.MarquerAnnonceAsync(numero, r, ct);
        public Task<bool> ToutAccuseAsync(int numero, CancellationToken ct = default) => b.ToutAccuseAsync(numero, ct);
        public Task TerminerAsync(int numero, long a, IReadOnlyDictionary<string, long> p, CancellationToken ct = default) => b.TerminerAsync(numero, a, p, ct);
    }

    [Fact]
    public void Le_contrat_accepte_les_lignes_d_instantane()
    {
        var lot = SerialisationSyncV1.AvecEmpreinte(new LotSyncDto(new EnTeteLotSyncDto(ContratSync.VersionV1, Officine, 1, 1, 2, ""),
            [new LigneSyncDto(5, "vente", "1", ContratSync.Instantane, Maintenant, """{"id":1}"""),
             new LigneSyncDto(5, "vente", "2", ContratSync.Instantane, Maintenant, """{"id":2}""")]));
        var relu = SerialisationSyncV1.Lire(SerialisationSyncV1.Serialiser(lot));
        Assert.True(SerialisationSyncV1.EmpreinteValide(relu));
        Assert.All(relu.Lignes, l => Assert.Equal(ContratSync.Instantane, l.Operation));
    }
}
