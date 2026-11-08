using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Application.Notifications;

// ---- Modèles ----------------------------------------------------------------

/// <summary>Réglages lus en base ; le mot de passe SMTP n'en sort jamais, seul son existence est indiquée.</summary>
public sealed record ReglagesNotification(
    string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur, string? SmtpIdentifiant, bool MotDePasseEnregistre,
    string? ModemPort, TimeOnly HeureRapport, DateOnly? DernierRapport,
    decimal? SmsSeuilEcart, bool SmsLotBloque, bool SmsSauvegarde)
{
    public bool SmtpConfigure => !string.IsNullOrWhiteSpace(SmtpServeur) && !string.IsNullOrWhiteSpace(SmtpExpediteur);
    public bool SmsActifs => !string.IsNullOrWhiteSpace(ModemPort);
}

/// <summary>Nouveau mot de passe : null = inchangé, chaîne vide = effacé.</summary>
public sealed record ModificationReglagesNotification(
    string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur, string? SmtpIdentifiant, string? NouveauMotDePasse,
    string? ModemPort, TimeOnly HeureRapport, decimal? SmsSeuilEcart, bool SmsLotBloque, bool SmsSauvegarde);

/// <summary>Paramètres de connexion passés à l'adaptateur d'envoi, mot de passe déchiffré en mémoire seulement.</summary>
public sealed record ConnexionSmtp(string Serveur, int Port, bool Tls, string Expediteur, string? Identifiant, string? MotDePasse);

public sealed record NouveauMessage(
    CanalNotification Canal, long DestinataireId, string Adresse, TypeNotification Type, string? Objet, string Corps, string? CorpsHtml);

public sealed record MessageEnFile(
    long Id, CanalNotification Canal, string Destinataire, string Adresse, TypeNotification Type, string? Objet, string Corps,
    string? CorpsHtml, string Statut, int Tentatives, DateTime? ProchaineTentative, string? DerniereErreur, DateTime CreeLe, DateTime? EnvoyeLe);

/// <summary>Contenu d'une notification : texte détaillé pour l'e-mail, texte court pour le SMS (null = pas de SMS).</summary>
public sealed record ContenuNotification(TypeNotification Type, string Objet, string TexteEmail, string? HtmlEmail, string? TexteSms);

public sealed record SaisieDestinataire(
    string? Nom, string? Email, string? Telephone, bool CanalEmail, bool CanalSms, IReadOnlyCollection<TypeNotification> Types, bool Actif);

// ---- Ports ------------------------------------------------------------------

public interface INotificationsDepot
{
    Task<ReglagesNotification> ReglagesAsync(CancellationToken ct = default);
    /// <param name="motDePasseChiffre">Utilisé seulement si <paramref name="changerMotDePasse"/> est vrai (null = effacer).</param>
    Task EnregistrerReglagesAsync(ModificationReglagesNotification reglages, bool changerMotDePasse, byte[]? motDePasseChiffre, CancellationToken ct = default);
    Task<byte[]?> MotDePasseChiffreAsync(CancellationToken ct = default);
    Task NoterRapportEnvoyeAsync(DateOnly jour, CancellationToken ct = default);

    Task<IReadOnlyList<Destinataire>> DestinatairesAsync(CancellationToken ct = default);
    Task<long> CreerDestinataireAsync(SaisieDestinataire d, CancellationToken ct = default);
    Task<bool> ModifierDestinataireAsync(long id, SaisieDestinataire d, CancellationToken ct = default);
    Task<bool> MarquerValideAsync(long id, bool valide, CancellationToken ct = default);

    Task AjouterMessagesAsync(IReadOnlyList<NouveauMessage> messages, CancellationToken ct = default);
    Task<IReadOnlyList<MessageEnFile>> MessagesAEnvoyerAsync(DateTime maintenantUtc, int limite, CancellationToken ct = default);
    Task MarquerEnvoyeAsync(long id, DateTime quandUtc, CancellationToken ct = default);
    /// <param name="prochaineTentativeUtc">Null : abandon, le message passe au statut « echec ».</param>
    Task MarquerEchecAsync(long id, string erreur, DateTime? prochaineTentativeUtc, CancellationToken ct = default);
    Task<IReadOnlyList<MessageEnFile>> FileAsync(int limite, CancellationToken ct = default);
}

/// <summary>Chiffrement des secrets enregistrés en base (DPAPI sur le PC serveur).</summary>
public interface IProtectionSecret
{
    byte[] Chiffrer(string secret);
    string Dechiffrer(byte[] chiffre);
}

public interface IEnvoiEmail
{
    Task EnvoyerAsync(ConnexionSmtp connexion, string adresse, string objet, string texte, string? html, CancellationToken ct = default);
}

public interface IEnvoiSms
{
    /// <summary>Envoi par commandes AT sur le port série du modem GSM.</summary>
    Task EnvoyerAsync(string portSerie, string numero, string texte, CancellationToken ct = default);
}

/// <summary>Met une notification en file pour chaque destinataire vérifié qui l'a demandée.</summary>
public interface INotificationService
{
    /// <returns>Nombre de messages mis en file.</returns>
    Task<int> MettreEnFileAsync(ContenuNotification contenu, CancellationToken ct = default);
}

/// <summary>Données agrégées du rapport quotidien : aucune donnée de patient ni d'ordonnance.</summary>
public interface IDonneesRapport
{
    Task<DonneesRapportQuotidien> LireAsync(DateOnly jour, CancellationToken ct = default);
}

public sealed record EcartEmploye(string Employe, string Poste, decimal Ecart);
public sealed record AlerteDuJour(string Type, string Produit, string? NumeroLot, string Detail);

public sealed record DonneesRapportQuotidien(
    DateOnly Jour, int NombreVentes, decimal ChiffreAffaires, decimal PartTiersPayant,
    IReadOnlyList<EcartEmploye> Ecarts, int Annulations, decimal MontantAnnule,
    IReadOnlyList<AlerteDuJour> Peremptions, IReadOnlyList<AlerteDuJour> Ruptures);

// ---- Mise en file -------------------------------------------------------------

public sealed class NotificationService(INotificationsDepot depot) : INotificationService
{
    public async Task<int> MettreEnFileAsync(ContenuNotification c, CancellationToken ct = default)
    {
        var reglages = await depot.ReglagesAsync(ct);
        var messages = Construire(c, await depot.DestinatairesAsync(ct), reglages.SmsActifs);
        if (messages.Count > 0) await depot.AjouterMessagesAsync(messages, ct);
        return messages.Count;
    }

    /// <summary>Un message par canal et par destinataire concerné ; pas de SMS sans modem configuré.</summary>
    public static List<NouveauMessage> Construire(ContenuNotification c, IEnumerable<Destinataire> destinataires, bool smsActifs)
    {
        var liste = new List<NouveauMessage>();
        foreach (var d in destinataires)
        {
            if (d.Recoit(c.Type, CanalNotification.Email))
                liste.Add(new NouveauMessage(CanalNotification.Email, d.Id, d.Adresse(CanalNotification.Email), c.Type, c.Objet, c.TexteEmail, c.HtmlEmail));
            if (smsActifs && c.TexteSms is not null && d.Recoit(c.Type, CanalNotification.Sms))
                liste.Add(new NouveauMessage(CanalNotification.Sms, d.Id, d.Adresse(CanalNotification.Sms), c.Type, null, TexteSms.Preparer(c.TexteSms), null));
        }
        return liste;
    }
}

// ---- Envoi de la file (appelé par la tâche de fond) ---------------------------

public sealed record BilanEnvoi(int Envoyes, int Echecs);

public sealed class TraiterFileAttente(INotificationsDepot depot, IEnvoiEmail email, IEnvoiSms sms, IProtectionSecret secrets)
{
    public async Task<BilanEnvoi> ExecuterAsync(DateTime maintenantUtc, CancellationToken ct = default)
    {
        var aEnvoyer = await depot.MessagesAEnvoyerAsync(maintenantUtc, 50, ct);
        if (aEnvoyer.Count == 0) return new BilanEnvoi(0, 0);

        var reglages = await depot.ReglagesAsync(ct);
        ConnexionSmtp? connexion = null;
        if (reglages.SmtpConfigure)
        {
            var chiffre = await depot.MotDePasseChiffreAsync(ct);
            connexion = new ConnexionSmtp(reglages.SmtpServeur!, reglages.SmtpPort, reglages.SmtpTls, reglages.SmtpExpediteur!,
                reglages.SmtpIdentifiant, chiffre is null ? null : secrets.Dechiffrer(chiffre));
        }

        int envoyes = 0, echecs = 0;
        foreach (var m in aEnvoyer)
        {
            try
            {
                if (m.Canal == CanalNotification.Email)
                {
                    if (connexion is null) throw new InvalidOperationException("Serveur SMTP non configuré.");
                    await email.EnvoyerAsync(connexion, m.Adresse, m.Objet ?? "Pharma Officine", m.Corps, m.CorpsHtml, ct);
                }
                else
                {
                    if (!reglages.SmsActifs) throw new InvalidOperationException("Modem GSM non configuré.");
                    await sms.EnvoyerAsync(reglages.ModemPort!, m.Adresse, m.Corps, ct);
                }
                await depot.MarquerEnvoyeAsync(m.Id, maintenantUtc, ct);
                envoyes++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Internet, serveur SMTP ou modem indisponible : nouvelle tentative plus tard
                var prochaine = RegleNouvelleTentative.Prochaine(maintenantUtc, m.CreeLe, m.Tentatives + 1);
                await depot.MarquerEchecAsync(m.Id, ex.Message, prochaine, ct);
                echecs++;
            }
        }
        return new BilanEnvoi(envoyes, echecs);
    }
}

// ---- Rapport quotidien ---------------------------------------------------------

public sealed class EnvoyerRapportQuotidien(INotificationsDepot depot, IDonneesRapport donnees, INotificationService notifications, Administration.IPharmacie pharmacie)
{
    /// <summary>Envoie le rapport du jour une fois l'heure réglée passée, une seule fois par jour.</summary>
    public async Task<bool> ExecuterAsync(DateTime maintenantLocal, CancellationToken ct = default)
    {
        var reglages = await depot.ReglagesAsync(ct);
        var jour = DateOnly.FromDateTime(maintenantLocal);
        if (TimeOnly.FromDateTime(maintenantLocal) < reglages.HeureRapport || reglages.DernierRapport >= jour) return false;
        await EnvoyerAsync(jour, ct);
        await depot.NoterRapportEnvoyeAsync(jour, ct);
        return true;
    }

    public async Task<int> EnvoyerAsync(DateOnly jour, CancellationToken ct = default)
    {
        var d = await donnees.LireAsync(jour, ct);
        var nom = (await pharmacie.LireAsync(ct)).Nom;
        return await notifications.MettreEnFileAsync(RapportQuotidien.Construire(d, nom), ct);
    }
}

/// <summary>Mise en forme du rapport : texte et HTML, à partir des seules données agrégées.</summary>
public static class RapportQuotidien
{
    private static readonly System.Globalization.CultureInfo Fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public static ContenuNotification Construire(DonneesRapportQuotidien d, string? pharmacie)
    {
        var titre = $"Rapport du {d.Jour.ToString("dd/MM/yyyy", Fr)}" + (pharmacie is null ? "" : $" · {pharmacie}");
        var texte = new System.Text.StringBuilder();
        texte.AppendLine(titre).AppendLine();
        texte.AppendLine($"Chiffre du jour : {Da(d.ChiffreAffaires)} ({d.NombreVentes} vente(s), dont {Da(d.PartTiersPayant)} en tiers payant)");
        texte.AppendLine($"Annulations : {d.Annulations} ({Da(d.MontantAnnule)})");
        texte.AppendLine().AppendLine("Écarts de caisse :");
        if (d.Ecarts.Count == 0) texte.AppendLine("  aucune clôture ce jour");
        foreach (var e in d.Ecarts) texte.AppendLine($"  {e.Employe} ({e.Poste}) : {Signe(e.Ecart)}");
        texte.AppendLine().AppendLine($"Nouvelles alertes de péremption : {d.Peremptions.Count}");
        foreach (var a in d.Peremptions) texte.AppendLine($"  {a.Produit}{Lot(a)} : {Detail(a)}");
        texte.AppendLine().AppendLine($"Nouvelles ruptures : {d.Ruptures.Count}");
        foreach (var a in d.Ruptures) texte.AppendLine($"  {a.Produit} : {Detail(a)}");

        var h = new System.Text.StringBuilder();
        h.Append("<!doctype html><html lang=\"fr\"><body style=\"font-family:Segoe UI,Arial,sans-serif;color:#1f2937;max-width:640px\">");
        h.Append($"<h1 style=\"color:#0E5A32;font-size:20px\">{E(titre)}</h1>");
        h.Append("<table style=\"border-collapse:collapse\">");
        Ligne(h, "Chiffre du jour", $"{Da(d.ChiffreAffaires)} ({d.NombreVentes} vente(s))");
        Ligne(h, "Dont tiers payant", Da(d.PartTiersPayant));
        Ligne(h, "Annulations", $"{d.Annulations} ({Da(d.MontantAnnule)})");
        h.Append("</table>");
        h.Append("<h2 style=\"font-size:16px\">Écarts de caisse par employé</h2>");
        if (d.Ecarts.Count == 0) h.Append("<p>Aucune clôture ce jour.</p>");
        else
        {
            h.Append("<table style=\"border-collapse:collapse\">");
            foreach (var e in d.Ecarts)
                Ligne(h, $"{e.Employe} ({e.Poste})", Signe(e.Ecart), e.Ecart != 0 ? "#912018" : null);
            h.Append("</table>");
        }
        Liste(h, "Nouvelles alertes de péremption", d.Peremptions);
        Liste(h, "Nouvelles ruptures", d.Ruptures);
        h.Append("<p style=\"color:#6b7280;font-size:12px\">Message automatique de Pharma Officine. Aucune donnée de patient n’est transmise.</p></body></html>");

        return new ContenuNotification(TypeNotification.RapportQuotidien, titre, texte.ToString(), h.ToString(), null);
    }

    private static void Liste(System.Text.StringBuilder h, string titre, IReadOnlyList<AlerteDuJour> alertes)
    {
        h.Append($"<h2 style=\"font-size:16px\">{E(titre)} : {alertes.Count}</h2>");
        if (alertes.Count == 0) return;
        h.Append("<ul>");
        foreach (var a in alertes) h.Append($"<li><strong>{E(a.Produit)}</strong>{E(Lot(a))} : {E(Detail(a))}</li>");
        h.Append("</ul>");
    }

    private static void Ligne(System.Text.StringBuilder h, string libelle, string valeur, string? couleur = null) =>
        h.Append($"<tr><td style=\"padding:4px 16px 4px 0\">{E(libelle)}</td><td style=\"padding:4px 0;font-weight:600{(couleur is null ? "" : ";color:" + couleur)}\">{E(valeur)}</td></tr>");

    // Le message de l'alerte reprend déjà le produit et le lot : on ne les répète pas
    private static string Detail(AlerteDuJour a)
    {
        foreach (var prefixe in new[] { $"{a.Produit}, lot {a.NumeroLot} : ", $"{a.Produit} " })
            if (a.Detail.StartsWith(prefixe, StringComparison.Ordinal) && a.Detail.Length > prefixe.Length)
                return char.ToUpper(a.Detail[prefixe.Length]) + a.Detail[(prefixe.Length + 1)..];
        return a.Detail;
    }

    private static string Lot(AlerteDuJour a) => a.NumeroLot is null ? "" : $" · lot {a.NumeroLot}";
    private static string Da(decimal m) => m.ToString("N2", Fr) + " DA";
    private static string Signe(decimal m) => (m > 0 ? "+" : "") + Da(m);
    private static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
}

// ---- Alertes immédiates du titulaire ------------------------------------------

/// <summary>Événements graves signalés au titulaire, par SMS (urgent) et par e-mail.</summary>
public interface IAlertesTitulaire
{
    Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default);
    Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default);
    Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default);
    Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default);
}

public sealed class AlertesTitulaire(INotificationsDepot depot, INotificationService notifications) : IAlertesTitulaire
{
    private static readonly System.Globalization.CultureInfo Fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public async Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default)
    {
        var r = await depot.ReglagesAsync(ct);
        var seuil = r.SmsSeuilEcart ?? seuilCaisse;
        if (Math.Abs(ecart) <= seuil) return;
        var montant = (ecart > 0 ? "+" : "") + ecart.ToString("N2", Fr) + " DA";
        var texte = $"Ecart de caisse {montant} a la cloture de {employe} ({poste}), seuil {seuil.ToString("N0", Fr)} DA.";
        await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.EcartCaisse,
            $"Écart de caisse {montant}", $"Écart de caisse de {montant} à la clôture de {employe} ({poste}). Seuil d’alerte : {seuil.ToString("N2", Fr)} DA.",
            null, texte), ct);
    }

    /// <summary>Ni motif (saisi librement, il pourrait citer un patient) ni liste des patients : produit, lot et nombres seulement.</summary>
    public async Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default)
    {
        var r = await depot.ReglagesAsync(ct);
        var texte = $"Lot {numeroLot} de {produit} bloque (rappel) : {boitesEnStock} boite(s) en stock, {boitesVendues} vendue(s).";
        await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.LotBloque,
            $"Lot {numeroLot} bloqué : {produit}",
            $"Le lot {numeroLot} de {produit} a été bloqué (rappel). Boîtes en stock : {boitesEnStock}. Boîtes déjà vendues : {boitesVendues}. La liste des patients à contacter est dans le logiciel (Stock, Traçabilité).",
            null, r.SmsLotBloque ? texte : null), ct);
    }

    public async Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default)
    {
        var r = await depot.ReglagesAsync(ct);
        var objet = disqueAbsent ? "Sauvegarde impossible : disque externe absent" : "Sauvegarde échouée";
        await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.Sauvegarde, objet,
            $"{objet}. {detail}", null, r.SmsSauvegarde ? $"{objet}. {detail}" : null), ct);
    }

    /// <summary>Écarts d'inventaire au-delà des seuils : le titulaire doit les valider (Stock, Inventaire).</summary>
    public async Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default)
    {
        var montant = (valeur > 0 ? "+" : "") + valeur.ToString("N2", Fr) + " DA";
        var signe = (boites > 0 ? "+" : "") + boites;
        await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.EcartInventaire,
            $"Inventaire rayon {rayon} : {lignes} écart(s) à valider",
            $"L'inventaire du rayon {rayon} laisse {lignes} écart(s) au-delà des seuils : {signe} boîte(s), {montant} au prix d'achat. Validez-les ou faites recompter (Stock, Inventaire).",
            null, $"Inventaire rayon {rayon} : {lignes} ecart(s) a valider ({signe} boite(s), {montant})."), ct);
    }
}

// ---- Administration : réglages, destinataires, test, file ---------------------

public sealed class ObtenirReglagesNotification(INotificationsDepot depot, IPermissionService droits)
{
    public async Task<ReglagesNotification> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les réglages des notifications", ct);
        return await depot.ReglagesAsync(ct);
    }
}

public sealed partial class ModifierReglagesNotification(INotificationsDepot depot, IProtectionSecret secrets, IJournal journal, IPermissionService droits)
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^COM\d{1,3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FormatPort();

    public async Task ExecuterAsync(ModificationReglagesNotification m, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "modifier les réglages des notifications", ct);
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var r = m with
        {
            SmtpServeur = Net(m.SmtpServeur), SmtpExpediteur = Net(m.SmtpExpediteur), SmtpIdentifiant = Net(m.SmtpIdentifiant),
            ModemPort = Net(m.ModemPort)?.ToUpperInvariant()
        };
        if (r.SmtpPort is < 1 or > 65535) throw new RegleMetierException("Port SMTP entre 1 et 65535.");
        if (r.SmtpExpediteur is not null && !r.SmtpExpediteur.Contains('@')) throw new RegleMetierException("Adresse de l’expéditeur invalide.");
        if ((r.SmtpServeur is null) != (r.SmtpExpediteur is null))
            throw new RegleMetierException("Renseignez à la fois le serveur SMTP et l’adresse de l’expéditeur, ou aucun des deux.");
        if (r.ModemPort is not null && !FormatPort().IsMatch(r.ModemPort)) throw new RegleMetierException("Port du modem invalide (ex. COM3).");
        if (r.SmsSeuilEcart is < 0) throw new RegleMetierException("Le seuil d’écart ne peut pas être négatif.");

        var changer = m.NouveauMotDePasse is not null;
        var chiffre = string.IsNullOrEmpty(m.NouveauMotDePasse) ? null : secrets.Chiffrer(m.NouveauMotDePasse);
        await depot.EnregistrerReglagesAsync(r, changer, chiffre, ct);
        await journal.EcrireAsync(utilisateurId, "reglages_notification", "parametre_notification", 1,
            new Dictionary<string, object?> { ["smtp"] = r.SmtpServeur, ["modem"] = r.ModemPort, ["mot_de_passe_modifie"] = changer },
            Domain.Securite.Permissions.AdminParametres, ct);
    }
}

public sealed class ListerDestinataires(INotificationsDepot depot, IPermissionService droits)
{
    public async Task<IReadOnlyList<Destinataire>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les destinataires", ct);
        return await depot.DestinatairesAsync(ct);
    }
}

/// <summary>Création ou modification ; un changement d'adresse retire la vérification.</summary>
public sealed class EnregistrerDestinataire(INotificationsDepot depot, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(long? id, SaisieDestinataire s, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "renseigner les destinataires des alertes", ct);
        RegleDestinataire.Verifier(s.Nom, s.Email, s.Telephone, s.CanalEmail, s.CanalSms, s.Types);
        var propre = s with
        {
            Nom = s.Nom!.Trim(),
            Email = string.IsNullOrWhiteSpace(s.Email) ? null : s.Email.Trim(),
            Telephone = string.IsNullOrWhiteSpace(s.Telephone) ? null : RegleDestinataire.NormaliserTelephone(s.Telephone)
        };
        if (id is null)
        {
            var nouveau = await depot.CreerDestinataireAsync(propre, ct);
            await Journaliser(nouveau, propre, utilisateurId, ct);
            return nouveau;
        }

        var avant = (await depot.DestinatairesAsync(ct)).FirstOrDefault(d => d.Id == id) ?? throw new RegleMetierException("Destinataire introuvable.");
        await depot.ModifierDestinataireAsync(id.Value, propre, ct);
        if (avant.Email != propre.Email || avant.Telephone != propre.Telephone)
            await depot.MarquerValideAsync(id.Value, false, ct);
        await Journaliser(id.Value, propre, utilisateurId, ct);
        return id.Value;
    }

    private Task Journaliser(long id, SaisieDestinataire d, long utilisateurId, CancellationToken ct) =>
        journal.EcrireAsync(utilisateurId, "destinataire_notification", "destinataire_notification", id,
            new Dictionary<string, object?> { ["nom"] = d.Nom, ["email"] = d.Email, ["telephone"] = d.Telephone, ["actif"] = d.Actif },
            Domain.Securite.Permissions.AdminParametres, ct);
}

/// <summary>Message de test vers un destinataire enregistré (même non vérifié : c'est ce qui permet de le vérifier).</summary>
public sealed class EnvoyerMessageTest(INotificationsDepot depot, IPermissionService droits)
{
    public async Task<int> ExecuterAsync(long destinataireId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "envoyer un message de test", ct);
        var d = (await depot.DestinatairesAsync(ct)).FirstOrDefault(x => x.Id == destinataireId)
                ?? throw new RegleMetierException("Destinataire introuvable : seules les adresses enregistrées reçoivent des messages.");
        if (!d.Actif) throw new RegleMetierException("Destinataire désactivé.");
        var reglages = await depot.ReglagesAsync(ct);
        var contenu = new ContenuNotification(TypeNotification.Test, "Pharma Officine : message de test",
            $"Bonjour {d.Nom}, ce message de test confirme que vous recevrez les alertes de la pharmacie. Signalez sa réception au titulaire pour valider votre adresse.",
            null, "Pharma Officine : message de test. Signalez sa reception au titulaire pour valider votre numero.");
        var messages = NotificationService.Construire(contenu, [d], reglages.SmsActifs);
        if (messages.Count == 0)
            throw new RegleMetierException(d.CanalSms && !reglages.SmsActifs ? "Modem GSM non configuré : aucun SMS possible." : "Aucun canal utilisable pour ce destinataire.");
        await depot.AjouterMessagesAsync(messages, ct);
        return messages.Count;
    }
}

public sealed class ValiderDestinataire(INotificationsDepot depot, IPermissionService droits)
{
    public async Task ExecuterAsync(long id, bool valide, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "valider un destinataire", ct);
        if (!await depot.MarquerValideAsync(id, valide, ct)) throw new RegleMetierException("Destinataire introuvable.");
    }
}

public sealed class ConsulterFileMessages(INotificationsDepot depot, IPermissionService droits)
{
    public async Task<IReadOnlyList<MessageEnFile>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter la file des messages", ct);
        return await depot.FileAsync(100, ct);
    }
}
