using Npgsql;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Comptes;

/// <summary>
/// Lots S7.1 et S7.2a (scripts centraux 030 et 032) : comptes distants des écrans du central, par la console (connexion du
/// propriétaire, comme les autres commandes). Chaque commande est notée au journal des accès distants (sans identifiant ni nom :
/// n° du compte seulement).
///   --creer-compte-editeur &lt;identifiant&gt; --nom &lt;nom&gt; [--telephone &lt;n°&gt;] [--email &lt;adresse&gt;]   au moins un des deux (les deux permis) ;
///                                                                                       mot de passe provisoire affiché UNE fois
///   --creer-compte-titulaire &lt;identifiant&gt; --nom &lt;nom&gt; [--telephone &lt;n°&gt;] [--email &lt;adresse&gt;] --auteur &lt;nom&gt;   idem
///   (lot S7.6, script central 037 : l'éditeur modifie ensuite e-mail et téléphone dans l'écran « Comptes » des écrans du central)
///   --code-rattachement &lt;identifiant&gt; &lt;officine (uuid)&gt; --auteur &lt;nom&gt;   code de 30 min affiché UNE fois (empreinte seule en base ;
///                                                                    développement ; le titulaire le tire du poste depuis le lot S7.2b,
///                                                                    mêmes tirage et règles : RattachementPoste, script central 033)
///   --comptes-distants                                                                       liste (sans empreinte ni contact complet)
///   --desactiver-compte &lt;identifiant&gt;                                                     sessions fermées
///   --deverrouiller-compte &lt;identifiant&gt;
///   --contact-compte &lt;identifiant&gt; [--email &lt;adresse&gt;|-] [--telephone &lt;n°&gt;|-] --auteur &lt;nom&gt;   contact du second facteur
///                    changé SUR LE SERVEUR (2026-11-21 : installation, adresse perdue ou erronée, quand l'écran « Comptes », qui exige un
///                    code, n'est pas joignable) ; « - » retire ; au moins un contact reste
///   --creer-comptes &lt;fichier.csv&gt; --auteur &lt;nom&gt; [--envoyer-identifiants]   import de comptes (script central 040, tout ou rien,
///                    ImportComptesConsole) ; mots de passe provisoires affichés UNE fois, identifiant par e-mail si demandé
/// </summary>
public static class ComptesDistantsAdministration
{
    public static readonly string[] Commandes =
        ["--creer-compte-editeur", "--creer-compte-titulaire", "--code-rattachement", "--comptes-distants", "--desactiver-compte", "--deverrouiller-compte", "--contact-compte",
         ImportComptesConsole.Commande];

    public static bool Concerne(string commande) => Commandes.Contains(commande);

    /// <summary>Arguments de --creer-compte-editeur / --creer-compte-titulaire contrôlés (règles pures, testées) : identifiant, nom, téléphone, e-mail.</summary>
    public static (string Identifiant, string Nom, string? Telephone, string? Email) LireCreation(string[] args)
    {
        var identifiant = ComptesDistants.NormaliserIdentifiant(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null);
        if (!ComptesDistants.IdentifiantValide(identifiant))
            throw new ArgumentException("Identifiant attendu : 3 à 60 caractères, minuscules, chiffres, « . _ - » (ex. editeur.dupont).");
        var nom = Option(args, "--nom")?.Trim();
        if (nom is not { Length: >= 1 and <= 120 }) throw new ArgumentException("Nom attendu : --nom <nom> (120 caractères au plus).");
        var telephone = Option(args, "--telephone")?.Replace(" ", "", StringComparison.Ordinal);
        var email = Option(args, "--email")?.Trim();
        if (telephone is null && email is null) throw new ArgumentException("Contact du second facteur attendu : --telephone <n°> ou --email <adresse>.");
        if (telephone is not null && !ComptesDistants.TelephoneValide(telephone)) throw new ArgumentException("Téléphone invalide : 8 à 15 chiffres, « + » permis en tête.");
        if (email is not null && !Depart.AdresseCourriel.EstValide(email)) throw new ArgumentException("Adresse e-mail invalide.");
        return (identifiant!, nom, telephone, email);
    }

    /// <summary>Auteur d'une commande d'écriture (--auteur, 1 à 100 caractères, comme les autres commandes de la console).</summary>
    public static string Auteur(string[] args) =>
        Option(args, "--auteur")?.Trim() is { Length: >= 1 and <= 100 } a ? a : throw new ArgumentException("Option --auteur <nom> obligatoire (1 à 100 caractères).");

    /// <summary>Arguments de --code-rattachement contrôlés (règle pure, testée) : identifiant du titulaire, officine (uuid), auteur.</summary>
    public static (string Identifiant, Guid Officine, string Auteur) LireCodeRattachement(string[] args)
    {
        var identifiant = ComptesDistants.NormaliserIdentifiant(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null);
        if (!ComptesDistants.IdentifiantValide(identifiant)) throw new ArgumentException("Attendu : --code-rattachement <identifiant du titulaire> <officine (uuid)> --auteur <nom>.");
        if (args.Length < 3 || !Guid.TryParse(args[2], out var officine)) throw new ArgumentException("Code officine (uuid) attendu après l'identifiant.");
        return (identifiant!, officine, Auteur(args));
    }

    /// <summary>
    /// Arguments de --contact-compte contrôlés (règle pure, testée) : identifiant, e-mail et téléphone (null = inchangé, "" = retiré),
    /// auteur. Au moins une des deux options.
    /// </summary>
    public static (string Identifiant, string? Email, string? Telephone, string Auteur) LireContact(string[] args)
    {
        var identifiant = ComptesDistants.NormaliserIdentifiant(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null);
        if (!ComptesDistants.IdentifiantValide(identifiant))
            throw new ArgumentException("Attendu : --contact-compte <identifiant> [--email <adresse>|-] [--telephone <n°>|-] --auteur <nom>.");
        var email = Option(args, "--email")?.Trim();
        var telephone = Option(args, "--telephone")?.Replace(" ", "", StringComparison.Ordinal);
        if (email is null && telephone is null) throw new ArgumentException("Contact attendu : --email <adresse> et/ou --telephone <n°> (« - » pour retirer).");
        if (email is not null && email != "-" && !Depart.AdresseCourriel.EstValide(email)) throw new ArgumentException("Adresse e-mail invalide.");
        if (telephone is not null && telephone != "-" && !ComptesDistants.TelephoneValide(telephone)) throw new ArgumentException("Téléphone invalide : 8 à 15 chiffres, « + » permis en tête.");
        return (identifiant!, email == "-" ? "" : email, telephone == "-" ? "" : telephone, Auteur(args));
    }

    public static async Task<int> ExecuterAsync(NpgsqlDataSource db, string[] args, TextWriter sortie, CancellationToken ct = default)
    {
        switch (args[0])
        {
            case "--creer-compte-editeur":
                return await CreerAsync(db, args, "editeur", $"console {Environment.UserName}@{Environment.MachineName}", sortie, ct);
            case "--creer-compte-titulaire":
                {
                    var auteur = Auteur(args);
                    return await CreerAsync(db, args, "titulaire", auteur, sortie, ct);
                }
            case "--code-rattachement":
                {
                    var (identifiant, officine, auteur) = LireCodeRattachement(args);
                    var (code, empreinte) = ComptesDistants.TirerCodeRattachement();
                    await using var cmd = db.CreateCommand("SELECT statut, expire_le FROM cloud_compte_distant_code_rattachement($1, $2, $3, $4)");
                    cmd.Parameters.AddWithValue(identifiant);
                    cmd.Parameters.AddWithValue(officine);
                    cmd.Parameters.AddWithValue(empreinte);
                    cmd.Parameters.AddWithValue(auteur);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    await Reception.DepotCentral.LigneAttendueAsync(r, "cloud_compte_distant_code_rattachement", ct);
                    var statut = r.GetString(0);
                    if (statut != "ok")
                    {
                        await sortie.WriteLineAsync(statut switch
                        {
                            "compte_inconnu" => $"Compte « {identifiant} » inconnu.",
                            "pas_titulaire" => $"Le compte « {identifiant} » n'est pas un compte titulaire.",
                            "compte_inactif" => $"Le compte « {identifiant} » est désactivé.",
                            "officine_inconnue" => $"Officine {officine} inconnue.",
                            "partie" => $"Officine {officine} partie : aucun rattachement.",
                            _ => $"Code refusé ({statut}).",
                        });
                        return 1;
                    }
                    var expire = r.GetDateTime(1);
                    await sortie.WriteLineAsync($"Code de rattachement de l'officine {officine} au compte « {identifiant} » (usage unique, valable jusqu'à "
                        + $"{expire.ToLocalTime():HH:mm}, 30 minutes ; affiché une seule fois, jamais gardé) :");
                    await sortie.WriteLineAsync(code);
                    return 0;
                }
            case "--comptes-distants":
                {
                    await using var cmd = db.CreateCommand("""
                        SELECT identifiant, nom, role, actif, mot_de_passe_provisoire, verrouille_jusqu_au, derniere_connexion_le,
                               telephone IS NOT NULL, email IS NOT NULL,
                               (SELECT count(*) FROM session_distante s WHERE s.compte_id = c.id AND s.fin_le IS NULL AND s.expire_le > now())
                        FROM compte_distant c ORDER BY role, identifiant
                        """);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    var vu = false;
                    while (await r.ReadAsync(ct))
                    {
                        vu = true;
                        var etat = !r.GetBoolean(3) ? "DÉSACTIVÉ" : !r.IsDBNull(5) && r.GetDateTime(5) > DateTime.UtcNow ? $"bloqué jusqu'au {r.GetDateTime(5).ToLocalTime():yyyy-MM-dd HH:mm}" : "actif";
                        await sortie.WriteLineAsync($"{r.GetString(0),-24} {r.GetString(2),-10} {etat,-28} {(r.GetBoolean(4) ? "mot de passe provisoire" : "mot de passe choisi"),-24} "
                            + $"dernière connexion {(r.IsDBNull(6) ? "jamais" : r.GetDateTime(6).ToLocalTime().ToString("yyyy-MM-dd HH:mm"))} ; "
                            + $"second facteur : {(r.GetBoolean(8) ? "e-mail" : "")}{(r.GetBoolean(7) && r.GetBoolean(8) ? " + " : "")}{(r.GetBoolean(7) ? "téléphone" : "")} ; "
                            + $"sessions ouvertes : {r.GetInt64(9)} ; {r.GetString(1)}");
                    }
                    if (!vu) await sortie.WriteLineAsync("Aucun compte distant.");
                    return 0;
                }
            case "--desactiver-compte":
                {
                    var identifiant = Identifiant(args);
                    await using var cmd = db.CreateCommand("SELECT cloud_compte_distant_desactiver($1)");
                    cmd.Parameters.AddWithValue(identifiant);
                    var n = (int)(await cmd.ExecuteScalarAsync(ct))!;
                    await sortie.WriteLineAsync(n < 0 ? $"Compte « {identifiant} » inconnu ou déjà désactivé." : $"Compte « {identifiant} » désactivé ; sessions fermées : {n}.");
                    return n < 0 ? 1 : 0;
                }
            case "--contact-compte":
                return await ContactAsync(db, args, sortie, ct);
            case ImportComptesConsole.Commande:
                return await ImportComptesConsole.ExecuterAsync(db, args, sortie, ct);
            case "--deverrouiller-compte":
                {
                    var identifiant = Identifiant(args);
                    await using var cmd = db.CreateCommand("SELECT cloud_compte_distant_deverrouiller($1)");
                    cmd.Parameters.AddWithValue(identifiant);
                    var ok = (bool)(await cmd.ExecuteScalarAsync(ct))!;
                    await sortie.WriteLineAsync(ok ? $"Compte « {identifiant} » débloqué (compteur d'échecs remis à zéro)." : $"Compte « {identifiant} » inconnu.");
                    return ok ? 0 : 1;
                }
        }
        return 1;
    }

    /// <summary>Création d'un compte (rôle fixé par la commande, jamais saisi) ; mot de passe provisoire affiché une seule fois.</summary>
    private static async Task<int> CreerAsync(NpgsqlDataSource db, string[] args, string role, string auteur, TextWriter sortie, CancellationToken ct)
    {
        var (identifiant, nom, telephone, email) = LireCreation(args);
        var provisoire = ComptesDistants.MotDePasseProvisoire();
        await using var cmd = db.CreateCommand("SELECT cloud_compte_distant_creer($1, $2, $3, $4, $5, $6, $7)");
        cmd.Parameters.AddWithValue(identifiant);
        cmd.Parameters.AddWithValue(nom);
        cmd.Parameters.AddWithValue(role);
        cmd.Parameters.AddWithValue(BCrypt.Net.BCrypt.HashPassword(provisoire, ComptesDistants.CoutBcrypt));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)telephone ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)email ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(auteur.Length > 100 ? auteur[..100] : auteur);
        try
        {
            await cmd.ExecuteScalarAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ArgumentException($"Le compte « {identifiant} » existe déjà.");
        }
        var libelle = role == "editeur" ? "éditeur" : "titulaire";
        await sortie.WriteLineAsync($"Compte {libelle} « {identifiant} » créé. Mot de passe PROVISOIRE (affiché une seule fois, à changer à la première connexion) :");
        await sortie.WriteLineAsync(provisoire);
        return 0;
    }

    /// <summary>
    /// Contact du second facteur changé sur le serveur : journal des accès (n° du compte) et journal de sécurité (auteur, ce qui a
    /// changé), jamais l'adresse ni le numéro. Un compte sans aucun contact est refusé (contrainte de la base).
    /// </summary>
    private static async Task<int> ContactAsync(NpgsqlDataSource db, string[] args, TextWriter sortie, CancellationToken ct)
    {
        var (identifiant, email, telephone, auteur) = LireContact(args);
        await using var connexion = await db.OpenConnectionAsync(ct);
        await using var transaction = await connexion.BeginTransactionAsync(ct);
        Guid compte;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE compte_distant SET
                   email = CASE WHEN $2::text IS NULL THEN email WHEN $2 = '' THEN NULL ELSE $2 END,
                   telephone = CASE WHEN $3::text IS NULL THEN telephone WHEN $3 = '' THEN NULL ELSE $3 END
            WHERE identifiant = $1 RETURNING id
            """, connexion, transaction))
        {
            cmd.Parameters.AddWithValue(identifiant);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)email ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)telephone ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            try
            {
                if (await cmd.ExecuteScalarAsync(ct) is not Guid id)
                {
                    await sortie.WriteLineAsync($"Compte « {identifiant} » inconnu.");
                    return 1;
                }
                compte = id;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation)
            {
                await sortie.WriteLineAsync($"Rien n'est changé : le compte « {identifiant} » doit garder au moins un contact (e-mail ou téléphone).");
                return 1;
            }
        }
        static string Quoi(string? v) => v is null ? "inchangé" : v.Length == 0 ? "retiré" : "modifié";
        var detail = $"console ({auteur}) : contact du compte n° {compte.ToString()[..8]} : e-mail {Quoi(email)}, téléphone {Quoi(telephone)}";
        await using (var cmd = new NpgsqlCommand("SELECT cloud_ecran_journaliser($1, NULL, NULL, 'compte_contact_modifie', 'ok', 'console')", connexion, transaction))
        {
            cmd.Parameters.AddWithValue(compte);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("INSERT INTO journal_securite (evenement, detail) VALUES ('compte_contact_modifie', $1)", connexion, transaction))
        {
            cmd.Parameters.AddWithValue(detail.Length > 500 ? detail[..500] : detail);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        await sortie.WriteLineAsync($"Contact du compte « {identifiant} » : e-mail {Quoi(email)}, téléphone {Quoi(telephone)}. Les prochains codes partent à ce contact.");
        return 0;
    }

    private static string Identifiant(string[] args)
    {
        var identifiant = ComptesDistants.NormaliserIdentifiant(args.Length > 1 ? args[1] : null);
        return ComptesDistants.IdentifiantValide(identifiant) ? identifiant! : throw new ArgumentException($"Attendu : {args[0]} <identifiant>.");
    }

    private static string? Option(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
