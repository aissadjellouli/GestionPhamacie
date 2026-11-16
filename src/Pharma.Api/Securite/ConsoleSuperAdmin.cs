using System.Text;
using Pharma.Application.Securite;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Api.Securite;

/// <summary>
/// Création ou modification du compte super admin, sur le PC serveur seulement : « Pharma.Api.exe --super-admin ».
/// Au moins un canal vérifié (SMS ou e-mail), sinon refus. Compte existant : mot de passe actuel et code envoyé sur chaque canal
/// vérifié ; nouveau téléphone : code reçu par SMS sur ce numéro ; nouvelle adresse e-mail : code reçu à cette adresse ; le dernier canal ne se retire pas.
/// </summary>
public static class ConsoleSuperAdmin
{
    public const string Argument = "--super-admin";

    public static async Task<int> ExecuterAsync(IServiceProvider services)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var portee = services.CreateScope();
        var sp = portee.ServiceProvider;
        var comptes = sp.GetRequiredService<ICompteSuperAdmin>();
        var envoi = sp.GetRequiredService<EnvoiCodeSuperAdmin>();
        try
        {
            Console.WriteLine($"Compte super admin : {comptes.Emplacement}");
            var actuel = comptes.Lire();
            if (actuel is not null)
            {
                Console.WriteLine($"Un compte existe ({actuel.Identifiant}, {Canaux(actuel)}). Il sera modifié.");
                if (!sp.GetRequiredService<IVerificateurMotDePasse>().Verifier(LireSecret("Mot de passe actuel : "), actuel.MotDePasseHash))
                    return Echec("Mot de passe faux : rien n'a été modifié.");
                if (!await CodeAsync(c => envoi.EnvoyerAsync(actuel, null, c), "modifier le compte super admin"))
                    return Echec("Code non validé : rien n'a été modifié.");
            }

            var identifiant = Lire($"Identifiant{(actuel is null ? "" : $" [{actuel.Identifiant}]")} : ") is { Length: > 0 } s ? s : actuel?.Identifiant ?? "";
            var motDePasse = LireSecret($"Nouveau mot de passe ({CreerCompteSuperAdmin.LongueurMin} caractères au moins) : ");
            var confirmation = LireSecret("Confirmation : ");
            Console.WriteLine("Au moins un canal vérifié est obligatoire : SMS (modem GSM) ou e-mail (serveur d'e-mail). Avec les deux, chaque code part sur les deux.");
            var saisieTelephone = Lire(actuel is null ? "Téléphone mobile (SMS, vide = aucun) : "
                : $"Téléphone mobile [{(actuel.ASms ? RegleDestinataire.MasquerTelephone(actuel.Telephone!) : "aucun")}] (vide = inchangé, « {CanauxSuperAdmin.Retrait} » = retirer) : ");
            var saisieEmail = Lire(actuel is null ? "E-mail (vide = aucun) : "
                : $"E-mail [{(actuel.AEmail ? actuel.Email : "aucun")}] (vide = inchangé, « {CanauxSuperAdmin.Retrait} » = retirer) : ");
            var souhaites = CanauxSuperAdmin.Souhaites(actuel, saisieTelephone, saisieEmail);
            CreerCompteSuperAdmin.Verifier(identifiant, motDePasse, confirmation, souhaites.Telephone, souhaites.Email);

            // Nouveau numéro : il doit recevoir le code par SMS sur ce numéro (l'e-mail ne prouve pas le numéro)
            var telephoneVerifie = false;
            if (CanauxSuperAdmin.ADeVerifier(actuel?.Telephone, souhaites.Telephone))
            {
                var provisoire = new CompteSuperAdmin(identifiant, "", souhaites.Telephone, null, actuel?.Envoi);
                telephoneVerifie = await CodeAsync(c => envoi.EnvoyerAsync(provisoire, souhaites.Telephone, c), "verifier le telephone du super admin");
            }

            // Nouvelle adresse : elle doit recevoir le code par e-mail à cette adresse (le SMS ne prouve pas l'adresse)
            var emailVerifie = false;
            if (CanauxSuperAdmin.ADeVerifier(actuel?.Email, souhaites.Email))
            {
                var provisoire = new CompteSuperAdmin(identifiant, "", null, souhaites.Email, actuel?.Envoi);
                emailVerifie = await CodeAsync(c => envoi.EnvoyerAAdresseAsync(provisoire, souhaites.Email!, c), "verifier l'e-mail du super admin");
            }

            // Un canal saisi mais non vérifié n'est pas enregistré ; sans aucun canal vérifié, rien n'est créé ni modifié
            var (telephone, email) = CanauxSuperAdmin.Retenir(actuel, souhaites, telephoneVerifie, emailVerifie);
            if (souhaites.Telephone is not null && telephone != souhaites.Telephone)
                Console.WriteLine("Téléphone non vérifié : il n'est pas enregistré" + (actuel?.ASms == true ? " (l'ancien numéro est conservé)." : "."));
            if (souhaites.Email is not null && email != souhaites.Email)
                Console.WriteLine("Adresse e-mail non vérifiée : elle n'est pas enregistrée" + (actuel?.AEmail == true ? " (l'ancienne adresse est conservée)." : "."));

            var compte = sp.GetRequiredService<CreerCompteSuperAdmin>().Executer(identifiant, motDePasse, confirmation, telephone, email);
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.SuperAdmin").LogWarning(
                "Compte super admin {Action} : {Identifiant}, {Canaux}", actuel is null ? "créé" : "modifié", compte.Identifiant, Canaux(compte));
            Console.WriteLine($"Compte super admin {(actuel is null ? "créé" : "modifié")} : {compte.Identifiant}, {Canaux(compte)}.");
            return 0;
        }
        catch (RegleMetierException e) { return Echec(e.Message); }
        catch (InvalidOperationException e) { return Echec(e.Message); }
    }

    /// <summary>Envoie un code (SMS et/ou e-mail) et demande de le saisir (3 essais). Faux si le code n'a pas pu partir ou n'est pas validé.</summary>
    private static async Task<bool> CodeAsync(Func<string, Task<ResultatEnvoi>> envoyer, string libelle)
    {
        var code = ValidationsSuperAdmin.NouveauCode();
        try
        {
            var resultat = await envoyer($"PharmaOfficine : code {code} pour {libelle}. Ne le donnez a personne.");
            Console.WriteLine($"{resultat.Message}.");
        }
        catch (RegleMetierException e)
        {
            Console.WriteLine(e.Message);
            return false;
        }
        for (var essai = 1; essai <= ValidationsSuperAdmin.EssaisMax; essai++)
            if (Lire("Code reçu : ") == code) return true;
            else Console.WriteLine("Code faux.");
        return false;
    }

    /// <summary>Canaux vérifiés du compte, masqués : « téléphone 06•••••512 et e-mail t•••@exemple.dz ».</summary>
    private static string Canaux(CompteSuperAdmin c) => (c.ASms, c.AEmail) switch
    {
        (true, true) => $"téléphone {RegleDestinataire.MasquerTelephone(c.Telephone!)} et e-mail {RegleDestinataire.MasquerEmail(c.Email!)}",
        (true, false) => $"téléphone {RegleDestinataire.MasquerTelephone(c.Telephone!)} (seul canal)",
        (false, true) => $"e-mail {RegleDestinataire.MasquerEmail(c.Email!)} (seul canal)",
        _ => "aucun canal"
    };

    private static int Echec(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static string Lire(string invite)
    {
        Console.Write(invite);
        return (Console.ReadLine() ?? "").Trim();
    }

    /// <summary>Saisie masquée (sauf entrée redirigée).</summary>
    private static string LireSecret(string invite)
    {
        Console.Write(invite);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
