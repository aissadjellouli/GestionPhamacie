using System.Globalization;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Textes affichés à partir des codes renvoyés par l'API.</summary>
public static class Libelles
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Dinars(decimal montant, int decimales = 0) =>
        montant.ToString(decimales == 0 ? "N0" : "N2", Fr) + " DA";

    public static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", Fr);
    public static string MoisAnnee(DateOnly d) => d.ToString("MM/yyyy", Fr);
    public static string DateLocale(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy", Fr);
    public static string DateHeureLocale(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy 'à' HH:mm", Fr);

    public static string Aujourdhui()
    {
        var texte = DateTime.Now.ToString("dddd d MMMM yyyy", Fr);
        return char.ToUpper(texte[0], Fr) + texte[1..];
    }

    // ---- Alertes ----------------------------------------------------------

    public static string Niveau(string niveau) => niveau switch
    {
        "urgent" => "Urgent",
        "attention" => "Attention",
        _ => "Info"
    };

    /// <summary>Classe CSS de la pastille : urgent, attention ou info.</summary>
    public static string ClasseNiveau(string niveau) => niveau is "urgent" or "attention" ? niveau : "info";

    public static string TypeAlerte(AlerteDto a) => a.Type switch
    {
        "peremption" => a.Niveau == "urgent" ? "Péremption imminente" : "Péremption proche",
        "perime" => "Périmé",
        "risque_ecoulement" => "Risque d’écoulement",
        "rupture" => "Rupture",
        "stock_critique" => "Stock critique",
        "dormant" => "Produit dormant",
        "surstock" => "Surstock",
        _ => a.Type
    };

    public static bool EstPeremption(AlerteDto a) => a.Type is "peremption" or "perime" or "risque_ecoulement";

    public static string TitreAlerte(AlerteDto a) =>
        a.NumeroLot is null ? a.Produit : $"{a.Produit} · lot {a.NumeroLot}";

    /// <summary>Le message SQL commence par le produit (et le lot) : on l'enlève, le titre les affiche déjà.</summary>
    public static string DetailAlerte(AlerteDto a)
    {
        string[] prefixes = a.NumeroLot is null
            ? [$"{a.Produit} : ", $"{a.Produit} "]
            : [$"{a.Produit}, lot {a.NumeroLot} : ", $"{a.Produit} : "];
        foreach (var p in prefixes)
            if (a.Message.StartsWith(p, StringComparison.Ordinal) && a.Message.Length > p.Length)
            {
                var reste = a.Message[p.Length..];
                return char.ToUpper(reste[0], Fr) + reste[1..];
            }
        return a.Message;
    }

    public static string? LibelleValeur(AlerteDto a) => a.Type switch
    {
        "peremption" or "risque_ecoulement" => "Valeur à risque",
        "perime" => "Perte",
        _ => null
    };

    /// <summary>Action principale proposée par la maquette pour chaque type d'alerte.</summary>
    public static string? ActionAlerte(AlerteDto a) => a.Type switch
    {
        "peremption" or "risque_ecoulement" => "Retour fournisseur",
        "perime" => "Retirer du stock",
        "rupture" => "Commander",
        "stock_critique" => "Ajouter à la commande",
        "dormant" => "Mettre en avant",
        _ => null
    };

    // ---- Achats -----------------------------------------------------------

    public static string StatutCommande(string s) => s switch
    {
        "brouillon" => "Brouillon", "envoyee" => "Envoyée", "recue_partielle" => "Reçue en partie",
        "recue" => "Reçue", "annulee" => "Annulée", _ => s
    };

    public static string ClasseStatutCommande(string s) => s switch
    {
        "brouillon" => "attention", "envoyee" or "recue_partielle" => "info", "recue" => "ok", _ => ""
    };

    public static string StatutRetour(string s) => s switch
    {
        "brouillon" => "Préparé", "envoye" => "Envoyé", "avoir_recu" => "Avoir reçu", "refuse" => "Annulé ou refusé", _ => s
    };

    public static string ClasseStatutRetour(string s) => s switch
    {
        "brouillon" => "attention", "envoye" => "info", "avoir_recu" => "ok", _ => ""
    };

    public static readonly (string Code, string Libelle)[] MotifsRetour =
    [
        ("peremption_proche", "Péremption proche"), ("perime", "Périmé"), ("defaut", "Défaut"),
        ("erreur_livraison", "Erreur de livraison"), ("rappel_lot", "Rappel de lot")
    ];

    public static string MotifRetour(string code) => MotifsRetour.FirstOrDefault(m => m.Code == code).Libelle ?? code;

    // ---- Stock ------------------------------------------------------------

    public static string StatutStock(string statut) => statut switch
    {
        "rupture" => "Rupture",
        "critique" => "Critique",
        "dormant" => "Dormant",
        "surstock" => "Surstock",
        _ => "Normal"
    };

    public static string ClasseStatutStock(string statut) => statut switch
    {
        "rupture" => "urgent",
        "critique" => "attention",
        "dormant" or "surstock" => "info",
        _ => "ok"
    };

    public static bool EstSousRegistre(string classe) => classe is "psychotrope" or "stupefiant";

    public static string ModePaiement(string? mode) => mode switch
    {
        "especes" => "espèces",
        "carte" => "carte",
        "cheque" => "chèque",
        "credit" => "crédit",
        _ => "—"
    };

    public static string Classe(string? classe) => classe switch
    {
        "liste_i" => "Liste I",
        "liste_ii" => "Liste II",
        "psychotrope" => "Psychotrope",
        "stupefiant" => "Stupéfiant",
        "libre" => "Libre",
        _ => "—"
    };

    /// <summary>Situation dans la nomenclature nationale.</summary>
    public static string SituationNomenclature(string? situation) => situation switch
    {
        "retire" => "Retiré",
        "non_renouvele" => "Non renouvelé",
        "en_vigueur" => "En vigueur",
        _ => "Absent de la nomenclature"
    };

    // ---- Journal des actions sensibles ------------------------------------

    public static string ActionJournal(string action) => action switch
    {
        "rappel_lot" => "Rappel de lot",
        "remise" => "Remise",
        "delivrance_psychotrope" => "Délivrance de psychotrope",
        "ecart_caisse" => "Écart de caisse",
        "parametres_caisse" => "Paramètres de caisse",
        "droits_modifies" => "Droits modifiés",
        "utilisateur_cree" => "Compte créé",
        "utilisateur_modifie" => "Compte modifié",
        "seuils_alerte" => "Seuils d’alerte",
        "pharmacie_modifiee" => "Identité de la pharmacie",
        "commande_emise" => "Commande émise",
        "commande_annulee" => "Commande annulée",
        "reception" => "Réception",
        "retour_prepare" => "Retour préparé",
        "retour_envoye" => "Retour envoyé",
        "avoir_recu" => "Avoir reçu",
        "retour_refuse" => "Retour annulé ou refusé",
        "produit_cree" => "Produit créé",
        "reglages_notification" => "Réglages des notifications",
        "destinataire_notification" => "Destinataire des alertes",
        "reglages_sauvegarde" => "Réglages des sauvegardes",
        "emplacement" => "Emplacement",
        "produit_range" => "Produit rangé",
        "inventaire_termine" => "Inventaire terminé",
        "ecart_inventaire_valide" => "Écart d’inventaire validé",
        "ecart_inventaire_refuse" => "Écart d’inventaire refusé",
        "seuils_inventaire" => "Seuils d’inventaire",
        "seuil_stock" => "Seuil de stock d’un produit",
        "peremption_verifiee" => "Date de péremption confirmée",
        _ => Humaniser(action)
    };

    /// <summary>Libellés des droits, pour un journal lisible (le code reste affiché s'il est inconnu).</summary>
    private static readonly Dictionary<string, string> LibellesDroits = new()
    {
        ["achats.commander"] = "Passer une commande",
        ["achats.receptionner"] = "Réceptionner une livraison",
        ["achats.retour_fournisseur"] = "Faire un retour fournisseur",
        ["admin.parametres"] = "Modifier les paramètres",
        ["admin.sauvegardes"] = "Gérer les sauvegardes",
        ["admin.utilisateurs"] = "Gérer les utilisateurs et les droits",
        ["caisse.annuler_vente"] = "Annuler une vente validée",
        ["caisse.ouvrir_tiroir"] = "Ouvrir le tiroir sans vente",
        ["caisse.remise"] = "Accorder une remise",
        ["caisse.retour_especes"] = "Rembourser un retour en espèces",
        ["caisse.supprimer_ligne"] = "Supprimer une ligne après scan",
        ["caisse.vendre"] = "Encaisser une vente",
        ["caisse.voir_ecart"] = "Voir le montant attendu et les écarts",
        ["catalogue.creer"] = "Créer un produit au catalogue",
        ["chifa.bordereaux"] = "Gérer les bordereaux et rejets",
        ["chifa.importer"] = "Importer les factures Chifa",
        ["credit.accorder"] = "Mettre une vente en crédit",
        ["credit.depasser_plafond"] = "Dépasser le plafond de crédit d’un client",
        ["prix.modifier"] = "Modifier un prix de vente",
        ["psychotropes.delivrer"] = "Délivrer un psychotrope",
        ["psychotropes.registre"] = "Consulter le registre",
        ["rapports.marges"] = "Voir les prix d’achat et les marges",
        ["rapports.ventes"] = "Voir les rapports de ventes",
        ["stock.ajuster"] = "Corriger le stock (inventaire)",
        ["stock.consulter"] = "Consulter le stock",
        ["stock.declarer_perime"] = "Déclarer un produit périmé ou cassé"
    };

    /// <summary>Détail d'une action du journal en phrase lisible, sans accolades ni guillemets.</summary>
    public static string DetailJournal(string action, string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
            var d = doc.RootElement;
            if (d.ValueKind != System.Text.Json.JsonValueKind.Object) return Valeur(d);
            string T(string nom) => d.TryGetProperty(nom, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null ? Valeur(v) : "—";
            decimal N(string nom) => d.TryGetProperty(nom, out var v) && v.TryGetDecimal(out var n) ? n : 0;
            bool B(string nom) => d.TryGetProperty(nom, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

            return action switch
            {
                "rappel_lot" => $"Lot {T("numero_lot")} · {T("motif")} · {T("ventes")} vente(s) concernée(s)",
                "remise" => $"{N("pourcentage"):0.##} % · ticket {T("ticket")} · {Dinars(N("montant"), 2)}",
                "delivrance_psychotrope" => $"Ticket {T("ticket")}",
                "ecart_caisse" => $"Écart {Dinars(N("ecart"), 2)} (attendu {Dinars(N("attendu"), 2)}, compté {Dinars(N("compte"), 2)})",
                "parametres_caisse" => "Règles de caisse modifiées",
                "droits_modifies" => Droits(d),
                "utilisateur_cree" => $"{T("nom")} ({T("identifiant")}), rôle {T("role")}",
                "utilisateur_modifie" => $"{T("identifiant")} : rôle {T("role")}, {(B("actif") ? "compte actif" : "compte désactivé")}"
                                         + (B("mot_de_passe_change") ? ", mot de passe changé" : ""),
                "seuils_alerte" or "pharmacie_modifiee" => Differences(d),
                "commande_emise" => $"Bon {T("numero")} · {Dinars(N("montant_ht"), 2)} HT",
                "commande_annulee" => $"Bon {T("numero")}",
                "reception" => $"BL {T("bl")} · {T("lots")} lot(s) · {Dinars(N("montant_ttc"), 2)} TTC",
                "retour_prepare" => $"{MotifRetour(T("motif"))} · {T("boites")} boîte(s) · avoir attendu {Dinars(N("montant_attendu"), 2)}",
                "retour_envoye" or "retour_refuse" => $"Retour {T("numero")}",
                "avoir_recu" => $"Retour {T("numero")} · avoir de {Dinars(N("montant"), 2)}",
                "produit_cree" => $"{T("nom")} · {T("dci")} {T("dosage")}, {T("forme")} · {Dinars(N("prix_public"), 2)}"
                                  + (T("code_barre") is "—" ? "" : $" · code {T("code_barre")}"),
                "reglages_notification" => $"Serveur e-mail {T("smtp")} · modem {T("modem")}" + (B("mot_de_passe_modifie") ? " · mot de passe changé" : ""),
                "destinataire_notification" => $"{T("nom")} · {T("email")} · {T("telephone")} · {(B("actif") ? "actif" : "désactivé")}",
                "emplacement" => $"{T("code")}" + (T("libelle") is "—" ? "" : $" · {T("libelle")}") + $" · {(T("zone") == "reserve" ? "réserve" : "vente")} · {(B("actif") ? "utilisé" : "désactivé")}",
                "produit_range" => $"{T("produit")} : vente {T("vente")}, réserve {T("reserve")}",
                "inventaire_termine" => $"Rayon {T("rayon")} · {T("lots")} lot(s) · {T("ajustes")} corrigé(s) · {T("a_valider")} à valider · écart {T("boites_ecart")} boîte(s), {Dinars(N("valeur_ecart"), 2)}",
                "ecart_inventaire_valide" or "ecart_inventaire_refuse" => $"{T("produit")} lot {T("lot")} (rayon {T("rayon")}) : théorique {T("theorique")}, compté {T("compte")}, écart {T("ecart")} ({Dinars(N("valeur"), 2)})",
                "seuils_inventaire" => $"{T("seuil_boites")} boîte(s) · {Dinars(N("seuil_montant"), 2)}",
                "seuil_stock" => $"{T("produit")} : {(T("apres") == "—" ? "seuil automatique" : $"seuil {T("apres")} boîte(s)")} (automatique : {T("automatique")}, avant : {(T("avant") == "—" ? "automatique" : T("avant"))})",
                "peremption_verifiee" => $"{T("produit")} lot {T("lot")} : péremption au {T("date")} (lue sur la boîte)",
                "reglages_sauvegarde" => $"{(B("actif") ? "Automatique" : "Manuelle seulement")} à {T("heure")} · dossier {T("dossier")} · {T("conservation")} conservée(s)",
                _ => Generique(d)
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return detailsJson;
        }
    }

    /// <summary>Un rôle par ligne : droits ajoutés et retirés en clair, et remise maximale.</summary>
    private static string Droits(System.Text.Json.JsonElement d)
    {
        if (!d.TryGetProperty("roles", out var roles) || roles.ValueKind != System.Text.Json.JsonValueKind.Array) return Generique(d);
        var lignes = new List<string>();
        foreach (var r in roles.EnumerateArray())
        {
            string Liste(string nom) => r.TryGetProperty(nom, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array
                ? string.Join(", ", v.EnumerateArray().Select(x => LibellesDroits.GetValueOrDefault(x.GetString() ?? "", x.GetString() ?? "")))
                : "";
            var role = r.TryGetProperty("role", out var n) ? n.GetString() : "?";
            var (ajoutes, retires) = (Liste("ajoutes"), Liste("retires"));
            var remise = r.TryGetProperty("remise_max", out var rm) && rm.TryGetDecimal(out var p) ? $"remise maximale {p:0.##} %" : null;
            var parties = new List<string>();
            if (ajoutes.Length > 0) parties.Add($"ajouté : {ajoutes}");
            if (retires.Length > 0) parties.Add($"retiré : {retires}");
            if (parties.Count == 0) parties.Add("aucun droit changé");
            if (remise is not null) parties.Add(remise);
            lignes.Add($"{role} : {string.Join(" ; ", parties)}");
        }
        return string.Join("\n", lignes);
    }

    /// <summary>Réglages avant / après : seules les valeurs qui changent, « libellé : ancienne → nouvelle ».</summary>
    private static string Differences(System.Text.Json.JsonElement d)
    {
        if (!d.TryGetProperty("avant", out var avant) || !d.TryGetProperty("apres", out var apres)) return Generique(d);
        var (a, b) = (Aplatir(avant), Aplatir(apres));
        var changes = b.Where(kv => !a.TryGetValue(kv.Key, out var ancien) || ancien != kv.Value)
                       .Select(kv => $"{kv.Key} : {a.GetValueOrDefault(kv.Key, "—")} → {kv.Value}")
                       .Concat(a.Keys.Except(b.Keys).Select(k => $"{k} : {a[k]} → —"))
                       .ToList();
        return changes.Count == 0 ? "Aucune valeur changée" : string.Join("\n", changes);
    }

    /// <summary>Toute autre action : « libellé : valeur » séparés par des points médians.</summary>
    private static string Generique(System.Text.Json.JsonElement d) =>
        string.Join(" · ", Aplatir(d).Select(kv => $"{kv.Key} : {kv.Value}"));

    private static Dictionary<string, string> Aplatir(System.Text.Json.JsonElement e, string prefixe = "")
    {
        var resultat = new Dictionary<string, string>();
        if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                var cle = prefixe.Length == 0 ? Humaniser(p.Name) : $"{prefixe}, {Humaniser(p.Name).ToLowerInvariant()}";
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var kv in Aplatir(p.Value, cle)) resultat[kv.Key] = kv.Value;
                else resultat[cle] = Valeur(p.Value);
            }
        else resultat[prefixe.Length == 0 ? "Valeur" : prefixe] = Valeur(e);
        return resultat;
    }

    private static string Valeur(System.Text.Json.JsonElement v) => v.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True => "oui",
        System.Text.Json.JsonValueKind.False => "non",
        System.Text.Json.JsonValueKind.Null => "—",
        System.Text.Json.JsonValueKind.String => LibellesDroits.GetValueOrDefault(v.GetString()!, v.GetString()!),
        System.Text.Json.JsonValueKind.Array => v.GetArrayLength() == 0 ? "aucun" : string.Join(", ", v.EnumerateArray().Select(Valeur)),
        System.Text.Json.JsonValueKind.Object => Generique(v),
        _ => v.ToString()
    };

    /// <summary>« jours_orange » devient « Jours orange ».</summary>
    private static string Humaniser(string code)
    {
        var s = code.Replace('_', ' ').Replace('.', ' ').Trim();
        return s.Length == 0 ? code : char.ToUpper(s[0]) + s[1..];
    }

    // ---- Traçabilité ------------------------------------------------------

    public static string StatutBoite(string statut) => statut switch
    {
        "en_stock" => "En stock",
        "vendue" => "Vendue",
        "retournee_fournisseur" => "Retournée au fournisseur",
        "detruite" => "Détruite",
        "retour_client" => "Retour client",
        _ => statut
    };

    public static string Etape(string type) => type switch
    {
        "reception" => "Réception",
        "vente" => "Vente",
        "retour_client" => "Retour client",
        "retour_fournisseur" => "Retour fournisseur",
        "destruction" => "Destruction",
        "blocage" => "Blocage",
        "correction" => "Correction",
        _ => type
    };
}
