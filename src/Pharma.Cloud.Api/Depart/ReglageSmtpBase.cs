using Npgsql;

namespace Pharma.Cloud.Api.Depart;

/// <summary>
/// Lot S7.6 (script central 037) : compte SMTP du réglage « Envoi des codes » (administration du site), relu par la console du
/// central (connexion du propriétaire) pour envoyer le mot de passe de l'archive de départ quand les variables PHARMA_CLOUD_SMTP_*
/// sont absentes (elles restent prioritaires : rien ne change pour un central qui les a). Mot de passe déchiffré avec les clés du
/// dossier du central (mêmes clés que les écrans) ; jamais affiché, jamais journalisé.
/// </summary>
public static class ReglageSmtpBase
{
    public static async Task<(ReglagesSmtpCentral? Reglages, string? Manque)> LireAsync(NpgsqlDataSource db, string? dossierCentral, CancellationToken ct)
    {
        await using (var existe = db.CreateCommand("SELECT to_regclass('public.parametre_envoi_codes') IS NOT NULL"))
            if (await existe.ExecuteScalarAsync(ct) is not true) return (null, "réglage « Envoi des codes » absent (script central 037 non passé)");
        await using var cmd = db.CreateCommand("""
            SELECT smtp_serveur, smtp_port, smtp_tls, smtp_identifiant, smtp_expediteur, smtp_mot_de_passe_chiffre FROM parametre_envoi_codes WHERE id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.IsDBNull(5)) return (null, "réglage « Envoi des codes » sans mot de passe SMTP");
        string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var protection = ProtectionSmtpCentral.Depuis(ProtectionSmtpCentral.DossierDuCentral(dossierCentral));
        if (protection is null) return (null, "dossier du central absent (PHARMA_CLOUD_DOSSIER) : mot de passe du réglage « Envoi des codes » illisible");
        var motDePasse = protection.Deproteger(r.GetString(5));
        if (motDePasse is null) return (null, "mot de passe du réglage « Envoi des codes » illisible (clés du dossier du central différentes)");
        var (reglages, manque) = ReglagesSmtpCentral.Creer(T(0), r.IsDBNull(1) ? null : r.GetInt32(1), T(2), T(3), motDePasse, T(4));
        return (reglages, manque is null ? null : manque + " (réglage « Envoi des codes »)");
    }

    /// <summary>Nom d'hôte du serveur d'e-mail (règle du script central 037) : lettres, chiffres, points et tirets, 253 caractères au plus.</summary>
    public static bool ServeurValide(string? serveur) =>
        serveur is { Length: > 0 } && System.Text.RegularExpressions.Regex.IsMatch(serveur, "^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$");

    /// <summary>
    /// Commande --envoi-codes de la console du central (2026-11-21) : l'installateur règle le compte e-mail des codes SUR LE SERVEUR
    /// (connexion du propriétaire de la base), comme on règle le poste devant le PC de l'officine ; le premier code de l'éditeur part
    /// ensuite par ce compte, sans variable d'environnement ni fichier. Canal « E-mail ». Mot de passe chiffré avec les clés du dossier
    /// du central (les mêmes que les écrans), jamais affiché ni journalisé ; journal de sécurité : serveur, port, sécurité seulement.
    /// </summary>
    public static async Task EnregistrerAsync(NpgsqlDataSource db, ProtectionSmtpCentral protection, ReglagesSmtpCentral smtp, string tls,
        string operateur, CancellationToken ct)
    {
        if (!ServeurValide(smtp.Hote)) throw new ArgumentException("Serveur d'e-mail invalide : le nom seul, ex. smtp.gmail.com (sans « https:// », sans port, sans espace).");
        if (tls is not ("starttls" or "ssl")) throw new ArgumentException("Sécurité attendue : starttls (port 587) ou ssl (port 465).");
        if (string.IsNullOrWhiteSpace(operateur) || operateur.Length > 100) throw new ArgumentException("Opérateur attendu (1 à 100 caractères) : --operateur <nom>.");
        var chiffre = protection.Proteger(smtp.MotDePasse);
        await using var connexion = await db.OpenConnectionAsync(ct);
        await using var transaction = await connexion.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            UPDATE parametre_envoi_codes SET canal = 'courriel', smtp_serveur = $1, smtp_port = $2, smtp_tls = $3, smtp_identifiant = $4,
                   smtp_expediteur = $5, smtp_mot_de_passe_chiffre = $6, modifie_par = NULL, modifie_le = now()
            WHERE id = 1
            """, connexion, transaction))
        {
            cmd.Parameters.AddWithValue(smtp.Hote);
            cmd.Parameters.AddWithValue(smtp.Port);
            cmd.Parameters.AddWithValue(tls);
            cmd.Parameters.AddWithValue(smtp.Utilisateur);
            cmd.Parameters.AddWithValue(smtp.Expediteur);
            cmd.Parameters.AddWithValue(chiffre);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Réglage « Envoi des codes » absent (script central 037 non passé).");
        }
        // Jamais l'identifiant, l'expéditeur ni le mot de passe au journal
        await using (var cmd = new NpgsqlCommand("INSERT INTO journal_securite (evenement, detail) VALUES ('envoi_codes_modifie', $1)", connexion, transaction))
        {
            cmd.Parameters.AddWithValue($"console (opérateur {operateur.Trim()}) : envoi des codes : canal courriel, SMTP {smtp.Hote}:{smtp.Port} {tls}, mot de passe remplacé");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
