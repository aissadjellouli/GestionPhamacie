# Fonctionnalités et état d’avancement

Légende : **Base** = prêt dans les scripts SQL · **API** = exposé par l’API · **Écran** = disponible dans le poste client · **Maquette** = dessiné seulement.

| Module | Fonction | État |
| --- | --- | --- |
| Alertes | Péremption à 3 niveaux de seuils, risque d’écoulement, valeur en dinars | Base, API, Écran |
| Alertes | Rupture avec alternatives de même DCI, stock critique, dormant, surstock | Base, API, Écran |
| Alertes | Reporter (date au choix), marquer traitée (action prise, employé) | Base, API, Écran |
| Caisse | Recherche tolérante aux fautes, lot FEFO, blocage du périmé expliqué, alternatives en rupture | Base, API, Écran |
| Caisse | Panier, encaissement (espèces avec rendu, carte, chèque), tiers payant CNAS/CASNOS (taux 80 ou 100 % choisi au comptoir), crédit avec plafond, remise plafonnée par rôle, ordonnance et registre des psychotropes, raccourci F12 | Base, API, Écran |
| Caisse | Ticket imprimé, tiroir-caisse | Bloqué : modèle d’imprimante ESC/POS et matériel à définir |
| Contrôle de caisse | Ouverture avec fond de caisse, clôture en comptage aveugle par coupure, écart réservé au droit « caisse.voir_ecart », rapport PDF, journal des actions sensibles, paramètres, déconnexion automatique | Base, API, Écran |
| Contrôle de caisse | Annulation de vente, code du titulaire pour une action refusée | Maquette |
| Stock | Consultation : indicateurs, lots, péremptions, grossistes, filtres | Base, API, Écran |
| Stock | Emplacements (rayon, étagère, tiroir ; vente et réserve) : gestion dans l'Administration, rangement et filtre par rayon dans le Stock, affichés dans les alertes, le rappel de lot (« boîtes à retirer ») et sur les étiquettes internes | Base, API, Écran |
| Stock | Inventaire tournant : rayon proposé chaque jour (dates à vérifier, jamais compté, le plus ancien), comptage à l'aveugle par lot (saisie ou scan d'étiquette ou de Data Matrix), réserve comptée avec le rayon, date de péremption confirmée pour les lots de la reprise, petits écarts corrigés tout de suite, gros écarts validés par le titulaire (prévenu par SMS ou e-mail), historique | Base, API, Écran |
| Stock | Registre des psychotropes | Base, Maquette |
| Traçabilité | Identité par boîte, historique (frise), avancement de l’étiquetage, rappel de lot (blocage, patients à contacter, export CSV, journal) | Base, API, Écran |
| Caisse | Ordonnance scannée (scanner WIA, caméra, fichier image ou PDF) : lecture par l'OCR de Windows, rapprochement avec le catalogue, nombre de boîtes lu, image liée à la vente ; équivalent proposé en rupture (même DCI, sinon autre dosage ou forme à valider) | Base, API, Écran |
| Caisse | Lecture assistée des ordonnances manuscrites : image agrandie, pivotée et zoomée à côté d'une recherche tolérante aux fautes | Écran (l'écriture manuscrite n'est pas lue automatiquement, voir ci-dessous) |
| Achats | Bons de commande par grossiste (brouillon, dernier prix d'achat repris, suggestions des ruptures et stocks critiques, émission en PDF, annulation) | Base, API, Écran |
| Achats | Réception avec ou sans commande : lots, péremption, prix facturé, lecture des Data Matrix (GTIN, lot, péremption, série), refus du périmé, dette du grossiste augmentée du TTC | Base, API, Écran |
| Achats | Retours au grossiste d'origine : préparation depuis une alerte, un rappel ou une recherche, scan exigé des boîtes identifiées, bon de retour PDF, avoir déduit de la dette, annulation ou refus avec remise en stock tracée | Base, API, Écran |
| Stock | Création d'un produit absent du catalogue (fiche dans Stock, ou à la réception, y compris depuis un Data Matrix inconnu) : DCI créée au besoin, doublons et code-barres déjà pris refusés, droit « catalogue.creer », journal | Base, API, Écran |
| Achats | Règlement des factures grossistes | À faire |
| Étiquettes | EAN-13 interne par boîte : planche PDF A4 (4 × 9 étiquettes 50 × 30 mm) et envoi ZPL à une imprimante thermique réseau | Base, API, Écran |
| Chifa | Import des factures signées, bordereaux, rejets | Connecteur à compléter (format inconnu) |
| Rapports | Ventes, top produits, marges, écarts de caisse | Maquette |
| Administration | Alertes et seuils (général, par grossiste avec délai de reprise, exceptions par produit, stock et commandes), recalcul immédiat des alertes | Base, API, Écran |
| Administration | Utilisateurs (création, rôle, désactivation, mot de passe) et matrice des droits par rôle, remise maximale, dernière connexion | Base, API, Écran |
| Administration | Identité de la pharmacie (en-tête des postes, rapport de clôture), caisse et journal des actions sensibles | Base, API, Écran |
| Administration | Notifications au titulaire (réservées au titulaire) : rapport quotidien par e-mail, SMS d'urgence par modem GSM (écart de caisse, lot bloqué, sauvegarde), destinataires vérifiés, file d'attente avec nouvelles tentatives, mot de passe SMTP chiffré (DPAPI) | Base, API, Écran (à tester avec un vrai modem et un vrai compte SMTP) |
| Administration | Sauvegardes : pg_dump quotidien vers un disque externe, conservation des N dernières, journal, alerte du titulaire en cas d'échec ou de disque absent | Base, API, Écran |
| Administration | Langue arabe ou anglaise, imprimantes, reprise de données | Maquette (bloqué : traductions, matériel, logiciel source) |
| Langues | Arabe (droite à gauche), français, anglais | Maquette |

## Ordonnances manuscrites

Un essai de lecture automatique de l'écriture manuscrite a été fait hors ligne avec le modèle libre TrOCR (licence MIT, ONNX Runtime) sur une vraie ordonnance : aucune lecture exploitable (par exemple « Sentreaux 4-my up » pour « Sintrom 4 mg cp »), environ 600 Mo de modèle et 1 Go de mémoire. Il n'est donc pas intégré ; la lecture assistée le remplace.

La liste des prochaines étapes est dans [feuille-de-route.md](feuille-de-route.md).
