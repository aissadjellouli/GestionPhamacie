# Ergonomie du poste : modifications du 2026-11-27

> Revue d'ergonomie du 2026-11-27 (31 propositions, captures des écrans à 1 366 × 768) puis « go P1, P2 » et « go P3 » du titulaire, et
> décisions prises au fil de la journée. Rapport de revue (hors dépôt) : `docs/taches/2026-11-27-revue-ergonomie/revue.md`.
> Règles d'affichage : [charte-graphique.md](charte-graphique.md). Règles métier : [regles-metier.md](regles-metier.md).

## Décisions du titulaire

| Sujet | Décision |
|---|---|
| Produit à stock 0 (n° 7) | Statut « **Pas en stock** » au lieu de « Normal » ; filtre « **En stock** » par défaut dans la liste des produits, « Tous » à un clic. Aucune alerte ni règle de rupture ne change. « Jamais reçu » non fait (le serveur ne compte que les lots encore en stock). |
| Remises à 0 DA au contrôle interne (n° 17) | **Une remise de 0 DA ne compte plus** (script 173) ; titre des cartes = nom + type d'anomalie. |
| Accueil (n° 20) | L'accueil **reste « Alertes du jour »** ; la tuile « Grossistes à payer » mène à Dettes et créances › Grossistes. |
| Barre d'état (n° 19) | Pastilles cliquables vers l'écran qui règle le problème ; pastilles techniques (central, 2e serveur, sauvegarde) **réservées au titulaire** et à qui a le droit de les régler ; l'imprimante en échec reste visible par tous. |
| Menus dépliables | **Toute rubrique qui a des sous-rubriques porte un chevron** (« › » replié, « ⌄ » déplié) et se replie ou se déplie au clic, à tout niveau, comme les thèmes de l'Administration. |
| Écran « Stock » du domaine Stock | Renommé « **Gestion du stock** » ; sa première rubrique « **Liste des produits** ». Adresse `/stock`, raccourci ⇧ S, droits inchangés. |

## Ce qui a changé, écran par écran

### Partout
- **Menu du haut** : la lettre du raccourci (⇧ lettre) est affichée sous chaque domaine ; le mot « **Réglages** » sous l'icône ⚙.
- **Bouton « ← … »** : toujours le titre de l'écran précédent (quatre titres longs raccourcis : « Droits expirés », « Sorties du tiroir »,
  « Conflits de facture Chifa », « Organismes repris »).
- **Menus de gauche** : resserrés pour ne plus défiler à 1 366 × 768 ; rubriques repliables homogènes (composant `GroupeMenu`, choix de
  l'employé gardé tant que le poste reste ouvert, groupe de l'écran affiché rouvert) ; écrans à rubriques avec chevron.
- **Boutons grisés** : fond gris, texte lisible (contraste AA, clair et sombre), bord en pointillé ; la raison est écrite à côté.
- **Aide « ? »** ajoutée sur Chifa, Bordereaux Chifa, Droits expirés, Pertes et vigilance, Sorties du tiroir, Clôture, État de la caisse,
  et les écrans d'Administration Alertes et seuils, Utilisateurs et droits, Mon offre, Postes autorisés, Sauvegardes, Taux de TVA.
- **Puces de période** (Aujourd'hui, 7 jours, 30 jours, Mois en cours) à côté de « Du / Au » : Trésorerie › Mouvements, Dettes,
  Financements › Compte du titulaire, Rapports. Calcul commun (`PeriodeRapide.Bornes`) : « 30 jours » = aujourd'hui et les 29 jours d'avant.
- **Tableaux compacts** (Liste des produits, Factures, Achats › Bons, Registre, Étiquettes, Contrôle interne) : une ligne par élément,
  information secondaire en petit sur la même ligne, actions en fin de ligne (classe `.tableau-compact`, 14 px : écart à la charte à
  trancher, voir plus bas).
- **Onglets** (Chifa, Bordereaux, Financements…) : l'onglet actif est visible (`aria-selected` stylé comme `aria-pressed`).

### Caisse
- **Clavier** : F2 Espèces (curseur dans « Montant reçu »), F4 Carte, F9 Chèque, F11 Crédit (patient choisi) ; « 3 * » tapé avant le
  code-barres du produit ou la recherche = 3 boîtes (une étiquette de boîte ou un Data Matrix désigne une seule boîte : la quantité n'y
  est pas appliquée, un message le dit ; correction du 2026-11-28) ; + / − du pavé numérique (champ de recherche vide) = quantité de la dernière ligne ; Entrée dans « Montant reçu » =
  encaisser (F12), sauf panier vide ou montant de 10 000 000 DA ou plus (code-barres scanné dans ce champ : champ vidé, rien n'est
  encaissé ; après une vente le curseur revient dans la recherche ; correction du 2026-11-28). Raccourcis affichés en petits cadres ; carte « Raccourcis clavier » dans l'aide de la caisse.
- **« Encaisser » grisé** : la raison est écrite sous le bouton ; à F12, le curseur va sur le champ manquant.
- **Ouverture de la caisse** : curseur dans le fond de caisse, Entrée = ouvrir, montant rappelé avec séparateur de milliers ; menu visible,
  actions impossibles grisées avec leur raison. Non fait : rappel du fond de la dernière clôture (le serveur ne le donne pas).
- **Caisse fermée** : État de la caisse et Clôture affichent le même état vide neutre avec « Ouvrir la caisse » (plus de bandeau rouge).
- Liens en double supprimés (« Retour à la caisse », « Retour au stock ») ; menu : « Relève d'équipe ».

### Stock, Achats, Patients, Finances, Pilotage
- **Gestion du stock** : « Nouveau produit » et « Modifier un produit » en boutons du titre de la rubrique ; statut « Pas en stock » ; filtre
  « En stock » par défaut.
- **Registre des psychotropes** : 8 colonnes au lieu de 12 (entrée/sortie en une colonne signée, soldes regroupés), actions en fin de ligne,
  plus de défilement horizontal ; tri par n° indiqué dans l'en-tête.
- **Étiquettes internes** : colonne « Boîtes sans étiquette » (« 3 sur 5 ») ; une phrase de consigne ; texte technique dans l'aide.
- **Fichier des patients** : bouton « **Nouveau patient** » (même fiche qu'à la caisse, route existante).
- **Patients en attente** : raison sous « Noter la demande » grisé ; aide du champ produit « Nom, DCI ou scan ».
- **Trésorerie** : carte entière cliquable vers les mouvements du compte ; un titre par carte ; « Caisses à verser ».
- **Financements** : un seul bouton « Nouveau prêt », vert quand la liste est vide.
- **Dettes et créances** : compteurs affichés même à 0 en gris neutre, alignés à droite (décision du 2026-11-27), MDN écrit en clair dans l'infobulle.
- **Rapports › Synthèse** : « dont honoraires (SHP) » avec infobulle ; bloc replié « Comment sont calculés ces chiffres ? » ; « Exporter en Excel ».
- **Contrôle interne** : cartes resserrées, titre = nom + type d'anomalie.

### Administration
- **Thèmes repliables** (Équipe, Caisse et contrôle, Fiscal, Stock et alertes, Produits, Chifa, Matériel et sauvegardes, Pharmacie et
  offre, Reprise de données).
- **Utilisateurs et droits** : « Enregistrer les droits » seulement sur « Droits par rôle » ; confirmation avant d'effacer un code comptoir.
- **Sauvegardes** : mots simples ; détails techniques repliés « Détails pour le technicien » ; bouton « Choisir le disque » dans le bandeau.
- **Postes autorisés** : « Dernière vérification », « Identifiant du PC », « Validité du code (minutes) » (le choix du mode est retiré :
  preuve toujours exigée, script 174).
- **Mon offre** : bouton « Copier » à côté du code de l'officine et du NIF ; packs « ✓ Inclus » ou « Non inclus ».
- **Alertes et seuils** : « Période de ventes prise en compte (jours) » ; « Modifications non enregistrées » dès qu'un champ change.

## Écarts de charte tranchés (2026-11-27, « le mieux pour toi » du titulaire)
- **Tableaux compacts** : 14 px gardés pour ces listes denses, inscrits dans la charte ; les autres tableaux restent en 15 px.
- **Compteurs du menu de Dettes** : affichés même à 0 (gris neutre), comme la charte le prévoit ; libellé et compteur sur une ligne,
  compteur à droite, libellé coupé par « … » si besoin (texte entier dans l'infobulle) : le menu ne défile pas à 1 366 × 768.

## Vérifications faites
Mesures réelles de l'état « après ergonomie » (avant menus repliables et preuve exigée) : 28 écrans × clair et sombre × 1 366 × 768 et
1 536 × 890 = 112 captures, **111 sans débordement** (Postes autorisés : 21 px à 1 366). Tests : architecture et application au vert à chaque lot.

### Essai réel du 2026-11-27 (PC non autorisé, clé du serveur dans le TPM)
- **Postes autorisés** : 0 px à 1 366 × 768 (les 21 px venaient du choix du mode, retiré).
- **Connexion du titulaire sur un PC non autorisé** : 102 px de débordement avec le refus d'un vendeur ; corrigé (intro d'une ligne, phrase
  « Chaque employé… » retirée dans ce mode, message de refus du serveur raccourci) : 0 px, clair et sombre, aux deux tailles.
- **Mon offre** : 93 px avec l'alerte d'horloge ; corrigé (toutes les alertes dans un seul bandeau, une ligne chacune ; explications de
  l'horloge dans l'aide « ? » ; champ du code sur 2 lignes ; espacement des cartes 8 px) : 0 px, clair et sombre, aux deux tailles.
- **Essai complet** : PC non autorisé (écran réduit, vendeur refusé, titulaire en Administration seulement, caisse refusée), code
  d'activation version 2 lié au TPM saisi depuis ce PC, code du poste, autorisation Windows, connexion normale avec accès complet ;
  rubriques repliables vérifiées au clic (Administration › Équipe, Stock › Gestion du stock) ; **16 écrans × clair et sombre × 1 366 × 768
  et 1 536 × 890 = 64 mesures, toutes à 0 px** (Accueil, Gestion du stock, Caisse, Factures, Registre, Patients, Trésorerie, Dettes,
  Financements, Rapports, Contrôle interne, Utilisateurs, Postes autorisés, Mon offre, Sauvegardes, Alertes et seuils).
- **Correction de méthode** : la première série de 64 mesures avait pris certains écrans **pendant leur chargement** (Dettes notamment) et
  ne mesurait pas le défilement du menu de gauche. Série refaite le même soir en attendant la fin du chargement et en mesurant aussi le
  menu : **16 écrans × clair et sombre × 2 tailles = 64 mesures, page et menu à 0 px** (après le passage des compteurs de Dettes sur une ligne).
- **Titre racine retiré** (décision du titulaire, même soir) : plus de « STOCK », « FINANCES »… ni « Accueil » en tête du menu de gauche ;
  détail dans [navigation.md](navigation.md), « Menu de gauche sans titre racine ». 28 mesures (7 écrans) : page et menu à 0 px.
