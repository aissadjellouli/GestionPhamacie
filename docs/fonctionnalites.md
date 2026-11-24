# Fonctionnalités et état d’avancement

Inventaire complet des fonctionnalités du logiciel, mis à jour à chaque modification.

Mis à jour le 2026-11-24.

**Mise à jour du 2026-11-23** (revue complète, non commitée) : écran de connexion avec les deux onglets toujours proposés
(dernier choix retenu sur le poste) ; ressaisie du mot de passe pour l'export de la clé de scellement, les notifications,
les anomalies traitées et la matrice des droits ; déconnexion après 15 minutes par défaut pour une installation neuve ;
don d'un lot bloqué ou périmé refusé ; balance mensuelle du registre avec le solde reporté du mois précédent ; facture
corrigée gardée dans l'année de son numéro ; dossier de sauvegarde limité aux disques du poste ; GED contrôlée à l'entrée.
Bascule du 2e serveur, vague 2 (non commitée) : 2e serveur en veille, échanges signés entre serveurs, décision de promotion à la majorité des postes, présence des postes, isolement du serveur principal, bascule des postes sans redémarrage, réplication synchrone ; promotion automatique et relais à venir. Bascule du 2e serveur (vague 1, non commitée) : serveur écarté en lecture seule, alertes, saut des compteurs à la promotion, droit de lecture de l'identifiant de la base, inactive avec un seul serveur ; postes et promotion automatique à venir. Ajouts de la nuit du 2026-11-23 : tests d'intégration SQL sur PostgreSQL (49 tests) ; installation neuve corrigée (données techniques de l'init) ; 2e serveur : détection d'un serveur écarté (lot 1 de la bascule allégée, lecture seule et écrans à venir). Ce sont des corrections et des règles de fonctions déjà comptées : le résumé ci-dessous ne change pas. Détail :
[regles-metier.md](regles-metier.md), section « Corrections de la revue complète (2026-11-23) ».


Légende des états :
- **Fait** : codé, disponible à l’écran ou par l’API. « Fait (à essayer…) » : codé, jamais essayé sur le vrai matériel ou en réel.
- **En cours** : en train d’être codé.
- **En attente** : prévu, décidé ou à décider, pas encore codé.
- **Bloqué** : attend un matériel, un format ou une décision externe.
- **À vérifier** : état non établi par les documents ; à confirmer.
- **Non proposé** : examiné et écarté (raison donnée dans la ligne).

## Résumé

| État | Nombre de fonctions |
| --- | --- |
| Fait | 214 |
| En cours | 1 |
| En attente | 47 |
| Bloqué | 9 |
| Non proposé | 1 |
| À vérifier | 0 |
| **Total** | **272** |

Recompté le 2026-11-24 à partir des lignes ci-dessous (272 lignes ; « Fait en local » compté dans « Fait ») ; « En cours » : démonstration cloud (Docker absent du poste).

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
| Vente en attente au changement d’utilisateur avec serveur injoignable : plus de panier perdu sans signalement ; mise en attente attendue avant de fermer la session, sinon fenêtre bloquante « Réessayer » / « Rester sur la vente », panier resté affiché (aucune copie locale du panier sur le poste) | Fait (2026-11-21 ; essai serveur arrêté, fenêtre mesurée à 0 px, clair et sombre) | Caisse ; regles-metier.md « Aucun panier perdu en silence » |
| Retour de médicaments par le patient, partiel, sans limite de durée : le pharmacien choisit produit par produit (remis en stock sur son lot, ou à détruire ; refuser = ne rien enregistrer) ; boîtes identifiées cochées par n° de série ; droit « caisse.retour_especes » ou validation sur place par code ; part payée par le patient rendue au prorata depuis la caisse ouverte du poste (attendu diminué), ou retirée de la dette pour une vente à crédit ; registre des psychotropes (retour puis destruction) ; vente en tiers payant : voir la ligne suivante ; motif choisi dans une liste réglable | Fait | Caisse |
| Remboursement sans ticket : vente retrouvée par le scan de la boîte rapportée | Fait | Caisse ; regles-metier.md |
| Retour partiel d’une vente en tiers payant (option A du 2026-11-21, script 155) : avant tout dépôt, part patient rendue au prorata et part de l’organisme des boîtes rendues retirée de sa créance (prorata de la part retenue, Chifa si régularisée ; majoration retirée quand plus aucune boîte remboursée ne reste) ; après dépôt, refus (« Vente déposée à la CNAS… ») sauf lignes non remboursées ; taux 100 % sans argent rendu, validation `caisse.retour_especes` ; journal fiscal au TTC complet ; rappel d’avoir AV- manuel pour une facture d’organisme du logiciel ; ticket réimprimé (imprimé et PDF) : « Part organisme retirée : X DA » sur la ligne du retour, plus « facture à corriger dans CHIFA-Officine » tant que le retour Chifa n’est pas marqué corrigé ; rapprochement d’un volet après un retour ajusté (script 157 : comparaison nette des retours, aucune double déduction, deux ordres) | Fait (2026-11-21 ; script 155 passé sur dev et test ; panneau de retour mesuré à 0 px) | Caisse ; regles-metier.md « Retour de médicaments par le patient » |
| Retour et dettes du patient (décision du titulaire du 2026-11-21, script 158) : la part de la régularisation Chifa « à la charge du patient » des boîtes rendues lui est rendue (impayée : retirée de sa dette) ; patient qui doit encore de l’argent et qui a payé sa part : alerte bloquante dans le panneau de retour (nom cliquable, total de ses dettes), « Déduire de ses dettes » (recommandé, choisi par défaut, accord du client, imputé comme un encaissement de dette, sans tiroir) ou « Rendre quand même » (validation `caisse.retour_especes`) ; achat à crédit : boîte reprise sans rien rendre, dette diminuée | Fait (2026-11-21 ; script 158 passé sur dev et test ; panneau de retour mesuré à 0 px) | Caisse ; regles-metier.md « Retour et dettes du patient » |
| Retour d’une vente régularisée « rendu au patient » (décision du titulaire du 2026-11-21, script 159) : la part de l’écart déjà rendue au patient pour les boîtes rendues (même prorata que la règle 158, solde exact aux dernières boîtes) est retenue sur ce que le retour lui rend (argent payé, puis avoir), avant la règle des dettes ; reste non couvert : ajouté à sa dette (relevé, journal) après l’alerte « Patient prévenu » dans le panneau de retour ; vente sans patient : retour refusé si un reste n’est pas couvert | Fait (2026-11-21 ; script 159 passé sur dev et test ; panneau de retour mesuré à 0 px) | Caisse ; regles-metier.md « Retour d’une vente régularisée « rendu au patient » » |
| Liste « Retours à corriger dans CHIFA-Officine » (Finances › Chifa) : retours avec part organisme des ventes Chifa, recherche et état (à corriger / corrigés / tous), paginée ; « Corrigé dans CHIFA-Officine » daté, signé, journalisé | Fait (2026-11-21 ; mesuré à 0 px) | Finances › Chifa ; regles-metier.md |
| Mention « part organisme retirée » sur le ticket d’un retour en tiers payant | En attente (reportée : ticket modifié par un autre lot) | regles-metier.md « Retour de médicaments par le patient » |
| Procédure de la CNAS pour une vente déposée (correction d’une facture signée, indu, retour d’un médicament remboursé, avoir sur bordereau) | Bloqué (réponse de la CNAS attendue) | chifa.md « Retour d’un médicament remboursé » |
| Ticket imprimé par Windows après la vente (imprimante du poste ou par défaut, rouleau 80 ou 58 mm) : pharmacie, ticket, vendeur, lignes, remise, part organisme, à payer, mode, rendu ; aucune donnée du patient ; ticket en PDF si l’imprimante enregistre un fichier | Fait (à essayer sur la vraie imprimante de tickets) | Caisse |
| Impression directe ESC/POS (coupe, logo), tiroir-caisse | Bloqué (modèle d’imprimante et matériel à définir) | feuille-de-route.md |
| Afficheur client réglé par poste : écran secondaire (fenêtre plein écran, toujours visible, sans prendre le focus : panier, total, payé, rendu, accueil) et/ou afficheur 2 lignes × 20 sur port série (ESC/POS Epson ou CD5220, vitesse réglable) ; essai depuis Administration › Imprimantes et étiquettes ; une panne d’afficheur ne bloque jamais la caisse | Fait (à essayer sur le vrai afficheur) | Caisse ; Administration › Imprimantes |
| Suggestions pendant la frappe (décision du titulaire, 2026-11-18), lot 0 (socle) et lot 1 (caisse) : composant commun `ChampSuggestions` (choix obligatoire ou valeur libre ; 10 propositions, sans accents ni majuscules, ↑ ↓ Entrée Échap), `ChoixPatient`, `ChoixMedecin`, `ValeursSaisies` ; valeurs déjà saisies par liste blanche du serveur (`GET /api/suggestions/{champ}`) ; recherche patient et médecin sans accents (index du script 150), ouverte aux droits Factures et registre sans dette ni plafond ; caisse : patient F6, médecin F5, flèches dans le panier, spécialité du nouveau médecin, tiroir (patient remboursé, libellé, bénéficiaire), annulation (client, dette), relevé de dette | Fait | Caisse ; regles-metier.md « Suggestions pendant la frappe » |
| Suggestions pendant la frappe (décision du titulaire, 2026-11-18), lots 2 à 5 : catalogue et médecins (DCI, forme, indications, nomenclature, spécialité, prescripteur du registre, DCI suivies), argent (banques, libellés de versement d’organisme mis par le logiciel, libellés de financements, prêteurs ; plus aucun libellé de trésorerie tapé à la main proposé, 2026-11-20), achats, stock et factures (patients en attente, rappel de lot, rayon, exceptions de seuils, produits sensibles, retours, patient et entreprise d’une facture), filtres produit des tableaux (`FiltreProduit`) | Fait (rayons proposés dès l’entrée dans le champ, tous, 30 au plus depuis le 2026-11-20 ; lots épuisés proposés au rappel ; écran Seuils par produit sans défilement, 8 lignes visibles à 1 366 × 768 et 10 à 1 536 × 890 depuis le 2026-11-20) | charte-graphique.md « Règle des listes » ; regles-metier.md (suggestions) |

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
| Facture et retour fournisseur laissés au changement d’utilisateur : gardés dans un fichier chiffré du poste (`%LOCALAPPDATA%\PharmaOfficine\taches-laissees.dat`, DPAPI utilisateur), relus à la première connexion qui suit le redémarrage, 7 jours au plus | Fait (2026-11-21 ; à essayer sur le poste : fermer et relancer le logiciel) | Bandeau du poste |
| Réglage de l’officine « Facture et retour fournisseur laissés » (titulaire seul, journal avant / après) : « Chiffré (AES-256, compte Windows) » (défaut) ou « Désactivé (gardé en mémoire seulement, perdu au redémarrage) », fichier du poste supprimé à sa prochaine connexion ; lu par chaque poste à la connexion (script 156) | Fait (2026-11-21 ; script 156 passé ; écran mesuré à 0 px) | Administration › Caisse et contrôle › Règles |

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
| Parapharmacie distinguée dans le catalogue (préalable à la fidélisation de la V2) : catégorie « Parapharmacie (à préciser) », règle stricte et rattrapage (script 154), reprise, « Produits à classer » avec reclassement groupé, catégorie dans « Modifier un produit », DCI/dosage/forme facultatifs, TVA 19 % proposée, pastille Stock, rapports parapharmacie / prestations | Fait le 2026-11-20 (script 154 passé sur dev et test ; écrans mesurés à 0 px, Modifier un produit fiche ouverte toléré) | regles-metier.md « Catégories » |

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
| Alerte « patient encore sous traitement » (tout psychotrope, valideur exigé), durée du traitement obligatoire à la caisse, ordonnance réutilisée « sans limite », nouvelles valeurs de départ (script 153, décision du titulaire du 2026-11-19) | Fait | Caisse ; Administration › Psychotropes ; regles-metier.md « Alertes à la délivrance » |
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
| Prévenir un patient quand un produit redevient disponible (SMS avec accord enregistré, formulaire papier d’accord) | Fait (à essayer avec le vrai modem) | Patients › Patients en attente (déplacé d’Achats le 2026-11-18) ; aussi en fenêtre depuis la Caisse et par le lien « Patients en attente » de la colonne de la Caisse ; accessible avec le seul `caisse.vendre` (produit choisi par la recherche du comptoir, 2026-11-20) |
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
| Financements (titulaire) : prêts bancaires et autres prêteurs, échéancier importé du tableau de la banque, paiement des échéances, apports du titulaire | Fait (tableaux réels des banques à essayer ; fiche d’un prêt ouvert revue le 2026-11-21 pour tenir sans défilement à 1 366 × 768 : tuiles compactes sur une rangée, formulaires en cadre défilant, mesurée à 0 px le 2026-11-21) | Finances › Financements ; charte-graphique.md |
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
| Barre d’état : élément « 2e serveur : … » seulement en anomalie (décroché, en retard), rien sans 2e serveur ; tout employé connecté, lien pour le titulaire | Fait (2026-11-21, à voir avec un vrai 2e serveur) | regles-metier.md « Barre d’état : liaisons » |
| Barre d’état : éléments fixes (Connecté / Serveur injoignable, poste · vendeur, date et heure, version en infobulle) et pastilles seulement s’il y a une information (caisse ouverte, ventes en attente, produits revenus, volets Chifa, Chifa à déposer, traites à payer, déconnexion dans N min, sauvegarde en retard ou en échec, central, 2e serveur, journaux en anomalie, imprimante de tickets en échec) ; regroupement « À traiter (N) » dès qu’une anomalie s’affiche ; une lecture groupée filtrée par les droits | Fait (2026-11-21, maquette validée ; mesurée dans l’application lancée, 0 px) | regles-metier.md « Barre d’état : éléments fixes et pastilles » |
| Barre d’état : pastille « Imprimante injoignable » en direct (imprimante éteinte alors que Windows accepte le ticket) | En attente (aucune source fiable : seul l’échec d’envoi d’un ticket est connu et signalé) | regles-metier.md « Barre d’état : éléments fixes et pastilles » |
| Barre d’état : pastille « Mise à jour disponible » | En attente (le logiciel ne sait pas qu’une nouvelle version existe) | regles-metier.md « Barre d’état : éléments fixes et pastilles » |
| Écran « 2e serveur » : réplication, versions, clé de scellement, dossiers Chifa / MDN, imprimantes | Fait | Administration › 2e serveur |
| Export et import de la clé de scellement | Fait | Administration › 2e serveur |
| Port 5432 ouvert au seul 2e serveur (script) | Fait (à essayer sur le vrai PC serveur) | scripts/second-serveur/ouvrir-port-base.ps1 |
| PC de secours : bandeau « serveur injoignable », bascule par script | Fait (à essayer en réel) | scripts/basculer-pc-secours.ps1 |
| Même clé des jetons (`Jwt:Cle`) sur les deux serveurs : contrôle bloquant à l'installation du 2e serveur (empreinte), alerte si elle diffère en marche | Fait (2026-11-24, à essayer en réel) | regles-metier.md « Bascule du 2e serveur, vague 2 », scripts/second-serveur |
| Détection d'un serveur écarté en marche (toutes les 30 s, deux relevés concordants) ; inactive avec un seul serveur | Fait (2026-11-23) | regles-metier.md « Tests d'intégration SQL et bascule du 2e serveur » |
| Serveur écarté en lecture seule (503 `serveur_ecarte`, base en lecture seule, tâches suspendues, démarrage en lecture seule, levée `--lever-ecart`) | Fait (2026-11-23, à essayer en réel) | regles-metier.md « Bascule du 2e serveur, vague 1 » |
| Alertes de bascule au titulaire (serveur remplacé, ancien principal actif, même timeline, autre lignée, isolement) | Fait (2026-11-23) | regles-metier.md « Bascule du 2e serveur, vague 1 » |
| Saut des compteurs (tickets, factures, avoirs, bons de commande) à la promotion du 2e serveur | Fait (2026-11-23, script 164) | regles-metier.md « Bascule du 2e serveur, vague 1 » |
| Droit de lire l'identifiant de la base pour le compte de l'API (script administrateur 163b) | Fait (à lancer par le titulaire avec le compte postgres) | scripts-base-de-donnees.md § 3.9 |
| 2e serveur en veille (API démarrée sur la copie, rôle publié, relance en principal après promotion) | Fait (2026-11-24, à essayer en réel) | regles-metier.md « Bascule du 2e serveur, vague 2 » |
| Échanges signés entre les deux serveurs (HMAC, horodatage, usage unique, réponse signée) | Fait (2026-11-24) | regles-metier.md « Bascule du 2e serveur, vague 2 » |
| Décision de promotion à la majorité des postes (rapports toutes les 15 s, postes connus figés à la dernière donnée reçue) | Fait (2026-11-24) | regles-metier.md « Bascule du 2e serveur, vague 2 » |
| Présence des postes et isolement du serveur principal (mise à l'écart à 20 s, levée automatique) | Fait (2026-11-24, script 165) | regles-metier.md « Bascule du 2e serveur, vague 2 » |
| Bascule des postes sans redémarrage ni déconnexion, panier en mémoire rejoué, poste isolé suspendu, case par poste | Fait (2026-11-24, à essayer en réel avec captures clair et sombre) | regles-metier.md « Bascule du 2e serveur, vague 2 » |
| Réplication synchrone choisie par transaction (ventes, factures, registre), au plus 2 s | Fait (2026-11-24, réglage `synchronous_standby_names` à poser par le titulaire) | regles-metier.md « Bascule du 2e serveur, vague 2 » |
| Mise hors jeu du serveur 1 et promotion automatique du 2e serveur (3 preuves, retrait rejouable, scripts 166 et 166b) | Fait (2026-11-24, à essayer en réel ; droit 166b à poser par le titulaire) | docs/taches/2026-11-23-bascule-2e-serveur/plan-vague-2.md |
| Scripts de mise en production tous dans `database/init`, livrés avec l'API (schéma, référentiel, fuseau, scripts d'administrateur) | Fait (2026-11-24) | scripts-base-de-donnees.md § 2 |
| Relais par le 2e serveur pour les postes qui ne joignent plus le serveur 1 | En attente (lot V2-5) | docs/taches/2026-11-23-bascule-2e-serveur/plan-vague-2.md |
| Essai réel de la bascule sur deux PostgreSQL (cas 1, 2, 3, isolement, un seul serveur) | En attente (lot V2-8) | docs/taches/2026-11-23-bascule-2e-serveur/plan-vague-2.md |

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
| Barre d’état : élément « Central : … » seulement en anomalie (activation, envoi suspendu, certificat, quota, erreur d’envoi, retard, attente), rien si la synchronisation n’est pas activée ; tout employé connecté, lien pour le droit admin.parametres | Fait (2026-11-21, à voir sur un poste relié au central) | regles-metier.md « Barre d’état : liaisons » |
| S4 : vérification d’intégrité au serveur central, alerte au titulaire | Fait | Synchronisation › Intégrité au central ; § 22 |
| S6.5 : intégrité après restauration (déclaration de la nouvelle époque, restaurations constatées au central, prise d’acte du titulaire, tickets avancés au-delà du central) | Fait (lot terminé le 2026-11-19, essai de bout en bout réussi ; synthèse § 31.9) ; en attente : avis du comptable sur les n° sautés | Synchronisation › Restaurations constatées ; § 31 |
| N° de tickets après une restauration : caisse en attente du contrôle du serveur central au démarrage (120 s au plus, réglable), n° réattribués signalés au central et listés avec la restauration | Fait (2026-11-19, scripts 152 et central 025) | Caisse (bandeau) ; Synchronisation › Réglages et › Restaurations constatées ; § 31.10 |
| S5.1 et S5.5 : ordres du central, employés et droits | Fait | Ordres du central ; § 24 |
| S5.2 : prix d’un produit envoyé par le central | Fait | § 25 |
| S5.3 : nomenclature nationale envoyée par le central | Fait | § 26 |
| S5.4 : commandes en brouillon envoyées par le central | Fait (plafond de 200 lignes à confirmer) | § 27 |
| Référentiels envoyés par le central : antibiotiques suivis, organismes, centres payeurs, prestations, indications, DCI | Fait | § 28 |
| Reclassement de deux tables liées aux produits (montantes) | Fait | § 29 |
| S6, fin : restauration ou bascule de la base d’une officine (retour en arrière détecté, envoi suspendu, avance des n°, alerte, « Repartir sur une nouvelle époque » par le titulaire, confirmée par le mot de passe du titulaire seul, sans code SMS ni e-mail, puis chargement complet) | Fait (essai de bout en bout avec le serveur central réussi le 2026-11-18, lot S6.4, § 30.7 ; repris avec S6.5, § 31.8) | Synchronisation › État ; conception-synchronisation.md § 30 |
| S6, fin : départ d’une officine (console du central : révocation des certificats, export zip avec empreintes, conservation après départ réglable, 6 ans par défaut, effacement en simulation puis sur confirmation après le délai, procès-verbal) ; archive au central des lignes remplacées par une époque plus récente | Fait (durée à confirmer par le juriste) | conception-synchronisation.md § 30.5 ; mise-en-production.md F8 |
| Départ d’une officine, décisions du 2026-11-20 : effacement aussi des éléments techniques (anomalies et états d’intégrité, ordres, éléments descendants, chargements, volumes, époques ; journal des ordres gardé) ; alertes recalculées non archivées (réglable par table) ; effacement d’une famille nominative aussi dans les lignes archivées ; rejeu des scripts centraux protégé | Fait (scripts centraux 026 à 028) | conception-synchronisation.md § 30.9 ; mise-en-production.md F8 |
| Export de départ en zip chiffré AES-256 (WinZip AE-2, ouverture avec 7-Zip) : mot de passe de 24 caractères tiré au sort, affiché une seule fois et écrit nulle part, à transmettre par un autre canal ; aucune copie en clair sur le disque ; empreinte SHA-256 de l’archive chiffrée au journal des départs et sur l’accusé de réception (2026-11-20) | Fait (ouverture avec 7-Zip à constater sur un poste réel) | regles-metier.md « Archive de départ en zip chiffré AES-256 » ; mise-en-production.md F8 |
| Mot de passe de l’archive de départ envoyé par e-mail au titulaire par le serveur central (option `--email`, compte SMTP de l’éditeur en variables d’environnement, TLS obligatoire ; adresse non enregistrée, masquée au journal ; échec non bloquant ; affichage unique gardé) (2026-11-20) | Fait (compte SMTP de l’éditeur à créer ; aucun envoi réel essayé) | regles-metier.md « Mot de passe de l’archive de départ envoyé par e-mail » ; mise-en-production.md 3.2 et F8 |
| Mot de passe de l’archive de départ envoyé par SMS | En attente (aucun fournisseur SMS choisi pour le serveur central) | reste-a-faire-synthese.md |
| Page d’état du central pour le développement (http://127.0.0.1:5092, lecture seule : officines, certificats, codes d’activation, chargement complet, derniers lots, époques, journal de sécurité ; aucune donnée de patient) et rôle de lecture technique `pharma_cloud_etat` (script central 029, sans connexion en production) | Fait (2026-11-21 ; script 029 passé sur `pharmacie_cloud_dev` ; page à constater dans Visual Studio) | poste-de-developpement.md ; regles-metier.md « Page d’état du central » |
| Remise de l’export de départ par lien de téléchargement à durée limitée (mot de passe par un autre canal : alors plus par e-mail si le lien part par e-mail) | En attente (dans aucun lot ; remise en main propre sur clé USB d’ici là) | mise-en-production.md F8 ; reste-a-faire-synthese.md |
| S6.5 : intégrité après restauration (époque déclarée au central, version divergente gardée à part, vérification par branches, classement « remplacée après restauration » sous conditions, alerte et export) | Fait (2026-11-19, voir la ligne S6.5 plus haut ; décisions D1 à D9 validées le 2026-11-18) | conception-synchronisation.md § 31 |
| S7.1 : écrans de l’éditeur au serveur central (https://127.0.0.1:5094 en développement) : connexion avec mot de passe BCrypt et code de second facteur, mot de passe provisoire à changer, verrouillage progressif, liste des officines filtrée et paginée (état, dernier contact, retard, dernier lot, chargement, échéance de certificat, anomalies), fiche d’une officine en rubriques (certificats, codes d’activation sans le code, chargements, lots sans contenu, époques, volumes, intégrité sans détail, restaurations, ordres sans données, journal de sécurité), mon journal des accès ; actions avec code de confirmation, motif et ressaisie : créer une officine (code d’activation affiché une fois, bouton Copier), nouveau code d’activation, révoquer un certificat ou un serveur ; comptes par la console du central ; jamais de donnée nominative pour l’éditeur | Fait en local (2026-11-21 ; 030 et 031 passés, 19 pages mesurées à 0 px ; rien en ligne) | ecrans-central.md ; conception-synchronisation.md § 32 |
| S7.2a : écrans du titulaire au serveur central (côté central seulement) : mes officines, rattacher une officine par code (30 minutes, usage unique, empreinte seule en base, 5 refus par quart d’heure), tableau de bord d’une officine en totaux seulement (chiffre d’affaires du jour, du mois et du mois précédent, ventes, part Chifa, crédits en cours, alertes de stock et de péremption, synchronisation, anomalies d’intégrité, ordres en attente ; « non disponible » si la donnée n’est pas montée), journal des accès de l’officine ; rôles étanches avec l’éditeur ; console `--creer-compte-titulaire`, `--code-rattachement` | Fait en local (2026-11-21 ; 032 passé, écrans mesurés à 0 px ; rien en ligne) | ecrans-central.md § 3 bis ; conception-synchronisation.md § 32.1 |
| S7.2b : rubrique « Accès à distance » du poste : le titulaire génère le code de rattachement de son officine (mot de passe ressaisi, synchronisation activée, code montré une fois, 30 minutes ; route centrale `POST /api/v1/rattachement` authentifiée par le certificat, réponse neutre, 5 codes par heure et par officine ; script central 033) | Fait en local (2026-11-21 ; 033 passé, rubrique mesurée à 0 px) | ecrans-central.md § 3 ter |
| S7.2b (reste) : voir et retirer depuis le poste les comptes distants rattachés | Fait en local avec S7.3 (2026-11-21) | ecrans-central.md § 3 quater |
| S7.3 : vue d’ensemble du titulaire (officines côte à côte, total du groupe « partiel » si une valeur manque) ; droit « ordres à distance » accordé et retiré depuis le poste (comptes rattachés, retrait immédiat, mot de passe ressaisi) ; ordres du titulaire en liste fermée (essai, sauvegarde confirmée sur le poste, vérification d’intégrité), code de confirmation, 24 heures, statut visible ; script central 034 | Fait en local (2026-11-21 ; 034 passé ; écrans mesurés à 0 px, ordres avec le droit accordé compris) | ecrans-central.md § 3 quater |
| S7.4 : réglages communs du titulaire : seuils d’alerte de stock et de péremption (liste fermée, bornes) proposés depuis le central à une ou plusieurs officines (droit « ordres » sur chacune, tout ou rien, code de confirmation) ; chaque titulaire accepte ou refuse sur son poste (avant / après, mot de passe ressaisi), rien sans acceptation ; suivi Proposé / Accepté / Refusé / Expiré au central | Fait en local (2026-11-21 ; 035 passé ; essai de bout en bout réussi) | ecrans-central.md § 3 quinquies |
| S7.4 (reste) : motifs, catégories et textes de ticket en réglages communs | Non proposé (texte de ticket inexistant ; motifs jugés trop risqués) | conception-synchronisation.md § 32.4 |
| S7.5 : lignes absentes vues au central (nombres seulement, jamais une ligne) : rubrique « Lignes absentes » de la fiche d’une officine pour l’éditeur (4 tuiles, tableau paginé par table et par chargement : n° du chargement, première constatation, cause probable « Restauration d’une sauvegarde » / « Nouvelle époque » / « Non expliquée », dérivée de ce que le central sait) ; tuile « Lignes absentes » du tableau de bord du titulaire et colonne « Absentes » de la vue d’ensemble (« non disponible » sans chargement complet) ; script central 036 | Fait en local (2026-11-21 ; 036 à passer sur le central de développement, écrans à mesurer) | ecrans-central.md § 3 sexies |
| Tuiles navigables du central : chaque tuile du tableau de bord du titulaire (14) et de la rubrique « Lignes absentes » de l’éditeur (4) mène à l’écran qui détaille son chiffre, ou à l’écran permis le plus proche (vue d’ensemble, mes officines, ordres avec le droit « ordres » ; chargements, restaurations, changements d’époque, tableau de la rubrique) ; infobulle qui dit où la tuile mène ; jamais un écran de l’autre rôle | Fait en local (2026-11-21 ; tests verts ; rendu à mesurer à 1 366 × 768 et 1 536 × 890, clair et sombre) | ecrans-central.md § 3 septies |
| Tuiles navigables du poste : toutes les tuiles (Alertes, Stock, Inventaire, Dettes, État de la caisse, Financements et fiche d’un prêt, Tableau de bord financier, Rapports, Reprise) mènent à l’écran qui détaille leur chiffre, ou à l’écran le plus proche, rubrique ou filtre en paramètre ; sur l’écran affiché, filtre appliqué ; infobulle qui dit où ; chiffre non cliquable seulement sans le droit d’ouvrir la cible (même contrôle que le menu) ; composant `TuileLien`, test `TuilesNavigablesTests` | Fait en local (2026-11-21 ; tests verts ; rendu à mesurer à 1 366 × 768 et 1 536 × 890, clair et sombre) | charte-graphique.md (« Tuiles navigables ») |
| Aide en contexte (« ? » des écrans, « Aide de la caisse ») : le panneau se ferme par Échap, par « Fermer » ou par un clic à l’extérieur (voile transparent) | Fait en local (2026-11-21 ; test vert ; à essayer sur le poste) | navigation.md |
| S7.6 : envoi des codes de second facteur réglé dans l’administration du site central (éditeur) : écran « Envoi des codes » (canal e-mail / SMS / les deux, serveur d’e-mail, mot de passe chiffré jamais réaffiché, vide = inchangé, code de confirmation, e-mail d’essai) ; écran « Comptes » (e-mail et téléphone d’un compte, au moins un, contact masqué) ; canal relu à chaque envoi, SMS indisponible signalé, les deux : l’e-mail part même sans SMS ; mode fichier gardé pour le développement ; départ d’une officine par le même compte si les variables SMTP manquent ; Gmail (mot de passe d’application) documenté ; script central 037 | Fait en local (2026-11-21 ; tests verts ; 037 passé sur le central de développement ; envoi réel par Gmail essayé avec succès) | ecrans-central.md § 3 octies ; poste-de-developpement.md |
| S7.6 (suite, 2026-11-21) : écran « Comptes » › « Ajouter un compte » (éditeur, code de confirmation, mot de passe provisoire montré une fois, script central 039) ; console `--envoi-codes` (compte e-mail des codes réglé sur le serveur, e-mail d'essai avant d'enregistrer) et `--contact-compte` (adresse d'un compte changée sur le serveur) | Fait en local (2026-11-21 ; tests verts ; 039 passé sur le central de développement) | ecrans-central.md § 3 octies ; poste-de-developpement.md |
| S7.6 (suite, script central 040) : import de comptes par fichier CSV à l’écran « Comptes » (éditeur : aperçu ligne par ligne sans code, un seul code pour tout le fichier, tout ou rien, 200 comptes) et en console (`--creer-comptes <fichier.csv> --auteur <nom> [--envoyer-identifiants]`) ; liste des comptes créés montrée une fois (mot de passe provisoire, identifiant envoyé par e-mail oui / non / échec, mot de passe par SMS ou à transmettre manuellement, « Copier ») ; case « Envoyer l’identifiant par e-mail » (création, import, remise à zéro ; jamais le mot de passe par e-mail) ; SMS du mot de passe prévu sans fournisseur | Fait en local (2026-11-21 ; tests verts ; script central 040 à passer ; écrans à mesurer) | ecrans-central.md § 3 decies |
| Durée de vie d’un mot de passe provisoire non utilisé, réglée par l’éditeur (« Envoi des codes », 1 à 30 jours, 7 par défaut) : connexion refusée une fois expiré ; état du mot de passe dans le tableau « Comptes » et lien « Régénérer le mot de passe » (motif prérempli, nouvelle durée complète) ; comptes provisoires créés avant le contrôle : durée complète à partir du déploiement (script central 041, revue du 2026-11-22) | Fait en local (2026-11-21 ; tests verts ; script central 040 à passer ; écrans à mesurer) | ecrans-central.md § 3 decies |
| Déconnexion des écrans du central après inactivité, réglée par l’éditeur par rôle (5 à 60 min ; éditeur 15, titulaire 30 par défaut) ; un utilisateur actif n’est jamais déconnecté (plus de durée maximale), pas même par une coupure réseau (revue du 2026-11-22) ; bandeau « Rester connecté » une minute avant, déconnexion d’elle-même avec « Session fermée après inactivité » | Fait en local (2026-11-21 ; tests verts ; script central 040 à passer ; bandeau et écran « Envoi des codes » à mesurer) | ecrans-central.md § 3 decies |
| S7.3 (reste) : ordre « message à afficher sur les postes » du titulaire à distance (texte simple de 200 caractères, sans lien, 1 à 7 jours, droit « ordres », code ; contrôlé au central et au poste) : pastille « Message » de la barre d’état pour tous les employés, petite fenêtre, « Ne plus afficher » pour le titulaire de l’officine | Fait en local (2026-11-21, scripts central 038 et local 160 à passer ; écrans à mesurer) | ecrans-central.md § 3 nonies |
| S7.2 (reste) : remise à zéro du mot de passe d’un compte distant par l’éditeur (mot de passe provisoire montré une fois, jamais par e-mail, sessions fermées, jamais son propre compte) ; retrait d’un rattachement par le titulaire depuis les écrans (accès coupé, ordres annulés) | Fait en local (2026-11-21, script central 038 à passer ; écrans à mesurer) | ecrans-central.md § 3 nonies |
| S7.2 (reste) : SMS (fournisseur), alertes de refus en série | En attente (après l’hébergeur) | ecrans-central.md § 8 |
| Mise en ligne des écrans du central (domaine, certificat, SMTP, connexion de production, comptes réels) | En attente (après le choix de l’hébergeur) | ecrans-central.md § 8 |
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
| Démonstration (page, démo interactive ou démo cloud) | En cours : démo cloud retenue ; étape 0 (API utilisable sous Linux) faite le 2026-11-20 ; étape 1 (Dockerfile, docker-compose) écrite le 2026-11-20 ; étape 2 (mode démo : marqueur de base, bandeau « Démonstration — données fictives », filigrane des PDF, fonctions coupées, comptes de démo figés, jeton 2 h, remise à zéro quotidienne, nomenclature + 905) écrite le 2026-11-21 ; aucun démarrage réel (Docker absent) | demo-cloud.md ; reste-a-faire-synthese.md |
| API utilisable sous Linux (secrets par DataProtection, outils PostgreSQL, racine des données, polices PDF embarquées, proxy inverse déclaré) ; Windows inchangé | Fait (build et tests sous Windows ; aucun démarrage réel sous Linux encore) | demo-cloud.md |
| Essais du matériel : modem GSM, compte SMTP, imprimantes, tiroir-caisse, disque de sauvegarde, iPhone | Bloqué (matériel de la pharmacie pilote) | a-faire.md « À vérifier sur le terrain » |
| CHIFA-Officine, lecteur de carte et Token : procédure d’installation | En attente | a-faire.md « Mise en production : à décider » |

## Ordonnances manuscrites

Un essai de lecture automatique de l’écriture manuscrite a été fait hors ligne avec le modèle libre TrOCR (licence MIT, ONNX Runtime) sur une vraie ordonnance : aucune lecture exploitable (par exemple « Sentreaux 4-my up » pour « Sintrom 4 mg cp »), environ 600 Mo de modèle et 1 Go de mémoire. Il n’est donc pas intégré ; la lecture assistée le remplace.

La liste des prochaines étapes est dans [feuille-de-route.md](feuille-de-route.md).
