using System.Security.Cryptography;
using Pharma.Application.Commun;
using Pharma.Application.Offre;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Editeur;

namespace Pharma.Application.Tests;

/// <summary>
/// Offre de base et options (décisions du titulaire du 2026-11-26, script 172) : code signé par l'éditeur, lié au code de l'officine et au
/// NIF ; sans activation valable, offre de base à 2 postes ; échéance avec 15 jours de grâce ; une activation modifiée ne donne rien.
/// </summary>
public class OffreTests
{
    private static readonly Guid Officine = Guid.Parse("3f2a0c4e-8b1d-4c7a-9e55-0a1b2c3d4e5f");
    private const string Nif = "000116001234567";
    private static readonly DateOnly Aujourdhui = new(2026, 11, 26);

    private sealed class Depot : IActivationsOffreDepot
    {
        public string? Nif { get; set; } = OffreTests.Nif;
        public Guid Officine { get; set; } = OffreTests.Officine;
        public List<ActivationEnregistree> Lignes { get; } = [];
        public List<(long, string)> Refus { get; } = [];

        public Task<IdentiteOffre> IdentiteAsync(CancellationToken ct = default) => Task.FromResult(new IdentiteOffre(Officine, Nif));
        public Task<IReadOnlyList<ActivationEnregistree>> ListerAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActivationEnregistree>>(Lignes.OrderByDescending(l => l.EmiseLeUtc).ThenByDescending(l => l.Id).ToList());

        public Task<bool> AjouterAsync(string code, DateTime emiseLeUtc, string source, long? saisiePar, Guid? ordreCentral, CancellationToken ct = default)
        {
            if (Lignes.Any(l => l.Code == code)) return Task.FromResult(false);
            Lignes.Add(new ActivationEnregistree(Lignes.Count + 1, code, emiseLeUtc, source, DateTime.UtcNow, null, ordreCentral, null, null));
            return Task.FromResult(true);
        }

        public HashSet<string> Rappels { get; } = [];
        public Task<bool> RappelNoteAsync(string etape, DateOnly fin, CancellationToken ct = default) => Task.FromResult(Rappels.Contains($"{etape}:{fin:yyyy-MM-dd}"));
        public Task<bool> NoterRappelAsync(string etape, DateOnly fin, CancellationToken ct = default) => Task.FromResult(Rappels.Add($"{etape}:{fin:yyyy-MM-dd}"));

        public Task<bool> SignalerRefusAsync(long id, string motif, CancellationToken ct = default)
        {
            if (Refus.Any(r => r.Item1 == id)) return Task.FromResult(false);
            Refus.Add((id, motif));
            return Task.FromResult(true);
        }

        /// <summary>Dernière date du journal fiscal des ventes (null : journal vide).</summary>
        public DateTime? DerniereFiscale { get; set; }
        public Task<DateTime?> DerniereDateFiscaleAsync(CancellationToken ct = default) => Task.FromResult(DerniereFiscale);

        /// <summary>Dates plancher notées au journal (offre_horloge_reculee), une fois chacune.</summary>
        public HashSet<DateTime> HorlogeReculee { get; } = [];
        public Task<bool> SignalerHorlogeReculeeAsync(DateTime plancherUtc, DateTime serveurUtc, CancellationToken ct = default) =>
            Task.FromResult(HorlogeReculee.Add(plancherUtc));

        /// <summary>Dates plancher notées au journal (offre_horloge_avance), une fois chacune.</summary>
        public HashSet<DateTime> HorlogeAvance { get; } = [];
        public Task<bool> SignalerHorlogeAvanceAsync(DateTime plancherUtc, DateTime serveurUtc, CancellationToken ct = default) =>
            Task.FromResult(HorlogeAvance.Add(plancherUtc));

        /// <summary>Marques de refus levées (offre_activation_refus_leve) : n° de l'activation.</summary>
        public List<long> Levees { get; } = [];
        public Task<bool> LeverRefusAsync(long id, CancellationToken ct = default)
        {
            var i = Lignes.FindIndex(l => l.Id == id);
            if (i < 0 || Lignes[i].RefuseeLeUtc is null) return Task.FromResult(false);
            Lignes[i] = Lignes[i] with { RefuseeLeUtc = null, MotifRefus = null };
            Refus.RemoveAll(r => r.Item1 == id);
            Levees.Add(id);
            return Task.FromResult(true);
        }
    }

    private static readonly ECDsa Cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static byte[] Publique => Cle.ExportSubjectPublicKeyInfo();

    private static string Code(int postes = 5, string[]? packs = null, DateOnly? fin = null, DateTime? emise = null, Guid? officine = null,
        string nif = Nif, bool transition = false, ECDsa? cle = null, string[]? serveurs = null) =>
        Program.Signer(new ActivationOffre(officine ?? Officine, nif, emise ?? new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), packs ?? ["A", "D"],
            postes, 0, fin, transition, serveurs), cle ?? Cle);

    /// <summary>Clé de serveur en mémoire (doublure de la clé TPM). PubliqueAnnoncee : clé publique annoncée à la place de la vraie (tricherie).</summary>
    private sealed class CleServeurFactice : ICleServeur
    {
        public ECDsa? Cle { get; set; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[]? PubliqueAnnoncee { get; set; }
        /// <summary>Protection annoncée de la clé (null : inconnue, comme une doublure qui ne le dit pas).</summary>
        public bool? EstMaterielle { get; set; }
        public bool? Materielle() => EstMaterielle;
        /// <summary>Clé présente mais illisible par ce compte (API lancée sans droits d'administrateur).</summary>
        public bool Illisible { get; set; }
        public bool AccesRefuse() => Illisible;
        public List<byte[]> Defis { get; } = [];
        public string Empreinte => EmpreinteServeur.Calculer(ClePublique()!);
        public byte[]? ClePublique() => PubliqueAnnoncee ?? Cle?.ExportSubjectPublicKeyInfo();
        public byte[]? Signer(byte[] donnees)
        {
            Defis.Add(donnees);
            return Cle?.SignData(donnees, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    /// <summary>Clé de CE serveur dans les bancs d'essai (les codes version 2 des tests la déclarent par défaut).</summary>
    private static readonly CleServeurFactice ServeurLocal = new();

    private static (Depot D, OffreCourante O, GererOffre G, JournalEspion J) Banc(DateOnly? jour = null, bool titulaire = true)
    {
        var d = new Depot();
        var h = new HorlogeFixe(jour ?? Aujourdhui);
        var o = new OffreCourante(d, h, [Publique], cleServeur: ServeurLocal);
        var j = new JournalEspion();
        return (d, o, new GererOffre(d, o, new DroitsFactices { Titulaire = titulaire }, j, h), j);
    }

    [Fact]
    public async Task Sans_activation_offre_de_base_a_deux_postes()
    {
        var (_, o, _, _) = Banc();
        var e = await o.LireAsync();
        Assert.Equal((EtatsOffre.Base, 2, 0), (e.Etat, e.Postes, e.Packs.Count));
        Assert.Equal(2, await o.MaximumPostesAsync());
    }

    [Fact]
    public async Task Un_code_signe_pour_cette_pharmacie_donne_ses_postes_et_ses_packs_et_va_au_journal()
    {
        var (d, o, g, j) = Banc();
        await g.SaisirAsync(1, Code(postes: 5, packs: ["D", "A"]));
        var e = await o.LireAsync();
        Assert.Equal((EtatsOffre.Active, 5), (e.Etat, e.Postes));
        Assert.Equal(["A", "D"], e.Packs);
        Assert.Null(e.Alerte);
        Assert.Contains(GererOffre.ActionActivee, j.Actions);
        Assert.Equal("code", Assert.Single(d.Lignes).Source);
    }

    [Fact]
    public async Task Un_code_d_une_autre_pharmacie_d_un_autre_NIF_ou_d_une_autre_cle_est_refuse()
    {
        var (_, _, g, _) = Banc();
        Assert.Contains("autre pharmacie", (await Assert.ThrowsAsync<RegleMetierException>(() => g.SaisirAsync(1, Code(officine: Guid.NewGuid())))).Message);
        Assert.Contains("autre NIF", (await Assert.ThrowsAsync<RegleMetierException>(() => g.SaisirAsync(1, Code(nif: "999999999")))).Message);
        using var autre = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Contains("Signature fausse", (await Assert.ThrowsAsync<RegleMetierException>(() => g.SaisirAsync(1, Code(cle: autre)))).Message);
        Assert.Equal("code", (await Assert.ThrowsAsync<RegleMetierException>(() => g.SaisirAsync(1, "PO1.abc"))).Champ);
    }

    [Fact]
    public async Task Le_NIF_doit_etre_saisi_et_se_compare_sans_espaces_ni_tirets()
    {
        var (d, o, g, _) = Banc();
        d.Nif = null;
        Assert.Contains("NIF de la pharmacie non saisi", (await Assert.ThrowsAsync<RegleMetierException>(() => g.SaisirAsync(1, Code()))).Message);
        d.Nif = "0001 1600-1234567";
        await g.SaisirAsync(1, Code());
        Assert.Equal(EtatsOffre.Active, (await o.LireAsync()).Etat);
    }

    [Fact]
    public async Task Un_contenu_modifie_dans_la_base_ramene_a_l_offre_de_base_et_est_signale_une_fois()
    {
        var (d, o, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 5));
        // Quelqu'un remplace le contenu signé (5 postes) par 50 postes, sans pouvoir refaire la signature
        var morceaux = d.Lignes[0].Code.Split('.');
        var faux = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(morceaux[1].Replace('-', '+').Replace('_', '/').PadRight((morceaux[1].Length + 3) / 4 * 4, '=')))!;
        faux["postes"] = 50;
        var contenu = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(faux.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        d.Lignes[0] = d.Lignes[0] with { Code = $"PO1.{contenu}.{morceaux[2]}" };

        var e = await o.LireAsync();
        Assert.Equal((EtatsOffre.Base, 2), (e.Etat, e.Postes));
        Assert.Contains("Signature fausse", e.Alerte);
        await o.LireAsync();
        Assert.Single(d.Refus);
    }

    [Fact]
    public async Task Echeance_message_des_90_jours_puis_15_jours_de_grace_puis_offre_de_base()
    {
        var fin = new DateOnly(2026, 12, 20);
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 4, fin: fin));

        async Task<EtatOffre> Le(DateOnly jour) => await new OffreCourante(d, new HorlogeFixe(jour), [Publique]).LireAsync();
        Assert.Contains("jusqu’au 20/12/2026 (24 jour(s))", (await Le(Aujourdhui)).Alerte);
        Assert.NotNull((await Le(new DateOnly(2026, 9, 21))).Alerte);   // 90 jours
        Assert.Null((await Le(new DateOnly(2026, 9, 20))).Alerte);      // 91 jours
        var grace = await Le(new DateOnly(2026, 12, 30));
        Assert.Equal((EtatsOffre.Grace, 4), (grace.Etat, grace.Postes));
        Assert.Contains("délai de grâce jusqu’au 04/01/2027", grace.Alerte);
        Assert.Equal((EtatsOffre.Active, 4), ((await Le(new DateOnly(2027, 1, 4))) is var dernier ? (EtatsOffre.Active, dernier.Postes) : default));
        var apres = await Le(new DateOnly(2027, 1, 5));
        Assert.Equal((EtatsOffre.Base, 2), (apres.Etat, apres.Postes));
    }

    [Fact]
    public async Task Le_code_le_plus_recent_l_emporte_un_plus_ancien_ou_echu_ou_deja_saisi_est_refuse()
    {
        var (_, o, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 3, emise: new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc)));
        await g.SaisirAsync(1, Code(postes: 6, emise: new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(6, await o.MaximumPostesAsync());
        Assert.Contains("plus ancien", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            g.SaisirAsync(1, Code(postes: 50, emise: new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc))))).Message);
        Assert.Contains("échu", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            g.SaisirAsync(1, Code(postes: 9, fin: new DateOnly(2026, 11, 1), emise: new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc))))).Message);
        var code = Code(postes: 7, emise: new DateTime(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc));
        await g.SaisirAsync(1, code);
        Assert.Contains("déjà enregistré", (await Assert.ThrowsAsync<RegleMetierException>(() => g.SaisirAsync(1, code))).Message);
    }

    [Fact]
    public async Task Code_de_transition_tout_compris_et_seul_le_titulaire_saisit()
    {
        var (_, o, g, _) = Banc();
        var a = Program.Activation(["signer", "--officine", Officine.ToString(), "--nif", "0001-16001234567", "--postes", "10", "--transition", "--fin", "2027-05-31",
                "--serveur", EmpreinteServeur.Afficher(ServeurLocal.Empreinte)],
            new DateTime(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc));
        Assert.Equal(8, a.Packs.Count);
        await g.SaisirAsync(1, Program.Signer(a, Cle));
        var e = await o.LireAsync();
        Assert.True(e.Transition);
        Assert.Equal(10, e.Postes);
        await Assert.ThrowsAsync<DroitRefuseException>(() => Banc(titulaire: false).G.SaisirAsync(2, Code()));
        Assert.Throws<ActivationInvalideException>(() => CodeActivation.Controler(a with { Fin = null }));
    }

    [Fact]
    public void L_outil_controle_ses_options_et_la_cle_chiffree_se_relit_avec_son_mot_de_passe()
    {
        Assert.Throws<ArgumentException>(() => Program.Activation(["signer", "--officine", "pas-un-code", "--nif", Nif, "--postes", "2"], DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => Program.Activation(["signer", "--officine", Officine.ToString(), "--nif", Nif], DateTime.UtcNow));
        Assert.Throws<ActivationInvalideException>(() => CodeActivation.Controler(Program.Activation(
            ["signer", "--officine", Officine.ToString(), "--nif", Nif, "--postes", "2", "--packs", "A,Z", "--serveur", ServeurLocal.Empreinte], DateTime.UtcNow)));

        var dossier = Path.Combine(Path.GetTempPath(), "offre-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<ArgumentException>(() => Program.GenererCle(dossier, "court"));
            Program.GenererCle(dossier, "mot de passe d'essai 172");
            Assert.Throws<ArgumentException>(() => Program.GenererCle(dossier, "mot de passe d'essai 172"));   // jamais écrasée
            Assert.Contains("ENCRYPTED PRIVATE KEY", File.ReadAllText(Path.Combine(dossier, Program.FichierCle)));
            Assert.Throws<ArgumentException>(() => Program.Charger(Path.Combine(dossier, Program.FichierCle), "mauvais mot de passe"));
            using var cle = Program.Charger(Path.Combine(dossier, Program.FichierCle), "mot de passe d'essai 172");
            var publique = Convert.FromBase64String(File.ReadAllText(Path.Combine(dossier, Program.FichierPublique)).Trim());
            Assert.Equal(5, CodeActivation.Lire(Code(cle: cle), publique).Postes);
        }
        finally { Directory.Delete(dossier, true); }
    }

    [Fact]
    public async Task Un_code_transmis_par_le_central_est_controle_comme_une_saisie()
    {
        var (d, o, g, j) = Banc();
        var ordre = Guid.NewGuid();
        await g.RecevoirDuCentralAsync(Code(postes: 3), ordre);
        Assert.Equal(("central", (Guid?)ordre), (d.Lignes[0].Source, d.Lignes[0].OrdreCentral));
        Assert.Equal(3, await o.MaximumPostesAsync());
        // Même entrée de journal que la saisie, sans utilisateur local, source « central » et identifiant de l'ordre
        Assert.Equal([GererOffre.ActionActivee], j.SansUtilisateur);
        Assert.Equal(GererOffre.SourceCentral, j.Details[0]["source"]);
        Assert.Equal(ordre.ToString("D"), j.Details[0]["ordre"]);
        Assert.Equal(3, j.Details[0]["postes"]);
        await Assert.ThrowsAsync<RegleMetierException>(() => g.RecevoirDuCentralAsync(Code(officine: Guid.NewGuid()), Guid.NewGuid()));
        Assert.Single(j.Actions);   // code refusé : rien au journal
    }

    // ---- Lot O6 : packs ----------------------------------------------------------------------------------------------------------------

    private sealed class AlertesEspion : Pharma.Application.Notifications.IAlertesTitulaire
    {
        public List<string> Envoyees { get; } = [];
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) { Envoyees.Add("ecart_caisse"); return Task.CompletedTask; }
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) { Envoyees.Add("lot_bloque"); return Task.CompletedTask; }
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) { Envoyees.Add("sauvegarde"); return Task.CompletedTask; }
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) { Envoyees.Add("inventaire"); return Task.CompletedTask; }
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) { Envoyees.Add("annulations"); return Task.CompletedTask; }
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) { Envoyees.Add("lot_delaisse"); return Task.CompletedTask; }
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) { Envoyees.Add("surveillance"); return Task.CompletedTask; }
        public Task SynchronisationAsync(string objet, string detail, string sms, CancellationToken ct = default) { Envoyees.Add("synchronisation"); return Task.CompletedTask; }
        public Task SecondServeurAsync(string objet, string detail, string sms, CancellationToken ct = default) { Envoyees.Add("second_serveur"); return Task.CompletedTask; }
        /// <summary>Messages mis en file à chaque alerte d'offre (0 : aucun destinataire).</summary>
        public int MessagesOffre { get; set; } = 1;
        public Task<int> OffreAsync(string objet, string detail, string sms, CancellationToken ct = default) { Envoyees.Add("offre:" + objet); return Task.FromResult(MessagesOffre); }
    }

    [Fact]
    public async Task Packs_inclus_selon_l_offre_aucun_sans_activation()
    {
        var (_, o, g, _) = Banc();
        Assert.False(await o.InclusAsync(PacksOffre.Finances));
        await g.SaisirAsync(1, Code(packs: ["A", "D"]));
        Assert.True(await o.InclusAsync(PacksOffre.Finances));
        Assert.False(await o.InclusAsync(PacksOffre.Comptoir));
        await Assert.ThrowsAsync<OptionNonIncluseException>(() => o.ExigerPackAsync(PacksOffre.Comptoir));
        Assert.Contains("pack H, Confort du comptoir", new OptionNonIncluseException("H").Message);
        // Consultables une fois fermés : B, C, D et les réglages du 2e serveur (F) ; A fermé entièrement (anomalies cachées, décision 1)
        Assert.Equal(["B", "C", "D", "F"], PacksOffre.Tous.Select(p => p.Code).Where(PacksOffre.LectureSeuleSiFerme));
    }

    [Fact]
    public async Task L_offre_est_gardee_une_minute_et_relue_apres_une_saisie()
    {
        var d = new Depot();
        var cache = new CacheOffre();
        var h = new HorlogeFixe(Aujourdhui);
        var o = new OffreCourante(d, h, [Publique], cache);
        var g = new GererOffre(d, o, new DroitsFactices { Titulaire = true }, new JournalEspion(), h);
        Assert.Equal(2, await o.MaximumPostesAsync());
        await g.SaisirAsync(1, Code(postes: 8));          // la saisie vide le cache
        Assert.Equal(8, await o.MaximumPostesAsync());
        d.Lignes.Clear();                                  // effacé dans la base : vu au plus tard une minute après
        Assert.Equal(8, await o.MaximumPostesAsync());
        Assert.Equal(2, await new OffreCourante(d, h, [Publique], new CacheOffre()).MaximumPostesAsync());
    }

    [Fact]
    public async Task Alertes_metier_selon_les_packs_alertes_de_securite_toujours()
    {
        var (_, o, g, _) = Banc();
        var espion = new AlertesEspion();
        var alertes = new Pharma.Application.Notifications.AlertesTitulaireSelonOffre(espion, o);
        async Task Tout()
        {
            await alertes.EcartCaisseAsync("x", "p", 10, 5); await alertes.LotBloqueAsync("p", "l", 1, 1); await alertes.EcartsInventaireAsync("r", 1, 1, 1);
            await alertes.AnnulationsAsync("p", 3, 2); await alertes.LotDelaisseAsync("m", false); await alertes.SurveillanceAsync("m", false);
            await alertes.SauvegardeEnEchecAsync(true, "d"); await alertes.SynchronisationAsync("o", "d", "s"); await alertes.SecondServeurAsync("o", "d", "s");
            await alertes.SecuriteAsync("journal rompu", true);
        }
        await Tout();
        // Sans aucun pack : la sécurité part toujours (journal rompu, poste révoqué, codes faux : même envoi qu'un point de contrôle)
        Assert.Equal(["sauvegarde", "synchronisation", "second_serveur", "surveillance"], espion.Envoyees);
        espion.Envoyees.Clear();
        await g.SaisirAsync(1, Code(packs: ["A", "E"]));
        await Tout();
        Assert.Equal(10, espion.Envoyees.Count);
    }

    [Fact]
    public async Task La_cle_de_secours_signe_aussi_sans_que_l_offre_retombe_a_la_base()
    {
        using var secours = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var d = new Depot();
        var h = new HorlogeFixe(Aujourdhui);
        var o = new OffreCourante(d, h, [Publique, secours.ExportSubjectPublicKeyInfo()]);
        var g = new GererOffre(d, o, new DroitsFactices { Titulaire = true }, new JournalEspion(), h);
        await g.SaisirAsync(1, Code(postes: 4, emise: new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc)));
        // Clé principale perdue : un code signé avec la clé de secours est accepté
        await g.SaisirAsync(1, Code(postes: 7, emise: new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc), cle: secours));
        Assert.Equal(7, await o.MaximumPostesAsync());
        // Une clé inconnue de la liste : refusée
        using var etrangere = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Contains("Signature fausse", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            g.SaisirAsync(1, Code(emise: new DateTime(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc), cle: etrangere)))).Message);
        // Clé principale retirée de la liste (volée) : ses codes ne valent plus, ceux de la clé de secours restent
        var sansPrincipale = new OffreCourante(d, h, [secours.ExportSubjectPublicKeyInfo()]);
        Assert.Equal(7, await sansPrincipale.MaximumPostesAsync());
    }

    [Fact]
    public void Les_cles_du_programme_sont_vides_ou_lisibles()
    {
        foreach (var cle in CleEditeur.Publiques)
        {
            using var e = ECDsa.Create();
            e.ImportSubjectPublicKeyInfo(cle, out _);
            Assert.Equal(256, e.KeySize);
        }
        Assert.True(CleEditeur.PrincipaleBase64 != CleEditeur.SecoursBase64 || CleEditeur.PrincipaleBase64.Length == 0,
            "La clé de secours doit être une autre clé que la principale.");
    }

    [Fact]
    public void Etape_du_rappel_de_fin_d_offre()
    {
        var fin = new DateOnly(2026, 12, 31);
        string? Le(int joursAvant) => RappelsOffre.Etape(fin, fin.AddDays(-joursAvant));
        Assert.Null(Le(91));
        Assert.Equal(["90j", "90j", "60j", "60j", "30j", "30j", "15j", "15j", "7j", "7j", "1j", "1j"],
            new[] { 90, 61, 60, 31, 30, 16, 15, 8, 7, 2, 1, 0 }.Select(Le));
        Assert.Equal(RappelsOffre.EtapeGrace, Le(-1));
        Assert.Equal(RappelsOffre.EtapeGrace, Le(-15));
        Assert.Null(Le(-16));                                   // grâce passée : offre de base
    }

    [Fact]
    public async Task Un_rappel_sans_message_mis_en_file_n_est_pas_note_et_repart_le_lendemain()
    {
        var fin = new DateOnly(2027, 6, 30);
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(fin: fin));
        var espion = new AlertesEspion { MessagesOffre = 0 };   // aucun destinataire, ou mise en file impossible
        async Task<string?> Le(int joursAvant)
        {
            var h = new HorlogeFixe(fin.AddDays(-joursAvant));
            return await new RappelsOffre(new OffreCourante(d, h, [Publique]), d, espion, h).ExecuterAsync();
        }

        Assert.Null(await Le(30));
        Assert.Empty(d.Rappels);                    // étape non notée
        espion.MessagesOffre = 1;
        Assert.Equal("30j", await Le(29));          // retentée le lendemain, notée cette fois
        Assert.Equal(["30j:2027-06-30"], d.Rappels);
        Assert.Null(await Le(28));                  // plus de double
        Assert.Equal(2, espion.Envoyees.Count);
    }

    [Fact]
    public async Task Sept_rappels_une_seule_fois_chacun_seulement_le_plus_proche_et_jamais_perdus()
    {
        var fin = new DateOnly(2027, 6, 30);
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(fin: fin));
        var espion = new AlertesEspion();
        async Task<string?> Le(int joursAvant)
        {
            var h = new HorlogeFixe(fin.AddDays(-joursAvant));
            return await new RappelsOffre(new OffreCourante(d, h, [Publique]), d, espion, h).ExecuterAsync();
        }

        Assert.Null(await Le(120));
        var envoyes = new List<string?>();
        foreach (var j in new[] { 90, 89, 60, 45, 30, 20, 15, 10, 7, 3, 1, 0, -1, -2 }) envoyes.Add(await Le(j));
        Assert.Equal(new string?[] { "90j", null, "60j", null, "30j", null, "15j", null, "7j", null, "1j", null, "grace", null }, envoyes);
        Assert.Equal(7, espion.Envoyees.Count);

        // Offre saisie à 10 jours de sa fin : seul le rappel des 15 jours part
        var fin2 = new DateOnly(2027, 9, 30);
        var (d2, _, g2, _) = Banc();
        await g2.SaisirAsync(1, Code(fin: fin2));
        var h2 = new HorlogeFixe(fin2.AddDays(-10));
        Assert.Equal("15j", await new RappelsOffre(new OffreCourante(d2, h2, [Publique]), d2, espion, h2).ExecuterAsync());

        // Offre définitive : jamais de rappel
        var (d3, _, g3, _) = Banc();
        await g3.SaisirAsync(1, Code());
        Assert.Null(await new RappelsOffre(new OffreCourante(d3, new HorlogeFixe(Aujourdhui), [Publique]), d3, espion, new HorlogeFixe(Aujourdhui)).ExecuterAsync());
    }

    [Fact]
    public void Sans_cle_publique_dans_le_programme_aucun_code_n_est_accepte()
    {
        var e = Assert.Throws<ActivationInvalideException>(() => CodeActivation.Lire(Code(), Array.Empty<byte[]>()));
        Assert.Contains("Clé de l’éditeur absente", e.Message);
    }

    // ---- Point 5 du 2026-11-27 : horloge (date effective) ------------------------------------------------------------------------------

    [Fact]
    public void Une_date_du_central_dans_le_futur_n_est_retenue_que_si_le_central_la_confirme()
    {
        var t = Midi(Aujourdhui);
        // Horloge du central qui saute d'un an pendant quelques minutes : rien n'est mémorisé, et la date juste reprend ensuite
        var f = new PlancherFactice { Centrale = t };
        Assert.False(DateCentraleOffre.Noter(f, t.AddDays(365), t.AddHours(1)));
        Assert.False(DateCentraleOffre.Noter(f, t.AddDays(365).AddMinutes(30), t.AddHours(1)));   // pas encore une heure du central : toujours en attente
        Assert.Equal(t.AddDays(365), DateCentraleOffre.SautEnAttente(f));
        Assert.True(DateCentraleOffre.Noter(f, t.AddHours(2), t.AddHours(2)));                    // central corrigé : date juste mémorisée
        Assert.Equal(((DateTime?)t.AddHours(2), (DateTime?)null), (f.Centrale, DateCentraleOffre.SautEnAttente(f)));

        // Horloge de CE serveur reculée de 30 jours, aucun échange depuis : le central est cru à sa deuxième réponse, une heure plus tard
        var recule = new PlancherFactice { Centrale = t.AddDays(-30) };
        Assert.False(DateCentraleOffre.Noter(recule, t, t.AddDays(-30)));
        Assert.True(DateCentraleOffre.Noter(recule, t.AddHours(1), t.AddDays(-30)));
        Assert.Equal(t.AddHours(1), recule.Centrale);

        // Deux jours d'écart au plus (serveur arrêté un week-end) : mémorisée tout de suite ; première mise en service (rien de connu) : aussi
        var arrete = new PlancherFactice { Centrale = t.AddDays(-2) };
        Assert.True(DateCentraleOffre.Noter(arrete, t, t.AddMinutes(-5)));
        Assert.True(DateCentraleOffre.Noter(new PlancherFactice(), t.AddDays(365)));
    }

    [Fact]
    public async Task La_grace_du_serveur_non_declare_n_est_accordee_qu_une_fois_et_jamais_depuis_une_date_future()
    {
        // Base restaurée sur un autre PC avec deux codes liés à l'ancien serveur : le plus récent (5 postes) et un plus ancien (10 postes)
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 10, packs: ["A", "B", "C"], emise: new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), serveurs: [ServeurLocal.Empreinte]));
        await g.SaisirAsync(1, Code(postes: 5, packs: ["A", "D"], serveurs: [ServeurLocal.Empreinte]));
        var autrePc = new CleServeurFactice();
        var h = new HorlogeFixe(Aujourdhui);
        var recent = d.Lignes.FindIndex(l => l.EmiseLeUtc == d.Lignes.Max(x => x.EmiseLeUtc));
        Assert.Equal((EtatsOffre.Grace, 5), (await new OffreCourante(d, h, [Publique], cleServeur: autrePc).LireAsync()) is var e ? (e.Etat, e.Postes) : default);

        // Grâce du code le plus récent passée : offre de base, pas une seconde grâce sur le code plus ancien
        d.Lignes[recent] = d.Lignes[recent] with { RefuseeLeUtc = Midi(Aujourdhui.AddDays(-16)) };
        var apres = await new OffreCourante(d, h, [Publique], cleServeur: autrePc).LireAsync();
        Assert.Equal((EtatsOffre.Base, 2), (apres.Etat, apres.Postes));

        // Première constatation datée dans le futur (horloge avancée ce jour-là) : la grâce n'est pas prolongée d'autant
        d.Lignes[recent] = d.Lignes[recent] with { RefuseeLeUtc = Midi(Aujourdhui.AddDays(365)) };
        var futur = await new OffreCourante(d, h, [Publique], cleServeur: autrePc).LireAsync();
        Assert.Equal((EtatsOffre.Base, 2), (futur.Etat, futur.Postes));
    }

    [Fact]
    public async Task Un_code_version_1_plus_ancien_qu_un_code_lie_aux_serveurs_n_est_plus_pris()
    {
        // Base restaurée sur un autre PC : code de transition version 1 « tout compris » (10 postes), puis code acheté lié à l'ancien serveur
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 10, packs: ["A", "B", "C", "D"], emise: new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), transition: true,
            fin: Aujourdhui.AddDays(200)));
        await g.SaisirAsync(1, Code(postes: 5, packs: ["A", "D"], serveurs: [ServeurLocal.Empreinte]));
        var autrePc = new CleServeurFactice();
        var h = new HorlogeFixe(Aujourdhui);
        var recent = d.Lignes.FindIndex(l => l.EmiseLeUtc == d.Lignes.Max(x => x.EmiseLeUtc));
        d.Lignes[recent] = d.Lignes[recent] with { RefuseeLeUtc = Midi(Aujourdhui.AddDays(-16)) };   // grâce du code acheté passée
        var apres = await new OffreCourante(d, h, [Publique], cleServeur: autrePc).LireAsync();
        Assert.Equal((EtatsOffre.Base, 2), (apres.Etat, apres.Postes));                              // pas « tout compris, 10 postes »

        // Sur le serveur déclaré, rien ne change : le code acheté est actif
        var declare = await new OffreCourante(d, h, [Publique], cleServeur: ServeurLocal).LireAsync();
        Assert.Equal((EtatsOffre.Active, 5), (declare.Etat, declare.Postes));

        // Un code version 1 seul (aucun code lié plus récent) reste pris
        var (d1, o1, g1, _) = Banc();
        await g1.SaisirAsync(1, Code(postes: 8));
        Assert.Equal(8, await o1.MaximumPostesAsync());
    }

    private sealed class PlancherFactice : IDatePlancherFichier
    {
        public DateTime? Valeur { get; set; }
        public int Ecritures { get; private set; }
        public DateTime? Lire() => Valeur;
        public void Ecrire(DateTime utc) { Valeur = utc; Ecritures++; }
        public DateTime? Centrale { get; set; }
        public int EcrituresCentrale { get; private set; }
        public DateTime? LireCentrale() => Centrale;
        public void NoterCentrale(DateTime utc) { Centrale = utc; EcrituresCentrale++; }
    }

    private static DateTime Midi(DateOnly jour) => jour.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

    [Fact]
    public void Date_effective_max_du_serveur_et_du_plancher_alerte_au_dela_d_une_heure()
    {
        var serveur = Midi(Aujourdhui);
        var enAvance = DateEffectiveOffre.Calculer(serveur, Aujourdhui, serveur.AddDays(-3), serveur.AddDays(-1));
        Assert.Equal((serveur, Aujourdhui, false), (enAvance.EffectiveUtc, enAvance.Aujourdhui, enAvance.Reculee));
        Assert.Equal(serveur.AddDays(-1), enAvance.PlancherUtc);                       // la plus grande des deux

        var recul = DateEffectiveOffre.Calculer(serveur, Aujourdhui, Midi(Aujourdhui.AddDays(40)), null);
        Assert.Equal((Midi(Aujourdhui.AddDays(40)), Aujourdhui.AddDays(40), true), (recul.EffectiveUtc, recul.Aujourdhui, recul.Reculee));

        var leger = DateEffectiveOffre.Calculer(serveur, Aujourdhui, null, serveur.AddMinutes(50));   // 50 minutes : toléré, sans alerte
        Assert.Equal((serveur.AddMinutes(50), false), (leger.EffectiveUtc, leger.Reculee));
        Assert.Equal((serveur, (DateTime?)null), (DateEffectiveOffre.Calculer(serveur, Aujourdhui, null, null).EffectiveUtc, DateEffectiveOffre.Calculer(serveur, Aujourdhui, null, null).PlancherUtc));

        // Fichier mis à jour au plus une fois par jour, jamais vers le passé
        Assert.Equal(serveur, DateEffectiveOffre.AEcrire(serveur, null));
        Assert.Null(DateEffectiveOffre.AEcrire(serveur.AddHours(5), serveur));
        Assert.Equal(serveur.AddDays(1), DateEffectiveOffre.AEcrire(serveur.AddDays(1), serveur));
        Assert.Null(DateEffectiveOffre.AEcrire(serveur.AddDays(-10), serveur));
    }

    [Fact]
    public async Task Reculer_l_horloge_ne_prolonge_pas_l_offre_et_l_alerte_n_est_notee_qu_une_fois()
    {
        var fin = new DateOnly(2026, 12, 20);   // grâce jusqu'au 04/01/2027
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 6, fin: fin));
        // Le journal fiscal a déjà vu le 10/01/2027 : l'horloge du serveur remise au 26/11/2026 ne rouvre rien
        d.DerniereFiscale = Midi(new DateOnly(2027, 1, 10));
        var h = new HorlogeFixe(Aujourdhui);
        var e = await new OffreCourante(d, h, [Publique]).LireAsync();
        Assert.Equal((EtatsOffre.Base, 2, (DateOnly?)new DateOnly(2027, 1, 10)), (e.Etat, e.Postes, e.Jour));
        Assert.Contains("L’horloge de ce serveur retarde", e.AlerteHorloge);
        await new OffreCourante(d, h, [Publique]).LireAsync();
        Assert.Single(d.HorlogeReculee);                                    // une seule fois au journal, le message reste

        // Même chose avec la date gardée hors de la base seulement (journal fiscal vide, ou effacé)
        d.DerniereFiscale = null;
        var fichier = new PlancherFactice { Valeur = Midi(new DateOnly(2026, 12, 28)) };
        var grace = await new OffreCourante(d, h, [Publique], plancher: fichier).LireAsync();
        Assert.Equal((EtatsOffre.Grace, 6), (grace.Etat, grace.Postes));      // le 28/12 : délai de grâce, pas « active »
        Assert.NotNull(grace.AlerteHorloge);
        Assert.Equal(0, fichier.Ecritures);                                 // rien de plus récent à écrire

        // Horloge juste : pas d'alerte, le fichier avance au plus une fois par jour
        var juste = new PlancherFactice();
        var h2 = new HorlogeFixe(new DateOnly(2026, 12, 1));
        var ok = await new OffreCourante(d, h2, [Publique], plancher: juste).LireAsync();
        Assert.Equal((EtatsOffre.Active, (string?)null), (ok.Etat, ok.AlerteHorloge));
        await new OffreCourante(d, h2, [Publique], plancher: juste).LireAsync();
        Assert.Equal((1, (DateTime?)h2.MaintenantUtc), (juste.Ecritures, juste.Valeur));
    }

    [Fact]
    public async Task Rappels_et_saisie_suivent_le_jour_effectif()
    {
        var fin = new DateOnly(2027, 6, 30);
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(fin: fin));
        // Serveur à 40 jours de la fin, journal fiscal à 7 jours : c'est le rappel des 7 jours qui part
        d.DerniereFiscale = Midi(fin.AddDays(-7));
        var h = new HorlogeFixe(fin.AddDays(-40));
        var espion = new AlertesEspion();
        Assert.Equal("7j", await new RappelsOffre(new OffreCourante(d, h, [Publique]), d, espion, h).ExecuterAsync());

        // Code échu d'après le jour effectif : refusé même si l'horloge du serveur a été reculée
        var (d2, _, _, _) = Banc();
        d2.DerniereFiscale = Midi(new DateOnly(2027, 3, 1));
        var o2 = new OffreCourante(d2, new HorlogeFixe(Aujourdhui), [Publique], cleServeur: ServeurLocal);
        var g2 = new GererOffre(d2, o2, new DroitsFactices { Titulaire = true }, new JournalEspion(), new HorlogeFixe(Aujourdhui));
        Assert.Contains("Code échu", (await Assert.ThrowsAsync<RegleMetierException>(() => g2.SaisirAsync(1, Code(fin: new DateOnly(2027, 1, 31))))).Message);
    }

    // ---- Point 5 du 2026-11-27 : matériel (codes version 2 liés aux serveurs) --------------------------------------------------------

    [Fact]
    public async Task Un_code_version_1_reste_accepte_mais_signale_non_lie_au_serveur()
    {
        var (_, o, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 5));
        var e = await o.LireAsync();
        Assert.Equal((EtatsOffre.Active, 5, (string?)null), (e.Etat, e.Postes, e.Alerte));
        Assert.Equal(OffreCourante.MessageNonLie, e.AlerteServeur);
        Assert.Equal(ServeurLocal.Empreinte, e.EmpreinteServeur);
        Assert.Null(CodeActivation.LireSansControle(Code()).Serveurs);
    }

    [Fact]
    public async Task Un_code_version_2_qui_declare_ce_serveur_ouvre_l_offre_apres_un_defi_signe()
    {
        var (_, o, g, j) = Banc();
        var autre = new CleServeurFactice();
        var avant = ServeurLocal.Defis.Count;
        await g.SaisirAsync(1, Code(postes: 9, serveurs: [autre.Empreinte, ServeurLocal.Empreinte]));
        var e = await o.LireAsync();
        Assert.Equal((EtatsOffre.Active, 9, (string?)null, (string?)null), (e.Etat, e.Postes, e.Alerte, e.AlerteServeur));
        Assert.Equal(2, ((string)j.Details[0]["serveurs"]!).Split(',').Length);
        // Un défi par contrôle (saisie, évaluation), jamais le même
        var defis = ServeurLocal.Defis.Skip(avant).ToList();
        Assert.True(defis.Count >= 2);
        Assert.Equal(defis.Count, defis.Select(Convert.ToBase64String).Distinct().Count());
    }

    [Fact]
    public async Task Un_code_version_2_d_un_autre_serveur_est_refuse_a_la_saisie_avec_l_empreinte_de_ce_serveur()
    {
        var (d, _, g, _) = Banc();
        var ailleurs = new CleServeurFactice();
        var x = await Assert.ThrowsAsync<RegleMetierException>(() => g.SaisirAsync(1, Code(serveurs: [ailleurs.Empreinte])));
        Assert.Contains("Ce serveur n’est pas déclaré dans le code de l’offre", x.Message);
        Assert.Contains(EmpreinteServeur.Afficher(ServeurLocal.Empreinte), x.Message);
        Assert.Equal("code", x.Champ);
        Assert.Empty(d.Lignes);
    }

    [Fact]
    public async Task Activation_copiee_vers_un_autre_serveur_quinze_jours_de_grace_puis_offre_de_base()
    {
        // Base restaurée sur un autre PC : le code rangé déclare l'ancien serveur, pas celui-ci
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 7, packs: ["A", "B"], serveurs: [ServeurLocal.Empreinte]));
        var autrePc = new CleServeurFactice();
        var h = new HorlogeFixe(Aujourdhui);
        var e = await new OffreCourante(d, h, [Publique], cleServeur: autrePc).LireAsync();
        Assert.Equal((EtatsOffre.Grace, 7), (e.Etat, e.Postes));
        Assert.Contains($"l’empreinte {EmpreinteServeur.Afficher(autrePc.Empreinte)}", e.Alerte);
        Assert.Contains("Délai de grâce jusqu’au 11/12/2026", e.Alerte);
        Assert.Single(d.Refus);                                                 // noté une fois (refusee_le, journal)

        // Grâce comptée depuis la première constatation (refusee_le) : 16 jours après, offre de base
        d.Lignes[0] = d.Lignes[0] with { RefuseeLeUtc = Midi(Aujourdhui.AddDays(-16)) };
        var apres = await new OffreCourante(d, h, [Publique], cleServeur: autrePc).LireAsync();
        Assert.Equal((EtatsOffre.Base, 2), (apres.Etat, apres.Postes));
        Assert.Contains("n’est pas déclaré", apres.Alerte);

        // Clé du serveur absente : même chemin, message de création de la clé
        var sansCle = await new OffreCourante(d, h, [Publique], cleServeur: new CleServeurFactice { Cle = null }).LireAsync();
        Assert.Equal(EtatsOffre.Base, sansCle.Etat);
        Assert.Contains("Clé de ce serveur absente", sansCle.Alerte);
        Assert.Null(sansCle.EmpreinteServeur);
        Assert.Contains("Clé de ce serveur absente", OffreCourante.MessageServeurNonDeclare(null));
    }

    [Fact]
    public async Task Cle_presente_mais_illisible_message_distinct_sans_inviter_a_la_recreer()
    {
        // Essai réel du 2026-11-27 : API lancée sans droits d'administrateur (Visual Studio), clé TPM présente mais « Accès refusé »
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 7, packs: ["A", "B"], serveurs: [ServeurLocal.Empreinte]));
        var illisible = new CleServeurFactice { Cle = null, Illisible = true };
        var e = await new OffreCourante(d, new HorlogeFixe(Aujourdhui), [Publique], cleServeur: illisible).LireAsync();
        Assert.Equal(EtatsOffre.Grace, e.Etat);
        Assert.Null(e.EmpreinteServeur);
        Assert.Equal(OffreCourante.MessageCleIllisible, e.AlerteCle);
        Assert.Contains("illisible", e.Alerte);
        Assert.DoesNotContain("créez-la", e.Alerte);
        Assert.Contains("ne recréez pas", OffreCourante.MessageServeurNonDeclare(null, accesRefuse: true));

        // Clé réellement absente : message de création inchangé, aucune alerte de clé
        var absente = await new OffreCourante(d, new HorlogeFixe(Aujourdhui), [Publique], cleServeur: new CleServeurFactice { Cle = null }).LireAsync();
        Assert.Contains("Clé de ce serveur absente", absente.Alerte);
        Assert.Null(absente.AlerteCle);
    }

    [Fact]
    public void La_preuve_exige_la_cle_privee_pas_seulement_l_empreinte()
    {
        var vrai = new CleServeurFactice();
        Assert.Equal(new PreuveServeur(vrai.Empreinte, true), PreuveCleServeur.Prouver(vrai, [vrai.Empreinte]));
        Assert.Equal(new PreuveServeur(vrai.Empreinte, false), PreuveCleServeur.Prouver(vrai, [new CleServeurFactice().Empreinte]));
        Assert.Equal(new PreuveServeur(null, false), PreuveCleServeur.Prouver(null, [vrai.Empreinte]));
        // Tricheur : annonce la clé publique du serveur déclaré, mais signe avec une autre clé
        var tricheur = new CleServeurFactice { PubliqueAnnoncee = vrai.ClePublique() };
        Assert.Equal(new PreuveServeur(vrai.Empreinte, false), PreuveCleServeur.Prouver(tricheur, [vrai.Empreinte]));
    }

    [Fact]
    public void Format_du_code_version_2_et_de_l_empreinte()
    {
        var e1 = ServeurLocal.Empreinte;
        Assert.Equal(32, e1.Length);
        var affichee = EmpreinteServeur.Afficher(e1);
        Assert.Matches("^([0-9A-F]{4}-){7}[0-9A-F]{4}$", affichee);
        Assert.Equal(e1, EmpreinteServeur.Normaliser(affichee.ToLowerInvariant()));
        Assert.Null(EmpreinteServeur.Normaliser("XYZ"));
        Assert.Null(EmpreinteServeur.Normaliser(e1 + "0"));

        var lu = CodeActivation.LireSansControle(Code(serveurs: [e1]));
        Assert.Equal([e1], lu.Serveurs);
        Assert.True(lu.LieeAuxServeurs);
        var a = new ActivationOffre(Officine, Nif, DateTime.UtcNow, [], 2, 0, null, false, [e1]);
        Assert.Throws<ActivationInvalideException>(() => CodeActivation.Controler(a with { Serveurs = [] }));
        Assert.Throws<ActivationInvalideException>(() => CodeActivation.Controler(a with { Serveurs = [e1, e1] }));
        Assert.Throws<ActivationInvalideException>(() => CodeActivation.Controler(a with { Serveurs = ["ABC"] }));
        var quatre = Enumerable.Range(0, 4).Select(_ => new CleServeurFactice().Empreinte).ToList();
        Assert.Throws<ActivationInvalideException>(() => CodeActivation.Controler(a with { Serveurs = quatre }));
        // Un code version 1 garde exactement son contenu d'avant (aucune clé « serveurs ») : les codes déjà signés se relisent
        Assert.DoesNotContain("serveurs", System.Text.Encoding.UTF8.GetString(CodeActivation.Contenu(a with { Serveurs = null })));
    }

    [Fact]
    public void L_outil_ne_signe_plus_que_des_codes_version_2()
    {
        string[] Base(params string[] plus) => ["signer", "--officine", Officine.ToString(), "--nif", Nif, "--postes", "3", .. plus];
        Assert.Contains("--serveur", Assert.Throws<ArgumentException>(() => Program.Activation(Base(), DateTime.UtcNow)).Message);
        Assert.Throws<ArgumentException>(() => Program.Activation(Base("--serveur", "pas-une-empreinte"), DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => Program.Activation(Base("--serveur", ServeurLocal.Empreinte, "--serveur", ServeurLocal.Empreinte.ToLowerInvariant()), DateTime.UtcNow));
        var quatre = Enumerable.Range(0, 4).SelectMany(_ => new[] { "--serveur", new CleServeurFactice().Empreinte }).ToArray();
        Assert.Throws<ArgumentException>(() => Program.Activation(Base(quatre), DateTime.UtcNow));
        var autre = new CleServeurFactice().Empreinte;
        var a = Program.Activation(Base("--serveur", EmpreinteServeur.Afficher(ServeurLocal.Empreinte).ToLowerInvariant(), "--serveur", autre), DateTime.UtcNow);
        Assert.Equal([ServeurLocal.Empreinte, autre], a.Serveurs);
        Assert.Equal(a.Serveurs!.Order(StringComparer.Ordinal), CodeActivation.Lire(Program.Signer(a, Cle), Publique).Serveurs!);
    }

    // ---- Corrections et compléments du point 5 (décisions du titulaire du 2026-11-27, points 3 et 4) -----------------------------------

    [Fact]
    public async Task Un_code_redevenu_valable_perd_sa_marque_de_refus_et_un_nouveau_refus_ouvre_un_nouveau_delai_de_grace()
    {
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 7, serveurs: [ServeurLocal.Empreinte]));
        var h = new HorlogeFixe(Aujourdhui);
        var autrePc = new CleServeurFactice();
        // Refus constaté il y a 16 jours (base restaurée sur un autre PC) : grâce passée, offre de base
        d.Lignes[0] = d.Lignes[0] with { RefuseeLeUtc = Midi(Aujourdhui.AddDays(-16)), MotifRefus = "Ce serveur n’est pas déclaré" };
        Assert.Equal(EtatsOffre.Base, (await new OffreCourante(d, h, [Publique], cleServeur: autrePc).LireAsync()).Etat);
        Assert.Empty(d.Levees);

        // Retour sur le serveur déclaré : le code repasse le contrôle, sa marque est levée (une seule fois, journal)
        var ok = await new OffreCourante(d, h, [Publique], cleServeur: ServeurLocal).LireAsync();
        Assert.Equal((EtatsOffre.Active, 7), (ok.Etat, ok.Postes));
        Assert.Equal([d.Lignes[0].Id], d.Levees);
        Assert.Null(d.Lignes[0].RefuseeLeUtc);
        Assert.Null(d.Lignes[0].MotifRefus);
        await new OffreCourante(d, h, [Publique], cleServeur: ServeurLocal).LireAsync();
        Assert.Single(d.Levees);

        // Nouveau refus : nouveau délai de grâce de 15 jours, compté depuis cette nouvelle constatation
        var e = await new OffreCourante(d, h, [Publique], cleServeur: autrePc).LireAsync();
        Assert.Equal((EtatsOffre.Grace, 7), (e.Etat, e.Postes));
        Assert.Contains("Délai de grâce jusqu’au 11/12/2026", e.Alerte);
    }

    [Fact]
    public async Task Un_code_valable_sans_marque_de_refus_ne_leve_rien()
    {
        var (d, o, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 4));
        await o.LireAsync();
        Assert.Empty(d.Levees);
        // Code échu (après la grâce) avec une marque : il n'est pas « redevenu valable », la marque reste
        var (d2, _, g2, _) = Banc();
        await g2.SaisirAsync(1, Code(fin: new DateOnly(2026, 12, 1)));
        d2.Lignes[0] = d2.Lignes[0] with { RefuseeLeUtc = Midi(Aujourdhui) };
        await new OffreCourante(d2, new HorlogeFixe(new DateOnly(2027, 2, 1)), [Publique]).LireAsync();
        Assert.Empty(d2.Levees);
    }

    [Fact]
    public void Horloge_en_avance_de_plus_de_deux_jours_sur_la_date_plancher_rien_n_est_ecrit()
    {
        var plancher = Midi(Aujourdhui);
        var juste = DateEffectiveOffre.Calculer(plancher.Add(DateEffectiveOffre.PasMax), Aujourdhui.AddDays(2), plancher, null);
        Assert.Equal((false, false), (juste.Avance, juste.Reculee));
        Assert.Equal(plancher.AddDays(2), juste.AEcrire(plancher));                    // 2 jours exactement : écrit
        var saut = DateEffectiveOffre.Calculer(plancher.AddDays(2).AddMinutes(1), Aujourdhui.AddDays(2), null, plancher);
        Assert.True(saut.Avance);
        Assert.Null(saut.AEcrire(plancher));                                          // au-delà : pas écrit
        Assert.Equal(plancher.AddDays(2).AddMinutes(1), saut.EffectiveUtc);           // l'offre se calcule quand même à la date du serveur
        // Première mise en service (aucune date plancher) : la date du serveur est écrite
        var premiere = DateEffectiveOffre.Calculer(plancher.AddYears(5), Aujourdhui.AddYears(5), null, null);
        Assert.Equal((false, (DateTime?)plancher.AddYears(5)), (premiere.Avance, premiere.AEcrire(null)));
        // La date du central compte dans la date plancher, pour le recul comme pour l'avance
        Assert.True(DateEffectiveOffre.Calculer(plancher, Aujourdhui, null, null, plancher.AddHours(3)).Reculee);
        Assert.True(DateEffectiveOffre.Calculer(plancher.AddDays(10), Aujourdhui.AddDays(10), plancher.AddDays(-20), null, plancher).Avance);
    }

    [Fact]
    public async Task Horloge_mise_par_erreur_loin_dans_le_futur_ne_bloque_pas_la_date_plancher_et_alerte_une_fois()
    {
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 6, fin: new DateOnly(2027, 6, 30)));
        d.DerniereFiscale = Midi(Aujourdhui.AddDays(-1));
        var fichier = new PlancherFactice { Valeur = Midi(Aujourdhui) };
        // Horloge du serveur mise en 2031 par erreur, avant toute vente
        var futur = new HorlogeFixe(new DateOnly(2031, 1, 15));
        var e = await new OffreCourante(d, futur, [Publique], plancher: fichier).LireAsync();
        Assert.Contains("L’horloge de ce serveur est en avance de", e.AlerteHorloge);
        Assert.Contains("avant d’encaisser", e.AlerteHorloge);
        await new OffreCourante(d, futur, [Publique], plancher: fichier).LireAsync();
        Assert.Single(d.HorlogeAvance);                                     // une seule fois au journal, le message reste
        Assert.Equal((0, (DateTime?)Midi(Aujourdhui)), (fichier.Ecritures, fichier.Valeur));   // le saut n'est pas écrit

        // Horloge corrigée : rien n'est resté dans le fichier, l'offre est de nouveau active, sans alerte, et le fichier reprend
        var corrigee = new HorlogeFixe(Aujourdhui.AddDays(1));
        var ok = await new OffreCourante(d, corrigee, [Publique], plancher: fichier).LireAsync();
        Assert.Equal((EtatsOffre.Active, (string?)null), (ok.Etat, ok.AlerteHorloge));
        Assert.Equal((1, (DateTime?)corrigee.MaintenantUtc), (fichier.Ecritures, fichier.Valeur));
    }

    [Fact]
    public async Task La_date_du_serveur_central_fait_foi_comme_date_plancher()
    {
        var fin = new DateOnly(2026, 12, 20);   // grâce jusqu'au 04/01/2027
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 6, fin: fin));
        // Journal fiscal vide, fichier ancien, mais le central a répondu le 10/01/2027 : l'horloge reculée au 26/11/2026 ne rouvre rien
        var fichier = new PlancherFactice { Valeur = Midi(Aujourdhui.AddDays(-5)), Centrale = Midi(new DateOnly(2027, 1, 10)) };
        var e = await new OffreCourante(d, new HorlogeFixe(Aujourdhui), [Publique], plancher: fichier).LireAsync();
        Assert.Equal((EtatsOffre.Base, (DateOnly?)new DateOnly(2027, 1, 10)), (e.Etat, e.Jour));
        Assert.Contains("L’horloge de ce serveur retarde", e.AlerteHorloge);
        // Sans central (jamais de date reçue) : rien ne change
        var sans = new PlancherFactice { Valeur = Midi(Aujourdhui.AddDays(-1)) };
        Assert.Equal((EtatsOffre.Active, (string?)null), ((await new OffreCourante(d, new HorlogeFixe(Aujourdhui), [Publique], plancher: sans).LireAsync()) is var s ? (s.Etat, s.AlerteHorloge) : default));
    }

    [Fact]
    public void La_date_du_central_est_memorisee_au_plus_une_fois_par_heure_et_jamais_en_arriere()
    {
        var f = new PlancherFactice();
        var t = Midi(Aujourdhui);
        Assert.True(DateCentraleOffre.Noter(f, t));
        Assert.False(DateCentraleOffre.Noter(f, t.AddMinutes(59)));
        Assert.False(DateCentraleOffre.Noter(f, t.AddDays(-2)));
        Assert.False(DateCentraleOffre.Noter(f, null));
        Assert.True(DateCentraleOffre.Noter(f, t.AddHours(1)));
        Assert.Equal((2, (DateTime?)t.AddHours(1)), (f.EcrituresCentrale, f.Centrale));
        Assert.Equal(0, f.Ecritures);                                       // la date plancher du fichier n'est pas touchée
    }

    [Fact]
    public async Task Mon_offre_avertit_d_une_cle_de_serveur_logicielle_sans_bloquer()
    {
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 5));
        var h = new HorlogeFixe(Aujourdhui);
        var logicielle = await new OffreCourante(d, h, [Publique], cleServeur: new CleServeurFactice { EstMaterielle = false }).LireAsync();
        Assert.Equal((EtatsOffre.Active, 5, OffreCourante.MessageCleLogicielle), (logicielle.Etat, logicielle.Postes, logicielle.AlerteCle));
        Assert.Null((await new OffreCourante(d, h, [Publique], cleServeur: new CleServeurFactice { EstMaterielle = true }).LireAsync()).AlerteCle);
        Assert.Null((await new OffreCourante(d, h, [Publique], cleServeur: new CleServeurFactice { EstMaterielle = null }).LireAsync()).AlerteCle);
        // Clé absente : l'alerte de la clé absente suffit, pas d'avertissement « logicielle »
        Assert.Null((await new OffreCourante(d, h, [Publique], cleServeur: new CleServeurFactice { Cle = null, EstMaterielle = false }).LireAsync()).AlerteCle);
        // Offre de base aussi
        var (_, ob, _, _) = Banc();
        Assert.Null((await ob.LireAsync()).AlerteCle);   // ServeurLocal ne dit pas sa protection
        Assert.Equal(OffreCourante.MessageCleLogicielle,
            (await new OffreCourante(new Depot(), h, [Publique], cleServeur: new CleServeurFactice { EstMaterielle = false }).LireAsync()).AlerteCle);
    }
}
