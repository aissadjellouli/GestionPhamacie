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

        public Task<bool> SignalerRefusAsync(long id, string motif, CancellationToken ct = default)
        {
            if (Refus.Any(r => r.Item1 == id)) return Task.FromResult(false);
            Refus.Add((id, motif));
            return Task.FromResult(true);
        }
    }

    private static readonly ECDsa Cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static byte[] Publique => Cle.ExportSubjectPublicKeyInfo();

    private static string Code(int postes = 5, string[]? packs = null, DateOnly? fin = null, DateTime? emise = null, Guid? officine = null,
        string nif = Nif, bool transition = false, ECDsa? cle = null) =>
        Program.Signer(new ActivationOffre(officine ?? Officine, nif, emise ?? new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), packs ?? ["A", "D"],
            postes, 0, fin, transition), cle ?? Cle);

    private static (Depot D, OffreCourante O, GererOffre G, JournalEspion J) Banc(DateOnly? jour = null, bool titulaire = true)
    {
        var d = new Depot();
        var h = new HorlogeFixe(jour ?? Aujourdhui);
        var o = new OffreCourante(d, h, [Publique]);
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
    public async Task Echeance_rappel_a_30_jours_puis_15_jours_de_grace_puis_offre_de_base()
    {
        var fin = new DateOnly(2026, 12, 20);
        var (d, _, g, _) = Banc();
        await g.SaisirAsync(1, Code(postes: 4, fin: fin));

        async Task<EtatOffre> Le(DateOnly jour) => await new OffreCourante(d, new HorlogeFixe(jour), [Publique]).LireAsync();
        Assert.Contains("jusqu’au 20/12/2026 (24 jour(s))", (await Le(Aujourdhui)).Alerte);
        Assert.Null((await Le(new DateOnly(2026, 11, 1))).Alerte);
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
        var a = Program.Activation(["signer", "--officine", Officine.ToString(), "--nif", "0001-16001234567", "--postes", "10", "--transition", "--fin", "2027-05-31"],
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
            ["signer", "--officine", Officine.ToString(), "--nif", Nif, "--postes", "2", "--packs", "A,Z"], DateTime.UtcNow)));

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
        var (d, o, g, _) = Banc();
        var ordre = Guid.NewGuid();
        await g.RecevoirDuCentralAsync(Code(postes: 3), ordre);
        Assert.Equal(("central", (Guid?)ordre), (d.Lignes[0].Source, d.Lignes[0].OrdreCentral));
        Assert.Equal(3, await o.MaximumPostesAsync());
        await Assert.ThrowsAsync<RegleMetierException>(() => g.RecevoirDuCentralAsync(Code(officine: Guid.NewGuid()), Guid.NewGuid()));
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
        }
        await Tout();
        Assert.Equal(["sauvegarde", "synchronisation", "second_serveur"], espion.Envoyees);
        espion.Envoyees.Clear();
        await g.SaisirAsync(1, Code(packs: ["A", "E"]));
        await Tout();
        Assert.Equal(9, espion.Envoyees.Count);
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
    public void Sans_cle_publique_dans_le_programme_aucun_code_n_est_accepte()
    {
        var e = Assert.Throws<ActivationInvalideException>(() => CodeActivation.Lire(Code(), Array.Empty<byte[]>()));
        Assert.Contains("Clé de l’éditeur absente", e.Message);
    }
}
