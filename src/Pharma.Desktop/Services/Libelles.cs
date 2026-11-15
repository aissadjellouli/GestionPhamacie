using System.Globalization;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Textes affichés à partir des codes renvoyés par l'API.</summary>
public static class Libelles
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Montant masqué par le serveur (droit manquant, ex. prix d'achat) : « — ».</summary>
    public static string Dinars(decimal? montant, int decimales = 0) => montant is { } m ? Dinars(m, decimales) : "—";

    public static string Dinars(decimal montant, int decimales = 0) =>
        montant.ToString(decimales == 0 ? "N0" : "N2", Fr) + " DA";

    /// <summary>Taux annuel d'un prêt tel qu'au contrat (« 5,75 % »), « — » s'il n'est pas saisi.</summary>
    public static string TauxPret(decimal? taux) => taux is { } t ? t.ToString("0.####", Fr) + " %" : "—";

    /// <summary>Durée d'un prêt en mois (« 60 mois »), « — » si elle n'est pas saisie.</summary>
    public static string DureePret(int? mois) => mois is { } m ? $"{m} mois" : "—";

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
        "lot_delaisse" => "Ancien lot délaissé (titulaire)",
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
        "lot_delaisse" => "Valeur du lot délaissé",
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
        "lot_delaisse" => "Lancer l’inventaire de ce produit",
        _ => null
    };

    // ---- Achats -----------------------------------------------------------

    /// <summary>Fiche contact du destinataire d'une facture (patient, entreprise ou organisme) : type et identifiant.</summary>
    public static (string Type, long? Id) ContactFacture(string destinataire, long? patientId, long? clientId, long? organismeId) => destinataire switch
    {
        "patient" => ("patient", patientId), "entreprise" => ("client", clientId), "organisme" => ("organisme", organismeId), _ => ("", null)
    };

    /// <summary>Fiche contact du tiers d'une ligne du suivi des dettes, selon sa catégorie (v_suivi_dettes, 039).</summary>
    public static string ContactDette(string categorie) => categorie switch
    {
        "patient" or "facture_patient" => "patient", "facture_entreprise" => "client",
        "facture_organisme" or "bordereau" => "organisme", "grossiste" or "effet" => "grossiste", _ => ""
    };

    /// <summary>Type de tiers d'une fiche contact.</summary>
    public static string TypeContact(string type) => type switch
    {
        "patient" => "Patient", "grossiste" => "Grossiste", "organisme" => "Organisme", "client" => "Entreprise cliente", "employe" => "Employé", _ => type
    };

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

    /// <summary>Référence d'un mouvement du relevé de dette : mode de règlement accentué (« (especes) » des mouvements anciens).</summary>
    public static string ReferenceReleve(string reference) =>
        reference.Replace("(especes)", "(espèces)", StringComparison.Ordinal).Replace("(cheque)", "(chèque)", StringComparison.Ordinal);

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
        "coordonnees_modifiees" => "Coordonnées modifiées",
        "patient_modifie" => "Fiche patient modifiée",
        "patient_cree" => "Patient créé",
        "droits_chifa_expires" => "Vente Chifa, droits expirés",
        "droits_chifa_expires_credit" => "Vente à crédit, droits Chifa expirés",
        "reglages_surveillance" => "Réglages du contrôle interne",
        "retour_essai" => "Remarque d’essai",
        "produit_bloque" => "Produit bloqué",
        "produit_debloque" => "Produit débloqué",
        "produit_classe" => "Catégorie de produit modifiée",
        "prix_modifie" => "Prix de vente modifié",
        "facture_rejet_leve" => "Rejet de facture levé",
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
        "tva_sorties" => "TVA à reverser sur les pertes et dons",
        "code_verification_envoye" => "Code de vérification envoyé à un destinataire",
        "destinataire_verifie" => "Destinataire vérifié par code",
        "seuil_stock" => "Seuil de stock d’un produit",
        "peremption_verifiee" => "Date de péremption confirmée",
        "avoir_emis" => "Avoir émis",
        "classe_produit" => "Classe de produit confirmée",
        "code_comptoir" => "Code comptoir choisi",
        "code_comptoir_bloque" => "Code comptoir bloqué",
        "compte_tresorerie_cree" => "Compte de trésorerie créé",
        "compte_tresorerie_modifie" => "Compte de trésorerie modifié",
        "connexion_bloquee" => "Connexion bloquée",
        "correction_credit" => "Dette d’un patient corrigée",
        "delai_alerte_dettes" => "Délai d’alerte des dettes",
        "facture_corrigee" => "Facture corrigée",
        "facture_emise" => "Facture émise",
        "facture_hors_suivi" => "Facture retirée du suivi",
        "facture_rejetee" => "Facture rejetée",
        "facture_remise_en_suivi" => "Facture remise dans le suivi",
        "facture_remplacee" => "Facture remplacée",
        "inventaire_complet_annule" => "Inventaire complet annulé",
        "inventaire_complet_cloture" => "Inventaire complet clôturé",
        "inventaire_complet_commence" => "Inventaire complet commencé",
        "mot_de_passe_change" => "Mot de passe changé",
        "mouvement_tresorerie" => "Mouvement de trésorerie",
        "nomenclature_importee" => "Nomenclature importée",
        "organisme_cree" => "Organisme créé",
        "organisme_modifie" => "Organisme modifié",
        "reception_produit_bloque" => "Réception d’un produit bloqué",
        "reglement_credit" => "Encaissement d’une dette",
        "reglement_grossiste" => "Paiement d’un grossiste",
        "traite_payee" => "Traite payée à un grossiste",
        "dette_soldee_par_facture" => "Dette d’un patient soldée par une facture encaissée",
        "releve_caisse" => "Relève de caisse",
        "remise_cheques" => "Remise de chèques en banque",
        "reprise_chargement" => "Reprise de données chargée",
        "reprise_extraction" => "Reprise de données extraite",
        "retour_client" => "Retour client",
        "transfert_tresorerie" => "Transfert entre comptes",
        "vente_annulee" => "Vente annulée",
        "vente_produit_bloque" => "Vente d’un produit bloqué",
        "vente_sans_ordonnance" => "Vente sans ordonnance (liste I ou II)",
        "versement_caisse_coffre" => "Versement de caisse au coffre",
        "sortie_stock" => "Sortie de stock (périmé, cassé ou don)",
        "correction_entree_stock" => "Correction d’une entrée de stock",
        "don_partiel" => "Don partiel du reste à charge (médicament)",
        "reste_charge_manuel_volet" => "Reste à charge saisi à la main (volet Chifa)",
        "remise_automatique_appliquee" => "Remise automatique appliquée",
        "remise_automatique_creee" => "Remise automatique créée",
        "remise_automatique_modifiee" => "Remise automatique modifiée",
        "remise_automatique_arretee" => "Remise automatique arrêtée",
        "don_remise_employe" => "Don et remise d’un employé modifiés",
        "produit_sensible" => "Produit marqué sensible",
        "produit_non_sensible" => "Produit retiré des sensibles",
        "comptage_reception" => "Livraison comptée (réception à l’aveugle)",
        "comptage_annule" => "Comptage de livraison annulé",
        "comptage_facture" => "Facture conforme au comptage",
        "ecart_reception" => "Écart entre comptage et facture",
        "ecart_reception_valide" => "Écart de réception validé",
        "ecart_reception_recompter" => "Livraison à recompter",
        "grossiste_cree" => "Grossiste créé",
        "grossiste_modifie" => "Grossiste modifié",
        "produit_modifie" => "Produit modifié",
        "medecin_modifie" => "Médecin modifié",
        "autorisation_code" => "Action autorisée par code",
        "annulations_excessives" => "Annulations au-delà du seuil",
        "catalogue_maj_volet_chifa" => "Catalogue mis à jour d’après un volet Chifa",
        "ecart_prix_chifa" => "Écart de prix avec Chifa (volet)",
        "code_chifa_associe" => "Code Chifa associé à un produit",
        "reprise_doublon_patient" => "Doublon de patient traité (reprise de données)",
        "reprise_annulation" => "Reprise de données annulée",
        "code_comptoir_demandeur_bloque" => "Code comptoir bloqué (demande d’autorisation)",
        "comptage_intermediaire" => "État intermédiaire de la caisse (comptage)",
        "comptage_recompte" => "Livraison recomptée",
        "reception_attente_abandonnee" => "Réception en attente abandonnée",
        "tiroir_ouvert" => "Tiroir ouvert sans vente",
        "correction_tiroir" => "Correction d’une sortie du tiroir (écriture inverse)",
        "motif_reglable" => "Motif ou catégorie modifié",
        "volet_chifa_rattache" => "Volet Chifa rattaché à une vente",
        "regularisation_part_organisme" => "Régularisation de la part organisme (volet Chifa)",
        "volet_chifa_patient_garde" => "Volet Chifa d’un autre assuré : patient choisi gardé",
        "vente_chifa_infos" => "Informations Chifa d’une vente modifiées",
        "vente_chifa_exclue" => "Vente Chifa exclue du traitement",
        "vente_chifa_retablie" => "Vente Chifa rétablie dans le traitement",
        "centre_payeur_cree" => "Centre payeur créé",
        "centre_payeur_modifie" => "Centre payeur modifié",
        "mot_de_passe_actuel_refuse" => "Mot de passe actuel refusé",
        "restauration_base" => "Base de données restaurée",
        "restauration_ged" => "Documents restaurés",
        "pret_cree" => "Prêt créé",
        "pret_modifie" => "Prêt modifié",
        "pret_document" => "Document de prêt ajouté",
        "echeancier_pret" => "Échéancier de prêt enregistré",
        "echeance_pret_payee" => "Échéance de prêt payée",
        "correspondance_echeancier" => "Correspondance d’échéancier de prêt enregistrée",
        "financement_utilise" => "Financement utilisé",
        "titulaire_rembourse" => "Titulaire remboursé",
        _ => Humaniser(action)
    };

    /// <summary>Libellés des droits, pour un journal lisible (le code reste affiché s'il est inconnu).</summary>
    /// <summary>Libellé d'un droit (ou « pharmacien titulaire » pour la marque du rôle).</summary>
    public static string Droit(string code) => code switch
    {
        "role.titulaire" => "pharmacien titulaire",
        "role.super_admin" => "super admin",
        _ => LibellesDroits.GetValueOrDefault(code, code)
    };

    private static readonly Dictionary<string, string> LibellesDroits = new()
    {
        ["achats.commander"] = "Passer une commande",
        ["achats.receptionner"] = "Réceptionner une livraison",
        ["achats.retour_fournisseur"] = "Faire un retour fournisseur",
        ["achats.valider_ecart"] = "Valider un écart entre le comptage et la facture d’une livraison",
        ["admin.parametres"] = "Modifier les paramètres",
        ["admin.sauvegardes"] = "Gérer les sauvegardes",
        ["reprise.doublons"] = "Valider les doublons d’import",
        ["admin.utilisateurs"] = "Gérer les utilisateurs et les droits",
        ["caisse.annuler_vente"] = "Annuler une vente validée",
        ["caisse.ouvrir_tiroir"] = "Ouvrir le tiroir sans vente",
        ["caisse.cloturer"] = "Faire la relève et clôturer la caisse",
        ["caisse.remise"] = "Accorder une remise",
        ["caisse.remises_auto"] = "Gérer les remises automatiques",
        ["caisse.vendre_sans_ordonnance"] = "Vendre sans ordonnance un médicament de liste I ou II",
        ["achats.regler"] = "Régler les grossistes",
        ["tresorerie.gerer"] = "Gérer la trésorerie",
        ["factures.gerer"] = "Émettre et gérer les factures clients",
        ["caisse.retour_especes"] = "Rembourser un retour en espèces",
        ["caisse.supprimer_ligne"] = "Supprimer une ligne après scan",
        ["caisse.vendre"] = "Encaisser une vente",
        ["caisse.voir_ecart"] = "Voir le montant attendu et les écarts",
        ["catalogue.creer"] = "Créer un produit au catalogue",
        ["chifa.bordereaux"] = "Gérer les bordereaux et rejets",
        ["chifa.importer"] = "Importer les factures Chifa",
        ["chifa.exclure"] = "Exclure une vente Chifa",
        // Actions accordables par le code d'un ayant droit « prix.modifier » (CodeComptoir.ActionsAutorisables), vues dans « Faire autoriser »
        ["prix.modifier:volet_chifa_ecart"] = "Rattacher un volet Chifa malgré des différences (ou régulariser l’écart de part organisme)",
        ["prix.modifier:choix_prix_chifa"] = "Choisir le prix d’une ligne de volet Chifa (s’aligner sur Chifa ou garder notre prix)",
        ["credit.accorder"] = "Mettre une vente en crédit",
        ["credit.depasser_plafond"] = "Dépasser le plafond de crédit d’un client",
        ["prix.modifier"] = "Modifier un prix de vente",
        ["produits.bloquer"] = "Bloquer un produit à l’achat ou à la vente",
        ["psychotropes.delivrer"] = "Délivrer un psychotrope",
        ["psychotropes.registre"] = "Consulter le registre",
        ["rapports.marges"] = "Voir les prix d’achat et les marges",
        ["rapports.ventes"] = "Voir les rapports de ventes",
        ["stock.ajuster"] = "Corriger le stock (inventaire)",
        ["stock.consulter"] = "Consulter le stock",
        ["stock.declarer_perime"] = "Déclarer un produit périmé ou cassé",
        ["stock.donner"] = "Donner un échantillon ou une gratuité (don à un nécessiteux)",
        ["stock.corriger_entree"] = "Corriger une entrée de stock"
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
            // Rubrique facultative : rien si elle est vide (pas de « — »)
            string Sauf(string nom, string libelle) => T(nom) is "—" or "" ? "" : $" · {libelle} : {T(nom)}";

            return action switch
            {
                "rappel_lot" => $"Lot {T("numero_lot")} · {T("motif")} · {T("ventes")} vente(s) concernée(s)",
                "remise" => $"{N("pourcentage"):0.##} % · ticket {T("ticket")} · {Dinars(N("montant"), 2)}",
                "delivrance_psychotrope" => $"Ticket {T("ticket")}",
                "vente_chifa_infos" => $"Ticket {T("ticket")}" + Sauf("date_soins", "date des soins") + Sauf("centre_payeur", "centre payeur")
                                       + Sauf("saisi_par", "saisi dans Chifa par") + Sauf("majoration", "majoration")
                                       + (N("majoration_montant") > 0 ? $" {Dinars(N("majoration_montant"), 2)}" : ""),
                "vente_chifa_exclue" => $"Ticket {T("ticket")} · motif : {T("motif")}",
                "vente_chifa_retablie" => $"Ticket {T("ticket")} · exclue pour : {T("motif")}",
                "centre_payeur_cree" or "centre_payeur_modifie" => $"{T("organisme")} · centre {T("code")} · {T("nom")}" + (B("actif") ? "" : " · désactivé"),
                "reste_charge_manuel_volet" => $"Ticket {T("ticket")} · volet n° {T("volet")} · part organisme calculée (prix Chifa) {Dinars(N("part_organisme_calculee"), 2)}"
                                               + $", reste calculé {Dinars(N("reste_calcule"), 2)} · reste saisi {Dinars(N("reste_saisi"), 2)} · écart {Dinars(N("ecart"), 2)}"
                                               + (T("valideur") is "—" ? "" : $" · autorisé par {T("valideur")}"),
                "ecart_caisse" => $"Écart {Dinars(N("ecart"), 2)} (attendu {Dinars(N("attendu"), 2)}, compté {Dinars(N("compte"), 2)})" + (B("releve") ? " · relève" : ""),
                "parametres_caisse" => "Règles de caisse modifiées",
                "droits_modifies" => Droits(d),
                "utilisateur_cree" => $"{T("nom")} ({T("identifiant")}), rôle {T("role")}",
                "utilisateur_modifie" => $"{T("identifiant")} : rôle {T("role")}, {(B("actif") ? "compte actif" : "compte désactivé")}"
                                         + (B("mot_de_passe_change") ? ", mot de passe changé" : "")
                                         + (d.TryGetProperty("role_avant", out var ra) && ra.GetString() is { } rav && rav != T("role") ? $" (avant : {rav})" : "")
                                         + (d.TryGetProperty("actif_avant", out var aa) && aa.ValueKind != System.Text.Json.JsonValueKind.Undefined && (aa.ValueKind == System.Text.Json.JsonValueKind.True) != B("actif") ? (B("actif") ? " (réactivé)" : " (désactivé)") : ""),
                "seuils_alerte" or "pharmacie_modifiee" or "reglages_surveillance" or "produit_modifie" or "patient_modifie" => Differences(d),
                "prix_modifie" => $"{T("produit")} : {Dinars(N("avant"), 2)} → {Dinars(N("apres"), 2)}" + (T("source") is "—" ? "" : $" ({T("source")})"),
                "patient_cree" => $"{T("patient")} · plafond de crédit {Dinars(N("plafond_credit"), 2)}",
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
                "tva_sorties" => $"TVA reversée : périmé {(B("perime") ? "oui" : "non")}, casse {(B("casse") ? "oui" : "non")}, don {(B("don") ? "oui" : "non")} (avant : {T("avant")})",
                "seuil_stock" => $"{T("produit")} : {(T("apres") == "—" ? "seuil automatique" : $"seuil {T("apres")} boîte(s)")} (automatique : {T("automatique")}, avant : {(T("avant") == "—" ? "automatique" : T("avant"))})",
                "peremption_verifiee" => $"{T("produit")} lot {T("lot")} : péremption au {T("date")} (lue sur la boîte)",
                // Détails lisibles (recette 2026-11-15) : plus de « Nature : depense », « Produit id », « Pourcentage : 15.00 »
                "mouvement_tresorerie" => $"{Humaniser(T("nature"))} de {Dinars(N("montant"), 2)} · {T("compte")}" + (T("categorie") is "—" ? "" : $" · {T("categorie")}") + (T("libelle") is "—" ? "" : $" · « {T("libelle")} »"),
                "remise_automatique_creee" or "remise_automatique_modifiee" => $"{N("pourcentage"):0.##} % sur {(T("cible") is "—" ? T("categorie") : T("cible"))}"
                                  + (T("debut") is "—" ? "" : $" du {T("debut")} au {T("fin")}") + (T("motif") is "—" ? "" : $" · motif : {T("motif")}"),
                "produit_bloque" or "produit_debloque" => $"{T("produit")} : achat {(B("achat") ? "bloqué" : "permis")}, vente {(B("vente") ? "bloquée" : "permise")}"
                                  + $" (avant : achat {(B("avant_achat") ? "bloqué" : "permis")}, vente {(B("avant_vente") ? "bloquée" : "permise")})" + (T("motif") is "—" ? "" : $" · motif : {T("motif")}"),
                "remise_automatique_arretee" => $"{N("pourcentage"):0.##} % sur {T("cible")} : arrêtée",
                "reglages_sauvegarde" => $"{(B("actif") ? "Automatique" : "Manuelle seulement")} à {T("heure")} · dossier {T("dossier")} · {T("conservation")} conservée(s)",
                // Libellés lisibles (recette 2026-11-15, 6e passage) : plus de « Motif reglable », « Prix applique », « Ventes : 3 306 236 »
                "motif_reglable" => $"{ListeMotifs(T("liste"))} : « {T("libelle")} » · {(B("actif") ? "proposé" : "désactivé")}",
                "ecart_prix_chifa" => $"Ticket {T("ticket")}" + (T("volet") is "—" ? "" : $" · volet n° {T("volet")}") + LignesEcartChifa(d)
                                      + (N("ecart_patient") != 0 ? $" · écart pour le patient {Dinars(N("ecart_patient"), 2)}" : "")
                                      + (T("valideur") is "—" ? "" : $" · autorisé par {T("valideur")}"),
                "reglement_credit" => $"{Dinars(N("montant"), 2)} en {T("mode")} · reçu n° {T("recu")} · reste dû {Dinars(N("reste_du"), 2)}" + VentesReglees(d),
                // Recette 2026-11-15 (7e passage, D5) : plus de codes bruts (charge_patient, assure_different, entree…) ni de rubriques vides
                "regularisation_part_organisme" => $"Ticket {T("ticket")} · volet n° {T("volet")} · {SortRegularisation(T("sort"))} · écart {Dinars(N("ecart"), 2)}"
                                      + $" (part de la vente {Dinars(N("part_organisme_vente"), 2)}, selon Chifa {Dinars(N("part_organisme_chifa"), 2)})"
                                      + (N("montant_patient") != 0 ? $" · pour le patient {Dinars(N("montant_patient"), 2)}" : "")
                                      + (N("dette_diminuee") != 0 ? $" · dette diminuée de {Dinars(N("dette_diminuee"), 2)}" : "")
                                      + (N("a_rendre") != 0 ? $" · à rendre {Dinars(N("a_rendre"), 2)}" : "")
                                      + (N("montant_pharmacie") < 0 ? $" · perte pharmacie {Dinars(-N("montant_pharmacie"), 2)}"
                                         : N("montant_pharmacie") > 0 ? $" · gain organisme {Dinars(N("montant_pharmacie"), 2)}" : "")
                                      + (T("patient") is "—" ? "" : $" · {T("patient")}"),
                "volet_chifa_rattache" => $"Ticket {T("ticket")} · volet n° {T("volet")}"
                                      + (B("concorde") ? " · concorde avec la vente" : $" · différences : {T("ecarts")}" + (T("motif") is "—" ? "" : $" · motif : {T("motif")}"))
                                      + (N("ecart_part_organisme") != 0 ? $" · écart de part organisme {Dinars(N("ecart_part_organisme"), 2)}" : ""),
                "tiroir_ouvert" => $"{T("motif")}" + (N("montant") != 0 ? $" · {(T("sens") == "entrée" ? "entrée" : "sortie")} de {Dinars(N("montant"), 2)}" : "")
                                      + (T("bon") is "—" ? "" : $" · bon {T("bon")}") + Sauf("categorie", "catégorie") + Sauf("libelle", "libellé")
                                      + Sauf("grossiste", "grossiste") + Sauf("beneficiaire", "bénéficiaire") + Sauf("reference", "BL / facture")
                                      + Sauf("patient", "patient") + Sauf("raison", "raison") + Sauf("regularisation_chifa", "régularisation Chifa de la vente")
                                      + Sauf("poste", "poste"),
                "correction_tiroir" => $"Bon {T("bon")} : écriture inverse du bon {T("bon_corrige")} ({T("motif_initial")}, {T("sens_initial")} de {Dinars(N("montant"), 2)})"
                                      + $" · motif : {T("motif")}" + Sauf("poste", "poste"),
                "releve_caisse" => $"Relève · fond reporté {Dinars(N("fond"), 2)}" + Sauf("poste", "poste"),
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
        if (!d.TryGetProperty("avant", out var avant) || !d.TryGetProperty("apres", out var apres))
        {
            // Champ par champ : { "tiroir": { "avant": 3, "apres": 5 }, "produit": "…" } → « Tiroir : 3 → 5 »
            static bool Paire(System.Text.Json.JsonElement v) => v.ValueKind == System.Text.Json.JsonValueKind.Object
                && v.TryGetProperty("avant", out _) && v.TryGetProperty("apres", out _);
            if (d.ValueKind != System.Text.Json.JsonValueKind.Object || !d.EnumerateObject().Any(p => Paire(p.Value))) return Generique(d);
            return string.Join("\n", d.EnumerateObject().Where(p => p.Value.ValueKind != System.Text.Json.JsonValueKind.Null).Select(p => Paire(p.Value)
                ? $"{Humaniser(p.Name)} : {Valeur(p.Value.GetProperty("avant"))} → {Valeur(p.Value.GetProperty("apres"))}"
                : $"{Humaniser(p.Name)} : {Valeur(p.Value)}"));
        }
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
                // Identifiants techniques (produit_id, ordonnance_id…) : rien à lire pour le titulaire
                if (p.Name == "id" || p.Name.EndsWith("_id", StringComparison.Ordinal)) continue;
                // Rubrique vide : pas listée (recette 2026-11-15, D5 : plus de « Raison : — » sur chaque ligne)
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                // Code d'un motif : seul le libellé du motif se lit, quand il est là
                if (p.Name == "motif_code" && e.TryGetProperty("motif", out var motif) && motif.ValueKind == System.Text.Json.JsonValueKind.String) continue;
                // Identifiants de ventes quand les n° de ticket sont là
                if (p.Name == "ventes" && e.TryGetProperty("tickets", out var tk) && tk.ValueKind == System.Text.Json.JsonValueKind.Array) continue;
                var cle = prefixe.Length == 0 ? Humaniser(p.Name) : $"{prefixe}, {Humaniser(p.Name).ToLowerInvariant()}";
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var kv in Aplatir(p.Value, cle)) resultat[kv.Key] = kv.Value;
                else resultat[cle] = ClesNumeros.Contains(p.Name) ? Brut(p.Value) : Valeur(p.Value);
            }
        else resultat[prefixe.Length == 0 ? "Valeur" : prefixe] = Valeur(e);
        return resultat;
    }

    private static string Valeur(System.Text.Json.JsonElement v) => v.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True => "oui",
        System.Text.Json.JsonValueKind.False => "non",
        System.Text.Json.JsonValueKind.Null => "—",
        System.Text.Json.JsonValueKind.String => LibellesDroits.TryGetValue(v.GetString()!, out var droit) ? droit
            : DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var jour) ? Date(jour)
            : ValeursCodees.GetValueOrDefault(v.GetString()!, v.GetString()!),
        // Nombres à la française, sans zéros inutiles : 15.00 → 15 ; 1477.77 → 1 477,77
        System.Text.Json.JsonValueKind.Number when v.TryGetDecimal(out var n) => n.ToString("#,0.##", Fr),
        System.Text.Json.JsonValueKind.Array => v.GetArrayLength() == 0 ? "aucun" : string.Join(", ", v.EnumerateArray().Select(Valeur)),
        System.Text.Json.JsonValueKind.Object => Generique(v),
        _ => v.ToString()
    };

    /// <summary>« jours_orange » devient « Jours orange ».</summary>
    /// <summary>Codes enregistrés tels quels dans le journal (moyens de paiement, motifs) : remis en français.</summary>
    private static readonly Dictionary<string, string> ValeursCodees = new()
    {
        ["especes"] = "espèces", ["cheque"] = "chèque", ["carte"] = "carte", ["virement"] = "virement", ["effet"] = "traite", ["prelevement"] = "prélèvement", ["credit"] = "crédit",
        ["financement"] = "prêt ou apport du titulaire",
        ["perime"] = "périmé", ["casse"] = "cassé", ["echantillon"] = "échantillon ou don", ["peremption_proche"] = "péremption proche",
        ["erreur_livraison"] = "erreur de livraison",
        ["depense"] = "dépense", ["encaissement"] = "encaissement", ["autre"] = "autre", ["defini"] = "défini", ["medicament"] = "médicament",
        ["dermo_cosmetique"] = "dermo-cosmétique", ["fermee"] = "fermée", ["ouverte"] = "ouverte", ["probleme"] = "problème", ["libre"] = "libre",
        ["versement_organisme"] = "versement d’un organisme", ["transfert"] = "transfert",
        // Recette 2026-11-15 (7e passage, D5) : régularisation Chifa, écarts du volet, sens du tiroir, catégories de dépenses du départ
        ["charge_patient"] = "à la charge du patient", ["perte_pharmacie"] = "perte pharmacie", ["avoir_patient"] = "rendu au patient",
        ["gain_organisme"] = "gain organisme", ["prix_sous_seuil"] = "écart de prix sous le seuil", ["assure_different"] = "assuré différent",
        ["quantite"] = "quantité différente", ["absent_vente"] = "sur le volet, pas dans la vente", ["absent_volet"] = "dans la vente, pas sur le volet",
        ["code_non_associe"] = "code Chifa non associé", ["concorde"] = "concorde", ["entree"] = "entrée", ["sortie"] = "sortie",
        ["entretien"] = "Entretien et réparations", ["fournitures"] = "Fournitures et consommables", ["energie"] = "Électricité, eau, gaz",
        ["impots"] = "Impôts et taxes", ["loyer"] = "Loyer et charges", ["salaires"] = "Salaires et cotisations",
        ["charges_financieres"] = "Charges financières (intérêts d’emprunt)"
    };

    /// <summary>Sort d'une régularisation Chifa en clair (même texte que RegularisationPartOrganisme.LibelleSort côté serveur).</summary>
    private static string SortRegularisation(string sort) => sort switch
    {
        "charge_patient" or "à la charge du patient" => "à la charge du patient",
        "perte_pharmacie" or "perte pharmacie" => "perte pharmacie",
        "avoir_patient" or "rendu au patient" => "rendu au patient",
        "gain_organisme" or "gain organisme" => "gain organisme",
        _ => sort
    };

    /// <summary>Clés entières du journal dont la forme mot à mot serait fausse.</summary>
    private static readonly Dictionary<string, string> ClesJournal = new()
    {
        ["reste_du"] = "Reste dû", ["autorise_par"] = "Autorisé par", ["valide_par"] = "Validé par", ["deja_rendu"] = "Déjà rendu",
        ["part_tiroir"] = "Part du tiroir", ["mode_remboursement"] = "Mode de remboursement", ["dette_diminuee"] = "Dette diminuée",
        // Recette 2026-11-15 (6e passage) : volet Chifa, écarts de prix, motifs
        ["prix_applique"] = "Prix appliqué", ["prix_chifa"] = "Prix Chifa", ["notre_prix"] = "Notre prix", ["choix"] = "Choix",
        ["motif"] = "Motif", ["motif_code"] = "Code du motif", ["part_organisme"] = "Part organisme", ["ecart"] = "Écart",
        ["volet"] = "N° du volet", ["ticket"] = "Ticket", ["tickets"] = "Tickets", ["ventes"] = "Ventes", ["vente"] = "Vente", ["recu"] = "Reçu n°",
        ["ecart_patient"] = "Écart pour le patient", ["valideur"] = "Autorisé par", ["validateur"] = "Validé par",
        ["part_organisme_calculee"] = "Part organisme calculée (prix Chifa)", ["part_organisme_vente"] = "Part organisme de la vente",
        ["part_organisme_chifa"] = "Part organisme selon Chifa", ["reste_calcule"] = "Reste calculé", ["reste_saisi"] = "Reste saisi",
        ["date_facture"] = "Date de la facture", ["date_ordonnance"] = "Date de l’ordonnance", ["concorde"] = "Concorde",
        ["ecarts"] = "Écarts", ["qte_volet"] = "Quantité du volet", ["qte_vente"] = "Quantité vendue",
        ["assure_volet"] = "Assuré du volet", ["num_assure_patient"] = "N° d’assuré du patient", ["tarif_reference"] = "Tarif de référence",
        ["remboursable"] = "Remboursable", ["montant_dette"] = "Montant de la dette", ["decision"] = "Décision", ["dette"] = "Dette",
        ["liste"] = "Liste", ["libelle"] = "Libellé", ["actif"] = "Actif", ["origine"] = "Origine", ["poste"] = "Poste",
        // Recette 2026-11-15 (7e passage, D5)
        ["session_precedente"] = "Session précédente n°", ["bon_corrige"] = "Bon corrigé", ["beneficiaire"] = "Bénéficiaire", ["a_rendre"] = "À rendre",
        ["sort"] = "Sort", ["sens"] = "Sens", ["sens_initial"] = "Sens d’origine", ["motif_initial"] = "Motif d’origine", ["bon"] = "Bon",
        ["montant_patient"] = "Pour le patient", ["montant_pharmacie"] = "Effet pour la pharmacie", ["ecart_part_organisme"] = "Écart de part organisme",
        ["regularisation_chifa"] = "Régularisation Chifa de la vente", ["grossiste"] = "Grossiste", ["raison"] = "Raison", ["reference"] = "Référence",
        ["categorie"] = "Catégorie", ["fond"] = "Fond", ["bon_origine"] = "Bon d’origine"
    };

    /// <summary>Clés dont la valeur est un numéro (vente, ticket, volet, reçu) : jamais formatée comme un montant (3306236, pas 3 306 236).</summary>
    private static readonly HashSet<string> ClesNumeros = new(StringComparer.Ordinal)
    {
        "ventes", "vente", "ticket", "tickets", "volet", "recu", "numero", "bl", "facture", "session_precedente", "bon", "bon_corrige", "bon_origine"
    };

    /// <summary>Valeur telle quelle (numéros) : tableau joint par des virgules, nombre sans séparateur de milliers.</summary>
    private static string Brut(System.Text.Json.JsonElement v) => v.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Number => v.GetRawText(),
        System.Text.Json.JsonValueKind.String => v.GetString()!,
        System.Text.Json.JsonValueKind.Null => "—",
        System.Text.Json.JsonValueKind.Array => v.GetArrayLength() == 0 ? "aucun" : string.Join(", ", v.EnumerateArray().Select(Brut)),
        _ => Valeur(v)
    };

    /// <summary>Nom lisible d'une liste de motifs réglables (Administration › Motifs et catégories).</summary>
    private static string ListeMotifs(string liste) => liste switch
    {
        "charge" => "Catégories de dépenses",
        "attente" => "Motifs de mise en attente",
        "tiroir" => "Motifs d’ouverture du tiroir",
        "sortie_stock" => "Motifs de sortie de stock",
        "retour_fournisseur" => "Motifs de retour fournisseur",
        "volet_absent" => "Motifs de vente Chifa sans n° de facture",
        "chifa_exclusion" => "Motifs d’exclusion d’une vente Chifa",
        "majoration_chifa" => "Types de majoration Chifa",
        _ => Humaniser(liste)
    };

    /// <summary>Lignes d'un écart de prix Chifa : « produit : prix Chifa …, notre prix …, appliqué … (choix) ».</summary>
    private static string LignesEcartChifa(System.Text.Json.JsonElement d)
    {
        if (!d.TryGetProperty("lignes", out var lignes) || lignes.ValueKind != System.Text.Json.JsonValueKind.Array) return "";
        static decimal Num(System.Text.Json.JsonElement l, string nom) => l.TryGetProperty(nom, out var v) && v.TryGetDecimal(out var n) ? n : 0;
        static string Choix(string? c) => c switch
        {
            "aligne_auto" => "aligné d’office", "aligne" => "aligné sur Chifa", "garde" => "notre prix gardé", "plus_eleve" => "prix le plus élevé",
            null => "", _ => Humaniser(c).ToLowerInvariant()
        };
        return string.Concat(lignes.EnumerateArray().Select(l =>
            $"\n{(l.TryGetProperty("produit", out var p) ? p.GetString() : "?")} : prix Chifa {Dinars(Num(l, "prix_chifa"), 2)}, notre prix {Dinars(Num(l, "notre_prix"), 2)}, "
            + $"prix appliqué {Dinars(Num(l, "prix_applique"), 2)}"
            + (l.TryGetProperty("choix", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? $" ({Choix(c.GetString())})" : "")));
    }

    /// <summary>Ventes soldées par un règlement de dette : n° de ticket si connus, sinon n° de vente bruts.</summary>
    private static string VentesReglees(System.Text.Json.JsonElement d)
    {
        if (d.TryGetProperty("tickets", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Array && t.GetArrayLength() > 0)
            return $" · ventes choisies : {Brut(t)}";
        if (d.TryGetProperty("ventes", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array && v.GetArrayLength() > 0)
            return $" · ventes choisies n° {Brut(v)}";
        return "";
    }

    private static string Humaniser(string code)
    {
        if (ClesJournal.TryGetValue(code, out var cle)) return cle;
        var s = string.Join(' ', code.Replace('_', ' ').Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => Accents.TryGetValue(m, out var a) ? a : m));
        return s.Length == 0 ? code : char.ToUpper(s[0]) + s[1..];
    }

    /// <summary>Mots des codes techniques (sans accents) remis en français dans le journal.</summary>
    private static readonly Dictionary<string, string> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["premiere"] = "première", ["echeance"] = "échéance", ["numero"] = "numéro", ["ecart"] = "écart", ["emise"] = "émise",
        ["regle"] = "réglé", ["reglement"] = "règlement", ["periode"] = "période", ["peremption"] = "péremption", ["prenom"] = "prénom",
        ["telephone"] = "téléphone", ["delai"] = "délai", ["categorie"] = "catégorie", ["quantite"] = "quantité", ["rembourse"] = "remboursé",
        ["reduction"] = "réduction", ["especes"] = "espèces", ["cheque"] = "chèque", ["reception"] = "réception", ["releve"] = "relevé",
        ["cree"] = "créé", ["modifie"] = "modifié", ["valide"] = "validé", ["deja"] = "déjà", ["rendu"] = "rendu", ["libelle"] = "libellé",
        ["frequence"] = "fréquence", ["deconnexion"] = "déconnexion", ["controle"] = "contrôle", ["ancienne"] = "ancienne", ["diminuee"] = "diminuée",
        ["reference"] = "référence", ["apres"] = "après", ["autorise"] = "autorisé", ["recu"] = "reçu", ["detruites"] = "détruites",
        ["boites"] = "boîtes", ["seuil"] = "seuil", ["delivrance"] = "délivrance", ["medecin"] = "médecin", ["specialite"] = "spécialité",
        ["entree"] = "entrée", ["debut"] = "début", ["ecran"] = "écran", ["depense"] = "dépense", ["mouvement"] = "mouvement", ["reglees"] = "réglées", ["reglee"] = "réglée", ["cle"] = "clé", ["etat"] = "état", ["etagere"] = "étagère"
    };

    // ---- Traçabilité ------------------------------------------------------

    public static string StatutBoite(string statut) => statut switch
    {
        "en_stock" => "En stock",
        "vendue" => "Vendue",
        "retournee_fournisseur" => "Retournée au fournisseur",
        "detruite" => "Détruite",
        "retour_client" => "Retour client",
        "donnee" => "Donnée (échantillon ou don)",
        "manquante" => "Manquante à la réception (à réclamer)",
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
        "inventaire" => "Vue à l’inventaire",
        "echantillon" => "Échantillon ou don",
        "manquant_reception" => "Manquante à la réception",
        _ => type
    };
}
