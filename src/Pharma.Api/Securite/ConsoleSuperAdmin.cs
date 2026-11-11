using System.Text;
using Pharma.Application.Securite;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Api.Securite;

/// <summary>
/// Création ou modification du compte super admin, sur le PC serveur seulement : « Pharma.Api.exe --super-admin ».
/// Compte existant : mot de passe actuel et code envoyé au téléphone actuel ; nouveau téléphone : code reçu par SMS sur ce numéro.
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
                Console.WriteLine($"Un compte existe ({actuel.Identifiant}, téléphone {RegleDestinataire.MasquerTelephone(actuel.Telephone)}). Il sera modifié.");
                if (!sp.GetRequiredService<IVerificateurMotDePasse>().Verifier(LireSecret("Mot de passe actuel : "), actuel.MotDePasseHash))
                    return Echec("Mot de passe faux : rien n'a été modifié.");
                if (!await CodeAsync(envoi, actuel, null, "modifier le compte super admin"))
                    return Echec("Code non validé : rien n'a été modifié.");
            }

            var identifiant = Lire($"Identifiant{(actuel is null ? "" : $" [{actuel.Identifiant}]")} : ") is { Length: > 0 } s ? s : actuel?.Identifiant ?? "";
            var motDePasse = LireSecret($"Nouveau mot de passe ({CreerCompteSuperAdmin.LongueurMin} caractères au moins) : ");
            var confirmation = LireSecret("Confirmation : ");
            var telephone = Lire($"Téléphone mobile (obligatoire){(actuel is null ? "" : " [inchangé]")} : ") is { Length: > 0 } t ? t : actuel?.Telephone ?? "";
            var email = Lire($"E-mail (obligatoire, secours si le SMS ne part pas){(actuel is null ? "" : $" [{actuel.Email}]")} : ") is { Length: > 0 } e ? e : actuel?.Email ?? "";
            CreerCompteSuperAdmin.Verifier(identifiant, motDePasse, confirmation, telephone, email);

            // Nouveau numéro : il doit recevoir le code (SMS seulement, l'e-mail ne prouve pas le numéro)
            var nouveauNumero = RegleDestinataire.NormaliserTelephone(telephone);
            if (actuel is null || nouveauNumero != actuel.Telephone)
            {
                var provisoire = new CompteSuperAdmin(identifiant, "", nouveauNumero, email, actuel?.Envoi);
                if (!await CodeAsync(envoi, provisoire, nouveauNumero, "verifier le telephone du super admin"))
                {
                    if (actuel is not null) return Echec("Nouveau téléphone non vérifié : rien n'a été modifié.");
                    if (!Lire("Téléphone non vérifié (modem GSM absent ?). Créer le compte quand même ? (o/n) : ").StartsWith('o'))
                        return Echec("Compte non créé.");
                }
            }

            var compte = sp.GetRequiredService<CreerCompteSuperAdmin>().Executer(identifiant, motDePasse, confirmation, telephone, email);
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.SuperAdmin").LogWarning(
                "Compte super admin {Action} : {Identifiant}, téléphone {Telephone}", actuel is null ? "créé" : "modifié",
                compte.Identifiant, RegleDestinataire.MasquerTelephone(compte.Telephone));
            Console.WriteLine($"Compte super admin {(actuel is null ? "créé" : "modifié")} : {compte.Identifiant}, téléphone {RegleDestinataire.MasquerTelephone(compte.Telephone)}.");
            return 0;
        }
        catch (RegleMetierException e) { return Echec(e.Message); }
        catch (InvalidOperationException e) { return Echec(e.Message); }
    }

    /// <summary>Envoie un code et demande de le saisir (3 essais). Faux si le code n'a pas pu partir ou n'est pas validé.</summary>
    private static async Task<bool> CodeAsync(EnvoiCodeSuperAdmin envoi, CompteSuperAdmin compte, string? numero, string libelle)
    {
        var code = ValidationsSuperAdmin.NouveauCode();
        try
        {
            var (canal, destinataire) = await envoi.EnvoyerAsync(compte, numero, $"PharmaOfficine : code {code} pour {libelle}. Ne le donnez a personne.");
            Console.WriteLine($"Code envoyé par {canal} au {destinataire}.");
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
