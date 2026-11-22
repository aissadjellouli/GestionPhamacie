using System.Text;
using Npgsql;
using Pharma.Cloud.Api;
using Pharma.Cloud.Api.Comptes;
using Pharma.Cloud.Api.Depart;
using Pharma.Shared;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Script central 040 : lecture du fichier CSV d'import de comptes (forme, séparateurs, BOM, doublons, 200 lignes au plus), e-mail de
/// l'identifiant (jamais le mot de passe), transmission (SMS ou manuel), console --creer-comptes tout ou rien sur une base JETABLE.
/// </summary>
public class ImportComptesTests
{
    private const string Entete = "role;identifiant;nom;email;telephone";

    private static LectureImportComptes Lire(string texte, bool bom = false) =>
        ImportComptesCsv.Lire((bom ? [0xEF, 0xBB, 0xBF] : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(texte)).ToArray());

    [Fact]
    public void Separateurs_bom_guillemets_entete_libre_et_normalisation()
    {
        var pointVirgule = Lire($"{Entete}\r\ntitulaire; Titulaire.Un ; Mme Un ;un@exemple.invalid;\r\n\r\nÉditeur;editeur.deux;M. Deux;;0555 000 002\r\n", bom: true);
        Assert.True(pointVirgule.Valable);
        Assert.Equal([new LigneCompteImport(2, "titulaire", "titulaire.un", "Mme Un", "un@exemple.invalid", null),
                      new LigneCompteImport(4, "editeur", "editeur.deux", "M. Deux", null, "0555000002")], pointVirgule.Lignes);
        // Virgule, colonnes dans un autre ordre, en-tête accentué, champ entre guillemets contenant le séparateur et un guillemet
        var virgule = Lire("Nom,Rôle,Identifiant,E-mail,Téléphone\n\"Dupont, \"\"Jean\"\"\",titulaire,jean.dupont,j@exemple.invalid,\n");
        Assert.True(virgule.Valable);
        Assert.Equal("Dupont, \"Jean\"", virgule.Lignes.Single().Nom);
        Assert.Equal("jean.dupont", virgule.Lignes.Single().Identifiant);
    }

    [Fact]
    public void Refus_du_fichier_entier()
    {
        Assert.Equal("vide", ImportComptesCsv.Lire([]).Refus);
        Assert.Equal("vide", Lire("\r\n  \r\n").Refus);
        Assert.Equal("vide", Lire(Entete + "\r\n").Refus);
        Assert.Equal("entete", Lire("role;identifiant;nom;email\nt;a.b.c;A;a@exemple.invalid").Refus);            // colonne manquante
        Assert.Equal("entete", Lire("role;identifiant;nom;email;telephone;autre\n").Refus);                         // colonne inconnue
        Assert.Equal("entete", Lire("role;role;nom;email;telephone\n").Refus);                                      // en double
        Assert.Equal("entete", Lire("role identifiant nom email telephone\n").Refus);                               // aucun séparateur
        Assert.Equal("encodage", ImportComptesCsv.Lire([.. Encoding.UTF8.GetBytes(Entete + "\n"), 0xC3, 0x28]).Refus);
        Assert.Equal("taille", ImportComptesCsv.Lire(new byte[ImportComptesCsv.OctetsMax + 1]).Refus);
        var deuxCents = Entete + "\n" + string.Join("\n", Enumerable.Range(1, 200).Select(i => $"titulaire;compte.{i:000};Nom {i};c{i}@exemple.invalid;"));
        Assert.True(Lire(deuxCents).Valable);
        Assert.Equal(200, Lire(deuxCents).Lignes.Count);
        Assert.Equal("trop_de_lignes", Lire(deuxCents + "\ntitulaire;compte.201;Nom;c@exemple.invalid;").Refus);
        foreach (var r in ImportComptesCsv.RefusFichier) Assert.True(ImportComptesCsv.LibellesRefus.ContainsKey(r));
    }

    [Fact]
    public void Statut_de_chaque_ligne_dans_l_ordre_de_la_base()
    {
        var l = Lire(string.Join("\n", Entete,
            "titulaire;bon.compte;Bon;b@exemple.invalid;",          // 2 ok
            "titulaire;BON.compte;Doublon;d@exemple.invalid;",      // 3 doublon (casse ignorée)
            "titulaire;x;Court;c@exemple.invalid;",                 // 4 identifiant
            "titulaire;sans.nom;;s@exemple.invalid;",               // 5 nom
            "admin;role.faux;Rôle;r@exemple.invalid;",              // 6 role
            "titulaire;sans.contact;Personne;;",                    // 7 contact
            "titulaire;mauvais.email;Adresse;pas-une-adresse;",     // 8 email
            "titulaire;mauvais.tel;Téléphone;;05-55",               // 9 telephone
            "titulaire;trop;de;colonnes;;;",                        // 10 colonnes
            "titulaire;\"guillemet;Ouvert;o@exemple.invalid;"))     // 11 forme
            .Lignes;
        Assert.Equal(["ok", "doublon", "identifiant", "nom", "role", "contact", "email", "telephone", "colonnes", "forme"], l.Select(x => x.Statut ?? "ok"));
        Assert.Equal(Enumerable.Range(2, 10), l.Select(x => x.Numero));
        foreach (var s in ImportComptesCsv.StatutsLigne) Assert.True(ImportComptesCsv.LibellesStatuts.ContainsKey(s), s);
        // Avis de la base : « existe » ajouté aux lignes valables seulement ; le refus de l'application prime
        var fusion = ImportComptesCsv.Fusionner(l, new Dictionary<int, string> { [2] = "existe", [3] = "ok" });
        Assert.Equal("existe", fusion[0].Statut);
        Assert.Equal("doublon", fusion[1].Statut);
        // Lignes envoyées à la base : forme JSON du script 040, empreinte seulement à la création
        var json = ImportComptesCsv.Json([l[0]], new Dictionary<int, string> { [2] = "$2a$12$x" });
        Assert.Contains("\"identifiant\":\"bon.compte\"", json);
        Assert.Contains("\"empreinte\":\"$2a$12$x\"", json);
        Assert.Contains("\"empreinte\":null", ImportComptesCsv.Json([l[0]]));
    }

    [Fact]
    public void Message_de_l_identifiant_sans_mot_de_passe_et_adresse_https_seulement()
    {
        var t = MessageIdentifiant.Texte("titulaire.un", "titulaire", "https://ecrans.exemple.invalid/", remise: false);
        Assert.Contains("Identifiant : titulaire.un", t);
        Assert.Contains("Le mot de passe provisoire vous sera transmis séparément", t);
        Assert.Contains("https://ecrans.exemple.invalid/", t);
        Assert.Contains(MessageIdentifiant.AdresseInconnue, MessageIdentifiant.Texte("a.b.c", "editeur", null, remise: true));
        Assert.Contains("remis à zéro", MessageIdentifiant.Texte("a.b.c", "editeur", null, remise: true));
        Assert.Null(MessageIdentifiant.Adresse("http://ecrans.exemple.invalid/"));
        Assert.Null(MessageIdentifiant.Adresse("javascript:alert(1)"));
        Assert.Null(MessageIdentifiant.Adresse("https://nom:secret@ecrans.exemple.invalid/"));
    }

    private sealed class CourrielFactice : IEnvoiCourriel
    {
        public List<(string Adresse, string Texte)> Envoyes { get; } = [];
        public bool EnPanne { get; set; }

        public Task EnvoyerAsync(string adresse, string objet, string texte, CancellationToken ct)
        {
            if (EnPanne) throw new IOException("serveur injoignable");
            lock (Envoyes) Envoyes.Add((adresse, objet + "\n" + texte));
            return Task.CompletedTask;
        }
    }

    private sealed class SmsFactice : IEnvoiSmsMotDePasse
    {
        public List<(string Telephone, string MotDePasse)> Envoyes { get; } = [];
        public bool Disponible => true;

        public Task EnvoyerAsync(string telephone, string identifiant, string motDePasse, CancellationToken ct)
        {
            lock (Envoyes) Envoyes.Add((telephone, motDePasse));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Transmission_identifiant_par_e_mail_mot_de_passe_par_sms_ou_manuel()
    {
        var comptes = new[]
        {
            new CompteATransmettre(Guid.NewGuid(), "a.mail", "titulaire", "a@exemple.test", null, "MotDePasseA23456"),
            new CompteATransmettre(Guid.NewGuid(), "b.tel", "editeur", null, "0555000001", "MotDePasseB23456"),
            new CompteATransmettre(Guid.NewGuid(), "c.deux", "titulaire", "c@exemple.test", "0555000002", "MotDePasseC23456"),
        };
        var courriel = new CourrielFactice();
        var notes = new List<string>();
        Task Noter(Guid c, string e, string o, CancellationToken _) { lock (notes) notes.Add(e + "|" + o); return Task.CompletedTask; }

        // Aucun fournisseur SMS : mot de passe « manuel » ; identifiant par e-mail seulement s'il y a une adresse
        var r = await TransmissionComptes.TransmettreAsync(comptes, true, courriel.EnvoyerAsync, new EnvoiSmsMotDePasseIndisponible(), null, false, Noter, CancellationToken.None);
        Assert.Equal(["ok", "sans_adresse", "ok"], r.Select(x => x.Email));
        Assert.Equal(["manuel_sans_telephone", "manuel", "manuel"], r.Select(x => x.Sms));
        Assert.Equal(2, courriel.Envoyes.Count);
        Assert.All(courriel.Envoyes, e => Assert.DoesNotContain("MotDePasse", e.Texte));      // jamais le mot de passe par e-mail
        Assert.Contains(courriel.Envoyes, e => e.Adresse == "a@exemple.test" && e.Texte.Contains("a.mail"));
        Assert.Equal(2, notes.Count(n => n == "identifiant_envoi|email:ok"));
        Assert.DoesNotContain(notes, n => n.Contains('@') || n.Contains("MotDePasse"));
        Assert.All(r, x => Assert.DoesNotContain(x.MotDePasse, x.ToString()));

        // Case décochée, e-mail en panne, fournisseur SMS en service
        var sms = new SmsFactice();
        courriel.EnPanne = true;
        r = await TransmissionComptes.TransmettreAsync(comptes, false, courriel.EnvoyerAsync, sms, null, false, null, CancellationToken.None);
        Assert.All(r, x => Assert.Equal("non_demande", x.Email));
        r = await TransmissionComptes.TransmettreAsync(comptes, true, courriel.EnvoyerAsync, sms, null, false, null, CancellationToken.None);
        Assert.Equal(["echec", "sans_adresse", "echec"], r.Select(x => x.Email));
        Assert.Equal(["manuel_sans_telephone", "envoye", "envoye"], r.Select(x => x.Sms));
        Assert.Contains(("0555000001", "MotDePasseB23456"), sms.Envoyes);
        // Aucun SMTP réglé : échec signalé, rien n'est annulé
        r = await TransmissionComptes.TransmettreAsync(comptes, true, null, sms, null, false, null, CancellationToken.None);
        Assert.Equal("echec", r[0].Email);
        Assert.Equal("échec", r[0].EmailTexte);
        Assert.Contains("à transmettre par SMS (manuel)", new CompteTransmis(Guid.Empty, "a", "titulaire", "x", "ok", "manuel").Copie);
    }

    [Fact]
    public async Task Adresse_invalid_ni_envoyee_ni_comptee_en_echec_ni_notee()
    {
        var comptes = new[] { new CompteATransmettre(Guid.NewGuid(), "m.mesure", "editeur", "mesure@exemple.invalid", null, "MotDePasseM23456") };
        var courriel = new CourrielFactice();
        var notes = new List<string>();
        Task Noter(Guid c, string e, string o, CancellationToken _) { lock (notes) notes.Add(e + "|" + o); return Task.CompletedTask; }
        var r = await TransmissionComptes.TransmettreAsync(comptes, true, courriel.EnvoyerAsync, new EnvoiSmsMotDePasseIndisponible(), null, false, Noter, CancellationToken.None);
        Assert.Equal("adresse_reservee", r[0].Email);
        Assert.Equal("non (adresse .invalid : rien envoyé)", r[0].EmailTexte);
        Assert.Empty(courriel.Envoyes);
        Assert.DoesNotContain(notes, n => n.StartsWith("identifiant_envoi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Serveur_d_e_mail_muet_la_liste_revient_dans_le_delai_total()
    {
        // 20 comptes, chaque envoi bloqué jusqu'à son délai (10 s) : sans délai total, 50 s ; avec 300 ms, la liste revient vite
        var comptes = Enumerable.Range(1, 20).Select(i =>
            new CompteATransmettre(Guid.NewGuid(), $"c{i}", "titulaire", $"c{i}@exemple.test", null, $"MotDePasse{i:D2}3456")).ToList();
        static Task Muet(string a, string o, string t, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var r = await TransmissionComptes.TransmettreAsync(comptes, true, Muet, new EnvoiSmsMotDePasseIndisponible(), null, false, null,
            CancellationToken.None, delaiParEnvoi: TimeSpan.FromSeconds(10), delaiTotal: TimeSpan.FromMilliseconds(300));
        Assert.True(chrono.Elapsed < TimeSpan.FromSeconds(5), $"liste rendue en {chrono.Elapsed}");
        Assert.Equal(20, r.Count);
        Assert.All(r, x => Assert.Equal("echec", x.Email));                 // à transmettre à la main
        Assert.Equal(comptes.Select(c => c.MotDePasse), r.Select(x => x.MotDePasse));   // aucun mot de passe perdu
    }

    [Fact]
    public void Arguments_de_la_console_controles()
    {
        Assert.True(ConsoleAdministration.Concerne(["--creer-comptes"]));
        Assert.Equal(("c.csv", "Installateur", true, (string?)null),
            ImportComptesConsole.Lire(["--creer-comptes", "c.csv", "--auteur", " Installateur ", "--envoyer-identifiants"]));
        Assert.False(ImportComptesConsole.Lire(["--creer-comptes", "c.csv", "--auteur", "A"]).EnvoyerIdentifiants);
        Assert.Throws<ArgumentException>(() => ImportComptesConsole.Lire(["--creer-comptes", "c.csv"]));
        Assert.Throws<ArgumentException>(() => ImportComptesConsole.Lire(["--creer-comptes", "--auteur", "A"]));
        Assert.Throws<ArgumentException>(() => ImportComptesConsole.Lire(["--creer-comptes", "c.csv", "--auteur", "A", "--adresse-ecrans", "http://x.invalid"]));
    }

    [Fact]
    public async Task Console_creer_comptes_tout_ou_rien_sur_une_base_jetable()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var db = NpgsqlDataSource.Create(base_.Connexion);
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_import_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dossier);
        try
        {
            var fichier = Path.Combine(dossier, "comptes.csv");
            // Une ligne invalide : rien n'est créé
            await File.WriteAllTextAsync(fichier, $"{Entete}\ntitulaire;console.un;Un;un@exemple.invalid;\ntitulaire;console.deux;;deux@exemple.invalid;\n");
            var sortie = new StringWriter();
            Assert.Equal(1, await ImportComptesConsole.ExecuterAsync(db, ["--creer-comptes", fichier, "--auteur", "Installateur"], sortie, CancellationToken.None));
            Assert.Contains("ligne 3 : nom obligatoire", sortie.ToString());
            Assert.Equal(0L, await Scalaire(db, "SELECT count(*) FROM compte_distant"));

            // Identifiant déjà pris (avis de la base) : rien n'est créé
            Assert.Equal(0, await ComptesDistantsAdministration.ExecuterAsync(db, ["--creer-compte-editeur", "console.pris", "--nom", "Pris", "--email", "p@exemple.invalid"], new StringWriter()));
            await File.WriteAllTextAsync(fichier, $"{Entete}\ntitulaire;console.un;Un;un@exemple.invalid;\ntitulaire;console.pris;Pris;x@exemple.invalid;\n", new UTF8Encoding(true));
            sortie = new StringWriter();
            Assert.Equal(1, await ImportComptesConsole.ExecuterAsync(db, ["--creer-comptes", fichier, "--auteur", "Installateur"], sortie, CancellationToken.None));
            Assert.Contains("ligne 3 : identifiant déjà pris", sortie.ToString());
            Assert.Equal(1L, await Scalaire(db, "SELECT count(*) FROM compte_distant"));

            // Fichier valable : comptes créés, mots de passe montrés une fois ; identifiant par e-mail (envoi factice), jamais le mot de passe
            await File.WriteAllTextAsync(fichier, $"{Entete}\ntitulaire;console.un;Un;un@exemple.test;\nediteur;console.deux;Deux;;0555000002\n");
            var courriel = new CourrielFactice();
            sortie = new StringWriter();
            var cles = new ProtectionSmtpCentral(Path.Combine(dossier, ProtectionSmtpCentral.SousDossier));
            await using (var cmd = db.CreateCommand("""
                UPDATE parametre_envoi_codes SET smtp_serveur = 'smtp.exemple.invalid', smtp_port = 587, smtp_identifiant = 'envoi@exemple.invalid',
                       smtp_expediteur = 'envoi@exemple.invalid', smtp_mot_de_passe_chiffre = $1
                """))
            {
                cmd.Parameters.AddWithValue(cles.Proteger("mot-de-passe-smtp-essai"));
                await cmd.ExecuteNonQueryAsync();
            }
            Assert.Equal(0, await ImportComptesConsole.ExecuterAsync(db,
                ["--creer-comptes", fichier, "--auteur", "Installateur", "--envoyer-identifiants", "--dossier", dossier], sortie, CancellationToken.None, _ => courriel));
            var lignes = sortie.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("2 compte(s) créé(s)", lignes[0]);
            var un = lignes.Single(l => l.StartsWith("console.un\t", StringComparison.Ordinal)).Split('\t');
            var deux = lignes.Single(l => l.StartsWith("console.deux\t", StringComparison.Ordinal)).Split('\t');
            Assert.Equal(16, un[2].Length);
            Assert.Equal("identifiant par e-mail : oui", un[3]);
            Assert.Equal("identifiant par e-mail : non (aucune adresse)", deux[3]);
            Assert.Equal("mot de passe : à transmettre par SMS (manuel)", deux[4]);
            Assert.True(BCrypt.Net.BCrypt.Verify(un[2], (string)(await Scalaire(db, "SELECT empreinte_mot_de_passe FROM compte_distant WHERE identifiant = 'console.un'"))!));
            Assert.Equal("titulaire|Installateur|true", await Scalaire(db,
                "SELECT role || '|' || cree_par || '|' || mot_de_passe_provisoire::text FROM compte_distant WHERE identifiant = 'console.un'"));
            var envoye = Assert.Single(courriel.Envoyes);
            Assert.Equal("un@exemple.test", envoye.Adresse);
            Assert.Contains("console.un", envoye.Texte);
            Assert.DoesNotContain(un[2], envoye.Texte);
            Assert.Contains(MessageIdentifiant.PhraseMotDePasse, envoye.Texte);
            // Journaux : jamais l'identifiant, le contact ni le mot de passe ; chaîne intacte
            Assert.Equal(0L, await Scalaire(db, $"""
                SELECT (SELECT count(*) FROM journal_securite WHERE detail LIKE '%console.%' OR detail LIKE '%@%' OR detail LIKE '%{un[2]}%')
                     + (SELECT count(*) FROM journal_acces_distant WHERE objet LIKE '%@%' OR objet LIKE '%console.%' OR objet LIKE '%{un[2]}%')
                """));
            Assert.Equal(1L, await Scalaire(db, "SELECT count(*) FROM journal_securite WHERE evenement = 'comptes_importes' AND detail LIKE 'console (Installateur)%'"));
            Assert.Equal(1L, await Scalaire(db, "SELECT count(*) FROM journal_acces_distant WHERE evenement = 'identifiant_envoi' AND objet = 'email:ok'"));
            Assert.Equal(DBNull.Value, await Scalaire(db, "SELECT premiere_rupture FROM cloud_ecran_verifier_journal_acces()"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Directory.Delete(dossier, true);
        }
    }

    private static async Task<object?> Scalaire(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }
}
