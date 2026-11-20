using System.Security.Cryptography.X509Certificates;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>
/// Synchronisation, lot S6.2 : base de l'officine revenue en arrière (restauration, bascule), avance des n°, alerte au titulaire,
/// « Repartir sur une nouvelle époque » (sans réseau ni base).
/// </summary>
public class RetourArriereSynchronisationTests
{
    private static readonly Guid Officine = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Maintenant = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

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

    /// <summary>File, lots, réglages et séquences en mémoire (comme le script 149).</summary>
    private sealed class Base : ISortieSyncDepot, IEnvoiSyncDepot
    {
        public ParametresEnvoiSync P = new(true, Officine, 1, ContratSync.VersionV1, 0, 0, "https://central:5090", Maintenant.AddDays(-1),
            null, null, null, null, "filtres", false, 0, 0);
        public readonly List<LigneSortieSync> Sortie = [];
        public readonly Dictionary<long, long> Rattachement = [];
        public readonly List<(LotEnregistre Lot, bool Accuse)> Lots = [];
        public readonly Dictionary<string, long> Sequences = new(StringComparer.Ordinal) { ["vente"] = 40, ["paiement"] = 900 };
        public int Detections, DetectionsRefus, Avances, NouvellesEpoques, ControlesDemarrage;
        public Task NoterControleDemarrageAsync(CancellationToken ct = default) { ControlesDemarrage++; return Task.CompletedTask; }
        public string? Natures;
        /// <summary>Tables dont l'avance échoue (verrou, séquence trop petite) : rendues avec une erreur, comme le script 149.</summary>
        public readonly HashSet<string> EchecsAvance = new(StringComparer.Ordinal);
        /// <summary>Journal des actions écrit dans la même transaction que la nouvelle époque.</summary>
        public readonly List<IReadOnlyDictionary<string, object?>> Journal = [];
        public (int? EpoqueCentral, int EpoqueAttendue)? DerniereDemande;

        public void Ajouter(int n) => Sortie.AddRange(Enumerable.Range(Sortie.Count + 1, n).Select(i =>
            new LigneSortieSync(i, "vente", i.ToString(), ContratSync.Creation, Maintenant, $$"""{"id":{{i}}}""")));

        public Task<EtatSync> EtatAsync(CancellationToken ct = default) => Task.FromResult(new EtatSync(P.Active, P.OfficineId, P.Epoque,
            P.VersionContrat, P.DernierLot, P.DernierAccuse, 2, 1_048_576, Sortie.Count(l => !Rattachement.ContainsKey(l.Numero)), []));
        public Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, IReadOnlyList<string> colonnes, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => !Rattachement.ContainsKey(l.Numero)).Take(maximum).ToList());
        public Task<bool> EnregistrerLotAsync(LotConstitue lot, CancellationToken ct = default)
        {
            foreach (var l in lot.Lignes) Rattachement[l] = lot.Numero;
            Lots.Add((new LotEnregistre(P.Epoque, lot.Numero, P.VersionContrat, lot.Empreinte, lot.ColonnesRetirees), false));
            P = P with { DernierLot = lot.Numero };
            return Task.FromResult(true);
        }

        public Task<ParametresEnvoiSync> ParametresAsync(CancellationToken ct = default) => Task.FromResult(P with { ColonnesInterdites = [] });
        public Task<IReadOnlyList<LotEnregistre>> LotsNonAccusesAsync(int maximum, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LotEnregistre>>(Lots.Where(l => !l.Accuse && l.Lot.Epoque == P.Epoque).Select(l => l.Lot).Take(maximum).ToList());
        public Task<IReadOnlyList<LotEnregistre>> LotsEntreAsync(int epoque, long du, long au, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LotEnregistre>>([]);
        public Task<IReadOnlyList<LigneSortieSync>> LignesDuLotAsync(int epoque, long numero, IReadOnlyList<string>? colonnes, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => Rattachement.TryGetValue(l.Numero, out var n) && n == numero).ToList());
        public Task<IReadOnlyList<string>> AppliquerDescendantsAsync(IReadOnlyList<ChangementDescendantSync> c, long versionMax, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(c.Select(_ => StatutsDescendant.Ancien).ToList());
        public Task NoterVersionDescendanteAsync(long version, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReformerLotAsync(int epoque, long numero, string empreinte, int octets, IReadOnlyList<string> colonnes, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnregistrerAccuseAsync(int epoque, long numero, CancellationToken ct = default)
        {
            for (var i = 0; i < Lots.Count; i++) if (Lots[i].Lot.Epoque == epoque && Lots[i].Lot.Numero <= numero) Lots[i] = (Lots[i].Lot, true);
            P = P with { DernierAccuse = Math.Max(P.DernierAccuse, numero) };
            return Task.CompletedTask;
        }
        public Task NoterEnvoiAsync(string? erreur, CancellationToken ct = default) { P = P with { DerniereErreur = erreur }; return Task.CompletedTask; }
        public Task NoterPassageAsync(string? nature, CancellationToken ct = default) { Natures += nature ?? "-"; return Task.CompletedTask; }
        public Task EnregistrerInscriptionAsync(Guid officine, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReglerAsync(string? a, string t, bool m, bool active, CancellationToken ct = default) => Task.CompletedTask;

        // Script 149
        public Task<bool> NoterRetourArriereAsync(long central, CancellationToken ct = default)
        {
            Detections++;
            if (central <= P.DernierLot) return Task.FromResult(false);
            if (P.RetourArriereLe is not null) { P = P with { RetourArriereCentral = Math.Max(P.RetourArriereCentral ?? 0, central) }; return Task.FromResult(false); }
            P = P with { RetourArriereLe = Maintenant, RetourArriereCentral = central, RetourArriereSequencesLe = null };
            return Task.FromResult(true);
        }
        public Task<bool> NoterRetourArriereRefusAsync(long central, CancellationToken ct = default)
        {
            DetectionsRefus++;
            if (central < 1) return Task.FromResult(false);
            if (P.RetourArriereLe is not null) { P = P with { RetourArriereCentral = Math.Max(P.RetourArriereCentral ?? 0, central) }; return Task.FromResult(false); }
            P = P with { RetourArriereLe = Maintenant, RetourArriereCentral = central, RetourArriereSequencesLe = null };
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<SequenceAvancee>> AvancerSequencesAsync(IReadOnlyDictionary<string, long> maximums, CancellationToken ct = default)
        {
            Avances++;
            var res = new List<SequenceAvancee>();
            foreach (var (table, max) in maximums.Where(m => Sequences.ContainsKey(m.Key)))
            {
                if (EchecsAvance.Contains(table)) { res.Add(new SequenceAvancee(table, null, null, "22003 valeur hors des bornes")); continue; }
                var avant = Sequences[table];
                Sequences[table] = Math.Max(avant, max);
                res.Add(new SequenceAvancee(table, avant, Sequences[table]));
            }
            // Comme le script 149 : l'avance n'est notée faite que si aucune table n'a échoué
            if (P.RetourArriereLe is not null && res.All(r => r.Erreur is null)) P = P with { RetourArriereSequencesLe = Maintenant };
            return Task.FromResult<IReadOnlyList<SequenceAvancee>>(res);
        }
        public Task<NouvelleEpoqueSync> NouvelleEpoqueAsync(long titulaireId, int? epoqueCentral, int epoqueAttendue,
            Func<NouvelleEpoqueSync, IReadOnlyDictionary<string, object?>> journal, CancellationToken ct = default)
        {
            DerniereDemande = (epoqueCentral, epoqueAttendue);
            if (P.RetourArriereLe is null) throw new InvalidOperationException("refusée en base sans détection");
            if (epoqueAttendue != P.Epoque) throw new InvalidOperationException("refusée en base : époque changée");
            NouvellesEpoques++;
            var ancienne = P.Epoque;
            var nouvelle = Math.Max(ancienne, epoqueCentral ?? 0) + 1;
            P = P with { Epoque = nouvelle, DernierLot = 0, DernierAccuse = 0, RetourArriereLe = null, RetourArriereCentral = null, RetourArriereSequencesLe = null };
            var r = new NouvelleEpoqueSync(nouvelle, ancienne, 7);
            Journal.Add(journal(r));   // même transaction que le changement d'époque
            return Task.FromResult(r);
        }

        // Script 151 (lot S6.5.4)
        public DeclarationEpoqueSync? Declaration;
        public readonly List<(int Epoque, bool? Avant, string Resultat)> Declarations = [];
        public long CompteurTicket = 480;
        public bool EchecCompteur;
        public readonly List<(int Epoque, string Cause, long Titulaire)> PrisesActe = [];
        public Task<DeclarationEpoqueSync?> DeclarationAEnvoyerAsync(CancellationToken ct = default) => Task.FromResult(Declaration);
        public Task NoterDeclarationAsync(int nouvelleEpoque, bool? avantPremierLot, string resultat, CancellationToken ct = default)
        {
            if (Declaration?.NouvelleEpoque == nouvelleEpoque) Declaration = null;   // notée une fois, comme sync_noter_declaration
            Declarations.Add((nouvelleEpoque, avantPremierLot, resultat));
            return Task.CompletedTask;
        }
        public Task<long> AvancerCompteurTicketAsync(long dernier, int epoque, CancellationToken ct = default)
        {
            if (EchecCompteur) throw new InvalidOperationException("verrou");
            var sautes = Math.Max(0, dernier - CompteurTicket);
            CompteurTicket = Math.Max(CompteurTicket, dernier);
            return Task.FromResult(sautes);
        }
        public Task PrendreActeRestaurationAsync(int nouvelleEpoque, string cause, long titulaireId, IReadOnlyDictionary<string, object?> journal,
            CancellationToken ct = default)
        {
            if (PrisesActe.Any(x => x.Epoque == nouvelleEpoque)) throw new RegleMetierException("déjà enregistrée");
            PrisesActe.Add((nouvelleEpoque, cause, titulaireId));
            Journal.Add(journal);
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

    /// <summary>Serveur central simulé : dernier lot reçu par époque, maximums par table (ou route absente : 404).</summary>
    private sealed class Central : IClientServeurCentral
    {
        public readonly Dictionary<int, long> Dernier = [];
        public Dictionary<string, long>? Maximums = new(StringComparer.Ordinal) { ["vente"] = 120, ["paiement"] = 15 };
        public bool EtatInjoignable;
        /// <summary>Plus grande époque renvoyée par /etat (null : serveur central antérieur, champ absent).</summary>
        public int? EpoqueMax;
        public int AppelsEtat, AppelsMaximums, Depots;
        /// <summary>Lot S6.5.4 : réponse à la déclaration (null : reçue ; 404, 409 : refus ; -1 : injoignable) et ordre des appels.</summary>
        public int? CodeDeclaration;
        public readonly List<string> Appels = [];
        public Task<ReponseDeclarationSync> DeclarerEpoqueAsync(string adresse, DeclarationEpoqueSync declaration, CancellationToken ct = default)
        {
            Appels.Add("declaration " + declaration.NouvelleEpoque);
            if (CodeDeclaration == -1) throw new HttpRequestException("Connexion impossible");
            if (CodeDeclaration is { } c) throw new ServeurCentralException($"serveur central : {c}", c);
            return Task.FromResult(new ReponseDeclarationSync(false, !Dernier.ContainsKey(declaration.NouvelleEpoque) || Dernier[declaration.NouvelleEpoque] == 0));
        }

        public Task<long?> DernierLotCentralAsync(string adresse, int epoque, CancellationToken ct = default)
        {
            AppelsEtat++;
            if (EtatInjoignable) throw new HttpRequestException("Connexion impossible");
            return Task.FromResult<long?>(Dernier.GetValueOrDefault(epoque));
        }
        public Task<int?> EpoqueMaxCentralAsync(string adresse, int epoque, CancellationToken ct = default)
        {
            if (EtatInjoignable) throw new HttpRequestException("Connexion impossible");
            return Task.FromResult(EpoqueMax);
        }
        public Task<IReadOnlyDictionary<string, long>> MaximumsAsync(string adresse, CancellationToken ct = default)
        {
            AppelsMaximums++;
            return Maximums is null ? throw new ServeurCentralException("serveur central : 404 Not Found", 404)
                : Task.FromResult<IReadOnlyDictionary<string, long>>(Maximums);
        }
        public Task<DescendantsRecus> EchangerAsync(string adresse, long versionAppliquee, CancellationToken ct = default) => Task.FromResult(DescendantsRecus.Aucun);
        public Task<ReponseActivationSync> ActiverAsync(string adresse, string code, string demandePem, Guid officine, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ReponseActivationSync> RenouvelerAsync(string adresse, string demandePem, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccuseLotSync> DeposerAsync(string adresse, byte[] contenu, long versionDescendante, CancellationToken ct = default)
        {
            Depots++;
            var lot = SerialisationSyncV1.Lire(contenu);
            Appels.Add($"lot {lot.EnTete.Epoque}/{lot.EnTete.NumeroLot}");
            var (e, n) = (lot.EnTete.Epoque, lot.EnTete.NumeroLot);
            if (n != Dernier.GetValueOrDefault(e) + 1) return Task.FromResult(new AccuseLotSync(e, n, StatutsLotSync.EmpreinteDifferente, Dernier.GetValueOrDefault(e)));
            Dernier[e] = n;
            return Task.FromResult(new AccuseLotSync(e, n, StatutsLotSync.Accepte, n));
        }
    }

    private static EnvoyerLotsSync Envoi(Base b, Central s)
    {
        var f = new FormatV1();
        return new EnvoyerLotsSync(b, new ConstitutionLotsSync(b, f), f, new Certificat(), s, new VerrouSync());
    }

    /// <summary>Base restaurée : 2 lots formés ici, le serveur central en a reçu 5 pour la même époque.</summary>
    private static (Base, Central) Restauree()
    {
        var (b, s) = (new Base(), new Central());
        b.P = b.P with { DernierLot = 2, DernierAccuse = 2 };
        s.Dernier[1] = 5;
        b.Ajouter(3);
        return (b, s);
    }

    [Fact]
    public async Task Central_en_avance_suspend_l_envoi_note_la_detection_une_fois_et_avance_les_numeros()
    {
        var (b, s) = Restauree();
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);

        Assert.Equal(EnvoyerLotsSync.MessageRetourArriere(5, 1, 2), r.Erreur![..EnvoyerLotsSync.MessageRetourArriere(5, 1, 2).Length]);
        Assert.Contains("jusqu’au lot 5 de l’époque 1, cette base jusqu’au lot 2", r.Erreur);
        Assert.Contains("nouvelle époque", r.Erreur);
        Assert.False(r.Bloquant);
        Assert.True(r.Suspendu);             // « Synchroniser maintenant » : avertissement, pas une erreur du serveur central
        Assert.Contains(EnvoyerLotsSync.PhraseIntegrite, r.Erreur);   // décision du titulaire (2026-11-18)
        Assert.Empty(b.Lots);                // rien formé
        Assert.Equal(0, s.Depots);           // rien envoyé
        Assert.Equal(Maintenant, b.P.RetourArriereLe);
        Assert.Equal(5, b.P.RetourArriereCentral);
        Assert.Equal(120, b.Sequences["vente"]);   // avancée au-delà du central
        Assert.Equal(900, b.Sequences["paiement"]); // jamais en arrière
        Assert.NotNull(b.P.RetourArriereSequencesLe);
        Assert.Equal(r.Erreur, b.P.DerniereErreur);
        Assert.Equal("-", b.Natures);        // pas une erreur persistante (alerte propre à la détection)

        // Passages suivants : toujours suspendu, détection et avance non refaites, central non réinterrogé
        b.Ajouter(2);
        var r2 = await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.StartsWith(EnvoyerLotsSync.MessageRetourArriere(5, 1, 2), r2.Erreur);
        Assert.EndsWith(EnvoyerLotsSync.PhraseAvanceFaite, r2.Erreur);   // la phrase sur l'avance reste tant qu'elle est vraie
        Assert.Equal(r2.Erreur, b.P.DerniereErreur);
        Assert.Equal((1, 1, 1, 0), (b.Detections, b.Avances, s.AppelsEtat, s.Depots));
        Assert.Empty(b.Lots);
    }

    /// <summary>Script 152 : l'encaissement suspendu au démarrage n'est permis que sur une réponse du serveur central (pas de retour en
    /// arrière, ou retour en arrière avec compteur et séquences avancés) ; jamais sur un central injoignable.</summary>
    [Fact]
    public async Task Controle_de_demarrage_note_seulement_sur_une_reponse_du_central()
    {
        var (b, s) = (new Base(), new Central());
        b.P = b.P with { DernierLot = 2, DernierAccuse = 2 };
        s.Dernier[1] = 2;
        s.EtatInjoignable = true;
        await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Equal(0, b.ControlesDemarrage);
        s.EtatInjoignable = false;
        await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Equal(1, b.ControlesDemarrage);

        // Retour en arrière : noté une fois le compteur des tickets et les séquences avancés
        var (b2, s2) = Restauree();
        await Envoi(b2, s2).ExecuterAsync(Maintenant);
        Assert.NotNull(b2.P.RetourArriereSequencesLe);
        Assert.Equal(1, b2.ControlesDemarrage);

        // Avance non faite (route des maximums absente) : pas noté, l'attente plafonnée s'applique
        var (b3, s3) = Restauree();
        s3.Maximums = null;
        await Envoi(b3, s3).ExecuterAsync(Maintenant);
        Assert.Null(b3.P.RetourArriereSequencesLe);
        Assert.Equal(0, b3.ControlesDemarrage);
    }

    [Fact]
    public async Task Central_egal_ou_en_retard_l_envoi_reste_normal()
    {
        var (b, s) = (new Base(), new Central());
        b.P = b.P with { DernierLot = 2, DernierAccuse = 2 };
        s.Dernier[1] = 2;
        b.Ajouter(3);
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(2, r.LotsAccuses);      // lots 3 et 4
        Assert.Null(b.P.RetourArriereLe);
        Assert.Equal(0, b.Detections);
        Assert.Equal(0, s.AppelsMaximums);

        // Central injoignable pour l'état : rien n'est formé ni envoyé à ce passage (erreur non bloquante), nouvel essai ensuite
        s.EtatInjoignable = true;
        b.Ajouter(1);
        var (lots, depots) = (b.Lots.Count, s.Depots);
        r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Equal(EnvoyerLotsSync.MessageEtatInjoignable("Connexion impossible"), r.Erreur);
        Assert.False(r.Bloquant);
        Assert.False(r.Suspendu);
        Assert.Equal((lots, depots), (b.Lots.Count, s.Depots));
        Assert.Null(b.P.RetourArriereLe);
        Assert.EndsWith(NaturesErreurSync.Autre, b.Natures);
        s.EtatInjoignable = false;
        r = await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.Null(r.Erreur);
        Assert.Equal(1, r.LotsAccuses);
    }

    [Fact]
    public async Task Etat_injoignable_puis_depot_refuse_empreinte_differente_pose_la_detection()
    {
        // Base restaurée au lot 2, puis lots 3 et suivants reformés ici AVANT toute détection (autre contenu que ceux du central)
        var (b, s) = (new Base(), new Central());
        b.P = b.P with { DernierLot = 2, DernierAccuse = 2 };
        b.Ajouter(4);
        var f = new FormatV1();
        await new ConstitutionLotsSync(b, f).ConstituerAsync(10);
        var formes = b.P.DernierLot;
        Assert.True(formes >= 3);
        s.Dernier[1] = formes;             // le central a reçu jusqu'au même n°, avec un autre contenu : /etat ne voit rien

        // 1. /etat injoignable : rien n'est envoyé à ce passage
        s.EtatInjoignable = true;
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Contains("injoignable pour le contrôle de l'état", r.Erreur);
        Assert.False(r.Bloquant);
        Assert.Equal(0, s.Depots);

        // 2. /etat répond (central = local) : le lot 3, jamais reformé, est refusé « empreinte différente » alors que le central a
        //    reçu au moins ce n° : la détection est posée, l'envoi suspendu, les n° avancés
        s.EtatInjoignable = false;
        r = await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.True(r.Suspendu);
        Assert.False(r.Bloquant);
        Assert.Equal(1, s.Depots);
        Assert.Equal(1, b.DetectionsRefus);
        Assert.Equal(0, b.Detections);
        Assert.NotNull(b.P.RetourArriereLe);
        Assert.Equal(formes, b.P.RetourArriereCentral);
        Assert.StartsWith(EnvoyerLotsSync.MessageRetourArriere(formes, 1, formes), r.Erreur);
        Assert.Equal(120, b.Sequences["vente"]);
        Assert.NotNull(b.P.RetourArriereSequencesLe);
        Assert.All(b.Lots, l => Assert.False(l.Accuse));   // rien n'est tenu pour accusé

        // 3. Passage suivant : toujours suspendu, plus aucun dépôt
        r = await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(2));
        Assert.True(r.Suspendu);
        Assert.Equal(1, s.Depots);
    }

    [Fact]
    public async Task Avance_incomplete_une_table_en_echec_les_autres_avancees_puis_nouvel_essai()
    {
        var (b, s) = Restauree();
        b.EchecsAvance.Add("paiement");
        s.Maximums = new(StringComparer.Ordinal) { ["vente"] = 120, ["paiement"] = 950 };
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Contains("Avance des n° incomplète : 1 table(s) en échec (paiement)", r.Erreur);
        Assert.Equal(120, b.Sequences["vente"]);       // l'autre table est avancée
        Assert.Null(b.P.RetourArriereSequencesLe);       // avance non notée faite

        b.EchecsAvance.Clear();
        r = await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.Contains("avancés au-delà", r.Erreur);
        Assert.Equal(950, b.Sequences["paiement"]);
        Assert.NotNull(b.P.RetourArriereSequencesLe);
        Assert.Equal(2, b.Avances);
    }

    [Fact]
    public async Task Route_des_maximums_absente_la_detection_est_faite_quand_meme_et_l_avance_reessayee()
    {
        var (b, s) = Restauree();
        s.Maximums = null; // 404 : serveur central sans le script 020
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.StartsWith(EnvoyerLotsSync.MessageRetourArriere(5, 1, 2), r.Erreur);
        Assert.Contains("Avance des n° non faite", r.Erreur);
        Assert.NotNull(b.P.RetourArriereLe);
        Assert.Null(b.P.RetourArriereSequencesLe);
        Assert.Equal(40, b.Sequences["vente"]);
        Assert.Empty(b.Lots);

        // Le serveur central est mis à jour : l'avance est faite au passage suivant, puis plus redemandée
        s.Maximums = new(StringComparer.Ordinal) { ["vente"] = 77 };
        r = await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.Contains("avancés", r.Erreur);
        Assert.Equal(77, b.Sequences["vente"]);
        await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(2));
        Assert.Equal(2, s.AppelsMaximums);
        Assert.Empty(b.Lots);
        Assert.Equal(0, s.Depots);
    }

    [Fact]
    public async Task Noms_de_table_invalides_ou_valeurs_nulles_ne_sont_pas_transmis_a_la_base()
    {
        var (b, s) = Restauree();
        s.Maximums = new(StringComparer.Ordinal) { ["vente"] = 50, ["Vente; DROP"] = 9, ["paiement"] = 0 };
        b.Sequences["Vente; DROP"] = 1;
        await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Equal(50, b.Sequences["vente"]);
        Assert.Equal(1, b.Sequences["Vente; DROP"]);
    }

    // ---- Alerte au titulaire : une par détection ----

    private sealed class Horloge(DateTime t) : IHorloge
    {
        public DateTime MaintenantUtc { get; set; } = t;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
    }

    private sealed class DepotSupervision : ISupervisionSyncDepot
    {
        public EtatSupervisionSync Etat = new(true, false, new AttenteSync(null, 0), null, null, null, new Dictionary<string, DateTime>(),
            ReglagesSupervisionSync.Defaut, RetourArriere: new RetourArriereSync(Maintenant, 5, 2, 1, true, null));
        public Task<EtatSupervisionSync> LireAsync(CancellationToken ct = default) => Task.FromResult(Etat);
        public Task NoterAlerteAsync(string nature, DateTime quandUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task NoterIntegriteSignaleeAsync(long derniereAnomalieId, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnregistrerReglagesAsync(ReglagesSupervisionSync reglages, CancellationToken ct = default) => Task.CompletedTask;
        public Task NoterAlerteRetourArriereAsync(DateTime quandUtc, CancellationToken ct = default)
        {
            Etat = Etat with { RetourArriere = Etat.RetourArriere! with { AlerteLeUtc = quandUtc } };
            return Task.CompletedTask;
        }
    }

    private sealed class Alertes : IAlertesTitulaire
    {
        public readonly List<(string Objet, string Detail, string Sms)> Envoyees = [];
        public Task SynchronisationAsync(string objet, string detail, string sms, CancellationToken ct = default)
        {
            Envoyees.Add((objet, detail, sms));
            return Task.CompletedTask;
        }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Une_seule_alerte_par_detection_puis_une_nouvelle_pour_une_nouvelle_detection()
    {
        var (d, a, h) = (new DepotSupervision(), new Alertes(), new Horloge(Maintenant.AddMinutes(1)));
        var surveiller = new SurveillerSynchronisation(d, new Certificat(), a, h);

        Assert.Equal(new[] { NaturesAlerteSync.RetourArriere }, await surveiller.ExecuterAsync());
        Assert.Single(a.Envoyees);
        Assert.Contains("lot 5", a.Envoyees[0].Detail);
        Assert.Contains("Repartir sur une nouvelle époque", a.Envoyees[0].Detail);
        Assert.Contains("Des anomalies d’intégrité sont attendues au serveur central pour les rangs restaurés", a.Envoyees[0].Detail);
        Assert.Contains("pas une falsification", a.Envoyees[0].Sms);
        Assert.DoesNotContain("é", a.Envoyees[0].Sms); // SMS sans accents

        h.MaintenantUtc = Maintenant.AddHours(3);
        Assert.Empty(await surveiller.ExecuterAsync()); // même détection : pas de rappel, même après une heure
        Assert.Single(a.Envoyees);

        // Nouvelle détection (après une nouvelle époque, nouvelle restauration) : nouvelle alerte
        d.Etat = d.Etat with { RetourArriere = d.Etat.RetourArriere! with { LeUtc = Maintenant.AddHours(4) } };
        h.MaintenantUtc = Maintenant.AddHours(4).AddMinutes(1);
        Assert.Equal(new[] { NaturesAlerteSync.RetourArriere }, await surveiller.ExecuterAsync());
        Assert.Equal(2, a.Envoyees.Count);
    }

    // ---- « Repartir sur une nouvelle époque » ----

    private sealed class Droits(bool titulaire) : IPermissionService
    {
        public Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<decimal> RemiseMaxAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult(titulaire);
    }

    private sealed class Journal : IJournal
    {
        public readonly List<(string Action, IReadOnlyDictionary<string, object?> Details)> Ecrits = [];
        public Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId,
            IReadOnlyDictionary<string, object?> details, string? permissionCode, CancellationToken ct = default)
        {
            Ecrits.Add((action, details));
            return Task.CompletedTask;
        }
    }

    /// <summary>Compte du titulaire (n° 1, mot de passe « secret ») et d'un assistant (n° 2) pour la ressaisie du mot de passe.</summary>
    private sealed class Comptes : IComptesLecture
    {
        public Task<Utilisateur?> TrouverActifAsync(string identifiant, CancellationToken ct = default) => Task.FromResult<Utilisateur?>(null);
        public Task NoterConnexionAsync(long utilisateurId, string? poste, string methode, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Utilisateur?> ParIdAsync(long utilisateurId, CancellationToken ct = default) =>
            Task.FromResult<Utilisateur?>(new Utilisateur { Id = utilisateurId, Identifiant = $"compte{utilisateurId}", NomComplet = "Compte", MotDePasseHash = "h:secret" });
        public Task ChangerMotDePasseAsync(long utilisateurId, string hash, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Verificateur : IVerificateurMotDePasse
    {
        public bool Verifier(string motDePasse, string hash) => hash == "h:" + motDePasse;
    }

    private const string BonMotDePasse = "secret";

    private static RepartirNouvelleEpoqueSync Repartir(Base b, Central s, bool titulaire = true, Journal? j = null, VerrouSync? v = null,
        EssaisConnexion? essais = null) =>
        new(b, new Droits(titulaire), v ?? new VerrouSync(), s, new VerificationMotDePasseActuel(new Comptes(), new Verificateur(), j ?? new Journal(), essais));

    [Fact]
    public async Task Nouvelle_epoque_refusee_sans_detection()
    {
        var (b, s, j) = (new Base(), new Central(), new Journal());
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Repartir(b, s, j: j).ExecuterAsync(1, BonMotDePasse, 1));
        Assert.Equal(RepartirNouvelleEpoqueSync.RefusSansDetection, e.Message);
        Assert.Equal((1, 0), (b.P.Epoque, b.NouvellesEpoques));
        Assert.Empty(j.Ecrits);
        Assert.Empty(b.Journal);
    }

    [Fact]
    public async Task Nouvelle_epoque_reservee_au_titulaire()
    {
        var (b, s) = Restauree();
        await Envoi(b, s).ExecuterAsync(Maintenant);
        await Assert.ThrowsAsync<DroitRefuseException>(() => Repartir(b, s, titulaire: false).ExecuterAsync(1, BonMotDePasse, 2));
        Assert.Equal(0, b.NouvellesEpoques);
    }

    [Fact]
    public async Task Nouvelle_epoque_exige_le_mot_de_passe_du_titulaire_essais_comptes_et_journalises()
    {
        var (b, s) = Restauree();
        await Envoi(b, s).ExecuterAsync(Maintenant);
        var (j, essais) = (new Journal(), new EssaisConnexion());

        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Repartir(b, s, j: j, essais: essais).ExecuterAsync(1, "faux", 1));
        Assert.Equal("Mot de passe actuel incorrect.", e.Message);
        Assert.Equal(RepartirNouvelleEpoqueSync.ChampMotDePasse, e.Champ);
        await Assert.ThrowsAsync<RegleMetierException>(() => Repartir(b, s, j: j, essais: essais).ExecuterAsync(1, null, 1));
        Assert.Equal(0, b.NouvellesEpoques);
        Assert.Equal(2, j.Ecrits.Count(x => x.Action == "mot_de_passe_actuel_refuse"));
        Assert.All(j.Ecrits, x => Assert.Equal("nouvelle_epoque_synchronisation", x.Details["contexte"]));

        // Blocage après EssaisMax mots de passe faux (même compteur que la connexion) : même le bon mot de passe est refusé
        for (var i = 2; i < EssaisConnexion.EssaisMax; i++)
            await Assert.ThrowsAsync<RegleMetierException>(() => Repartir(b, s, j: j, essais: essais).ExecuterAsync(1, "faux", 1));
        e = await Assert.ThrowsAsync<RegleMetierException>(() => Repartir(b, s, j: j, essais: essais).ExecuterAsync(1, BonMotDePasse, 1));
        Assert.StartsWith("Mot de passe bloqué", e.Message);
        Assert.Equal(0, b.NouvellesEpoques);
    }

    [Fact]
    public async Task Nouvelle_epoque_autorisee_apres_detection_journalisee_puis_l_envoi_reprend()
    {
        var (b, s) = Restauree();
        await Envoi(b, s).ExecuterAsync(Maintenant);
        var repartir = Repartir(b, s);

        // Écran périmé (époque affichée différente) : refus, rien ne change
        await Assert.ThrowsAsync<RegleMetierException>(() => repartir.ExecuterAsync(2, BonMotDePasse, 1));
        Assert.Equal(0, b.NouvellesEpoques);

        var r = await repartir.ExecuterAsync(1, BonMotDePasse, 1);
        Assert.Equal(new NouvelleEpoqueSync(2, 1, 7), r);
        Assert.Equal((null, 1), b.DerniereDemande);      // serveur central sans EpoqueMax : époque locale + 1
        Assert.Equal((2, 0L, 0L), (b.P.Epoque, b.P.DernierLot, b.P.DernierAccuse));
        Assert.Null(b.P.RetourArriereLe);
        var details = Assert.Single(b.Journal);           // écrit avec le changement d'époque (même transaction)
        Assert.Equal("1 -> 2", details["epoque"]);
        Assert.Equal(5L, details["dernier_lot_central"]);
        Assert.Equal(2L, details["dernier_lot_local"]);
        Assert.Equal("mot_de_passe", details["validation"]);

        // Époque 2 : rien au serveur central, la détection ne se pose pas et les lots repartent depuis 1
        var envoi = await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.Null(envoi.Erreur);
        Assert.Equal(2, envoi.LotsAccuses);
        Assert.Equal(2, s.Dernier[2]);
        Assert.Equal(5, s.Dernier[1]);
    }

    [Fact]
    public async Task Nouvelle_epoque_validee_par_le_mot_de_passe_seul_sans_aucun_message_SMS_ni_e_mail()
    {
        // Décision du titulaire du 2026-11-19 : mot de passe ressaisi, seul facteur ; ni code SMS ni e-mail
        var (b, s) = Restauree();
        await Envoi(b, s).ExecuterAsync(Maintenant);
        var (j, alertes) = (new Journal(), new Alertes());

        var r = await Repartir(b, s, j: j).ExecuterAsync(1, BonMotDePasse, 1);

        Assert.Equal(2, r.Epoque);
        var details = Assert.Single(b.Journal);
        Assert.Equal("mot_de_passe", details["validation"]);
        Assert.DoesNotContain(details, x => x.Key.Contains("code", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(details.Values, v => v is string t && t.Contains(BonMotDePasse));   // jamais le mot de passe au journal
        Assert.Empty(j.Ecrits);       // mot de passe juste : aucun refus noté
        Assert.Empty(alertes.Envoyees);

        // Aucun moyen d'envoyer un message : ni le cas d'usage ni la vérification du mot de passe ne reçoivent un service d'alertes,
        // de notifications, de SMS ou d'e-mail
        var messagerie = new[] { typeof(IAlertesTitulaire), typeof(INotificationService), typeof(IEnvoiSms), typeof(IEnvoiEmail) };
        foreach (var type in new[] { typeof(RepartirNouvelleEpoqueSync), typeof(VerificationMotDePasseActuel) })
        {
            var dependances = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();
            Assert.NotEmpty(dependances);
            Assert.DoesNotContain(dependances, d => messagerie.Contains(d) || d.Namespace == typeof(IAlertesTitulaire).Namespace);
        }
    }

    [Fact]
    public async Task Nouvelle_epoque_au_dela_de_la_plus_grande_epoque_connue_du_central()
    {
        var (b, s) = Restauree();
        await Envoi(b, s).ExecuterAsync(Maintenant);
        s.EpoqueMax = 4;   // le central connaît déjà les époques 2 à 4 (restaurations précédentes, 2e serveur)
        var r = await Repartir(b, s).ExecuterAsync(1, BonMotDePasse, 1);
        Assert.Equal(5, r.Epoque);
        Assert.Equal((4, 1), b.DerniereDemande);
        Assert.Equal(4, b.Journal.Single()["epoque_max_central"]);
    }

    [Fact]
    public async Task Nouvelle_epoque_refusee_si_le_central_est_injoignable()
    {
        var (b, s) = Restauree();
        await Envoi(b, s).ExecuterAsync(Maintenant);
        s.EtatInjoignable = true;
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Repartir(b, s).ExecuterAsync(1, BonMotDePasse, 1));
        Assert.Contains("Serveur central injoignable", e.Message);
        Assert.Equal(0, b.NouvellesEpoques);
        Assert.NotNull(b.P.RetourArriereLe);
    }

    [Fact]
    public async Task Nouvelle_epoque_refusee_pendant_un_passage_d_envoi()
    {
        var (b, s) = Restauree();
        await Envoi(b, s).ExecuterAsync(Maintenant);
        var verrou = new VerrouSync();
        Assert.True(verrou.Prendre());
        await Assert.ThrowsAsync<RegleMetierException>(() => Repartir(b, s, v: verrou).ExecuterAsync(1, BonMotDePasse, 1));
        Assert.Equal(0, b.NouvellesEpoques);
    }

    // ---- Lot S6.5.4 : déclaration du changement d'époque, compteur des tickets, prise d'acte ----

    private static DeclarationEpoqueSync DeclarationEpoque2() => new(1, 2, 5, 9, Maintenant.AddMinutes(-30), Maintenant.AddMinutes(-20), 1,
        [new FinChaineSync("journal_vente", "officine", 4, "4", new string('a', 64))]);

    /// <summary>Base repartie sur l'époque 2 (nouvelle époque faite), déclaration notée, trois lignes à envoyer.</summary>
    private static (Base, Central) NouvelleEpoque2()
    {
        var (b, s) = (new Base(), new Central());
        b.P = b.P with { Epoque = 2, DernierLot = 0, DernierAccuse = 0 };
        b.Declaration = DeclarationEpoque2();
        b.Ajouter(3);
        return (b, s);
    }

    [Fact]
    public async Task Declaration_envoyee_avant_tout_lot_de_la_nouvelle_epoque_puis_plus_jamais()
    {
        var (b, s) = NouvelleEpoque2();
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);

        Assert.Null(r.Erreur);
        Assert.Equal("declaration 2", s.Appels[0]);                 // avant le premier lot de l'époque 2
        Assert.StartsWith("lot 2/", s.Appels[1]);
        Assert.Equal((2, (bool?)true), (b.Declarations.Single().Epoque, b.Declarations.Single().Avant));
        Assert.StartsWith("Reçue", b.Declarations.Single().Resultat);
        b.Ajouter(1);
        await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.Single(s.Appels, a => a.StartsWith("declaration", StringComparison.Ordinal));   // jamais renvoyée
    }

    [Fact]
    public async Task Central_injoignable_pour_la_declaration_rien_n_est_envoye_puis_nouvel_essai()
    {
        var (b, s) = NouvelleEpoque2();
        s.CodeDeclaration = -1;
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Contains("déclaration de l'époque 2", r.Erreur);
        Assert.False(r.Bloquant);
        Assert.Equal(0, s.Depots);                                   // aucun lot sans la déclaration
        Assert.Empty(b.Declarations);

        s.CodeDeclaration = null;
        var r2 = await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.Null(r2.Erreur);
        Assert.Single(b.Declarations);
        Assert.True(s.Depots > 0);
    }

    [Theory]
    [InlineData(404, "ne connaît pas la route")]
    [InlineData(409, "Refusée par le serveur central")]
    public async Task Route_absente_ou_autre_declaration_resultat_note_et_lots_envoyes(int code, string attendu)
    {
        var (b, s) = NouvelleEpoque2();
        s.CodeDeclaration = code;
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Contains(attendu, b.Declarations.Single().Resultat);
        Assert.Null(b.Declarations.Single().Avant);
        Assert.True(s.Depots > 0);
    }

    [Fact]
    public async Task Declaration_d_une_epoque_depassee_notee_sans_appel()
    {
        var (b, s) = NouvelleEpoque2();
        b.P = b.P with { Epoque = 3 };
        await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.DoesNotContain(s.Appels, a => a.StartsWith("declaration", StringComparison.Ordinal));
        Assert.Contains("dépassée", b.Declarations.Single().Resultat);
    }

    [Fact]
    public async Task Compteur_des_tickets_avance_au_dela_du_central_avec_les_sequences()
    {
        var (b, s) = Restauree();
        s.Maximums = new(StringComparer.Ordinal) { ["vente"] = 120, [EnvoyerLotsSync.CleTicketCentral] = 500 };
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Equal(500, b.CompteurTicket);
        Assert.Contains("Compteur des tickets avancé au-delà du n° 500", r.Erreur);
        Assert.Contains("(20 n° sauté(s)", r.Erreur);
        Assert.Equal(120, b.Sequences["vente"]);
        Assert.NotNull(b.P.RetourArriereSequencesLe);
    }

    [Fact]
    public async Task Echec_du_compteur_des_tickets_rien_n_est_note_fait_tout_est_reessaye()
    {
        var (b, s) = Restauree();
        s.Maximums = new(StringComparer.Ordinal) { ["vente"] = 120, [EnvoyerLotsSync.CleTicketCentral] = 500 };
        b.EchecCompteur = true;
        var r = await Envoi(b, s).ExecuterAsync(Maintenant);
        Assert.Contains("Compteur des tickets non avancé", r.Erreur);
        Assert.Null(b.P.RetourArriereSequencesLe);                    // avance non notée : réessayée
        Assert.Equal(40, b.Sequences["vente"]);
        Assert.Equal(480, b.CompteurTicket);

        b.EchecCompteur = false;
        await Envoi(b, s).ExecuterAsync(Maintenant.AddMinutes(1));
        Assert.Equal((500L, 120L), (b.CompteurTicket, b.Sequences["vente"]));
        Assert.NotNull(b.P.RetourArriereSequencesLe);
    }

    [Fact]
    public async Task Prise_d_acte_reservee_au_titulaire_cause_obligatoire_une_fois_par_epoque()
    {
        var b = new Base();
        b.P = b.P with { Epoque = 2 };
        await Assert.ThrowsAsync<DroitRefuseException>(() => new PrendreActeRestaurationSync(b, new Droits(false)).ExecuterAsync(2, "Panne du disque", 2));
        var prendre = new PrendreActeRestaurationSync(b, new Droits(true));
        await Assert.ThrowsAsync<RegleMetierException>(() => prendre.ExecuterAsync(2, "  x ", 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => prendre.ExecuterAsync(3, "Panne du disque", 1));   // époque inconnue ici
        await prendre.ExecuterAsync(2, "  Panne du disque, sauvegarde de la veille  ", 1);
        Assert.Equal((2, "Panne du disque, sauvegarde de la veille", 1L), b.PrisesActe.Single());
        Assert.Equal(2, b.Journal.Single()["epoque"]);
        await Assert.ThrowsAsync<RegleMetierException>(() => prendre.ExecuterAsync(2, "Encore", 1));
    }

    [Fact]
    public void Resume_du_central_avec_restauration_lu_statut_classe_et_anomalies_classees()
    {
        var json = """
            {"format":"integrite-v1","statut":"restauration","anomalies_total":2,"anomalies_classees":2,"derniere_anomalie_id":7,
             "chaines":[],"sceaux":{"ok":1,"attente":0,"incoherents":0},
             "anomalies":[{"id":7,"nature":"remplacee_apres_restauration","journal":"journal_vente","rang":4,"classee":true}],
             "restaurations":[{"id":3,"journal":"journal_vente","ancienne_epoque":1,"nouvelle_epoque":2,"rang_commun":3,"fin_ancienne_rang":5,
               "lignes":2,"du":"2026-11-17T08:00:00Z","au":"2026-11-17T09:00:00Z","montant_ttc":70.00,"sceaux_perdus":1,
               "classe":"restauration_a_justifier","prise_acte":false,"tickets_reattribues":["T-017620","T-017621",12,""]}]}
            """;
        var ic = IntegriteCentrale.Lire(json)!;
        Assert.Equal(IntegriteCentrale.StatutRestauration, ic.Statut);
        Assert.Equal(2, ic.AnomaliesClassees);
        Assert.True(ic.Anomalies.Single().Classee);
        var b = ic.RestaurationsListe.Single();
        Assert.Equal((1, 2, 3L, 5L, 2L, 70.00m, 1L, false), (b.AncienneEpoque, b.NouvelleEpoque, b.RangCommun, b.FinAncienneRang, b.Lignes, b.MontantTtc,
            b.SceauxPerdus, b.PriseActe));
        Assert.True(b.AJustifier);
        // Script central 025 : n° de tickets réutilisés lus (valeurs non textuelles ou vides écartées)
        Assert.Equal(["T-017620", "T-017621"], b.TicketsReattribues);
        Assert.StartsWith("Lignes remplacées après une restauration", IntegriteCentrale.LibelleNature("remplacee_apres_restauration"));
        // Résumé d'un central antérieur : aucune restauration, rien ne casse
        Assert.Empty(IntegriteCentrale.Lire("""{"format":"integrite-v1","statut":"ok","chaines":[],"anomalies":[]}""")!.RestaurationsListe);
        // Central sans le script 025 : aucun n° réutilisé
        var sans = IntegriteCentrale.Lire(json.Replace(""","tickets_reattribues":["T-017620","T-017621",12,""]""", ""))!;
        Assert.Empty(sans.RestaurationsListe.Single().TicketsReattribues!);
    }
}
