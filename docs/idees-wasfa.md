# Wasfa : analyse complète et idées à reprendre

> Source : captures et vidéos du logiciel Wasfa filmées par le titulaire (2026-11-15). Inspiration fonctionnelle seulement : ni l'apparence ni le nom de Wasfa ne sont repris. Données personnelles masquées « [masqué] ». Les images extraites des vidéos ne sont pas dans le dépôt.

## 1. Inventaire par source

### Image 16.43.33 — menu principal Wasfa [constaté]
- Ruban d'onglets : Fichier, Entrées de stock, Sorties de stock, CHIFA, CVM, Trésorerie, Journaux Situation stock, Journaux Mouvements stock, Journaux Trésorerie, Journaux CHIFA, Tables, Inventaires de stock, Outils.
- Onglet **Tables** ouvert : Fournisseurs, Pharmaciens, Clients divers, Médecins, Répertoire contacts, Médicaments de l'officine, Articles, Types de charges, Types de prestations, Motifs retours stock, Motifs sorties diverses, Marques/Laboratoires, Indications thérapeutiques, Motifs instances, Motifs ouverture tiroir caisse.
- Page d'accueil « Raccourcis » : 7 (puis 8 dans les vidéos) grandes tuiles avec icône, titre, description et raccourci clavier :
  Ventes au comptoir (Ctrl+Maj+V), CHIFA non traitées (Ctrl+Maj+N), Ventes CVM (Ctrl+Maj+M, visible dans les vidéos), CHIFA avant signature (Ctrl+Maj+A), Consultation stock (Ctrl+Maj+S), Recette (Ctrl+Maj+R), Libérer les produits (« produits en cours d'utilisation ou bloqués suite à un arrêt anormal du système »), Achats de produits (Ctrl+Maj+H).
- Bas de page : version (« 2003-2026 », date de mise à jour), contacts de l'éditeur.

### Image 16.44.36 — bloc contact de l'éditeur [constaté]
Téléphones, e-mail, site, noms des concepteurs. Sans intérêt fonctionnel (non recopié).

### Vidéo 16.55.42 (38 s) — lancement et connexion
- [constaté] Bureau Windows : deux icônes séparées « CHIFA_OFFICINE » (logiciel CNAS) et « Wasfa ». Écran de démarrage « Lancement en cours… ».
- [constaté] Fenêtre « Identification » : identifiant en liste déroulante + mot de passe, OK / Annuler, encart « Option Direct Pass (CB) disponible… Paramétrage du logiciel / onglet Général » [supposé : connexion par carte ou code-barres].
- [constaté] Onglets de l'accueil : Raccourcis, Informations et aides, Wasfa Mobile, Mise à jour.
- [constaté] Écran « Les médicaments périmés et/ou en voie de péremption » : options Périmés / En voie de péremption (nombre de jours), Tous / Du … au …, colonnes Désignation, Qté, Prix vente, Lot, Pér. ; totaux Achats / Ventes en bas.
- [constaté] Ouverture de la vente au comptoir (voir vidéo suivante).

### Vidéo 16.56.53 (18 s) — écran de vente au comptoir
- [constaté] Bandeau : logo, date et heure en gros chiffres, gros afficheur vert du total (« 0.00 »).
- [constaté] Ligne de saisie : Prix vente, Quantité, Désignation (« F7 liste des produits »), Code à barres, Pér., N° lot, Stock restant (case jaune), Stock dispo. ; bouton « INS », case « Accès au stock », boutons Insérer ligne / Annuler la saisie / Supprimer la ligne.
- [constaté] Tableau du panier : Désignation, Prix, Qté, Pér., N° lot, Code barres, Stock.
- [constaté] Colonne droite : Monnaie (rendu), Remise (%), « Date vente (Alt+d) » modifiable, case « Imprimer ticket de caisse », « Prestation (F10) » (bouton vertical), nom de l'utilisateur [masqué].
- [constaté] Volet « Règlement de la vente » : Comptant (liste), Client, Plafond dette, **Dette** (case rouge), Crédit, Mnt versement, TPE, Compte bancaire, Remarque.
- [constaté] Barre d'icônes en bas : CNT, CAS, CVM, A, EV, V, etc. (raccourcis vers les autres écrans depuis la caisse).

### Vidéo 22.04.28 (149 s) — tour complet CHIFA [constaté sauf mention]
1. **CHIFA_OFFICINE (logiciel CNAS)**, écran « Vente CHIFA » : Facture N°, N° assuré [masqué], Centre, CNAS, Bénéficiaire [masqué], Consultation (Généraliste), Prescripteur, Date naissance, Taux 100 %, Date fin droit, Spécialité, Date soins, Bordereau N°, Établissement ; détail : Médicament, Durée, Quantité, PPA, Posologie (nombre, forme « Ampoule(s) », « fois par jour »), Valider / Annuler / Substitution ; Montant assuré, Montant officine, Type maj (« Aucune majoration »), Montant maj, Montant maj FAE, Total officine ; **« 3 Vignettes »** ; liste DCI / tarif ; boutons Nouvelle facture, Aperçu, **Signer**, Annuler, E-Ordonnance, Médic. indisponibles, Autres.
2. **Wasfa › « Ordonnances CHIFA avant signature – Pour CHIFA 3 »** : N° facture (saisi), bouton Actualiser ; Total facture, Montant assuré, Montant officine ; gros chiffre vert « 3 » (nombre de produits ou de vignettes [supposé]) et afficheur « Caisse » ; à gauche **« Produits récupérés de CHIFA »** (Nom commercial, PPA, Qté, TS) ; à droite **« Produits correspondants dans le stock »** (Qté, Désignation) ; en bas **Total produits récupérés / Total produits correspondants / Différence (en rouge)**, N° de l'assuré, Nom et prénom de l'assuré, case « Vente crédit », Mnt versement, Dette ; boutons Fermer, Enregistrer, **Imprimer étiquette**, Autres raccourcis. Gestes : l'opérateur montre une ligne CHIFA non encore associée à un produit du stock (différence rouge = 370,30, le prix d'une ligne non rapprochée).
3. **Consultation du stock** : filtre, Imprimer, Modifier Qté, « e-Stocks – Transferts », **Modifier Prix/Pér.**, Imprimer CB ; colonnes Désignation, Qté, P. vente, Lot, Pér., SHP, P. achat, Date achat, code barres… ; fenêtre « Modifier le P. vente / Date de péremption des entrées des produits » : fournisseur, n° facture, date facture, PPA, n° lot, péremption, SHP, prix achat ; zone « Modifications » (PPA, n° lot, Pér., SHP), remarque « le prix d'achat doit être modifié dans l'entrée en stock… », Valider (F12) / Aide (F1) / Fermer.
4. **CHIFA non traitées** (« Ordonnances CHIFA non traitées ») : « Filtre de recherche des ordonnances non traitées par mois » (liste 2026-11, 2026-10…), Rechercher ; liste : N° facture, date-heure, N° assuré [masqué], rang, nom [masqué], bénéficiaire, montants ; totaux « Total ventes », « Mnt CHIFA » en bas.
5. Ouverture d'une ordonnance non traitée : **« Récupération et traitement des ordonnances CNAS – [centre] »** : N° facture, Date ordonnance, Date de soins, N° assuré, Rang ; gros « 6 » vert + total « 793,23 » ; produits récupérés (6 lignes) ; zone de saisie identique à la caisse (prix, quantité, désignation F7, code barres, lot) pour **poser chaque produit du stock en face** ; boutons Fermer, Enregistrer, Supprimer, Imprimer ticket, **Exclure du traitement**, Autres raccourcis ; règlement (Comptant), Dette, « Saisie dans Chifa par » [utilisateur].
6. Retour accueil ; Fichier › Outils : Options / Paramétrage du logiciel, Utilisateurs, Changement mot de passe, Actions sensibles, Sauvegarde des données, Ouvrir tiroir caisse, Libérer produits, Maintenance, Données supprimées [lecture partielle]. Fenêtre « Paramétrage du logiciel » (identité de la pharmacie, n° pharmacien dans l'impression des codes-barres, géolocalisation, demande d'activation à distance).

### Vidéo 22.04.28 (1) = (2) (149 s) — CHIFA non traitées, suite [constaté]
Même écran « Récupération et traitement des ordonnances CNAS » : l'opérateur montre la liste des produits récupérés, la zone vide « Produits correspondants dans le stock », le total, la différence en rouge, « Nom et prénom de l'assuré » et « Ayant droit » [masqués], « Saisie dans Chifa par » ; liste des mois (jusqu'à 2024) ; même enchaînement que ci-dessus, filmé de plus près.

### Vidéo 22.04.28 (3) (75 s) — vente au comptoir et crédits clients [constaté]
- Vente d'une boîte (CLAMOXYL) : ligne au panier avec lot, péremption, code barres, stock ; total en orange.
- Fenêtre « Liste de valeurs – Liste des clients (Divers/CHIFA/CVM) » : Nom et prénom [masqué], N° assuré, type client, n° carte, date fin droit…
- Fenêtre **« Paiement des crédits clients »** : Client, Remarque, n° ; Type paiement : Espèces (Alt+e), Chèque (Alt+c) [n°, date, banque client, « en attente d'encaissement / dépôt », compte], TPE (Alt+t) ; historique : Date, Opération, Paiement, Type pièce, N° pièce, Date pièce ; totaux opérations / paiements / solde ; Imprimer.

### Vidéo 22.04.28 (4) (37 s) — Ventes CVM [constaté partiellement]
- Tuile « Ventes CVM » (convention militaire [supposé d'après « Saisie des ordonnances de la convention militaire » lu sur la tuile]).
- Écran de saisie à champs jaunes (montants par catégorie, taux, totaux) ; l'opérateur pointe les champs avec une feuille de soins papier à la main. Libellés illisibles à cette distance.

### Vidéo 22.04.29 (149 s) — saisie CVM / ordonnance papier [constaté partiellement]
- Écran « Vente CVM » [supposé] : gros « 1 » vert, afficheur « Caisse 0,00 », Dette en rouge, produits récupérés / correspondants comme pour CHIFA ; fenêtres superposées de saisie (nom, n°, dates) remplies au clavier à partir d'une feuille papier rose (feuille de soins / « ورقة العلاج ») [supposé].
- Libellés trop flous pour un relevé fiable.

### Vidéo 22.04.29 (1) (67 s) — périmés et ventes avec montants négatifs [constaté]
- Écran « Les médicaments périmés et/ou en voie de péremption » (sablier « calcul en cours »).
- Vente au comptoir avec 4 lignes identiques puis 3, 2, 1 et un total **négatif** (−1 481,04 → −1 110,78 → −740,52 → −370,26) [supposé : retour client saisi en quantités négatives, ligne par ligne ; aucune fenêtre de motif n'est visible].

### Vidéo 22.04.29 (2) (42 s) — visite de l'officine [constaté]
Rayonnages, comptoir, un poste dédié à « CHIFA OFFICINE » (écran d'accueil CNAS, lecteur de carte), tiroirs, ordonnances papier. Pas d'écran Wasfa exploitable.

### Vidéo 16.55.42 / Wasfa2 : barre du bas de la caisse [constaté]
Icônes PCL, DPC, CNT, CAS, CVM, A, DTC, EV, V et un bouton rouge (quitter) : la caisse donne accès direct aux autres traitements sans repasser par l'accueil.

---

## 2. Tableau comparatif

| Fonction Wasfa | Chez nous | Où (fichier / écran) | Intérêt | Effort | Remarque |
|---|---|---|---|---|---|
| Accueil à tuiles + raccourci Ctrl+Maj+lettre | Oui | `docs/navigation.md` (menu deux niveaux, Ctrl+K, Ctrl+Maj+lettre) | Faible | – | Déjà mieux couvert. |
| Vente au comptoir : stock restant, lot, pér. sur la ligne | Oui | Caisse (`fonctionnalites.md` l.10) | Faible | – | |
| Gros afficheur du total à la caisse | Partiel | Caisse, afficheur client | Moyen | S | Vérifier la lisibilité du total à 2 m (comptoir). |
| Date de vente modifiable (Alt+D) | Non | – | Faible | M | Risque de fraude ; à n'autoriser qu'au titulaire si demandé. |
| Case « imprimer le ticket » par vente | Partiel | Administration › Imprimantes (ticket auto par poste) | Moyen | S | Bascule par vente au comptoir. |
| Prestations de service (F10, « Types prestations ») | Non | – | Moyen | M | Prise de tension, glycémie, injection… vendues sans stock. |
| Règlement : comptant / crédit avec plafond / TPE / versement partiel | Oui | Caisse, crédit avec plafond | Faible | – | |
| Paiement des crédits clients (espèces, chèque, TPE, historique) | Oui | « Encaisser une dette » (`fonctionnalites.md` l.43) | Faible | – | Chèque « en attente de dépôt » existe en trésorerie. |
| Clients divers / CHIFA / CVM dans une même liste | Partiel | Patients + organismes (MDN à créer) | Moyen | S | Type « CVM/MDN » sur la fiche patient (n° d'affilié). |
| **CHIFA avant signature** (rapprochement Chifa ↔ stock avant signature) | Partiel | Volet Chifa par QR à la caisse (`chifa.md`, script 060/086) | **Fort** | M | Nous rapprochons après signature (QR du volet). Wasfa rapproche **avant** : erreurs de produit corrigées avant de signer. |
| Liste « Produits récupérés de CHIFA » vs « correspondants dans le stock » + Différence | Partiel | Écarts de prix Chifa signalés à la caisse | **Fort** | M | Afficher les deux colonnes et la différence en rouge, bloquant tant que non nulle (ou justifiée). |
| **CHIFA non traitées** (factures signées dans Chifa mais non sorties du stock), filtre par mois | Non | Import Chifa bloqué (format inconnu) | **Fort** | L | Wasfa lit les ordonnances Chifa [supposé : dans la base ou les exports de CHIFA_OFFICINE]. Chez nous : liste « ventes Chifa à déposer » prévue (`conception-chifa-mdn.md` §3.2). |
| « Exclure du traitement » une ordonnance Chifa | Non | – | Moyen | S | Ordonnance déjà servie autrement, à ne plus proposer ; motif obligatoire. |
| « Saisie dans Chifa par » (utilisateur) | Non | – | Moyen | S | Traçabilité : qui a saisi dans Chifa ≠ qui a vendu. |
| Nombre de vignettes affiché en grand | Non | – | Moyen | S | Contrôle visuel : vignettes collées = nombre de boîtes remboursées. |
| Imprimer étiquette depuis l'écran Chifa | Partiel | Étiquettes internes par boîte | Faible | S | [supposé] étiquette de vignette ou de boîte. |
| Bordereaux CHIFA, journaux CHIFA | Partiel | Table `bordereau`, versement organisme ; écran Bordereaux prévu | **Fort** | L | Déjà conçu (`conception-chifa-mdn.md` §3.2-3.4). |
| Rejets CHIFA | Partiel | Conçu, non fait (§3.4) | Fort | M | Non vu dans les vidéos [constaté : aucun écran de rejet filmé]. |
| **Ventes CVM** (convention militaire) | Partiel | Canal `mdn` (script 089), organisme « MDN » à créer | Fort | M | Wasfa a un écran dédié. Format papier à relever (feuille rose). |
| Consultation du stock : modifier prix / pér. / lot d'une entrée | Partiel | Stock (consultation) ; prix et lots à la réception | Moyen | M | Correction tracée d'une péremption mal saisie, avec ancien → nouveau au journal. |
| Périmés / en voie de péremption (jours, période, totaux) | Oui | Alertes de péremption à 3 seuils | Faible | – | |
| **Libérer les produits** (bloqués après arrêt anormal) | Non nécessaire | Ventes en attente serveur, verrous de transaction (`regles-metier.md` l.121) | Faible | – | Chez nous aucun verrou ne survit à un arrêt (transactions). À ne pas reprendre ; voir §4. |
| Recette (journalière ou filtres) | Oui | Rapports, clôtures | Faible | – | |
| Achats de produits | Oui | Achats › Réception | Faible | – | |
| Entrées / sorties de stock, motifs retours et sorties diverses (table) | Partiel | Sorties : périmé, cassé, don, échantillon (liste fixe) ; motifs de retour fixes | Moyen | S | Liste fixe chez nous ; rendre une partie paramétrable par le titulaire. |
| **Motifs ouverture tiroir caisse** (table) | Partiel | Tiroir ouvert sans vente tracé (`tiroir_ouvert`, `regles-metier.md` l.1391-1396), sans motif | **Fort** | S | Motif obligatoire choisi dans une liste (monnaie, erreur de rendu, dépôt…), utile au contrôle interne. Bloqué tant que l'ouverture matérielle du tiroir n'existe pas (`fonctionnalites.md` l.14). |
| **Motifs instances** (table) | Non | Ventes en attente sans motif | Moyen | S | [supposé] « instance » = vente/ordonnance mise en attente (produit manquant, patient revient). Motif + liste des instances = suivi des produits dus. |
| **Types de charges** (table) | Partiel | Catégories de dépense fixes (7) | Moyen | S | Rendre la liste paramétrable par le titulaire. |
| **Indications thérapeutiques** (table) | Non | Catégories produit + DCI | Moyen | M | Recherche par indication (« toux », « fièvre ») au conseil ; données à saisir par le titulaire. |
| Marques / Laboratoires (table) | Partiel | Laboratoire cité pour « moins cher » | Faible | S | |
| Pharmaciens, Répertoire contacts | Partiel | Fiche contact, utilisateurs | Faible | – | |
| Inventaires de stock | Oui | Inventaire tournant et complet | Faible | – | Nous allons plus loin. |
| e-Stocks / transferts | Non | – | Faible | L | [supposé] échange entre pharmacies ; hors périmètre. |
| Wasfa Mobile | Partiel | Scanner mobile, onglet Pilotage | Faible | – | Contenu non filmé. |
| Option « Direct Pass (CB) » à la connexion | Partiel | Code comptoir 4-6 chiffres | Faible | S | [supposé] badge code-barres ; notre code comptoir couvre le besoin. |
| Actions sensibles, sauvegarde, données supprimées | Oui | Journal des actions, sauvegardes | Faible | – | |
| Raccourcis vers CHIFA/CVM depuis la barre de la caisse | Partiel | Ctrl+K, menu | Moyen | S | Un bouton « Chifa à traiter (n) » dans la caisse. |
| Retour client en quantité négative dans la caisse | Oui, autrement | Retour avec motif, lot, droit (`fonctionnalites.md` l.19) | Faible | – | Notre retour est plus sûr ; ne pas copier. |

---

## 3. Ce qu'on peut reprendre, par priorité

### Priorité 1 — Chifa (le cœur de l'usage chez le titulaire)
1. **Écran « Chifa à rapprocher » (équivalent « avant signature »)** — intérêt fort, effort M. Saisir ou scanner le n° de facture Chifa (ou le QR) **avant** l'encaissement ; deux colonnes « Produits Chifa » / « Produits servis du stock » ; total, **différence en rouge** ; encaissement bloqué tant que la différence n'est pas nulle ou justifiée (motif). Prolonge la reprise du volet par QR existante. *Limite* : Wasfa lit les produits « récupérés de CHIFA » avant signature ; nous n'avons que le QR imprimé sur le volet **après** signature [constaté dans `chifa.md`]. Sans accès aux données de CHIFA_OFFICINE, l'« avant signature » chez nous = saisie manuelle des lignes ou lecture du QR d'aperçu, à vérifier.
2. **« Chifa non traitées » = ventes Chifa à déposer / ordonnances Chifa sans vente** — intérêt fort, effort L. Liste filtrée par mois, totaux (ventes, montant Chifa), ouverture d'une ligne pour la traiter (poser les produits du stock), bouton « Exclure du traitement » avec motif, « Saisie dans Chifa par ». Dépend de l'import Chifa (format inconnu) ; en attendant, version manuelle : ventes Chifa sans n° de bordereau après N jours.
3. **Nombre de vignettes** affiché en grand sur la vente Chifa et sur le ticket interne — intérêt moyen, effort S (le nombre de boîtes remboursables est déjà connu).
4. **Bordereaux et rejets** — déjà conçus (`conception-chifa-mdn.md` §3.2-3.4) ; Wasfa confirme le besoin (onglets CHIFA, Journaux CHIFA). Effort L.
5. **Ventes CVM / MDN** — écran ou mode de caisse dédié à la convention militaire (n° d'affilié, feuille de soins papier, taux) sur le canal `mdn` existant. Effort M ; modèle de feuille à obtenir.

### Priorité 2 — Caisse
6. **Motif d'ouverture du tiroir** (liste paramétrable) — fort, S, dès que l'ouverture du tiroir est branchée.
7. **Motif de mise en attente (« instances »)** et liste des instances avec produit dû — moyen, S.
8. **Prestations de service** vendues sans stock (types paramétrables : tension, glycémie, injection…) — moyen, M.
9. **Case « imprimer le ticket » par vente** à la caisse — moyen, S.
10. **Accès direct depuis la caisse** aux traitements Chifa en attente (pastille avec le nombre) — moyen, S.

### Priorité 3 — Stock
11. **Correction tracée du prix de vente / de la péremption / du n° de lot d'une entrée** depuis la consultation du stock (droit dédié, ancien → nouveau au journal, prix d'achat jamais modifié ici comme Wasfa) — moyen, M.
12. « Libérer les produits » : **ne pas reprendre** (voir §4) ; éventuellement un écran d'administration « Ventes en attente / réceptions en attente de tous les postes » pour débloquer à la main, s'il manque.

### Priorité 4 — Tables de référence paramétrables par le titulaire
13. **Types de charges** (catégories de dépenses aujourd'hui fixes) — moyen, S.
14. **Motifs de sorties diverses et de retours** (aujourd'hui fixes) — moyen, S ; garder les motifs système (périmé, cassé, don) non supprimables car utilisés par les rapports et la TVA.
15. **Indications thérapeutiques** liées aux produits, pour la recherche au conseil — moyen, M (saisie des données à prévoir).
16. Marques / laboratoires — faible, S.

---

## 4. Ce qu'il ne faut pas reprendre, et points incertains

### À ne pas reprendre
- **Apparence et nom** : couleurs, logo, gros chiffres verts façon afficheur, tuiles rondes à lettres ; notre charte (`docs/charte-graphique.md`) s'applique.
- **« Libérer les produits »** : chez Wasfa, des produits restent « en cours d'utilisation » après un arrêt anormal [constaté dans la description de la tuile]. Chez nous les lots ne sont verrouillés que pendant la transaction et les ventes en attente sont sur le serveur [constaté dans `regles-metier.md`] : le problème n'existe pas, un tel bouton masquerait des erreurs.
- **Date de vente modifiable au comptoir (Alt+D)** : permet d'antidater une vente ; contraire au contrôle de caisse. À la rigueur titulaire seul, avec journal.
- **Retour en quantités négatives dans le panier** [supposé d'après la vidéo 22.04.29 (1)] : sans motif, ni lot, ni droit ; notre retour est plus contrôlé.
- **Saisie complète de l'ordonnance Chifa** (assuré, posologie, durée, signature) : c'est le rôle de CHIFA_OFFICINE, logiciel de la CNAS que nous ne remplaçons pas (`chifa.md`).
- **Fenêtres superposées multiples** (liste de valeurs, paiement, saisie) : contraire à notre règle « écrans sans défilement, rubriques à gauche ».
- **Identifiant en liste déroulante à la connexion** : divulgue la liste des comptes.

### Points incertains
- [inconnu] Comment Wasfa obtient les « produits récupérés de CHIFA » avant signature : lecture de la base locale de CHIFA_OFFICINE, d'un export, ou d'une interface autorisée par la CNAS ? Non visible dans les vidéos. C'est **la** question qui conditionne l'effort de la priorité 1.
- [supposé] Le gros chiffre (« 3 », « 6 », « 1 ») = nombre de produits (ou de vignettes) de l'ordonnance : il vaut le nombre de lignes visibles dans les trois cas filmés.
- [supposé] « CVM » = convention militaire (lu « Saisie des ordonnances de la convention militaire » sur une image floue).
- [supposé] « Motifs instances » = motifs de mise en attente d'une vente ou d'une ordonnance.
- [supposé] « Direct Pass (CB) » = connexion par carte à code-barres.
- [inconnu] Contenu des onglets Trésorerie, Journaux, Inventaires, Wasfa Mobile, e-Stocks : non filmés.
- [inconnu] Écran des rejets et des bordereaux CHIFA : non filmé.
- [constaté] Deux vidéos de Wasfa2 sont des doublons ; la saisie CVM est trop floue pour relever ses champs.

---

## 5. Questions pour le titulaire

1. Dans Wasfa, d'où viennent les « produits récupérés de CHIFA » : Wasfa lit-il directement CHIFA_OFFICINE sur le même PC, ou faut-il exporter un fichier ? Un éditeur vous a-t-il parlé d'une autorisation de la CNAS ?
2. Dans votre travail quotidien, servez-vous d'abord dans Chifa puis dans Wasfa, ou l'inverse ? À quel moment « avant signature » vous évite-t-il une erreur ?
3. « CHIFA non traitées » : combien d'ordonnances par mois restent non traitées, et pourquoi (oubli, produit manquant, servi plus tard) ? Utilisez-vous « Exclure du traitement » ?
4. CVM : est-ce bien la convention militaire (MDN) ? Pouvez-vous fournir une feuille de soins anonymisée et dire qui paie, à quel taux, avec quel bordereau ?
5. Les « instances » : que signifie ce mot chez vous, et quels motifs avez-vous dans la table ?
6. Motifs d'ouverture du tiroir : quels motifs utilisez-vous ? Le tiroir s'ouvre-t-il depuis le logiciel ou à la clé ?
7. Indications thérapeutiques : les avez-vous remplies vous-même ? Les utilisez-vous pour conseiller ?
8. Prestations (F10) : quelles prestations vendez-vous, et à quel prix ?
9. Avez-vous déjà eu besoin de « Libérer les produits » ? À quelle fréquence ?
10. Modifier la date d'une vente (Alt+D) : en avez-vous besoin, pour quel cas ?
11. Le nombre de vignettes affiché en grand vous sert-il à contrôler les vignettes collées sur le volet ?
12. Que vous manque-t-il dans Wasfa (ce que vous aimeriez qu'un nouveau logiciel fasse mieux) ?

## 6. Réponses cherchées sur Internet (2026-11-15)

Rien d'officiel trouvé ; sources commerciales ou anciennes, à confirmer par le titulaire.

- **D'où Wasfa tire les produits Chifa avant signature** : la fiche commerciale de Wasfapro (Comparili, ancienne d'environ 4 ans) annonce le « déstockage des ventes CHIFA (CNAS, CASNOS) avant ou après signature » et le « contrôle de la facture CHIFA avant signature avec rapport d'erreurs ». Le mécanisme technique (lecture de la base de CHIFA_OFFICINE, fichier exporté) n'est pas décrit. D'autres logiciels (CTI Pharm, Profficine) indiquent le dossier d'installation de CHIFA_OFFICINE, ce qui suggère une lecture des fichiers ou de la base locale de CHIFA_OFFICINE (supposé). Même question posée à la CNAS (demande-cnas.md).
- **« CHIFA non traitées »** : aucune source publique.
- **CVM** : aucune source ne définit le sigle ; l'hypothèse « convention militaire » (ministère de la Défense nationale) reste à confirmer. Un texte de janvier 2026 crée la Pharmacie centrale des armées, sans parler des officines.
- **« Instances »** : aucune source ; hypothèse : ventes mises en attente ou produits dus au patient.
- **Paiement CNAS** : un document de présentation indique un paiement des factures un mois après le dépôt du bordereau (convention officine–CNAS).
- **Homonymes** : les applications mobiles « Wasfa » (App Store, Google Play) sont des services de photo d'ordonnance, sans rapport avec le logiciel de gestion.

Sources : https://comparili.net/produit/wasfapro-secteur-medical-et-pharmaceutique-logiciel-algerie-comparili/ ; https://www.ctib.dz/telechargement.php ; https://www.profficine.com/ ; https://www.arpce.dz/en/file/j2m9a9 ; https://lechiffredaffaires.dz/la-convention-entre-lorganisme-de-securite-sociale-et-lofficine-pharmaceutique/ ; https://play.google.com/store/apps/details?id=dz.wasfa.pharmacy

## 7. Comparaison à jour : Wasfa / Pharma Officine (état au 15/11/2026, après les lots du jour)

| Domaine | Wasfa | Pharma Officine | Existe chez nous ? | Point fort Wasfa | Point faible Wasfa | Point fort Pharma Officine | Point faible Pharma Officine |
|---|---|---|---|---|---|---|---|
| Vente au comptoir | Oui | Oui | Oui | Simple, raccourcis clavier | Date de vente modifiable (Alt+D), retour en quantité négative | Lots, péremption, recherche « disponible d'abord », retour contrôlé avec motif et droit | Tiroir et imprimante non encore branchés au matériel |
| Chifa avant signature (rapprochement avec le stock) | Oui | Oui, après signature | Partiel | Corrige les erreurs avant de signer | Mécanisme d'accès à Chifa inconnu | Rapprochement volet ↔ vente (QR), écarts en rouge, motif et journal (095, 097) | Pas de transmission vers CHIFA-Officine (format inconnu, demandé à la CNAS) |
| Chifa non traitées | Oui | Liste « Ventes Chifa en attente de volet » | Partiel | Liste par mois, exclusion d'une ordonnance | — | Vente encaissée sans n° avec motif, rapprochable plus tard, « en retard » signalé | Pas de lecture des ordonnances signées dans Chifa |
| Écarts de prix Chifa | Non vu | Oui | Oui (mieux) | — | — | Seuil réglable, choix de l'ayant droit, prix le plus élevé sinon, catalogue mis à jour d'après le volet | — |
| Volet / patient prérempli par QR | Non vu | Oui | Oui (mieux) | — | — | Patient créé depuis le QR, date d'ordonnance, CNAS/CASNOS, ayants droit (lien familial) | Sens des champs « 24 », « 2 » et du suffixe non confirmé |
| Nombre de vignettes | Oui | Oui | Oui | Affiché en grand | — | Affiché en grand (094) | — |
| Bordereaux et rejets Chifa | Oui (journaux) | Conçu | Non (conçu) | Suivi des bordereaux | — | Ventes Chifa/MDN jamais refacturées (089) | Écran Bordereaux, paiement par bordereau et rejets à faire |
| Convention militaire (CVM / MDN) | Oui, écran dédié | Canal MDN + organisme MDN | Partiel | Saisie dédiée | Sigle et format non documentés | Ventes MDN jamais refacturées | Pas de saisie dédiée (modèle de feuille manquant) |
| Prestations de service (sans stock) | Oui (F10) | Non | Non | Prise de tension, injection… | — | — | À faire |
| Crédit client | Oui | Oui | Oui (mieux) | Paiement simple | — | Plafond par droit, imputation par vente ou facture, reçu, relevé, surplus annoncé, « Remis à / Pour » | — |
| Tiroir-caisse : motif d'ouverture | Oui (table) | Oui | Oui (mieux) | Liste réglable | — | Motif obligatoire, montant et écriture en trésorerie, prise en compte à la clôture, seuil et alerte (094, 096) | Tiroir matériel non branché |
| Motifs d'instances (mises en attente) | Oui (table) | Oui | Oui | Liste réglable | Sens d'« instance » non confirmé | Motif + produits dus (094) | Produits dus non visibles après reprise |
| Types de charges | Oui (table) | Oui | Oui | Réglable | — | Catégories réglables, contrôlées en trésorerie | — |
| Motifs retours / sorties de stock | Oui (table) | Libellés réglables | Partiel | Ajout libre | — | Codes sûrs (TVA, registre, rapports) | Ajout d'un nouveau motif impossible (contraintes) |
| Indications thérapeutiques | Oui (table) | Non | Non | Recherche par indication | — | — | À faire (fiche produit, recherche au comptoir) |
| Correction prix / péremption / lot d'une entrée | Oui | Partiel | Partiel | Correction directe | Traçabilité non vue | Prix réservé à `prix.modifier` | Correction tracée depuis le stock à faire |
| Libérer les produits (verrous après arrêt) | Oui | Inutile | Non nécessaire | — | Signe de verrous qui restent après un arrêt | Transactions : aucun verrou ne survit | — |
| Inventaire | Oui | Oui | Oui (mieux) | — | — | Tournant, à l'aveugle, écarts validés | — |
| Sécurité, droits, journal | Connexion, actions sensibles | Oui | Oui (mieux) | — | Liste des identifiants à la connexion | Droits fins, code comptoir en liste blanche, blocages, journal lisible, HTTPS possible | — |
| Interface | Fenêtres superposées, tuiles | Écrans fixes, rubriques | — | Familière aux pharmaciens | Fenêtres empilées | Écran fixe, listes paginées et filtrées | — |

> Réponse du titulaire (15/11/2026) : sens de « CVM » inconnu, probablement les ventes au MDN (militaires) ; à confirmer. Le seuil de sortie de caisse (2 000 DA par défaut) reste paramétrable dans Administration › Caisse.
