using Npgsql;
using NpgsqlTypes;
using Pharma.Cloud.Api.Depart;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Comptes;

/// <summary>
/// Script central 040 : --creer-comptes &lt;fichier.csv&gt; --auteur &lt;nom&gt; [--envoyer-identifiants] [--adresse-ecrans &lt;https://…&gt;]
/// [--dossier &lt;dossier du central&gt;] [--dossier-cles &lt;dossier des clés des écrans&gt;] : import de comptes distants par la console (connexion du propriétaire, aucun code). Mêmes
/// contrôles que l'écran « Comptes » (ImportComptesCsv, puis la base : cloud_compte_distant_creer_lot), TOUT OU RIEN. Liste
/// identifiant + mot de passe provisoire affichée UNE fois ; identifiant par e-mail si demandé (SMTP des variables PHARMA_CLOUD_SMTP_*,
/// sinon réglage « Envoi des codes » en base) ; mot de passe JAMAIS par e-mail (SMS si un fournisseur est en service, sinon manuel).
/// </summary>
public static class ImportComptesConsole
{
    public const string Commande = "--creer-comptes";

    /// <summary>Arguments contrôlés (règle pure, testée) : fichier, auteur, envoi de l'identifiant, adresse des écrans.</summary>
    public static (string Fichier, string Auteur, bool EnvoyerIdentifiants, string? AdresseEcrans) Lire(string[] args)
    {
        var fichier = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        if (fichier is null) throw new ArgumentException("Attendu : --creer-comptes <fichier.csv> --auteur <nom> [--envoyer-identifiants] [--adresse-ecrans <https://…>].");
        var adresse = Option(args, "--adresse-ecrans");
        if (adresse is not null && MessageIdentifiant.Adresse(adresse) is null) throw new ArgumentException("Adresse des écrans attendue en https://… (--adresse-ecrans).");
        return (fichier, ComptesDistantsAdministration.Auteur(args), args.Contains("--envoyer-identifiants"), MessageIdentifiant.Adresse(adresse));
    }

    /// <param name="fabrique">Envoi SMTP (tests : envoi factice) ; null : MailKit.</param>
    /// <param name="sms">Envoi du mot de passe par SMS ; null : aucun fournisseur (manuel).</param>
    public static async Task<int> ExecuterAsync(NpgsqlDataSource db, string[] args, TextWriter sortie, CancellationToken ct,
        Func<ReglagesSmtpCentral, IEnvoiCourriel>? fabrique = null, IEnvoiSmsMotDePasse? sms = null)
    {
        var (fichier, auteur, envoyer, adresse) = Lire(args);
        if (!File.Exists(fichier)) throw new ArgumentException($"Fichier introuvable : {fichier}");
        if (new FileInfo(fichier).Length > ImportComptesCsv.OctetsMax) throw new ArgumentException("Fichier trop gros (128 Ko au plus). Rien n'est créé.");
        var lecture = ImportComptesCsv.Lire(await File.ReadAllBytesAsync(fichier, ct));
        if (lecture.Refus is { } refus)
        {
            await sortie.WriteLineAsync($"Rien n'est créé : {ImportComptesCsv.LibellesRefus.GetValueOrDefault(refus, refus)}.");
            return 1;
        }
        if (!lecture.Valable) return await RefusLignesAsync(lecture.Lignes, sortie);
        var provisoires = lecture.Lignes.ToDictionary(l => l.Numero, _ => ComptesDistants.MotDePasseProvisoire());
        var empreintes = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        Parallel.ForEach(provisoires, p => empreintes[p.Key] = BCrypt.Net.BCrypt.HashPassword(p.Value, ComptesDistants.CoutBcrypt));

        var resultat = new List<(int Numero, string Statut, Guid? Compte)>();
        await using (var cmd = db.CreateCommand("SELECT numero, statut, compte FROM cloud_compte_distant_creer_lot($1, $2)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = ImportComptesCsv.Json(lecture.Lignes, empreintes), NpgsqlDbType = NpgsqlDbType.Jsonb });
            cmd.Parameters.AddWithValue(auteur);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) resultat.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2)));
        }
        if (resultat.Any(r => r.Statut != "ok"))
            return await RefusLignesAsync(ImportComptesCsv.Fusionner(lecture.Lignes, resultat.ToDictionary(r => r.Numero, r => r.Statut)), sortie);

        var parLigne = lecture.Lignes.ToDictionary(l => l.Numero);
        var comptes = resultat.OrderBy(r => r.Numero).Select(r =>
        {
            var l = parLigne[r.Numero];
            return new CompteATransmettre(r.Compte!.Value, l.Identifiant, l.Role, l.Email, l.Telephone, provisoires[r.Numero]);
        }).ToList();
        Func<string, string, string, CancellationToken, Task>? courriel = null;
        if (envoyer && comptes.Any(c => c.Email is not null))
        {
            var (smtp, manque) = ReglagesSmtpCentral.Lire(Environment.GetEnvironmentVariable);
            if (smtp is null)
                (smtp, manque) = await ReglageSmtpBase.LireAsync(db, Option(args, "--dossier") ?? Environment.GetEnvironmentVariable("PHARMA_CLOUD_DOSSIER"), ct,
                    Option(args, ReglageSmtpBase.OptionDossierCles));
            if (smtp is not null) courriel = (fabrique ?? (s => new EnvoiCourrielSmtp(s)))(smtp).EnvoyerAsync;
            else await sortie.WriteLineAsync($"Identifiants non envoyés par e-mail : {manque}.");
        }
        var transmis = await TransmissionComptes.TransmettreAsync(comptes, envoyer, courriel, sms ?? new EnvoiSmsMotDePasseIndisponible(), adresse, remise: false,
            async (compte, evenement, objet, jt) =>
            {
                await using var cmd = db.CreateCommand("SELECT cloud_ecran_journaliser($1, NULL, NULL, $2, $3, $4)");
                cmd.Parameters.AddWithValue(compte);
                cmd.Parameters.AddWithValue(evenement);
                cmd.Parameters.AddWithValue(objet.EndsWith(":ok", StringComparison.Ordinal) ? "ok" : "refus");
                cmd.Parameters.AddWithValue(objet);
                await cmd.ExecuteNonQueryAsync(jt);
            }, ct);
        await sortie.WriteLineAsync($"{transmis.Count} compte(s) créé(s). Mots de passe PROVISOIRES (affichés une seule fois, à changer à la première connexion ;"
            + " jamais par e-mail : par SMS ou de vive voix) :");
        await sortie.WriteLineAsync("identifiant\trôle\tmot de passe\tidentifiant par e-mail\tmot de passe");
        foreach (var t in transmis) await sortie.WriteLineAsync(t.Copie);
        return 0;
    }

    private static async Task<int> RefusLignesAsync(IReadOnlyList<LigneCompteImport> lignes, TextWriter sortie)
    {
        await sortie.WriteLineAsync("Rien n'est créé (tout ou rien) : lignes à corriger dans le fichier :");
        foreach (var l in lignes.Where(l => !l.Valable))
            await sortie.WriteLineAsync($"  ligne {l.Numero} : {ImportComptesCsv.LibellesStatuts.GetValueOrDefault(l.Statut!, l.Statut!)}");
        return 1;
    }

    private static string? Option(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
