using Npgsql;
using Pharma.Cloud.Api.Depart;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Lot S7.6 (script central 037) : migration sans perte des comptes et des codes existants (état d'avant 037 reconstitué puis
/// script repassé), réglage SMTP de la base relu par la console (départ d'une officine), mot de passe chiffré par la protection
/// des données du dossier du central. Base JETABLE ; sans PostgreSQL, psql ou droit CREATEDB : la partie base ne fait rien.
/// </summary>
public class EnvoiCodesReglageTests
{
    private static string Script => Path.Combine(Depot.Racine(), "database", "cloud", "037_envoi_codes_reglage.sql");

    [Fact]
    public void Reglage_smtp_de_la_base_tls_obligatoire_et_champs_nommes_sans_valeur()
    {
        var (gmail, aucun) = ReglagesSmtpCentral.Creer("smtp.gmail.com", 587, "starttls", "compte@exemple.invalid", "abcdabcdabcdabcd", "compte@exemple.invalid");
        Assert.Null(aucun);
        Assert.Equal(MailKit.Security.SecureSocketOptions.StartTls, gmail!.Securite);
        Assert.DoesNotContain("abcd", gmail.ToString());
        Assert.Equal(MailKit.Security.SecureSocketOptions.SslOnConnect,
            ReglagesSmtpCentral.Creer("smtp.exemple.invalid", 465, "ssl", "u", "m", "e@exemple.invalid").Reglages!.Securite);
        Assert.Contains("TLS", ReglagesSmtpCentral.Creer("smtp.exemple.invalid", 25, "aucun", "u", "m", "e@exemple.invalid").Manque);
        var (_, manque) = ReglagesSmtpCentral.Creer("smtp.gmail.com", 587, "starttls", "compte@exemple.invalid", null, "compte@exemple.invalid");
        Assert.Equal("réglage SMTP incomplet (mot de passe)", manque);
        Assert.Contains("port", ReglagesSmtpCentral.Creer("h", 0, "starttls", "u", "m", "e@exemple.invalid").Manque);
    }

    [Fact]
    public void Protection_du_mot_de_passe_dans_le_dossier_du_central_et_jamais_dans_un_depot()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_central_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var protection = ProtectionSmtpCentral.Depuis(ProtectionSmtpCentral.DossierDuCentral(dossier))!;
            var chiffre = protection.Proteger("abcd efgh ijkl mnop");
            Assert.Matches("^[A-Za-z0-9_-]{40,}$", chiffre);
            Assert.DoesNotContain("abcd", chiffre);
            Assert.Equal("abcd efgh ijkl mnop", protection.Deproteger(chiffre));
            // Même dossier : relu par un autre processus (la console) ; autre dossier : illisible, jamais d'exception
            Assert.Equal("abcd efgh ijkl mnop", new ProtectionSmtpCentral(Path.Combine(dossier, ProtectionSmtpCentral.SousDossier)).Deproteger(chiffre));
            Assert.Null(ProtectionSmtpCentral.Depuis(Path.Combine(dossier, "autre"))!.Deproteger(chiffre));
            Assert.Null(protection.Deproteger("pas-un-texte-chiffre"));
            Assert.True(Directory.EnumerateFiles(Path.Combine(dossier, ProtectionSmtpCentral.SousDossier), "*.xml").Any());
            // Dossier de clés dans le dépôt : refusé
            Assert.Null(ProtectionSmtpCentral.Depuis(Path.Combine(Depot.Racine(), "TestResults", "cles")));
            Assert.Null(ProtectionSmtpCentral.Depuis(null));
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }

    [Fact]
    public async Task Migration_sans_perte_des_comptes_et_des_codes_puis_reglage_relu_par_la_console()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var db = NpgsqlDataSource.Create(base_.Connexion);
        async Task<object?> Sql(string sql, params object[] p)
        {
            await using var cmd = db.CreateCommand(sql);
            foreach (var v in p) cmd.Parameters.AddWithValue(v);
            return await cmd.ExecuteScalarAsync();
        }

        // État d'AVANT 037 reconstitué : ni réglage, ancienne contrainte du canal (fichier, email, sms) ; comptes et codes existants
        await Sql("DROP TABLE parametre_envoi_codes CASCADE");
        await Sql("ALTER TABLE code_second_facteur DROP CONSTRAINT ck_code_second_facteur_canal");
        await Sql("ALTER TABLE code_second_facteur ADD CONSTRAINT code_second_facteur_canal_check CHECK (canal IN ('fichier', 'email', 'sms'))");
        foreach (var (ident, tel, mail) in new[] { ("mail.seul", (string?)null, (string?)"m@exemple.invalid"), ("tel.seul", "0555000001", null), ("les.deux", "+213555000002", "d@exemple.invalid") })
            await Sql("SELECT cloud_compte_distant_creer($1, 'Compte essai', 'titulaire', '$2a$12$' || repeat('a', 53), $2::text, $3::text, 'tests 037')", ident,
                (object?)tel ?? DBNull.Value, (object?)mail ?? DBNull.Value);
        await Sql("""
            INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le) SELECT repeat('9', 64), id, 'code', now() + interval '10 minutes' FROM compte_distant WHERE identifiant = 'mail.seul';
            INSERT INTO code_second_facteur (compte_id, session_id, objet, empreinte_code, canal, expire_le)
            SELECT compte_id, id, 'connexion', repeat('8', 64), 'email', now() + interval '5 minutes' FROM session_distante WHERE empreinte_jeton = repeat('9', 64);
            """);
        var avant = await Sql("SELECT string_agg(identifiant || '|' || coalesce(telephone, '') || '|' || coalesce(email, ''), ',' ORDER BY identifiant) FROM compte_distant");

        base_.Rejouer(Script);
        base_.Rejouer(Script);   // rejeu : rien de plus

        Assert.Equal(avant, await Sql("SELECT string_agg(identifiant || '|' || coalesce(telephone, '') || '|' || coalesce(email, ''), ',' ORDER BY identifiant) FROM compte_distant"));
        Assert.Equal("email", await Sql("SELECT canal FROM code_second_facteur WHERE empreinte_code = repeat('8', 64)"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM parametre_envoi_codes"));
        Assert.Equal(false, await Sql("SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'code_second_facteur_canal_check')"));
        Assert.Equal(true, await Sql("SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_code_second_facteur_canal')"));

        // Réglage relu par la console (départ d'une officine) : déchiffré avec les clés du dossier du central
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_central_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.Contains("sans mot de passe", (await ReglageSmtpBase.LireAsync(db, dossier, CancellationToken.None)).Manque);
            var chiffre = ProtectionSmtpCentral.Depuis(ProtectionSmtpCentral.DossierDuCentral(dossier))!.Proteger("abcdabcdabcdabcd");
            await Sql("""
                UPDATE parametre_envoi_codes SET smtp_serveur = 'smtp.gmail.com', smtp_port = 587, smtp_tls = 'starttls', smtp_identifiant = 'compte@exemple.invalid',
                       smtp_expediteur = 'compte@exemple.invalid', smtp_mot_de_passe_chiffre = $1 WHERE id = 1
                """, chiffre);
            var (smtp, manque) = await ReglageSmtpBase.LireAsync(db, dossier, CancellationToken.None);
            Assert.Null(manque);
            Assert.Equal(("smtp.gmail.com", 587, "abcdabcdabcdabcd"), (smtp!.Hote, smtp.Port, smtp.MotDePasse));
            Assert.Contains("illisible", (await ReglageSmtpBase.LireAsync(db, dossier + "-autre", CancellationToken.None)).Manque);
            Assert.Contains("PHARMA_CLOUD_DOSSIER", (await ReglageSmtpBase.LireAsync(db, null, CancellationToken.None)).Manque);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
            if (Directory.Exists(dossier + "-autre")) Directory.Delete(dossier + "-autre", true);
        }
    }
}
