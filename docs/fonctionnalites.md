# Fonctionnalités et état d’avancement

Inventaire complet des fonctionnalités du logiciel, mis à jour à chaque modification.

Mis à jour le 2026-11-18.

Légende des états :
- **Fait** : codé, disponible à l’écran ou par l’API. « Fait (à essayer…) » : codé, jamais essayé sur le vrai matériel ou en réel.
- **En cours** : en train d’être codé.
- **En attente** : prévu, décidé ou à décider, pas encore codé.
- **Bloqué** : attend un matériel, un format ou une décision externe.
- **À vérifier** : état non établi par les documents ; à confirmer.

## Résumé

| État | Nombre de fonctions |
| --- | --- |
| Fait | 164 |
| En cours | 0 |
| En attente | 45 |
| Bloqué | 8 |
| À vérifier | 1 |
| **Total** | **218** |

## Caisse et ventes

| Fonction | État | Où / référence |
| --- | --- | --- |
| Recherche tolérante aux fautes, lot FEFO, blocage du périmé expliqué, alternatives en rupture, stock restant du produit sur chaque ligne (et après la vente), boîte scannée (étiquette interne ou Data Matrix) servie avec son propre lot et avertissement si un lot proche de la péremption doit partir avant (« J’ai pris une boîte du lot … » pour basculer) | Fait | Caisse |
| Recherche : disponible d’abord, puis rupture avec remplaçant, puis sans remplaçant ; produit moins cher d’un autre laboratoire suggéré ; recherche par indication (« toux », « fièvre ») | Fait | Caisse ; regles-metier.md « Équivalents et recherche » |
| Panier, encaissement (espèces avec rendu, carte, chèque), tiers payant CNAS/CASNOS (taux 80 ou 100 % choisi au comptoir), crédit avec plafond, remise plafonnée par rôle, ordonnance et registre des psychotropes, raccourci F12 | Fait | Caisse |
| Caisse en trois parties (panier, client et ordonnance, paiement), raccourcis F5 à F8 et F12, écran sans défilement (à 1 366 × 768 aussi : bloc client résumé de lui-même si l’écran déborde, rouvert par F6, F5 ou « Modifier », 2026-11-18), panier jamais encaissé deux fois (clé d’encaissement) | Fait | Caisse ; regles-metier.md « Encaissement » |
| Reste à charge saisi en tiers payant, part de l’organisme sur les seules lignes remboursables | Fait | Caisse |
| Remise du vendeur (0 à 100 %, maximum par employé), don partiel sur un médicament, remise négative refusée avec un message | Fait | Caisse |
| Remises automatiques par produit ou catégorie sur une période (jamais sur un produit remboursable) | Fait | Stock › Remises automatiques |
| Prestations sans stock (prise de tension, glycémie…), remboursables ou non par l’organisme, prix 0 refusé | Fait | Caisse ; Administration › Prestations |
| Avoir du patient : né d’une annulation, utilisé en paiement (combinable), remboursé par sortie de tiroir | Fait | Caisse ; fiche patient |
| « Remis à » / « Pour » (membre de la famille), affiché sur le ticket, le relevé et les fiches | Fait | Caisse |
| Médecin prescripteur créé depuis la caisse (n° d’ordre, code CNAS, wilaya) | Fait | Caisse ; Administration › Médecins |
| Vente sans ordonnance d’un médicament de liste I ou II : droit dédié ou code d’un collègue, inscrite au journal | Fait | Caisse |
| Ventes en attente : panier et règlement en cours mis de côté sur le serveur (coupure, redémarrage), repris par n’importe quel employé du poste (le panier en cours part d’abord en attente), abandon avec confirmation, 20 au plus par poste ; prix et parts recalculés à la reprise ; motif obligatoire au bouton « Attente », produits dus listés | Fait | Caisse |
| Quitter la caisse ou changer d’utilisateur panier rempli : vente mise en attente automatiquement, reprise par « Retour » ou par le prochain employé connecté sur le poste | Fait | Caisse |
| Vente en attente au changement d’utilisateur avec serveur injoignable : panier perdu sans signalement | En attente | a-faire.md « Points ouverts » |
| Retour de médicaments par le patient, partiel, sans limite de durée : le pharmacien choisit produit par produit (remis en stock sur son lot, ou à détruire ; refuser = ne rien enregistrer) ; boîtes identifiées cochées par n° de série ; droit « caisse.retour_especes » ou validation sur place par code ; part payée par le patient rendue au prorata depuis la caisse ouverte du poste (attendu diminué), ou retirée de la dette pour une vente à crédit ; registre des psychotropes (retour puis destruction) ; refusé pour une vente en tiers payant (annuler la vente complète) ; motif choisi dans une liste réglable | Fait | Caisse |
| Remboursement sans ticket : vente retrouvée par le scan de la boîte rapportée | Fait | Caisse ; regles-metier.md |
| Retour partiel d’une vente en tiers payant (régularisation de la part de l’organisme) | En attente | a-faire.md « Prochaines demandes » (format Chifa) |
| Ticket imprimé par Windows après la vente (imprimante du poste ou par défaut, rouleau 80 ou 58 mm) : pharmacie, ticket, vendeur, lignes, remise, part organisme, à payer, mode, rendu ; aucune donnée du patient ; ticket en PDF si l’imprimante enregistre un fichier | Fait (à essayer sur la vraie imprimante de tickets) | Caisse |
| Impression directe ESC/POS (coupe, logo), tiroir-caisse | Bloqué (modèle d’imprimante et matériel à définir) | feuille-de-route.md |
| Afficheur client réglé par poste : écran secondaire (fenêtre plein écran, toujours visible, sans prendre le focus : panier, total, payé, rendu, accueil) et/ou afficheur 2 lignes × 20 sur port série (ESC/POS Epson ou CD5220, vitesse réglable) ; essai depuis Administration › Imprimantes et étiquettes ; une panne d’afficheur ne bloque jamais la caisse | Fait (à essayer sur le vrai afficheur) | Caisse ; Administration › Imprimantes |
| Suggestions pendant la frappe (décision du titulaire, 2026-11-18), lot 0 (socle) et lot 1 (caisse) : composant commun `ChampSuggestions` (choix obligatoire ou valeur libre ; 10 propositions, sans accents ni majuscules, ↑ ↓ Entrée Échap), `ChoixPatient`, `ChoixMedecin`, `ValeursSaisies` ; valeurs déjà saisies par liste blanche du serveur (`GET /api/suggestions/{champ}`) ; recherche patient et médecin sans accents (index du script 150), ouverte aux droits Factures et registre sans dette ni plafond ; caisse : patient F6, médecin F5, flèches dans le panier, spécialité du nouveau médecin, tiroir (patient remboursé, libellé, bénéficiaire), annulation (client, dette), relevé de dette | Fait | Caisse ; regles-metier.md « Suggestions pendant la frappe » |
| Suggestions pendant la frappe (décision du titulaire, 2026-11-18), lots 2 à 5 : catalogue et médecins (DCI, forme, indications, nomenclature, spécialité, prescripteur du registre, DCI suivies), argent (banques, libellés de trésorerie et de financements, prêteurs), achats, stock et factures (patients en attente, rappel de lot, rayon, exceptions de seuils, produits sensibles, retours, patient et entreprise d’une facture), filtres produit des tableaux (`FiltreProduit`) | Fait (rayons proposés dès l’entrée dans le champ ; lots épuisés proposés au rappel ; écran Seuils par produit sans défilement) | charte-graphique.md « Règle des listes » ; regles-metier.md (suggestions) |

## Contrôle de caisse

| Fonction | État | Où / référence |
| --- | --- | --- |
| Ouverture avec fond de caisse, clôture en comptage aveugle par coupure, écart réservé au droit « caisse.voir_ecart », rapport PDF, journal des actions sensibles, paramètres, déconnexion automatique | Fait | Caisse › Clôture |
| Caisse partagée par poste : une seule caisse ouverte par poste (tiroir), plusieurs employés y vendent, chaque vente reste à son vendeur ; « Changer d’utilisateur » sans fermer la caisse, par code comptoir personnel (4 à 6 chiffres, haché, bloqué 5 minutes après 5 erreurs, effaçable par l’administrateur des utilisateurs) ; connexion par code limitée aux écrans du comptoir ; relève facultative au changement d’équipe ; vendeurs listés sur le rapport de clôture | Fait | Caisse ; Mon code |
| Annulation d’une vente, sans limite de durée : vente récente annulée par le caissier, vente ancienne validée sur place par le code du pharmacien ou de l’assistant (droit « caisse.annuler_vente ») ; motif obligatoire ; stock remis sur le même lot, boîtes et registre des psychotropes mis à jour, crédit du patient diminué ; argent rendu depuis la caisse ouverte du poste, clôtures passées inchangées ; refus si la vente est sur un bordereau Chifa | Fait | Caisse |
| État de la caisse sans compter ni clôturer (ventes, carte, chèque, crédit, tiers payant, par employé ; jamais d’espèces ni d’attendu) et comptage intermédiaire du tiroir sans clôture, affiché comme la clôture (070) | Fait | Caisse › État |
| Versement des caisses au coffre à la clôture : manuel (Trésorerie) ou automatique (compté moins le fond par défaut, pas à la relève), réglé par le titulaire | Fait | Administration › Caisse |
| Ouverture du tiroir sans vente (F8) avec motif, sorties de tiroir suivies | Fait | Caisse ; Contrôle interne › Tiroir |
| Passation au « ⇄ Changer » : relève de caisse proposée (facultative) ; facture, retour fournisseur et réception en cours laissés au suivant, repris d’un clic dans le bandeau « Tâches laissées en cours sur ce poste » (selon ses droits) | Fait | Bandeau du poste |
| Facture et retour fournisseur laissés au changement d’utilisateur : gardés seulement dans la mémoire du poste (perdus au redémarrage) | En attente | a-faire.md « Points ouverts » |

## Contrôle interne

| Fonction | État | Où / référence |
| --- | --- | --- |
| Écran « Contrôle interne » réservé au titulaire : anomalies par employé et par jour (à examiner, traitées avec l’action prise, jamais supprimées), alerte par e-mail et SMS sans donnée de patient, seuils réglables (urgent au double) ; caisse : boîtes retirées du panier avant paiement, tiroir ouvert sans vente, ventes en attente abandonnées, ventes hors horaires, remises répétées | Fait | Pilotage › Contrôle interne |
| Stock : boîtes fantômes, boîtes manquantes ajustées, casses, échantillons et dons (droit « stock.donner », à qui et pourquoi, jamais un psychotrope) ; produits sensibles choisis par le titulaire, comptés à part et régulièrement (alerte de retard) | Fait | Contrôle interne |
| Comptes : patient récent avec une grosse dette, crédits accordés par employé, crédit jamais remboursé, autorisations par code trop fréquentes, employé sur deux postes à la fois, baisses de prix hors titulaire | Fait | Contrôle interne |
| Pertes et vigilance par employé (titulaire) : perte jour par jour, boîtes fantômes, anomalies, présence ; par employé : perte moyenne présent / absent, manques, annulations, retours, remises, crédits, codes, casses, dons, ajustements ; 366 jours au plus | Fait | Contrôle interne › Risques |
| Abandon d’une réception en attente levé en anomalie du contrôle interne | En attente (à décider) | a-faire.md « Points ouverts » |

## Stock et inventaire

| Fonction | État | Où / référence |
| --- | --- | --- |
| Consultation : indicateurs, lots, péremptions, grossistes, filtres ; liste paginée et filtrée par le serveur, propositions pendant la saisie | Fait | Stock |
| Emplacements (rayon, étagère, tiroir ; vente et réserve) : gestion dans l’Administration, rangement et filtre par rayon dans le Stock, affichés dans les alertes, le rappel de lot et sur les étiquettes internes | Fait | Administration › Emplacements ; Stock |
| Inventaire tournant : rayon proposé chaque jour, comptage à l’aveugle par lot (saisie ou scan), réserve comptée avec le rayon, date de péremption confirmée pour les lots de la reprise, petits écarts corrigés tout de suite, gros écarts validés par le titulaire (prévenu par SMS ou e-mail), étiquetage des boîtes sans identité du rayon à la fin du comptage, historique | Fait | Stock › Inventaire |
| Inventaire complet (annuel), pharmacie ouverte ou fermée : tous les rayons, réserve et produits sans emplacement, outil de l’inventaire tournant ; avancement par rayon ; clôture ; état d’inventaire PDF valorisé au prix d’achat HT, par rayon, total en lettres, signatures ; annulation possible | Fait | Stock › Inventaire |
| Ancien lot délaissé : raison obligatoire à la caisse quand un lot plus récent est servi ; alerte au titulaire seul (SMS ou e-mail) au-delà de seuils réglables ; bouton « Lancer l’inventaire de ce produit » | Fait | Caisse ; Alertes |
| Sorties de stock sans vente : périmé, cassé, échantillon / don (droits dédiés, commentaire obligatoire pour casse et don, registre des psychotropes inscrit automatiquement) | Fait | Stock ; regles-metier.md « Sorties de stock » |
| Correction d’une entrée de stock (PPA, n° de lot, péremption, SHP, quantité) avec motif et historique des corrections | Fait | Stock › Entrées de stock |
| Péremption saisie en MM/AA comme sur la boîte | Fait | Réception, inventaire |
| Blocage d’un produit à l’achat, à la vente ou les deux (droit « produits.bloquer », motif) | Fait | Stock |
| Seuil d’alerte de stock par produit saisi par le pharmacien, sinon automatique | Fait | Administration › Seuils |
| Étiquetage du stock existant rayon par rayon avant l’activation du mode strict | En attente (sur le terrain) | a-faire.md « À vérifier sur le terrain » |

## Achats et grossistes

| Fonction | État | Où / référence |
| --- | --- | --- |
| Bons de commande par grossiste (brouillon, saisie assistée du produit : liste du catalogue sous le champ dès la première lettre, flèches et Entrée, stock affiché, dernier prix d’achat repris, suggestions des ruptures et stocks critiques, émission en PDF, annulation) | Fait | Achats › Commandes |
| Multi-grossistes : catalogues et disponibilités importés (Excel ou CSV, colonnes réglées par grossiste), comparaison des prix par produit, grossiste proposé, commande répartie (un bon par grossiste), envoi du bon par e-mail | Fait (formats réels des grossistes à vérifier) | Achats › Commander ; Catalogues des grossistes |
| Ruptures chez les grossistes (historique noté à chaque import) | Fait | Achats › Ruptures |
| Réception avec ou sans commande : lots, péremption, prix facturé, lecture des Data Matrix (GTIN, lot, péremption, série), refus du périmé, dette du grossiste augmentée du TTC ; lot unique par produit, n° de lot, grossiste et péremption (le même lot relivré complète le lot existant ; chaque boîte garde le prix d’achat de sa livraison) ; date différente ou lot bloqué : choix de l’utilisateur ; une étiquette interne créée et imprimée pour chaque boîte sans Data Matrix, réimpression par livraison ; chaque boîte garde son bon de livraison | Fait | Achats › Réceptions |
| Facture d’achat : remise de ligne, TVA, timbre, SHP par liste (contrôlé), unités gratuites (à 0 DA ou réparties), marge affichée | Fait | Achats › Réceptions |
| PA TTC et PV HT affichés à la réception (et prix public HT sur la fiche produit), pour information | Fait | Réception ; fiche produit |
| Réception à l’aveugle (réglable, active par défaut) : comptage sans voir le bon, puis facture saisie par une autre personne et comparée ; écart bloquant jusqu’à validation (« achats.valider_ecart », titulaire prévenu) ; alertes : même personne au comptage et à la facture, retour sans avoir après 30 jours, avoir incomplet | Fait | Achats › Réceptions |
| Réceptions en attente (069) : saisie mise de côté (bouton, départ de l’écran, déconnexion), reprise depuis n’importe quel poste ; comptage à l’aveugle repris par son auteur ou qui décide des écarts ; abandon tracé, scans du mobile rendus | Fait | Achats › Réceptions en attente |
| Retours au grossiste d’origine : préparation depuis une alerte, un rappel ou une recherche, scan exigé des boîtes identifiées, bon de retour PDF, avoir déduit de la dette, annulation ou refus avec remise en stock tracée | Fait | Achats › Retours |
| Retour en préparation et facture en préparation gardés pour la session : retrouvés par « Retour » | Fait | Achats, Factures |
| « Payer un grossiste » (droit « achats.regler ») : dette par grossiste, factures ouvertes, règlement imputé aux factures cochées ou aux plus anciennes ; espèces (coffre, complément pris dans le tiroir), chèque, virement, effet ; derniers règlements ; journal | Fait | Achats › Payer un grossiste |
| Traites à payer : paiement à l’échéance confirmé par le titulaire | Fait | Achats › Traites à payer |
| Commandes intelligentes (Ramadan, classement ABC) | En attente (V2) | a-faire.md « Questions pour la V2 » |

## Catalogue et nomenclature

| Fonction | État | Où / référence |
| --- | --- | --- |
| Nomenclature nationale (titulaire) : import du fichier Excel officiel du MIPH (en vigueur, non renouvelés, retraits) ; création d’un produit depuis la nomenclature ; psychotropes reconnus par leur DCI ; produits du catalogue à vérifier avec classement en un clic, inscrit au journal | Fait | Administration › Nomenclature |
| Création d’un produit absent du catalogue (fiche dans Stock, ou à la réception, y compris depuis un Data Matrix inconnu) : DCI créée au besoin, doublons et code-barres déjà pris refusés, droit « catalogue.creer », journal ; classe imposée (psychotrope, stupéfiant) et produit « à vérifier » pour un employé non titulaire | Fait | Stock ; Réception |
| Catégories de produits (médicament, dermo-cosmétique, hygiène…) | Fait | Fiche produit |
| Indications thérapeutiques (plusieurs par produit) | Fait | Administration › Indications |
| Contenance (unités par boîte) : saisie sur la fiche, remplie depuis la nomenclature (scripts 132 à 139) | Fait | Fiche produit |
| Saisie en lot de la contenance manquante (écran « Contenance manquante ») et règle des « B/N FL », « B/N SERINGUE » | En attente (à décider) | a-faire.md « À vérifier » |
| Substitution : équivalent exact seulement (activé par défaut), liste blanche, génériques fabriqués en Algérie en tête | Fait | Caisse ; Administration |
| Prix public dans la nomenclature et barème des marges (décret 98-44) avant tout calcul automatique | En attente (à confirmer) | a-faire.md « Prochaines demandes » |
| Parapharmacie distinguée dans le catalogue (préalable à la fidélisation de la V2) | À vérifier | a-faire.md « Prochaines demandes » |

## Traçabilité

| Fonction | État | Où / référence |
| --- | --- | --- |
| Identité par boîte, historique (frise), avancement de l’étiquetage, rappel de lot (blocage, patients à contacter, export CSV, journal) | Fait | Stock › Traçabilité ; tracabilite.md |
| Étiquettes : EAN-13 interne par boîte, planche PDF A4 (4 × 9 étiquettes 50 × 30 mm) et envoi ZPL à une imprimante thermique réseau | Fait (à essayer sur la vraie imprimante d’étiquettes) | Stock › Étiquettes internes |
| Réglage de traçabilité (patient obligatoire : jamais, ordonnance, psychotrope, toujours) | Fait | Administration › Traçabilité |
| Sérialisation nationale : vérification, décommissionnement et réactivation des boîtes Data Matrix, file hors ligne rejouée, alerte si refus ; identifiants GS1 réglables | Fait (adaptateur factice) | Administration › Sérialisation DataMatrix |
| Raccordement à la vraie base nationale de sérialisation | Bloqué (API officielle inconnue) | tracabilite.md |

## Psychotropes

| Fonction | État | Où / référence |
| --- | --- | --- |
| Registre des psychotropes (droit « Consulter le registre ») : entrées et sorties inscrites automatiquement, numéro d’ordre sans trou, soldes par produit et par lot, solde d’ouverture, balance mensuelle, filtres, export PDF (paysage, cachet et griffe) et CSV ; noms en clair pour le titulaire seulement | Fait | Stock › Registre |
| Registre chaîné, scellé chaque mois, corrigé seulement par une écriture d’annulation (titulaire) | Fait | Stock › Registre |
| Porteur de l’ordonnance quand ce n’est pas le patient : nom, adresse et pièce d’identité obligatoires, inscrits au registre | Fait | Caisse |
| Psychotrope au panier d’un employé sans le droit de délivrer : la caisse nomme les employés qui l’ont pour reprendre la vente ; encaissement bloqué, même refus côté serveur | Fait | Caisse |
| Alertes à la délivrance (même patient, même ordonnance, quantité, prescripteur hors zone, durée × contenance), « Forcer » avec motif, liste des alertes forcées | Fait | Caisse ; Administration › Psychotropes |
| Valeurs de départ des seuils d’alerte des psychotropes | En attente (décision du titulaire) | reste-a-faire-synthese.md § 4 |
| Répertoire national des ordonnances (loi 23-05) | Bloqué (adaptateur factice, API officielle inconnue) | regles-metier.md « Alertes à la délivrance » |
| Suivi des antibiotiques : liste des DCI suivies, rapport par molécule et par mois | Fait | Rapports ; Administration › Psychotropes |
| Liste définitive des 214 DCI d’antibiotiques | En attente (décision du titulaire) | reste-a-faire-synthese.md § 4 |
| Colonnes des annexes 12 et 13 du décret 19-379 ; registre informatisé accepté par la DSP | En attente (à confirmer) | a-faire.md « Prochaines demandes » |

## Patients et crédit

| Fonction | État | Où / référence |
| --- | --- | --- |
| Patients : création depuis la caisse, recherche (nom, téléphone par début de numéro, n° d’assuré), modifications journalisées, un seul patient par n° d’assuré | Fait | Caisse ; fiche contact |
| Fichier des patients : liste paginée et filtrée par le serveur (nom, prénom, n° d’assuré, téléphone ; organisme, malade chronique, à crédit, droits Chifa expirés ou bientôt expirés, accord SMS), nom cliquable vers la fiche patient, dette selon le droit qui la montre déjà | Fait | Patients › Fichier des patients ; regles-metier.md « Patients » |
| Lien avec l’assuré principal (conjoint, enfant…), rattachement proposé | Fait | Fiche patient |
| Vente à crédit (droit « credit.accorder »), plafond et dépassement par droit ou code | Fait | Caisse |
| Encaisser une dette : patients avec un crédit, dette, plafond et dernières ventes à crédit ; remboursement total ou partiel en espèces, carte, chèque ou avoir, ventes à solder cochées, surplus annoncé ; reçu imprimable ; journal | Fait | Caisse › Encaisser une dette |
| Correction d’une dette (titulaire), relevé de dette jamais modifié | Fait | Caisse |
| Fiche contact (patient, grossiste, entreprise, organisme, employé) | Fait | Partout |
| Prévenir un patient quand un produit redevient disponible (SMS avec accord enregistré, formulaire papier d’accord) | Fait (à essayer avec le vrai modem) | Patients › Patients en attente (déplacé d’Achats le 2026-11-18) ; aussi en fenêtre depuis la Caisse |
| Fidélisation parapharmacie | En attente (V2) | a-faire.md « Questions pour la V2 » |

## Chifa / tiers payant

| Fonction | État | Où / référence |
| --- | --- | --- |
| Volet Chifa scanné à la caisse (QR code) : vente reprise en tiers payant sans ressaisie, code produit Chifa associé une fois au catalogue, écarts de prix signalés, volet gardé sur la vente et repris une seule fois ; comparaison volet / vente ligne à ligne | Fait (format du QR à confirmer avec la CNAS) | Caisse ; chifa.md |
| Écran Chifa : ventes en attente de volet, circuit « la caisse d’abord », vente « Facturée par Chifa » sans n° avec motif | Fait | Finances › Chifa |
| Bordereaux (CNAS, CASNOS, MDN, par centre payeur) : préparation, dépôt, rejets, paiements, impayés après 45 jours, historique | Fait | Bordereaux Chifa |
| Ventes MDN et canal de facturation de la part organisme (jamais refacturée par le logiciel) | Fait | regles-metier.md « Ventes Chifa / MDN » |
| Centres payeurs, historique Chifa repris, conflits de n° de facture Chifa (script 101) | Fait | Administration › Centres payeurs, Historique Chifa, Conflits |
| Droits Chifa expirés ou bientôt expirés : avertissement à la caisse, liste des patients concernés | Fait | Patients › Droits Chifa |
| Import des factures signées, bordereaux et rejets depuis les fichiers de Chifa | Bloqué (format inconnu) | chifa.md |
| Envoi des produits vers CHIFA-Officine et import de son fichier | Bloqué (réponse de la CNAS) | a-faire.md « Plus tard » |
| Majorations Chifa autres que « Garde » | En attente (décision du titulaire) | reste-a-faire-synthese.md § 4 |
| Script 101b (suppression de l’ancienne colonne du n° de facture Chifa) | En attente (décision d’exécution) | mise-en-production.md, étape 8 |

## Ordonnances et OCR

| Fonction | État | Où / référence |
| --- | --- | --- |
| Ordonnance scannée (scanner WIA, caméra, fichier image ou PDF) : lecture par l’OCR de Windows, rapprochement avec le catalogue, nombre de boîtes lu, image liée à la vente ; équivalent proposé en rupture (même DCI, sinon autre dosage ou forme à valider) | Fait | Caisse |
| Lecture assistée des ordonnances manuscrites : image agrandie, pivotée et zoomée à côté d’une recherche tolérante aux fautes (l’écriture manuscrite n’est pas lue automatiquement, voir ci-dessous) | Fait | Caisse |

## Alertes

| Fonction | État | Où / référence |
| --- | --- | --- |
| Péremption à 3 niveaux de seuils, risque d’écoulement, valeur en dinars (réservée au droit des marges) | Fait | Alertes |
| Rupture avec alternatives de même DCI, stock critique, dormant, surstock | Fait | Alertes |
| Reporter (date au choix), marquer traitée (action prise, employé) ; liste paginée et filtrée par le serveur | Fait | Alertes |
| Alertes et seuils (général, par grossiste avec délai de reprise, exceptions par produit, stock et commandes), seuil par produit sinon automatique (ventes par jour × (délai de livraison + stock de sécurité) ; 0 = pas d’alerte), recalcul immédiat | Fait | Administration › Seuils |
| Rapport quotidien des alertes ouvertes (tâche du soir) | Fait | notifications.md |

## Pilotage et rapports

| Fonction | État | Où / référence |
| --- | --- | --- |
| Rapports jour, semaine, mois ou dates choisies : chiffre d’affaires, part Chifa à recevoir, crédits, remises, annulations ; marge brute au prix réel des boîtes (droit « rapports.marges ») ; ventes par jour ; produits les plus vendus ; clôtures ; sorties de stock sans vente ; marge après pertes et dons avec la TVA à reverser par motif ; export PDF et CSV | Fait | Pilotage › Rapports |
| Retours clients déduits du chiffre d’affaires des rapports (ou ligne « retours ») | En attente (à décider) | a-faire.md « Prochaines demandes » |
| Pilotage mobile réservé au titulaire : chiffre du jour et comparaison avec la veille, caisses ouvertes, clôtures et écarts, alertes, gros écarts d’inventaire validés depuis le téléphone, pertes et dons, trésorerie, factures à encaisser | Fait | Scanner mobile › Pilotage |
| Accès au pilotage hors de la pharmacie par VPN | En attente (VPN à choisir avec le prestataire) | acces-distant.md |
| Tableau de bord financier : panier moyen, marge, créances organismes, valeur et rotation du stock, stock dormant, marge par produit ou laboratoire, export PDF et Excel | Fait | Pilotage financier |
| Tableau de bord d’accueil (gestion) | Fait | Accueil |

## Factures, trésorerie et financements

| Fonction | État | Où / référence |
| --- | --- | --- |
| Droit « factures.gerer » : factures aux patients, aux entreprises (RC, NIF, NIS, AI) et aux organismes ; ventes choisies une à une, par ordonnance ou par période, lignes libres ; numéros annuels sans trou (F-2026-0001, AV-2026-0001) ; TVA, total en lettres, PDF A4 ; rejet puis correction (versions) ou remplacement ; avoir ; encaissement ; retrait du suivi avec motif ; journal | Fait | Finances › Factures |
| Trésorerie (droit « tresorerie.gerer ») : coffre et banques, dépenses par catégorie, encaissements divers, versements CNAS / CASNOS, transferts, caisses à verser au coffre, chèques à remettre, mouvements calculés sans saisie, mouvements par compte et période, journal | Fait | Finances › Trésorerie |
| Mouvements d’un compte paginés et filtrés par le serveur (période, employé), totaux de tout le filtre | Fait | Finances › Trésorerie |
| Dettes et créances : échéancier, alertes à partir de 30 jours (réglable), créances Chifa / MDN | Fait | Finances › Dettes et créances |
| Financements (titulaire) : prêts bancaires et autres prêteurs, échéancier importé du tableau de la banque, paiement des échéances, apports du titulaire | Fait (tableaux réels des banques à essayer) | Finances › Financements |
| Remboursement anticipé, renégociation, échéance payée en partie | En attente (à préciser) | a-faire.md « À confirmer avec les banques » |
| TVA des pertes et dons, du don partiel, des intérêts et de la marge des prêts | En attente (réponse du comptable) | a-faire.md « À confirmer avec le comptable » |

## Fiscal et intégrité

| Fonction | État | Où / référence |
| --- | --- | --- |
| Journal fiscal des ventes chaîné (ventes, annulations, retours, avoirs, régularisations), tickets « T- » numérotés sans trou, contrôle de continuité | Fait | Contrôle interne › Journal fiscal |
| Scellement à chaque clôture (sceau imprimé sur le rapport) et clôture fiscale mensuelle archivée | Fait | Contrôle interne › Journal fiscal |
| Récapitulatif TVA par période, export d’audit CSV ou PDF | Fait (format DGI à vérifier) | Contrôle interne › Journal fiscal |
| Format de l’export pour les impôts et taux de TVA | En attente (décision du titulaire) | reste-a-faire-synthese.md § 4 |
| Intégrité des journaux : chaînage SHA-256 en base, sceaux HMAC, vérification chaque nuit, rupture signalée au titulaire, « Vérifier maintenant » | Fait | Contrôle interne › Intégrité des journaux |
| Réglages fiscaux (conservation 6 ans au moins) | Fait | Administration › Réglage fiscal |
| Journal des actions filtré par catégorie (modifications, autorisations par code et blocages), modifications affichées « ancienne → nouvelle valeur » ; journal non vidable | Fait | Administration › Journal |
| Séparation des rôles PostgreSQL (script 111b) : empêcher, et pas seulement détecter, une falsification | En attente (à passer par le titulaire avec un superutilisateur) | securite-et-droits.md |

## Notifications

| Fonction | État | Où / référence |
| --- | --- | --- |
| Notifications au titulaire (réservées au titulaire) : rapport quotidien par e-mail, SMS d’urgence par modem GSM (écart de caisse, lot bloqué, sauvegarde), destinataires vérifiés par un code reçu, e-mails signés du nom de la pharmacie, file d’attente avec nouvelles tentatives, mot de passe SMTP chiffré (DPAPI) | Fait (à essayer avec un vrai modem et un vrai compte SMTP) | Administration › Notifications |
| File d’attente paginée et filtrée (objet, statut, période) ; mot de passe du compte d’envoi jamais réaffiché | Fait | Administration › Notifications |
| Alerte qui n’a pas pu partir mémorisée et signalée par un bandeau de l’Accueil | Fait | Accueil |

## Sauvegardes et GED

| Fonction | État | Où / référence |
| --- | --- | --- |
| Sauvegardes : pg_dump quotidien vers un disque externe, conservation des N dernières (1 à 365), journal, alerte du titulaire en cas d’échec ou de disque absent | Fait (à essayer sur le vrai disque) | Administration › Sauvegardes |
| Alerte au titulaire (SMS et e-mail) si aucune sauvegarde réussie sur le disque externe depuis N jours (7 par défaut, 1 à 90), une alerte par période, même sauvegarde automatique désactivée (script 148) | Fait | Administration › Sauvegardes |
| Rotation de deux disques externes, dont un toujours hors de l’officine, obligatoire sans 2e serveur | Fait (règle d’installation) | mise-en-production.md, étape 11.2 bis |
| Script planifié `sauvegarde_pharmacie.ps1` qui écrit lui aussi au journal des sauvegardes (réussie, disque absent, échouée) | Fait | scripts/sauvegarde_pharmacie.ps1 |
| Choix d’un seul mécanisme de sauvegarde (application ou script planifié) et ses dossiers | En attente (décision du titulaire) | a-faire.md « Mise en production : à décider » |
| Copie de secours de la sauvegarde vers un second emplacement (PC de secours) | Fait (à essayer en réel) | Administration › Sauvegardes |
| Test de restauration de la dernière copie dans une base temporaire | Fait | Administration › Sauvegardes |
| Restauration de la base et de la GED par le super admin (ancienne base gardée) | Fait | Administration › Restauration |
| GED : ordonnances, factures, rapports, clôtures… jamais écrasés, copiés sur le disque externe avec la base | Fait | regles-metier.md « GED » |
| Copie automatique de la GED vers le 2e serveur, fréquence réglable (1 à 60 minutes) | Fait (à essayer entre deux vrais serveurs) | Administration › 2e serveur ; ged-protection-a-decider.md |
| Sauvegarde chiffrée de la GED hors de l’officine | En attente (après le lot 9 et l’hébergeur) | ged-protection-a-decider.md |
| Obligations de conservation des ordonnances et avis ANPDP pour une copie hors de l’officine | En attente (à vérifier) | ged-protection-a-decider.md § 5 |

## 2e serveur et PC de secours

| Fonction | État | Où / référence |
| --- | --- | --- |
| Réplication en continu de la base vers le 2e serveur (lecture seule), jamais deux API qui écrivent | Fait (à essayer sur les vrais serveurs) | regles-metier.md « 2e serveur » |
| Bascule manuelle (script), remise en second, vérification | Fait (à essayer en réel) | scripts/second-serveur |
| Alerte de réplication (décrochage ou retard) | Fait | Administration › 2e serveur |
| Écran « 2e serveur » : réplication, versions, clé de scellement, dossiers Chifa / MDN, imprimantes | Fait | Administration › 2e serveur |
| Export et import de la clé de scellement | Fait | Administration › 2e serveur |
| Port 5432 ouvert au seul 2e serveur (script) | Fait (à essayer sur le vrai PC serveur) | scripts/second-serveur/ouvrir-port-base.ps1 |
| PC de secours : bandeau « serveur injoignable », bascule par script | Fait (à essayer en réel) | scripts/basculer-pc-secours.ps1 |
| Clé `Jwt__Cle` identique sur le PC de secours et le 2e serveur, ou reconnexion après bascule | En attente (à décider) | a-faire.md « Mise en production : à décider » |

## Sécurité et droits

| Fonction | État | Où / référence |
| --- | --- | --- |
| Utilisateurs (création, rôle, désactivation, mot de passe provisoire) et matrice des droits par rôle, remise maximale, droit de don par personne, dernière connexion | Fait | Administration › Droits |
| Connexion, verrouillage, révocation des connexions, sessions par poste, code comptoir, autorisation sur place par code | Fait | securite-et-droits.md |
| Compte super admin hors de la base (fichier chiffré du PC serveur, au moins un canal vérifié SMS ou e-mail), réservé à la reprise et à la restauration : chaque action sensible validée par mot de passe ressaisi et code à 6 chiffres, 3 codes faux = 15 minutes de blocage | Fait | Administration › Reprise, Restauration |
| HTTPS sur le réseau de l’officine (HTTP limité au PC serveur), autorité unique par pharmacie | Fait (à essayer en réel) | scripts/certificat_https.ps1 ; mise-en-production.md |
| Journaux du serveur et du poste sans données personnelles | Fait | securite-et-droits.md |
| Port 5432 de la base fermé aux autres postes (écoute sur 127.0.0.1 seulement, contrôle à l'installation) | Fait (procédure écrite, à dérouler sur le vrai PC serveur) | mise-en-production.md, étapes 2.4 et 4 bis |

## Administration et réglages

| Fonction | État | Où / référence |
| --- | --- | --- |
| Identité de la pharmacie (en-tête des postes, rapport de clôture), caisse et journal des actions sensibles | Fait | Administration › Pharmacie, Caisse |
| Imprimantes et étiquettes (par poste) : imprimante de tickets parmi celles de Windows, rouleau 80 ou 58 mm, ticket automatique, adresse IP de l’imprimante d’étiquettes, impressions d’essai | Fait | Administration › Imprimantes |
| Motifs et catégories réglables (tiroir, attente, charges, sorties, retours, Chifa) | Fait | Administration › Motifs |
| Reprise de données (super admin) : assistant ODBC, correspondance proposée puis confirmée, données des patients masquées, rapprochement des produits, contrôles, chargement en une transaction, vérification des totaux ; profils de reprise par logiciel précédent ; doublons d’import | Fait (à essayer sur une vraie base de l’ancien logiciel) | Administration › Reprise, Profils, Doublons |
| Langue arabe (droite à gauche) et anglaise | En attente (traductions ; seul le français est disponible) | feuille-de-route.md |
| Remarques d’essai du pharmacien (problème, suggestion, question) | Fait | Guide d’essai |
| Affichage clair, sombre ou automatique, choisi par chaque employé ; impressions toujours claires | Fait | Bandeau du poste |
| Navigation : menu en deux niveaux (domaines en haut, écrans à gauche), tableau de bord ou Caisse à l’arrivée, recherche Ctrl + K, raccourcis Ctrl + Maj + lettre ; écrans sans droit masqués ; ⚙ Administration, menu à son nom, « ⇄ Changer » | Fait | navigation.md |
| Retour à l’écran précédent partout (pastille, Alt + ←, bouton de la souris) avec onglet, filtres, page et élément ouvert retrouvés | Fait | navigation.md |
| Écrans sans défilement (seul le tableau défile), listes paginées et filtrées par le serveur, accord des pluriels | Fait | regles-metier.md |
| Indicateur de chargement, tableaux en relief, liens et boutons mis en évidence au survol | Fait | Partout |
| Mise en évidence des boutons au survol en thème clair | En attente (à préciser par le titulaire) | a-faire.md « Points ouverts » |
| Messagerie interne | En attente (V2) | a-faire.md « Questions pour la V2 » |

## Scanner mobile

| Fonction | État | Où / référence |
| --- | --- | --- |
| Page web du serveur (Wi-Fi local, https) : contrôle en rayon (stock, emplacement, lots, péremption, lots bloqués), confirmation des dates, rangement, inventaire tournant, scans de réception importés sur le poste ; caméra (Android, ZXing), douchette ou laser | Fait | scanner-mobile.md |
| Caméra sur iPhone et Firefox Android ; réglage GS1 des douchettes ; petits Data Matrix | Fait (à essayer sur le vrai matériel) | a-faire.md « Scanner mobile : suites » |

## Synchronisation et serveur central

| Fonction | État | Où / référence |
| --- | --- | --- |
| S1 : file de sortie locale numérotée, désactivée par défaut | Fait | conception-synchronisation.md § 11 |
| S2 : serveur central (réception, idempotence, accusés), base commune cloisonnée par officine, certificat client, patients sous pseudonyme | Fait | conception-synchronisation.md § 12 |
| S6 : chargement complet (première activation, réactivation), lignes absentes signalées | Fait | conception-synchronisation.md § 13 |
| Réglages du serveur central et voie descendante (réglages descendants) | Fait | conception-synchronisation.md § 14, § 15 |
| S3 : envoi environ chaque minute, supervision, purge de la file, bandeau des postes, alertes au titulaire | Fait | Administration › Synchronisation ; § 20 |
| Valeurs de départ de la supervision (bandeau, retard, erreur, conservation) | En attente (à confirmer par le titulaire) | a-faire.md « Plus tard » |
| S4 : vérification d’intégrité au serveur central, alerte au titulaire | Fait | Synchronisation › Intégrité au central ; § 22 |
| S6.5 : intégrité après restauration (déclaration de la nouvelle époque, restaurations constatées au central, prise d’acte du titulaire, tickets avancés au-delà du central) | Fait (lot terminé le 2026-11-19, essai de bout en bout réussi ; synthèse § 31.9) ; en attente : avis du comptable sur les n° sautés | Synchronisation › Restaurations constatées ; § 31 |
| N° de tickets après une restauration : caisse en attente du contrôle du serveur central au démarrage (120 s au plus, réglable), n° réattribués signalés au central et listés avec la restauration | Fait (2026-11-19, scripts 152 et central 025) | Caisse (bandeau) ; Synchronisation › Réglages et › Restaurations constatées ; § 31.10 |
| S5.1 et S5.5 : ordres du central, employés et droits | Fait | Ordres du central ; § 24 |
| S5.2 : prix d’un produit envoyé par le central | Fait | § 25 |
| S5.3 : nomenclature nationale envoyée par le central | Fait | § 26 |
| S5.4 : commandes en brouillon envoyées par le central | Fait (plafond de 200 lignes à confirmer) | § 27 |
| Référentiels envoyés par le central : antibiotiques suivis, organismes, centres payeurs, prestations, indications, DCI | Fait | § 28 |
| Reclassement de deux tables liées aux produits (montantes) | Fait | § 29 |
| S6, fin : restauration ou bascule de la base d’une officine (retour en arrière détecté, envoi suspendu, avance des n°, alerte, « Repartir sur une nouvelle époque » par le titulaire, puis chargement complet) | Fait (essai de bout en bout avec le serveur central à faire, lot S6.4) | Synchronisation › État ; conception-synchronisation.md § 30 |
| S6, fin : départ d’une officine (console du central : révocation des certificats, export zip avec empreintes, conservation après départ réglable, 6 ans par défaut, effacement en simulation puis sur confirmation après le délai, procès-verbal) ; archive au central des lignes remplacées par une époque plus récente | Fait (durée à confirmer par le juriste) | conception-synchronisation.md § 30.5 ; mise-en-production.md F8 |
| S6.5 : intégrité après restauration (époque déclarée au central, version divergente gardée à part, vérification par branches, classement « remplacée après restauration » sous conditions, alerte et export) | En attente (décisions D1 à D9 validées, code sur « go S6.5 ») | conception-synchronisation.md § 31 |
| S7 : écrans du central, accès à distance du titulaire, multi-officines | En attente (après l’hébergeur) | conception-synchronisation.md § 10 |
| Découpage par officine des grandes tables du central | En attente | reste-a-faire-synthese.md § 7 |
| Compatibilité des versions du format d’échange | En attente (à l’arrivée d’une version 2) | a-faire.md « Plus tard » |
| Effacement automatique au central | En attente (durées réglementaires à vérifier) | a-faire.md « Plus tard » |
| Serveur central réel (hébergeur agréé ARPCE, nom de domaine, tâches planifiées) | Bloqué (démarches du titulaire) | reste-a-faire-synthese.md § 5 |
| Essai de bout en bout par l’échange HTTPS réel | Bloqué (serveur central réel) | a-faire.md « Plus tard » |

## Mutualisation des réglages

| Fonction | État | Où / référence |
| --- | --- | --- |
| M1 à M3 : conception, tables et garde côté officine (script 141) et central (script 010) | Fait | conception-synchronisation.md § 16 bis |
| M4 : application des modèles, « Adopter », garde dans les 11 cas d’usage, journal avant / après | Fait | § 16 bis.12 |
| M5 et M7 : routes de l’API locale et bandeau « Fixé / Proposé par le serveur central » dans les 9 écrans, Synchronisation › Réglages mutualisés | Fait | Administration › Synchronisation |
| M6 : outil d’administration du central (publier, imposer ou proposer, libérer, rétablir, journal) | Fait | mise-en-production.md, parcours E |
| M8 et M9 : 13 scénarios réussis, première réception d’un modèle proposé (script 142), documentation finale | Fait | § 16 bis.13 |
| Mode « local » par table au central | En attente (à décider) | a-faire.md « Plus tard » |

## Chiffrement (lot 9)

Uniquement sur « go chiffrement » du titulaire. Conception et décisions DC1 à DC7 acceptées le 2026-11-17.

| Fonction | État | Où / référence |
| --- | --- | --- |
| C1 : clés de recouvrement de l’éditeur, outil hors ligne, script central | En attente (« go chiffrement ») | chiffrement-data.md § 11 |
| C2 : clé de l’officine, code de recouvrement du titulaire, envoi des enveloppes | En attente (« go chiffrement ») | chiffrement-data.md § 11 |
| C3 : chiffrement des lots envoyés au central | En attente (« go chiffrement ») | chiffrement-data.md § 11 |
| C4 : ouverture à deux (titulaire et éditeur) | En attente (« go chiffrement ») | chiffrement-data.md § 11 |
| C5 : changement de code, rotation de la clé, effacement au départ | En attente (« go chiffrement ») | chiffrement-data.md § 11 |
| C6 : bascule vers le chiffrement hybride (résistance au quantique) | En attente (« go chiffrement ») | chiffrement-data.md § 11 |
| Réponse à une autorité sans le titulaire (décision DC8) | En attente (avis du juriste) | chiffrement-data.md § 7 bis |

## Mise en production et matériel

| Fonction | État | Où / référence |
| --- | --- | --- |
| Scripts de base : création d’une base neuve, mise à jour d’une officine en service, création de la base centrale | Fait | scripts/mep ; mise-en-production.md |
| Restauration par script et script planifié de sauvegarde | Fait | scripts/restaurer_pharmacie.ps1, sauvegarde_pharmacie.ps1 |
| Paquet d’essai | Fait | scripts/creer_paquet_essai.ps1 |
| Livraison officielle (paquet de production, dossiers de destination) | En attente | a-faire.md « Mise en production : à décider » |
| Installateur et mises à jour automatiques des postes | En attente | feuille-de-route.md ; mise-en-production.md |
| Démonstration (page, démo interactive ou démo cloud) | En attente (décision du titulaire) | reste-a-faire-synthese.md § 4 |
| Essais du matériel : modem GSM, compte SMTP, imprimantes, tiroir-caisse, disque de sauvegarde, iPhone | Bloqué (matériel de la pharmacie pilote) | a-faire.md « À vérifier sur le terrain » |
| CHIFA-Officine, lecteur de carte et Token : procédure d’installation | En attente | a-faire.md « Mise en production : à décider » |

## Ordonnances manuscrites

Un essai de lecture automatique de l’écriture manuscrite a été fait hors ligne avec le modèle libre TrOCR (licence MIT, ONNX Runtime) sur une vraie ordonnance : aucune lecture exploitable (par exemple « Sentreaux 4-my up » pour « Sintrom 4 mg cp »), environ 600 Mo de modèle et 1 Go de mémoire. Il n’est donc pas intégré ; la lecture assistée le remplace.

La liste des prochaines étapes est dans [feuille-de-route.md](feuille-de-route.md).
