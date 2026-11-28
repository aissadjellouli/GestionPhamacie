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

        public int Reformes;

        public void Ajouter(int n) => Sortie.AddRange(Enumerable.Range(Sortie.Count + 1, n).Select(i =>
            new LigneSortieSync(i, "vente", i.ToString(), ContratSync.Creation, Maintenant, $$"""{"id":{{i}}}""")));

        /// <summary>Fournisseur avec son téléphone (famille « fournisseurs », script 130).</summary>
        public void AjouterFournisseur()
        {
            var i = Sortie.Count + 1;
            Sortie.Add(new LigneSortieSync(i, "fournisseur", i.ToString(), ContratSync.Creation, Maintenant, $$"""{"id":{{i}},"telephone":"0550000000"}"""));
        }

        /// <summary>Utilisateur avec son nom complet (famille « employes », script 130).</summary>
        public void AjouterUtilisateur()
        {
            var i = Sortie.Count + 1;
            Sortie.Add(new LigneSortieSync(i, "utilisateur", i.ToString(), ContratSync.Creation, Maintenant, $$"""{"id":{{i}},"nom_complet":"Nom Secret"}"""));
        }

        /// <summary>Catalogue des familles (comme sync_famille_colonne, réduit aux colonnes des essais).</summary>
        public readonly List<(string Famille, string Colonne)> Catalogue = [("fournisseurs", "fournisseur.telephone"), ("employes", "utilisateur.nom_complet")];
        public readonly List<ChangementDescendantSync> Recus = [];
        public bool PanneDescendants;
        /// <summary>Statut imposé à un élément (par clé), comme sync_recevoir_descendant pour un élément « mutu_ » d'une table non mutualisable.</summary>
        public readonly Dictionary<string, string> StatutsForces = [];

        private IReadOnlyList<string> Interdites(IReadOnlyList<string>? familles) =>
            Catalogue.Where(c => !(familles ?? []).Contains(c.Famille)).Select(c => c.Colonne).Distinct().Order(StringComparer.Ordinal).ToList();

        /// <summary>Comme sync_retirer_colonnes : colonnes « table.colonne » retirées (null : aucun retrait).</summary>
        private static LigneSortieSync Filtrer(LigneSortieSync l, IReadOnlyList<string>? colonnes)
        {
            if (colonnes is null || l.DonneesJson is null) return l;
            var o = System.Text.Json.Nodes.JsonNode.Parse(l.DonneesJson)!.AsObject();
            foreach (var c in colonnes.Where(c => c.StartsWith(l.Table + ".", StringComparison.Ordinal))) o.Remove(c[(l.Table.Length + 1)..]);
            return l with { DonneesJson = o.ToJsonString() };
        }

        public Task<EtatSync> EtatAsync(CancellationToken ct = default) => Task.FromResult(new EtatSync(P.Active, P.OfficineId, P.Epoque,
            P.VersionContrat, P.DernierLot, P.DernierAccuse, 2, 1_048_576, Sortie.Count(l => !Rattachement.ContainsKey(l.Numero)), Interdites(P.FamillesEnvoyees)));
        public Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, IReadOnlyList<string> colonnes, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => !Rattachement.ContainsKey(l.Numero)).Take(maximum).Select(l => Filtrer(l, colonnes)).ToList());
        public Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, IReadOnlyList<string> colonnes, IReadOnlyList<string> tables,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => !Rattachement.ContainsKey(l.Numero) && tables.Contains(l.Table)).Take(maximum)
                .Select(l => Filtrer(l, colonnes)).ToList());
        public Task<bool> EnregistrerLotAsync(LotConstitue lot, CancellationToken ct = default)
        {
            foreach (var l in lot.Lignes) Rattachement[l] = lot.Numero;
            Lots.Add((new LotEnregistre(P.Epoque, lot.Numero, P.VersionContrat, lot.Empreinte, lot.ColonnesRetirees), false));
            P = P with { DernierLot = lot.Numero };
            return Task.FromResult(true);
        }

        public Task<ParametresEnvoiSync> ParametresAsync(CancellationToken ct = default) => Task.FromResult(P with { ColonnesInterdites = Interdites(P.FamillesEnvoyees) });
        public Task<IReadOnlyList<LotEnregistre>> LotsNonAccusesAsync(int maximum, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LotEnregistre>>(Lots.Where(l => !l.Accuse).Select(l => l.Lot).OrderBy(l => l.Numero).Take(maximum).ToList());
        public Task<IReadOnlyList<LigneSortieSync>> LignesDuLotAsync(int epoque, long numero, IReadOnlyList<string>? colonnes, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Sortie.Where(l => Rattachement.TryGetValue(l.Numero, out var n) && n == numero).Select(l => Filtrer(l, colonnes)).ToList());
        /// <summary>Comme sync_recevoir_descendant : statut par élément, table inconnue ignorée, version acquittée non modifiée.</summary>
        public Task<IReadOnlyList<string>> AppliquerDescendantsAsync(IReadOnlyList<ChangementDescendantSync> changements, long versionMax, CancellationToken ct = default)
        {
            if (PanneDescendants) throw new InvalidOperationException("panne de la base");
            var statuts = new List<string>();
            foreach (var c in changements)
            {
                Recus.Add(c);
                if (c.Version > versionMax) { statuts.Add(StatutsDescendant.Refuse); continue; }
                if (StatutsForces.TryGetValue(c.Cle, out var force)) { statuts.Add(force); continue; }
                if (c.Table != "reglage_central") { statuts.Add(StatutsDescendant.TableInconnue); continue; }
                if (c.Version <= P.FamillesVersion) { statuts.Add(StatutsDescendant.Ancien); continue; }
                var f = System.Text.Json.JsonDocument.Parse(c.DonneesJson).RootElement.GetProperty("familles").EnumerateArray().Select(x => x.GetString()!);
                P = P with { FamillesEnvoyees = f.Distinct().Order(StringComparer.Ordinal).ToList(), FamillesLuesLe = Maintenant, FamillesVersion = c.Version };
                statuts.Add(StatutsDescendant.Applique);
            }
            return Task.FromResult<IReadOnlyList<string>>(statuts);
        }
        public Task NoterVersionDescendanteAsync(long version, CancellationToken ct = default)
        {
            P = P with { DescendantVersion = Math.Max(P.DescendantVersion, version) };
            return Task.CompletedTask;
        }
        public Task ReformerLotAsync(int epoque, long numero, string empreinte, int octets, IReadOnlyList<string> colonnes, CancellationToken ct = default)
        {
            var i = Lots.FindIndex(l => l.Lot.Numero == numero && !l.Accuse);
            Assert.True(i >= 0, "lot non accusé attendu (jamais de reforme d'un lot accusé)");
            var avant = Lots[i].Lot.ColonnesRetirees ?? [];
            Assert.True(avant.All(colonnes.Contains) && colonnes.Count > avant.Count, "reforme : retirer davantage seulement");
            Lots[i] = (Lots[i].Lot with { Empreinte = empreinte, ColonnesRetirees = colonnes, ReformeLe = Maintenant }, false);
            Reformes++;
            return Task.CompletedTask;
        }
        public Task EnregistrerAccuseAsync(int epoque, long numero, CancellationToken ct = default)
        {
            for (var i = 0; i < Lots.Count; i++) if (Lots[i].Lot.Numero <= numero) Lots[i] = (Lots[i].Lot, true);
            P = P with { DernierAccuse = Math.Max(P.DernierAccuse, numero) };
            return Task.CompletedTask;
        }
        public Task NoterEnvoiAsync(string? erreur, CancellationToken ct = default) { Envois.Add(erreur); P = P with { DerniereErreur = erreur }; return Task.CompletedTask; }
        public Task EnregistrerInscriptionAsync(Guid officine, CancellationToken ct = default) { P = P with { InscriteLe = Maintenant }; return Task.CompletedTask; }
        public Task ReglerControleDemarrageAsync(int secondes, CancellationToken ct = default) { P = P with { DelaiControleDemarrage = secondes }; return Task.CompletedTask; }
        public Task ReglerAsync(string? a, string t, bool m, bool active, CancellationToken ct = default)
        {
            P = P with { Adresse = a, TextesLibres = t, EnvoyerMedecins = m, Active = active };
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<LotEnregistre>> LotsEntreAsync(int epoque, long du, long au, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LotEnregistre>>(Lots.Select(l => l.Lot with { Accuse = l.Accuse }).Where(l => l.Numero >= du && l.Numero <= au).OrderBy(l => l.Numero).ToList());
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
        private IReadOnlyList<string> reglage = ["courriel_commandes", "fournisseurs", "pharmacie"];
        /// <summary>Réglage du central : chaque changement publie une nouvelle version (comme cloud_publier_descendant).</summary>
        public IReadOnlyList<string> Reglage { get => reglage; set { reglage = value; VersionReglage++; } }
        public long VersionReglage = 1;
        public bool ReglagesEnPanne;
        public readonly List<string> Contenus = [];
        public readonly List<long> VersionsRecues = [];

        /// <summary>Éléments en plus (table inconnue…), renvoyés tant que leur version dépasse la version acquittée.</summary>
        public readonly List<ChangementDescendantSync> Autres = [];

        private DescendantsRecus Plus(long apres)
        {
            var l = Autres.Where(a => a.Version > apres).ToList();
            if (VersionReglage > apres) l.Add(new("reglage_central", "familles_envoyees", VersionReglage, System.Text.Json.JsonSerializer.Serialize(new { familles = reglage })));
            var ecartesCeTour = VersionEcartee == 0 || VersionEcartee > apres ? Ecartes : 0;
            return new DescendantsRecus(l, Math.Max(VersionReglage, Autres.Select(a => a.Version).DefaultIfEmpty(0).Max()), ecartesCeTour,
                VersionEcartee > apres ? VersionEcartee : 0, DateCentrale);
        }
        /// <summary>Date du serveur central portée par chaque réponse (null : central antérieur au 2026-11-27).</summary>
        public DateTime? DateCentrale;
        /// <summary>Éléments écartés par le client à la réception (mal formés ou trop gros).</summary>
        public int Ecartes;
        /// <summary>Plus grande version parmi les éléments écartés (0 : non précisée) ; ils ne sont plus rendus une fois acquittés.</summary>
        public long VersionEcartee;

        public Task<DescendantsRecus> EchangerAsync(string adresse, long versionAppliquee, CancellationToken ct = default)
        {
            if (ReglagesEnPanne || Coupure) throw new HttpRequestException("Connexion impossible");
            VersionsRecues.Add(versionAppliquee);
            return Task.FromResult(Plus(versionAppliquee));
        }
        public Guid OfficineRendue = Officine;
        /// <summary>Contrôle « base revenue en arrière » (lot S6.2) : le central de ces essais n'a qu'une époque ; jamais en avance sur l'officine.</summary>
        public Task<long?> DernierLotCentralAsync(string adresse, int epoque, CancellationToken ct = default) => Task.FromResult<long?>(epoque == 1 ? Dernier : 0);
        public int Renouvellements;

        public Task<ReponseActivationSync> ActiverAsync(string adresse, string code, string demandePem, Guid officine, CancellationToken ct = default) =>
            Task.FromResult(new ReponseActivationSync(OfficineRendue, "principal", "CERT", "AC", Maintenant.AddYears(3), "0123"));

        public Task<AccuseLotSync> DeposerAsync(string adresse, byte[] contenu, long versionDescendante, CancellationToken ct = default)
        {
            if (Coupure) throw new HttpRequestException("Connexion impossible");
            if (Panne is not null) throw Panne;
            var lot = SerialisationSyncV1.Lire(contenu);
            Assert.True(SerialisationSyncV1.EmpreinteValide(lot));
            Contenus.Add(System.Text.Encoding.UTF8.GetString(contenu));
            var n = lot.EnTete.NumeroLot;
            if (n <= Dernier) return Task.FromResult(new AccuseLotSync(1, n, Recus[n] == lot.EnTete.Empreinte ? StatutsLotSync.DejaRecu : StatutsLotSync.EmpreinteDifferente, Dernier));
            if (n > Dernier + 1) return Task.FromResult(new AccuseLotSync(1, n, StatutsLotSync.Trou, Dernier));
            Recus[n] = lot.EnTete.Empreinte;
            Dernier = n;
            return Task.FromResult(new AccuseLotSync(1, NumeroAccuseFaux ?? n, StatutsLotSync.Accepte, Dernier, Plus(versionDescendante)));
        }

        /// <summary>Refus du renouvellement par le central (certificat plus récent, quota d'un renouvellement par jour…).</summary>
        public ServeurCentralException? RefusRenouvellement;

        public Task<ReponseActivationSync> RenouvelerAsync(string adresse, string demandePem, CancellationToken ct = default)
        {
            Renouvellements++;
            if (RefusRenouvellement is not null) throw RefusRenouvellement;
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

    /// <summary>Fichier de la date plancher de l'offre en mémoire.</summary>
    private sealed class PlancherOffre : Pharma.Application.Offre.IDatePlancherFichier
    {
        public DateTime? Centrale;
        public int Ecritures;
        public DateTime? Lire() => null;
        public void Ecrire(DateTime utc) => throw new InvalidOperationException("La synchronisation n'écrit jamais la date plancher du fichier.");
        public DateTime? LireCentrale() => Centrale;
        public void NoterCentrale(DateTime utc) { Centrale = utc; Ecritures++; }
    }

    [Fact]
    public async Task La_date_du_central_est_memorisee_pour_l_offre_meme_sans_lot_et_jamais_en_arriere()
    {
        // Décision du titulaire du 2026-11-27 (point 4) : la date du central fait foi, date plancher supplémentaire de l'offre
        var (b, c, s) = (new Base(), new Certificat(), new Central { DateCentrale = Maintenant.AddMinutes(3) });
        var plancher = new PlancherOffre();
        var f = new FormatV1();
        EnvoyerLotsSync Envoi() => new(b, new ConstitutionLotsSync(b, f), f, c, s, new VerrouSync(), datePlancher: plancher);
        Assert.Null((await Envoi().ExecuterAsync(Maintenant)).Erreur);          // échange léger seul, aucun lot
        Assert.Equal((Maintenant.AddMinutes(3), 1), (plancher.Centrale, plancher.Ecritures));
        // Au plus une fois par heure ; une date plus ancienne (central restauré) ne fait rien reculer
        s.DateCentrale = Maintenant.AddMinutes(30);
        await Envoi().ExecuterAsync(Maintenant);
        s.DateCentrale = Maintenant.AddDays(-3);
        await Envoi().ExecuterAsync(Maintenant);
        Assert.Equal((Maintenant.AddMinutes(3), 1), (plancher.Centrale, plancher.Ecritures));
        // Avec l'accusé d'un lot aussi ; central antérieur (sans date) : rien ne change
        s.DateCentrale = Maintenant.AddHours(2);
        b.Ajouter(1);
        Assert.Equal(1, (await Envoi().ExecuterAsync(Maintenant)).LotsAccuses);
        Assert.Equal((Maintenant.AddHours(2), 2), (plancher.Centrale, plancher.Ecritures));
        s.DateCentrale = null;
        await Envoi().ExecuterAsync(Maintenant);
        Assert.Equal(2, plancher.Ecritures);
        // Sans fichier (tests, autre usage) : rien n'est mémorisé, rien ne casse
        Assert.False(Pharma.Application.Offre.DateCentraleOffre.Noter(null, Maintenant));
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
    public async Task Elements_descendants_ecartes_signales_dans_la_note_du_passage()
    {
        // Revue du 2026-11-22 : un élément mal formé ou trop gros était sauté sans rien en dire
        var (b, c, s) = (new Base(), new Certificat(), new Central { Ecartes = 2 });
        b.Ajouter(1);
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Contains("2 élément(s) descendant(s) écarté(s)", b.Envois[^1]);
    }

    [Fact]
    public async Task Elements_descendants_ecartes_sont_acquittes_meme_sans_element_garde()
    {
        // Revue du 2026-11-23 : la version acquittée ne tenait compte que des éléments gardés ; un écarté était redonné sans fin
        var (b, c, s) = (new Base(), new Certificat(), new Central { Ecartes = 1, VersionEcartee = 50 });
        b.Ajouter(1);
        Assert.Null((await Envoi(b, c, s).ExecuterAsync(Maintenant)).Erreur);
        Assert.Equal(50, b.P.DescendantVersion);
        // Passage où il ne reste QUE un élément écarté (rien de gardé) : sa version est quand même acquittée
        s.VersionEcartee = 60;
        Assert.Null((await Envoi(b, c, s).ExecuterAsync(Maintenant)).Erreur);
        Assert.Equal(60, b.P.DescendantVersion);
        Assert.Contains("1 élément(s) descendant(s) écarté(s)", b.Envois[^1]);
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Equal(60, s.VersionsRecues[^1]);   // le central reçoit 60 : il ne redonne pas l'écarté
    }

    [Fact]
    public async Task Element_ecarte_apres_un_element_bloquant_n_est_pas_acquitte_et_les_deux_messages_restent()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central { Ecartes = 1, VersionEcartee = 50 });
        s.Autres.Add(new("prix_central", "x", 5, "{}"));
        b.Ajouter(1);
        Assert.Null((await Envoi(b, c, s).ExecuterAsync(Maintenant)).Erreur);
        Assert.Equal(4, b.P.DescendantVersion);   // avant le premier bloquant, jamais jusqu'à l'écarté
        Assert.Contains("écarté(s)", b.Envois[^1]);
        Assert.Contains("ignoré(s)", b.Envois[^1]);
    }

    [Theory]
    [InlineData(403, false)]   // un certificat plus récent existe
    [InlineData(429, true)]    // un renouvellement par jour
    public async Task Renouvellement_refuse_ne_fait_pas_echouer_un_passage_dont_les_lots_sont_accuses(int code, bool quota)
    {
        // Revue du 2026-11-22 : le refus du renouvellement mettait en erreur bloquante (attente de 30 min) un passage réussi
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        c.Info = c.Info! with { EcheanceUtc = Maintenant.AddDays(30) };
        s.RefusRenouvellement = new ServeurCentralException("refusé par le serveur central", code, quota);
        b.Ajouter(5);
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.False(r.Bloquant);
        Assert.Equal(3, r.LotsAccuses);
        Assert.Equal(1, s.Renouvellements);
        Assert.Null(c.Installe);
        Assert.Contains("non renouvelé", b.Envois[^1]);
        Assert.Contains("nouvel essai au prochain passage", b.Envois[^1]);
        // Passage suivant : nouvel essai, réussi cette fois
        s.RefusRenouvellement = null;
        Assert.Null((await Envoi(b, c, s).ExecuterAsync(Maintenant)).Erreur);
        Assert.Equal("CERT2", c.Installe);
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
        // Script 152 : attente de la caisse au démarrage, 0 à 600 s ; null : inchangée
        await new ReglerSync(b, journal).ExecuterAsync(new ReglagesSync(true, "https://central:5090", "retires", true, 30), 3);
        Assert.Equal(30, b.P.DelaiControleDemarrage);
        await new ReglerSync(b, journal).ExecuterAsync(new ReglagesSync(true, "https://central:5090", "retires", true), 3);
        Assert.Equal(30, b.P.DelaiControleDemarrage);
        await Assert.ThrowsAsync<RegleMetierException>(() => new ReglerSync(b, journal).ExecuterAsync(new ReglagesSync(true, null, "filtres", false, 601), 3));
        await Assert.ThrowsAsync<RegleMetierException>(() => new ReglerSync(b, journal).ExecuterAsync(new ReglagesSync(true, null, "filtres", false, -1), 3));
    }

    // ---- Familles nominatives réglées au serveur central (script 130) ----

    [Fact]
    public async Task Reglage_jamais_lu_rien_de_nominatif_ne_part_puis_le_reglage_lu_s_applique()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        s.ReglagesEnPanne = true;
        b.AjouterFournisseur();
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur); // le dépôt passe, seul l'échange léger a échoué
        Assert.DoesNotContain("0550000000", s.Contenus[^1]); // lot formé avant tout réglage reçu
        Assert.Equal(1, b.P.FamillesVersion); // réglage descendu par l'accusé du lot
        s.ReglagesEnPanne = false;
        b.AjouterFournisseur();
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Equal(new[] { "courriel_commandes", "fournisseurs", "pharmacie" }, b.P.FamillesEnvoyees);
        Assert.Contains("0550000000", s.Contenus[^1]);
        Assert.Equal(new[] { "utilisateur.nom_complet" }, b.Lots[^1].Lot.ColonnesRetirees); // figées à la constitution
    }

    [Fact]
    public async Task Une_ligne_deja_en_file_est_filtree_si_sa_famille_est_decochee_et_le_cache_sert_si_le_central_est_injoignable()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.P = b.P with { FamillesEnvoyees = ["fournisseurs"] };
        b.AjouterFournisseur(); // inscrite pendant que la famille était envoyée
        s.Reglage = ["pharmacie"];
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.DoesNotContain("0550000000", s.Contenus[^1]);
        // Central injoignable pour le réglage : dernier réglage connu gardé (jamais élargi par défaut)
        s.ReglagesEnPanne = true;
        b.AjouterFournisseur();
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Equal(new[] { "pharmacie" }, b.P.FamillesEnvoyees);
        Assert.DoesNotContain("0550000000", s.Contenus[^1]);
    }

    [Fact]
    public async Task Un_lot_non_accuse_est_reforme_sans_la_famille_decochee_avant_envoi()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        await Envoi(b, c, s).ExecuterAsync(Maintenant); // réglage reçu (fournisseurs envoyés)
        b.AjouterFournisseur();
        s.Coupure = true;
        await Envoi(b, c, s).ExecuterAsync(Maintenant); // lot 1 formé avec le téléphone, non envoyé
        Assert.DoesNotContain("fournisseur.telephone", b.Lots[0].Lot.ColonnesRetirees!);
        var origine = b.Lots[0].Lot.Empreinte;
        s.Coupure = false;
        s.Reglage = ["pharmacie"];
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(1, b.Reformes);
        Assert.NotEqual(origine, b.Lots[0].Lot.Empreinte);
        Assert.True(b.Lots[0].Accuse);
        Assert.DoesNotContain("0550000000", s.Contenus[^1]);
        Assert.All(s.Contenus, x => Assert.DoesNotContain("0550000000", x));
    }

    [Fact]
    public async Task Un_lot_reforme_deja_recu_sous_sa_forme_d_origine_vaut_accuse()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        await Envoi(b, c, s).ExecuterAsync(Maintenant); // réglage reçu (fournisseurs envoyés)
        b.AjouterFournisseur();
        s.Coupure = true;
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        // Le central avait reçu le lot d'origine (accusé perdu), puis la famille est décochée
        s.Coupure = false;
        (s.Recus[1], s.Dernier) = (b.Lots[0].Lot.Empreinte, 1);
        s.Reglage = [];
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.True(b.Lots[0].Accuse);
        Assert.Equal(1, b.Reformes);
    }

    [Fact]
    public async Task Le_reglage_descend_meme_sans_lot_et_le_dernier_applique_sert_si_le_central_est_coupe()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant); // rien à envoyer : l'échange léger fait quand même descendre
        Assert.Null(r.Erreur);
        Assert.Equal(1, b.P.FamillesVersion);
        Assert.Equal(new[] { "courriel_commandes", "fournisseurs", "pharmacie" }, b.P.FamillesEnvoyees);
        s.Reglage = ["pharmacie"];
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Equal(2, b.P.DescendantVersion);
        Assert.Equal(1, s.VersionsRecues[^1]); // acquittement de la version appliquée
        // Central coupé : aucun blocage local, dernière version appliquée gardée, rien de nominatif décoché ne part
        s.Coupure = true;
        b.AjouterFournisseur();
        r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Contains("injoignable", r.Erreur);
        Assert.Equal(new[] { "pharmacie" }, b.P.FamillesEnvoyees);
        s.Coupure = false;
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.DoesNotContain("0550000000", s.Contenus[^1]);
    }

    [Fact]
    public async Task Un_reglage_porte_par_l_accuse_d_un_lot_est_applique()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        b.Ajouter(1);
        s.ReglagesEnPanne = true; // échange léger en échec : seul l'accusé du lot fait descendre
        s.Reglage = ["employes"];
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Equal(new[] { "employes" }, b.P.FamillesEnvoyees);
        Assert.Equal(2, b.P.FamillesVersion);
    }

    // ---- Corrections après revue (script 136) ----

    [Fact]
    public async Task Une_colonne_ajoutee_a_une_famille_apres_coup_ne_rend_pas_un_lot_deja_constitue_non_renvoyable()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        s.Reglage = ["pharmacie"];
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        b.AjouterFournisseur();
        s.Panne = new HttpRequestException("coupure");
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Equal(["fournisseur.telephone", "utilisateur.nom_complet"], b.Lots[0].Lot.ColonnesRetirees);
        b.Catalogue.Add(("pharmacie", "fournisseur.nif")); // colonne ajoutée à une famille envoyée : le lot figé ne change pas
        s.Panne = null;
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(0, b.Reformes);
        Assert.True(b.Lots[0].Accuse);
    }

    [Fact]
    public async Task Un_lot_deja_accuse_renvoye_apres_un_trou_est_filtre_sans_reforme_en_base()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        await Envoi(b, c, s).ExecuterAsync(Maintenant); // réglage reçu (fournisseurs envoyés)
        b.AjouterFournisseur();
        b.Ajouter(3);
        s.Panne = new HttpRequestException("coupure");
        await Envoi(b, c, s).ExecuterAsync(Maintenant); // lots 1 et 2 formés avec le téléphone
        b.Lots[0] = (b.Lots[0].Lot, true); // lot 1 accusé ici, perdu côté central (restauration)
        s.Panne = null;
        s.Reglage = ["pharmacie"]; // famille « fournisseurs » décochée depuis
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(2, s.Dernier);
        Assert.All(s.Contenus, x => Assert.DoesNotContain("0550000000", x));
        Assert.Equal(0, b.Reformes); // jamais de reforme du lot 1 déjà accusé (le lot 2 n'a rien de nominatif)
        Assert.DoesNotContain("fournisseur.telephone", b.Lots[0].Lot.ColonnesRetirees!); // base du lot 1 inchangée
    }

    [Fact]
    public async Task Un_lot_anterieur_au_script_130_sans_familles_ne_laisse_rien_de_nominatif_partir_lors_d_une_reprise()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        b.AjouterUtilisateur();
        b.Ajouter(3);
        s.Panne = new HttpRequestException("coupure");
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        b.Lots[0] = (b.Lots[0].Lot with { ColonnesRetirees = [], ColonnesFigees = false }, true); // lot ancien, familles NULL
        s.Panne = null;
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(2, s.Dernier);
        Assert.All(s.Contenus, x => Assert.DoesNotContain("Nom Secret", x));
    }

    [Fact]
    public async Task Un_lot_reforme_dont_l_accuse_s_est_perdu_est_tenu_pour_recu_d_apres_la_base_et_non_une_variable_locale()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        b.AjouterFournisseur();
        s.Panne = new HttpRequestException("coupure");
        await Envoi(b, c, s).ExecuterAsync(Maintenant);
        var origine = b.Lots[0].Lot.Empreinte;
        s.Reglage = [];
        await Envoi(b, c, s).ExecuterAsync(Maintenant); // reforme en base, envoi encore en panne
        Assert.NotNull(b.Lots[0].Lot.ReformeLe);
        // Nouveau passage (autre instance) : le central a la forme d'origine ; la décision vient de reforme_le
        s.Panne = null;
        (s.Recus[1], s.Dernier) = (origine, 1);
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.True(b.Lots[0].Accuse);
        Assert.Equal(1, b.Reformes);
    }

    [Fact]
    public async Task Un_element_descendant_d_une_table_inconnue_est_ignore_sans_arreter_l_envoi_ni_sauter_de_version()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        s.Autres.Add(new("prix_central", "x", 5, "{}"));
        b.Ajouter(1);
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(1, s.Dernier);
        Assert.Equal(4, b.P.DescendantVersion); // jamais jusqu'à l'élément ignoré (redonné à chaque passage)
        Assert.Contains("ignoré", b.Envois[^1]);
        Assert.Equal(1, b.P.FamillesVersion); // le réglage valide, lui, est appliqué
    }

    [Fact]
    public async Task Un_element_mutu_invalide_est_acquitte_et_ne_bloque_pas_les_suivants()
    {
        // Script 141 : l'élément d'une table non mutualisable est journalisé et ignoré, mais acquitté (contrairement à « table_inconnue »)
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        s.Autres.Add(new("reglage_central", "mutu_table_inconnue", 5, "{}"));
        b.StatutsForces["mutu_table_inconnue"] = StatutsDescendant.Invalide;
        b.Ajouter(1);
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(5, b.P.DescendantVersion);
        Assert.DoesNotContain("ignoré", b.Envois[^1] ?? "");
    }

    private static EnvoyerLotsSync EnvoiAvecReglages(Base b, Certificat c, Central s, DepotReglagesEnMemoire reglages)
    {
        var f = new FormatV1();
        return new EnvoyerLotsSync(b, new ConstitutionLotsSync(b, f), f, c, s, new VerrouSync(), null,
            new Pharma.Application.Reglages.AppliquerReglagesMutualises(reglages));
    }

    private static DepotReglagesEnMemoire ReglageInventaireRecu()
    {
        var d = new DepotReglagesEnMemoire();
        var t = Pharma.Domain.Reglages.TablesMutualisables.Inventaire;
        var element = System.Text.Encoding.UTF8.GetString(Pharma.Shared.SerialisationMutuV1.Serialiser(t, new Pharma.Shared.ElementMutuDto("impose", 1, null,
            new DateTime(2026, 11, 20, 9, 12, 0, DateTimeKind.Utc), "admin-central-1", new Pharma.Shared.MutuInventaireDto(5, 4000m))));
        d.Etats[t] = d.Etats[t] with { ModeRecu = "impose", VersionRecue = 1, ModeleJson = element };
        d.Locales[t] = "{\"seuil_boites\":2,\"seuil_montant\":2000}";
        return d;
    }

    [Fact]
    public async Task Les_reglages_recus_sont_appliques_a_chaque_passage_meme_si_le_central_est_coupe()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central { Coupure = true });
        var d = ReglageInventaireRecu();
        b.Ajouter(1);
        var r = await EnvoiAvecReglages(b, c, s, d).ExecuterAsync(Maintenant);
        Assert.NotNull(r.Erreur);                                          // le dépôt du lot échoue (central coupé)
        Assert.Equal("applique", d.Etats[Pharma.Domain.Reglages.TablesMutualisables.Inventaire].Etat);   // mais le modèle déjà reçu est appliqué
    }

    [Fact]
    public async Task Rien_n_est_applique_si_la_synchronisation_est_desactivee()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.P = b.P with { Active = false };
        var d = ReglageInventaireRecu();
        await EnvoiAvecReglages(b, c, s, d).ExecuterAsync(Maintenant);
        Assert.Equal("aucun", d.Etats[Pharma.Domain.Reglages.TablesMutualisables.Inventaire].Etat);
    }

    [Fact]
    public async Task Une_panne_de_l_application_des_reglages_n_arrete_pas_l_envoi_des_lots()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        var d = new PanneReglages();
        b.Ajouter(1);
        var f = new FormatV1();
        var r = await new EnvoyerLotsSync(b, new ConstitutionLotsSync(b, f), f, c, s, new VerrouSync(), null,
            new Pharma.Application.Reglages.AppliquerReglagesMutualises(d)).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(1, s.Dernier);
        Assert.Contains("Réglages mutualisés non appliqués", b.Envois[^1]);
    }

    private sealed class PanneReglages : Pharma.Application.Reglages.IReglagesMutualisesDepot
    {
        public Task<bool> ApplicationPossibleAsync(CancellationToken ct = default) => throw new InvalidOperationException("panne de la base");
        public Task<(string Mode, bool AjoutsLocaux)> ModeEffectifAsync(string table, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Pharma.Application.Reglages.EtatReglage>> EtatsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Pharma.Application.Reglages.ITransactionReglage> OuvrirAsync(string table, CancellationToken ct = default) => throw new NotSupportedException();
        public Task JournaliserAsync(string action, string table, string modeAvant, string modeApres, long? version, string? auteur, long? utilisateurId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Desactiver_la_synchronisation_libere_les_reglages_imposes_au_journal_pas_la_reactivation()
    {
        var b = new Base();
        var d = new DepotReglagesEnMemoire();
        d.Etats[Pharma.Domain.Reglages.TablesMutualisables.Inventaire] = d.Etats[Pharma.Domain.Reglages.TablesMutualisables.Inventaire] with { Mode = "impose" };
        var regler = new ReglerSync(b, new JournalEspion(), null, new Pharma.Application.Reglages.LibererReglagesMutualises(d));
        await regler.ExecuterAsync(new ReglagesSync(false, "https://central:5090", "filtres", false), 3);
        var j = Assert.Single(d.Journal);
        Assert.Equal(("reglage_central_libere", "impose", "local", 3L), (j.Action, j.ModeAvant, j.ModeApres, j.Utilisateur));
        d.Journal.Clear();
        await regler.ExecuterAsync(new ReglagesSync(true, "https://central:5090", "filtres", false), 3);   // réactivation : rien à journaliser ici
        Assert.Empty(d.Journal);
    }

    [Fact]
    public async Task Une_erreur_d_application_des_descendants_n_arrete_pas_l_envoi_des_lots()
    {
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.PanneDescendants = true;
        b.Ajouter(1);
        var r = await Envoi(b, c, s).ExecuterAsync(Maintenant);
        Assert.Null(r.Erreur);
        Assert.Equal(1, s.Dernier);
        Assert.Contains("non appliqués", b.Envois[^1]);
    }
    [Fact]
    public async Task Sans_le_pack_G_les_ordres_sont_releves_et_seuls_les_resultats_d_ordres_partent()
    {
        // Décision du titulaire du 2026-11-27 (point 3) : voie réduite, aucune donnée ne quitte l'officine hormis les résultats d'ordres
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        b.Ajouter(3);                                                   // ventes en file
        var envoi = Envoi(b, c, s);
        var releve = await envoi.ExecuterSansPackAsync(Maintenant, envoyerResultats: false);
        Assert.Null(releve.Erreur);
        Assert.NotEmpty(s.VersionsRecues);                              // échange léger fait : les ordres descendent
        Assert.Empty(b.Lots);                                           // rien de formé
        Assert.Equal(0, s.Dernier);

        var i = b.Sortie.Count + 1;
        b.Sortie.Add(new LigneSortieSync(i, EnvoyerLotsSync.TableResultatsOrdres, "o1", ContratSync.Creation, Maintenant, $$"""{"id":{{i}},"statut":"applique"}"""));
        var r = await envoi.ExecuterSansPackAsync(Maintenant, envoyerResultats: true);
        Assert.Null(r.Erreur);
        Assert.Equal(1, r.LotsAccuses);
        Assert.Equal(1, s.Dernier);
        Assert.Equal([i], b.Rattachement.Keys.ToArray());               // seule la ligne de résultat est partie
        Assert.All(s.Contenus, contenu => Assert.DoesNotContain("\"vente\"", contenu));
        Assert.Equal(3, b.Sortie.Count(l => l.Table == "vente" && !b.Rattachement.ContainsKey(l.Numero)));   // ventes gardées en file

        // Désactivée ou sans activation : rien
        b.P = b.P with { Active = false };
        Assert.Equal(EnvoyerLotsSync.ArretDesactivee, (await envoi.ExecuterSansPackAsync(Maintenant, true)).Arret);
    }

    [Fact]
    public async Task Sans_le_pack_G_le_certificat_est_renouvele_dans_les_60_jours_de_son_echeance()
    {
        // Décision du titulaire du 2026-11-28 : sans renouvellement, plus aucun code d'offre ne descendrait par le central à l'échéance
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        var envoi = Envoi(b, c, s);
        Assert.Null((await envoi.ExecuterSansPackAsync(Maintenant, envoyerResultats: false)).Erreur);
        Assert.Equal(0, s.Renouvellements);                             // échéance lointaine : rien

        c.Info = c.Info! with { EcheanceUtc = Maintenant.AddDays(30) };
        Assert.Null((await envoi.ExecuterSansPackAsync(Maintenant, envoyerResultats: false)).Erreur);
        Assert.Equal((1, "CERT"), (s.Renouvellements, c.Installe?[..4]));

        // Refus : une note, jamais une erreur du passage
        c.Info = c.Info! with { EcheanceUtc = Maintenant.AddDays(30) };
        s.RefusRenouvellement = new ServeurCentralException("refusé par le serveur central", 422);
        var refuse = await envoi.ExecuterSansPackAsync(Maintenant, envoyerResultats: false);
        Assert.Null(refuse.Erreur);
        Assert.Contains("non renouvelé", b.Envois[^1]);
    }

    [Fact]
    public async Task Sans_le_pack_G_un_central_injoignable_n_est_pas_note_comme_un_passage_reussi()
    {
        // Correction du 2026-11-28 : l'échange léger est le seul appel de la voie réduite ; son échec doit laisser une erreur visible
        // (Administration › Synchronisation, journal du serveur), puis disparaître au passage réussi suivant
        var (b, c, s) = (new Base(), new Certificat(), new Central());
        var envoi = Envoi(b, c, s);
        s.Coupure = true;
        var panne = await envoi.ExecuterSansPackAsync(Maintenant, envoyerResultats: false);
        Assert.StartsWith("Serveur central injoignable", panne.Erreur);
        Assert.False(panne.Bloquant);
        Assert.StartsWith("Serveur central injoignable", b.Envois[^1]);
        Assert.Empty(s.VersionsRecues);

        s.Coupure = false;
        var repris = await envoi.ExecuterSansPackAsync(Maintenant, envoyerResultats: false);
        Assert.Null(repris.Erreur);
        Assert.Null(b.Envois[^1]);
        Assert.NotEmpty(s.VersionsRecues);
    }
}
