using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Application.SuperAdmin;

// ---- Modèles ----------------------------------------------------------------

/// <summary>
/// Copie des réglages d'envoi (modem GSM, serveur SMTP) gardée avec le compte : le code part même si la base est détruite.
/// Mise à jour à chaque enregistrement des notifications et à chaque envoi de code lu en base.
/// </summary>
public sealed record CopieEnvoi(string? ModemPort, string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur,
    string? SmtpIdentifiant, string? SmtpMotDePasse)
{
    public bool SmtpConfigure => !string.IsNullOrWhiteSpace(SmtpServeur) && !string.IsNullOrWhiteSpace(SmtpExpediteur);
}

/// <summary>Compte du super admin : hors de la base, dans un fichier chiffré du PC serveur. Téléphone et e-mail obligatoires.</summary>
/// <param name="DossierSauvegardes">Copie du dossier des sauvegardes (disque externe) : la restauration le retrouve même base détruite.</param>
public sealed record CompteSuperAdmin(string Identifiant, string MotDePasseHash, string Telephone, string Email, CopieEnvoi? Envoi = null,
    string? DossierSauvegardes = null);

/// <summary>Demande de validation envoyée : où le code est parti (numéro ou adresse masqués) et jusqu'à quand il vaut.</summary>
public sealed record DemandeValidation(Guid Id, string Canal, string Destinataire, DateTime ExpireLe);

/// <summary>Validation vérifiée pour la requête en cours (code juste, action prévue).</summary>
public sealed record ValidationAcceptee(string Action, string Canal, string Destinataire);

// ---- Ports ------------------------------------------------------------------

/// <summary>Fichier chiffré du compte (DPAPI, portée machine) sur le PC serveur.</summary>
public interface ICompteSuperAdmin
{
    string Emplacement { get; }
    CompteSuperAdmin? Lire();
    void Enregistrer(CompteSuperAdmin compte);
}

/// <summary>Pharmacien titulaire actif : auteur des données chargées par le super admin (registre des psychotropes compris).</summary>
public interface ITitulaireLecture
{
    Task<long?> IdAsync(CancellationToken ct = default);
}

// ---- Actions ----------------------------------------------------------------

public static class ActionsSuperAdmin
{
    public const string RepriseExtraire = "reprise.extraire";
    public const string RepriseCharger = "reprise.charger";
    public const string RepriseAnnuler = "reprise.annuler";
    public const string ChangerTelephone = "compte.telephone";
    public const string RestaurerBase = "restauration.base";
    public const string RestaurerGed = "restauration.ged";

    /// <summary>Libellé de l'action, repris dans l'avertissement, le SMS et le journal.</summary>
    public static readonly IReadOnlyDictionary<string, string> Libelles = new Dictionary<string, string>
    {
        [RepriseExtraire] = "extraire les donnees de l'ancien logiciel",
        [RepriseCharger] = "charger la reprise dans la base",
        [RepriseAnnuler] = "annuler la reprise",
        [ChangerTelephone] = "changer le telephone du super admin",
        [RestaurerBase] = "restaurer la base de donnees",
        [RestaurerGed] = "restaurer les documents de la GED"
    };

    /// <summary>Marque ajoutée aux droits du poste pour afficher les écrans du super admin.</summary>
    public const string Marque = "role.super_admin";
}

// ---- Super admin de la requête -----------------------------------------------

/// <summary>Renseigné par l'API pour la requête en cours : identifiant du super admin connecté et validation vérifiée.</summary>
public sealed class SuperAdminCourant
{
    public string? Identifiant { get; set; }
    public ValidationAcceptee? Validation { get; set; }

    public void Exiger(string action)
    {
        if (Identifiant is null)
            throw new DroitRefuseException(ActionsSuperAdmin.Marque, action, $"Seul le super admin peut {action}.");
    }

    /// <summary>Action sensible : le code reçu par SMS (ou e-mail) doit avoir été vérifié pour cette action précise.</summary>
    public ValidationAcceptee ExigerValidation(string action, string libelle)
    {
        Exiger(libelle);
        return Validation is { } v && v.Action == action
            ? v
            : throw new DroitRefuseException(ActionsSuperAdmin.Marque, libelle, $"Validation par code manquante pour {libelle}.");
    }

    /// <summary>Détails ajoutés au journal : qui a lancé l'action, et par quel code elle a été validée.</summary>
    public Dictionary<string, object?> Trace() => new()
    {
        ["super_admin"] = Identifiant,
        ["validation"] = Validation is { } v ? $"code envoyé par {v.Canal} au {v.Destinataire}" : null
    };

    public string Auteur => $"super admin {Identifiant}";
}

// ---- Codes de validation ------------------------------------------------------

/// <summary>
/// Codes à 6 chiffres en mémoire du serveur (jamais en base : elle peut être détruite) : valables 5 minutes, à usage unique,
/// liés à une action. 3 codes faux : nouvelles demandes bloquées 15 minutes. Mots de passe faux : blocage comme à la connexion.
/// </summary>
public sealed class ValidationsSuperAdmin(IHorloge? horloge = null)
{
    public static readonly TimeSpan Duree = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Blocage = TimeSpan.FromMinutes(15);
    public const int EssaisMax = 3;

    private sealed record Demande(string Action, byte[] Empreinte, DateTime ExpireLe, string Canal, string Destinataire);

    private readonly ConcurrentDictionary<Guid, Demande> demandes = new();
    private readonly object verrou = new();
    private int codesFaux;
    private DateTime? bloqueJusqu;

    /// <summary>Mots de passe faux (connexion et validations) : même règle que les employés.</summary>
    public EssaisConnexion MotsDePasse { get; } = new();

    private DateTime Maintenant => horloge?.MaintenantUtc ?? DateTime.UtcNow;

    public DateTime? BloqueJusqu
    {
        get { lock (verrou) return bloqueJusqu is { } b && b > Maintenant ? b : null; }
    }

    public static string NouveauCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public DemandeValidation Enregistrer(string action, string code, string canal, string destinataire)
    {
        foreach (var (id, d) in demandes)
            if (d.ExpireLe <= Maintenant) demandes.TryRemove(id, out _);
        var demande = new Demande(action, Empreinte(code), Maintenant + Duree, canal, destinataire);
        var cle = Guid.NewGuid();
        demandes[cle] = demande;
        return new DemandeValidation(cle, canal, destinataire, demande.ExpireLe);
    }

    /// <summary>Vérifie et consomme le code. Faux : message clair ; trois codes faux bloquent les validations 15 minutes.</summary>
    public ValidationAcceptee Verifier(Guid id, string? code, string action)
    {
        if (BloqueJusqu is { } b)
            throw new RegleMetierException($"Validations bloquées après {EssaisMax} codes faux : réessayez après {b.ToLocalTime():HH:mm}.", "code_validation");
        if (!demandes.TryGetValue(id, out var d) || d.Action != action)
            throw new RegleMetierException("Demande de validation inconnue : demandez un nouveau code.", "code_validation");
        if (d.ExpireLe <= Maintenant)
        {
            demandes.TryRemove(id, out _);
            throw new RegleMetierException("Code expiré (5 minutes) : demandez un nouveau code.", "code_validation");
        }
        if (string.IsNullOrWhiteSpace(code) || !CryptographicOperations.FixedTimeEquals(Empreinte(code.Trim()), d.Empreinte))
        {
            lock (verrou)
            {
                if (++codesFaux >= EssaisMax)
                {
                    (codesFaux, bloqueJusqu) = (0, Maintenant + Blocage);
                    demandes.Clear();
                    throw new RegleMetierException($"Code faux : validations bloquées {Blocage.TotalMinutes:0} minutes après {EssaisMax} codes faux.", "code_validation");
                }
            }
            throw new RegleMetierException("Code faux : vérifiez le code reçu.", "code_validation");
        }
        demandes.TryRemove(id, out _);
        lock (verrou) codesFaux = 0;
        return new ValidationAcceptee(d.Action, d.Canal, d.Destinataire);
    }

    private static byte[] Empreinte(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));
}

// ---- Envoi du code -----------------------------------------------------------

/// <summary>
/// Envoie le code au super admin : SMS par le modem GSM, sinon e-mail. Essai avec les réglages de la base, puis, en cas d'échec ou
/// de base inaccessible, avec la copie gardée dans le fichier du compte. La copie n'est remplacée par les réglages de la base
/// qu'après un envoi réussi avec eux : une base restaurée avec des réglages périmés ne l'écrase pas.
/// </summary>
public sealed class EnvoiCodeSuperAdmin(
    ICompteSuperAdmin comptes, IEnvoiSms sms, IEnvoiEmail email, IProtectionSecret secrets, INotificationsDepot? depot = null,
    Action<string>? signaler = null)
{
    public async Task<(string Canal, string Destinataire)> EnvoyerAsync(CompteSuperAdmin compte, string? numero, string texte, CancellationToken ct = default)
    {
        var enBase = await ReglagesBaseAsync(compte, ct);
        var essais = new List<(CopieEnvoi Reglages, string Origine)>();
        if (enBase is not null) essais.Add((enBase, "réglages de la base"));
        if (compte.Envoi is not null && compte.Envoi != enBase) essais.Add((compte.Envoi, "copie du compte"));

        var erreurs = new List<string>();
        foreach (var (reglages, origine) in essais)
        {
            var avant = erreurs.Count;
            if (await EssayerAsync(reglages, compte, numero, texte, erreurs, ct) is { } envoye)
            {
                // Réglages de la base vérifiés par cet envoi : ils deviennent la copie du compte
                if (ReferenceEquals(reglages, enBase) && reglages != compte.Envoi)
                    try { if (comptes.Lire() is { } c) comptes.Enregistrer(c with { Envoi = reglages }); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return envoye;
            }
            if (essais.Count > 1)
                for (var i = avant; i < erreurs.Count; i++) erreurs[i] = $"{erreurs[i]} [{origine}]";
        }
        if (essais.Count == 0) erreurs.Add("ni modem GSM ni serveur e-mail réglés");

        throw new RegleMetierException($"Code non envoyé ({string.Join(" ; ", erreurs)}). Vérifiez le modem GSM et le serveur e-mail " +
                                       "(Administration › Notifications au titulaire), ou restaurez depuis le PC serveur (scripts\\restaurer_pharmacie.ps1).");
    }

    /// <summary>SMS, puis e-mail ; null si aucun n'est parti (erreurs ajoutées à la liste).</summary>
    private async Task<(string Canal, string Destinataire)?> EssayerAsync(CopieEnvoi envoi, CompteSuperAdmin compte, string? numero, string texte,
        List<string> erreurs, CancellationToken ct)
    {
        var telephone = numero ?? compte.Telephone;
        if (!string.IsNullOrWhiteSpace(envoi.ModemPort))
        {
            try
            {
                await sms.EnvoyerAsync(envoi.ModemPort, telephone, TexteSms.Preparer(texte), ct);
                return ("SMS", RegleDestinataire.MasquerTelephone(telephone));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                erreurs.Add($"SMS : {e.Message}");
                signaler?.Invoke($"Code super admin : SMS non envoyé ({e.Message}), essai par e-mail");
            }
        }
        else erreurs.Add("SMS : modem GSM non configuré");

        // Changement de téléphone : le code du nouveau numéro ne peut pas partir par e-mail (il doit prouver le numéro)
        if (numero is null && envoi.SmtpConfigure)
        {
            try
            {
                await email.EnvoyerAsync(new ConnexionSmtp(envoi.SmtpServeur!, envoi.SmtpPort, envoi.SmtpTls, envoi.SmtpExpediteur!, envoi.SmtpIdentifiant, envoi.SmtpMotDePasse),
                    compte.Email, "Code de validation du super admin", texte, null, ct);
                return ("e-mail", RegleDestinataire.MasquerEmail(compte.Email));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                erreurs.Add($"e-mail : {e.Message}");
            }
        }
        else if (numero is null) erreurs.Add("e-mail : serveur SMTP non configuré");
        return null;
    }

    /// <summary>Réglages lus en base ; null si elle ne répond pas (détruite, en cours de restauration…).</summary>
    private async Task<CopieEnvoi?> ReglagesBaseAsync(CompteSuperAdmin compte, CancellationToken ct)
    {
        if (depot is null) return null;
        try
        {
            var r = await depot.ReglagesAsync(ct);
            string? motDePasse = null;
            if (r.MotDePasseEnregistre && await depot.MotDePasseChiffreAsync(ct) is { } chiffre)
                try { motDePasse = secrets.Dechiffrer(chiffre); } catch (InvalidOperationException) { motDePasse = null; }
            return new CopieEnvoi(r.ModemPort, r.SmtpServeur, r.SmtpPort, r.SmtpTls, r.SmtpExpediteur, r.SmtpIdentifiant, motDePasse);
        }
        catch (Exception e) when (e is not OperationCanceledException and not RegleMetierException)
        {
            signaler?.Invoke($"Code super admin : base inaccessible ({e.Message}), réglages d'envoi du fichier du compte");
            return null;
        }
    }

    /// <summary>Recopie des réglages enregistrés dans l'administration (le mot de passe SMTP n'est recopié que s'il change).</summary>
    public void Recopier(ModificationReglagesNotification m)
    {
        if (comptes.Lire() is not { } compte) return;
        var motDePasse = m.NouveauMotDePasse is null ? compte.Envoi?.SmtpMotDePasse : m.NouveauMotDePasse.Length == 0 ? null : m.NouveauMotDePasse;
        comptes.Enregistrer(compte with
        {
            Envoi = new CopieEnvoi(m.ModemPort, m.SmtpServeur, m.SmtpPort, m.SmtpTls, m.SmtpExpediteur, m.SmtpIdentifiant, motDePasse)
        });
    }
}

// ---- Cas d'usage --------------------------------------------------------------

/// <summary>Connexion du super admin : identifiant et mot de passe du fichier du compte. Null : refus (sans dire pourquoi).</summary>
public sealed class SeConnecterSuperAdmin(ICompteSuperAdmin comptes, IVerificateurMotDePasse verificateur, ValidationsSuperAdmin etat, IHorloge? horloge = null)
{
    public CompteSuperAdmin? Executer(string? identifiant, string? motDePasse)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || string.IsNullOrEmpty(motDePasse)) return null;
        var nom = "super:" + identifiant.Trim();
        var maintenant = horloge?.MaintenantUtc ?? DateTime.UtcNow;
        if (etat.MotsDePasse.BloqueJusqu(nom, maintenant) is { } jusqu)
            throw new RegleMetierException($"Connexion bloquée après {EssaisConnexion.EssaisMax} mots de passe faux : réessayez après {jusqu.ToLocalTime():HH:mm}.");
        var compte = comptes.Lire();
        if (compte is null || !string.Equals(compte.Identifiant, identifiant.Trim(), StringComparison.OrdinalIgnoreCase)
            || !verificateur.Verifier(motDePasse, compte.MotDePasseHash))
        {
            if (etat.MotsDePasse.NoterEchec(nom, maintenant))
                throw new RegleMetierException($"Connexion bloquée {EssaisConnexion.Blocage.TotalMinutes:0} minutes après {EssaisConnexion.EssaisMax} mots de passe faux.");
            return null;
        }
        etat.MotsDePasse.NoterSucces(nom);
        return compte;
    }
}

/// <summary>
/// Avant chaque action sensible : le super admin ressaisit son mot de passe, puis un code part sur son téléphone (e-mail en secours).
/// </summary>
public sealed class DemanderValidationSuperAdmin(
    SeConnecterSuperAdmin verifier, ValidationsSuperAdmin etat, EnvoiCodeSuperAdmin envoi, SuperAdminCourant courant)
{
    public async Task<DemandeValidation> ExecuterAsync(string? action, string? motDePasse, CancellationToken ct = default)
    {
        if (action is null || !ActionsSuperAdmin.Libelles.TryGetValue(action, out var libelle))
            throw new RegleMetierException("Action inconnue.");
        courant.Exiger(libelle);
        if (string.IsNullOrEmpty(motDePasse)) throw new RegleMetierException("Ressaisissez votre mot de passe de super admin.", "mot_de_passe_validation");
        if (etat.BloqueJusqu is { } b)
            throw new RegleMetierException($"Validations bloquées après {ValidationsSuperAdmin.EssaisMax} codes faux : réessayez après {b.ToLocalTime():HH:mm}.");
        var compte = verifier.Executer(courant.Identifiant, motDePasse)
                     ?? throw new RegleMetierException("Mot de passe faux.", "mot_de_passe_validation");
        var code = ValidationsSuperAdmin.NouveauCode();
        var (canal, destinataire) = await envoi.EnvoyerAsync(compte, null,
            $"PharmaOfficine : code {code} pour {libelle}. Valable 5 min. Ne le donnez a personne.", ct);
        return etat.Enregistrer(action, code, canal, destinataire);
    }
}

/// <summary>Création du compte sur le PC serveur (commande Pharma.Api.exe --super-admin) : téléphone et e-mail obligatoires.</summary>
public sealed class CreerCompteSuperAdmin(ICompteSuperAdmin comptes, Administration.IHacheurMotDePasse hacheur)
{
    public const int LongueurMin = 12;

    public static void Verifier(string? identifiant, string? motDePasse, string? confirmation, string? telephone, string? email)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || identifiant.Trim().Length < 4)
            throw new RegleMetierException("Identifiant de 4 caractères au moins.");
        if (motDePasse is null || motDePasse.Length < LongueurMin)
            throw new RegleMetierException($"Mot de passe de {LongueurMin} caractères au moins.");
        if (motDePasse != confirmation) throw new RegleMetierException("Les deux mots de passe sont différents.");
        if (!RegleDestinataire.EstMobile(telephone))
            throw new RegleMetierException("Téléphone obligatoire : mobile 05, 06 ou 07 suivi de 8 chiffres, ou +213.");
        if (!RegleDestinataire.EstEmail(email)) throw new RegleMetierException("E-mail obligatoire (secours si le SMS ne part pas).");
    }

    public CompteSuperAdmin Executer(string identifiant, string motDePasse, string confirmation, string telephone, string email)
    {
        Verifier(identifiant, motDePasse, confirmation, telephone, email);
        var compte = new CompteSuperAdmin(identifiant.Trim(), hacheur.Hacher(motDePasse),
            RegleDestinataire.NormaliserTelephone(telephone), email.Trim(), comptes.Lire()?.Envoi, comptes.Lire()?.DossierSauvegardes);
        comptes.Enregistrer(compte);
        return compte;
    }
}
