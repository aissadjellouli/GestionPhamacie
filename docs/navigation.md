# Navigation et retour à l'écran précédent

## Menu en deux niveaux, accueil, recherche et raccourcis (validé le 2026-11-13)

- **En haut, les domaines seulement** : Caisse, Stock, Achats, Finances, Patients, Pilotage (et ⚙ Administration). Un clic ouvre le premier
  écran permis du domaine. Plus de menus déroulants. Le compteur des alertes urgentes est sur « Stock ».
- **À gauche, les écrans du domaine** ; l'écran affiché est déplié avec ses rubriques (composant `NavDomaine`). Domaine d'un seul
  écran (Caisse, Achats) : ses rubriques directement. Les groupes « Autres écrans du stock » ont disparu (le test
  `NavigationTests` refuse un menu vertical fait à la main et ces groupes).

  | Domaine | Écrans |
  |---|---|
  | Caisse (Ctrl + Maj + V) | Caisse (vente, annulation, crédit, tiroir, état, relève, clôture) |
  | Stock (Ctrl + Maj + S) | Alertes, Stock, Inventaire, Registre des psychotropes, Étiquettes internes (`stock/etiquettes`), Traçabilité et rappel de lot |
  | Achats (Ctrl + Maj + H) | Achats (commandes, réceptions et réimpression des étiquettes d'une livraison, retours, règlements, traites à payer, grossistes) |
  | Finances (Ctrl + Maj + F) | Factures, Trésorerie, Dettes et créances, Financements (titulaire seul) (Chifa : à venir) |
  | Patients (Ctrl + Maj + T) | Fichier des patients (`patients`), Droits expirés ou bientôt expirés (`patients/droits-chifa`), Patients en attente (`patients/attente`, accessible avec le seul `caisse.vendre`) |
  | Pilotage (Ctrl + Maj + P) | Rapports, Contrôle interne |

  Étiquettes internes : dans le Stock depuis le 2026-11-13 (stock.consulter ou achats.receptionner ; imprimer : achats.receptionner) ;
  l'ancienne adresse `achats?onglet=etiquettes` y renvoie. Patients en attente (« Prévenir quand disponible ») : dans le domaine
  Patients depuis le 2026-11-18 (droit de vendre, comme avant) ; l'ancienne adresse `achats?onglet=patients-attente` y renvoie ;
  Achats › Ruptures garde sa colonne « Patients en attente » (nombre). Depuis le 2026-11-20, l'écran est **accessible avec le seul
  `caisse.vendre`** : sans `stock.consulter`, le produit attendu se choisit par la recherche du comptoir (`GET /api/caisse/produits`,
  composant `ChoixProduit`) ; lien « Patients en attente » aussi dans la colonne de la Caisse (groupe Comptoir, après « Vente »,
  `NavCaisse`, avec `caisse.vendre`). Domaine Patients réservé au droit de vendre (`MiseEnPage`). Un écran sans droit n'apparaît pas ; un domaine sans écran permis non plus. Description unique : `Services/Domaines.cs`.
- **Accueil selon les droits** : qui a un droit de gestion (achats, stock, rapports, trésorerie, factures, paramètres, titulaire)
  arrive sur le **tableau de bord** (`/accueil` : alertes urgentes, lots périmant dans les 6 mois, réceptions en attente, écarts
  d'inventaire à valider ; chaque tuile ouvre son écran et n'apparaît que si l'employé peut l'ouvrir) ; les autres (vendeur,
  stagiaire) arrivent directement sur la **Caisse**. Le logo ramène à cet accueil ; un écran refusé aussi. Titulaire : bandeau
  « N alerte(s) n'ont pas pu être envoyées » en tête du tableau de bord (5 plus récentes, « Marquer comme vues »,
  « Vérifier les destinataires » vers Administration › Notifications) (2026-11-14) ; puis bandeau « Écarts de caisse récents »
  (caisses clôturées depuis 7 jours avec un écart non nul, pas encore vues : date, poste, employé, montant ; 5 plus récents,
  « Marquer comme vus ») ; les autres employés ne voient ni l'un ni l'autre (2026-11-14).
- **Recherche partout : Ctrl + K** (ou « ⌕ Rechercher… » dans l'en-tête) : écrans et rubriques permis, produits (ouverts dans
  le Stock filtré sur leur nom), patients (fiche contact). Dès 2 lettres, sans majuscules ni accents ; ↑ ↓, Entrée, Échap.
  Composant `RechercheGlobale`.
- **Raccourcis** : Ctrl + Maj + la lettre affichée à côté du domaine ; Ctrl + K ; F12 (encaisser) ; Alt + ← (retour).
- **Ce qui est interdit n'est pas montré** : un écran ou une rubrique sans droit est absent des menus (plus de rubrique grisée :
  Administration, Achats, « Ouvrir le tiroir » de la Caisse) ; un groupe dont aucune rubrique n'est permise disparaît aussi.
- **Compteurs du travail en attente** (`Services/CompteursAttente.cs`), selon les droits, relus au changement d'écran au plus
  toutes les 30 secondes : Achats (réceptions en attente + bons en brouillon ; à gauche « En attente » et « Bons de commande »),
  Finances (factures à encaisser + sommes dues en alerte, en rouge si une facture est échue ou une somme en alerte ; à gauche
  « Factures » et « Dettes et créances »), Stock (alertes urgentes).
- **Pas d'écran sans issue** : une liste vide sans filtre propose l'étape suivante (`ActionSuivante`) : aucun écart → aller au
  comptage ; aucun inventaire → commencer un comptage ; aucune réception en attente, aucun produit en stock → réceptionner une
  livraison ; aucun bon → voir les produits à commander ; aucun retour → préparer un retour ; registre vide sur la période → voir
  les 12 derniers mois ; aucun produit sensible → les choisir ; aucun emplacement → les créer ; aucune vente depuis l'ouverture
  → aller à la vente ; aucune facture → nouvelle facture ; aucun compte → « Comptes et soldes ». Le bouton n'apparaît que si
  l'employé peut ouvrir l'écran proposé.
- **Produits et lots cliquables** (`LienProduit`, souligné en pointillé comme les contacts) : ouvrent le Stock filtré dessus ;
  dans les écarts à valider, le résultat d'inventaire, le registre, Achats › À commander, Préparer un retour et Rapports › Sorties. **Jamais dans la feuille de comptage** (comptage à l'aveugle :
  le Stock montrerait les quantités).
- **Clavier** (`poste.js`) : à l'ouverture d'un écran ou d'une rubrique, le curseur va dans sa première recherche (sauf s'il est
  déjà dans un champ) ; Échap efface la recherche, sauf si une liste de propositions ou une fenêtre est ouverte (Échap la ferme).
- **Aide « ? »** à côté du titre de chaque écran de domaine (`AideEcran`) : quelques lignes sur l'écran, les raccourcis et le lien
  vers le Guide ; Échap ferme. La Caisse n'a pas de titre : « ? Aide de la caisse » en bas de son menu.
- **Serveur lent** (`BarreChargement`, `Services/AppelsEnCours.cs`) : barre fine en haut dès 0,3 s d'appel ; au-delà de 5 s d'attente continue,
  bandeau « Le serveur met du temps à répondre… » ; au-delà de 20 s, bandeau rouge (vérifier le réseau et le PC serveur ; une sauvegarde
  ou un export peut être long). Chaque appel est abandonné à son délai : 30 s en général, 2 min pour un document (PDF, export,
  étiquettes, rapport), 10 min pour une opération lourde (sauvegarde, restauration, reprise, import de nomenclature ou de scans, GED) ;
  l'écran affiche alors « Le serveur met trop de temps à répondre… ». Écran de connexion : il attend l'état du poste (3 s au plus)
  avant d'afficher le bon mode.
- **En-tête toujours sur une ligne**, menu des domaines toujours entier : selon la largeur utile (fenêtre ÷ zoom du poste, classes
  `largeur-*` posées par `poste.js`), d'abord les lettres de raccourci disparaissent (≤ 1 850 px), puis « Rechercher… » devient la loupe
  (≤ 1 650 px), puis le nom du logiciel et de la pharmacie se réduisent au logo (≤ 1 450 px, texte au survol du logo). Le nom de
  l'écran précédent reste **entier** (plafond 200 px, le plus long : « Tableau de bord financier », 183 px ; mesuré à 1 366 × 768 et
  1 536 × 890, clair et sombre) : seul le bouton se resserre, par « … » et infobulle, si la place manque ; il disparaît sous 1 250 px.
- **Caisse : F7** remet le curseur dans « Scanner ou rechercher un produit » (repris de l'exemple Pharma) ; F12 encaisse.

## Retour à l'écran précédent

Partout dans le poste, **« ← Retour »** ramène à l'écran précédent **tel qu'on l'a laissé** : onglet, filtres, page, élément
ouvert et fiche contact. Trois façons de revenir :

- le bouton « ← *écran* » en haut à gauche, sur la ligne du menu (après le logo), estompé quand il n'y a pas d'écran précédent ;
- **Alt + ←** au clavier ;
- le bouton **précédent** de la souris.

Le retour du navigateur intégré n'est jamais utilisé : Alt + ← et le bouton de la souris passent par l'historique du poste.

Code : `src/Pharma.Desktop/Services/HistoriqueNavigation.cs` (historique), `Composants/Mise/MiseEnPage.razor` (bouton),
`wwwroot/js/poste.js` (`pharma.retour` : clavier et souris).

## Fonctionnement

- À chaque changement d'écran, l'écran quitté est empilé avec son adresse et son état (50 écrans au plus, les plus anciens
  sont oubliés).
- L'état d'un écran est ce qu'il déclare à l'historique : `Historique.Suivre("clé", e => …)` dit quoi retenir en partant,
  `Historique.Restaurer("clé")` le relit à l'arrivée, avant le premier chargement. Une arrivée normale (menu, lien) part de zéro.
- **Tout tableau paginé retient ses filtres et sa page** ; un test d'architecture (`RetourEcranTests`) le vérifie pour
  chaque composant qui affiche une pagination, sauf les écrans jamais rouverts (restauration, reprise), listés dans le test
  et vérifiés contre `HistoriqueNavigation.JamaisRouverts`.
- **Une page retrouvée qui n'existe plus** (la liste a raccourci entre-temps) est ramenée à la dernière page par le pied de
  liste (`Pagination`), comme après une ligne traitée ou un filtre.
- **Un état retrouvé est lu une seule fois** : un composant rouvert ensuite sur le même écran (ex. une nouvelle facture) repart à vide.
- **Saisies gardées pour la session** (Retours avant « Préparer », facture en préparation) : retrouvées par « Retour » tant que l'employé reste connecté ; la Caisse et les Réceptions, elles, sont mises en attente sur le serveur.
- **Les onglets et rubriques sont dans l'adresse** (`achats?onglet=…`, `factures?suivi=…`, `dettes?onglet=…`, `surveillance?onglet=traitees`, `tresorerie?rubrique=…`, `financements?rubrique=…&pret=…`, `?rubrique=…` pour les Alertes, `stock?rubrique=…`, `stock/registre?rubrique=balance`, `inventaire?rubrique=…`) :
  changer d'onglet est un changement d'écran, et « Retour » revient à l'onglet précédent.
- **Une fiche contact ouverte est rouverte au retour**. Un raccourci de la fiche (Factures, Relevé de dette…) laisse la fiche
  ouverte jusqu'au changement d'écran pour que l'historique la retienne.
- Un écran peut agir avant d'être quitté (`Historique.AvantDeQuitter`) ; s'il répond « non », on reste sur l'écran.

## Navigations avec retour

| D'où | Vers | Ce que « Retour » retrouve |
|---|---|---|
| Fiche patient | Relevé de dette (Caisse), Factures du patient | l'écran d'origine, fiche rouverte |
| Fiche grossiste | Payer un grossiste et relevé, Bons de commande | idem |
| Fiche organisme ou client | Factures | idem |
| Fiche employé | Journal des actions, Utilisateurs et droits | idem |
| Menu principal (11 onglets), menu Administration (11 écrans) | tout écran | écran précédent avec son état |
| Alertes | Seuils, Achats (règlements, commandes) | Alertes avec son filtre |
| Stock | Traçabilité, Registre, Étiquettes, Inventaire | Stock : recherche, filtre, rayon |
| Traçabilité | Stock, Étiquettes, retour fournisseur d'un lot rappelé | Traçabilité |
| Inventaire, Registre | Stock | l'écran d'origine |
| Dettes et créances | « Payer » / « Encaisser » (Caisse, Trésorerie › Saisir un mouvement, ou Achats › Payer un grossiste) | Dettes : rubrique du tiers, filtres et page de la liste |
| Achats › Commandes | « Réceptionner » (Achats › Réceptions) | Commandes : filtres, page, bon ouvert |
| Caisse | Clôture, Relevé | Caisse (vente reprise, partie « Encaisser une dette » rouverte) |
| Contrôle interne | Pertes et vigilance | Contrôle interne (rubrique) |
| Administration › Caisse | Imprimantes | Administration › Caisse (filtres et page du journal) |
| Barre d'état (en bas) | Caisse ou Clôture, Sauvegardes, Administration › Synchronisation (élément « Central : … », droit admin.parametres), Administration › 2e serveur (élément « 2e serveur : … », titulaire) | écran précédent |
| Nom d'un tiers cliquable (17 écrans) | sa fiche (fenêtre) | Échap ou « Fermer » referme la fiche |

### État retrouvé, écran par écran

| Écran | Clé | Retenu |
|---|---|---|
| Caisse | `caisse` | partie dans l'adresse (vente, `?rubrique=annulation`, `?rubrique=credit`) ; passer d'une partie à l'autre garde le panier ; vente mise en attente en quittant la Caisse (reprise au retour) |
| Caisse › Annulation ou retour | `annulation` | page de la liste des ventes de la caisse |
| Caisse › Encaisser une dette | `credit` | patient ouvert, recherche, page des patients en crédit, filtres et page du relevé |
| Stock | `stock` | rubrique dans l'adresse (`produits`, `ensemble`, `nouveau`, `modifier`, `sortie`, `blocage`) ; recherche, filtre, rayon |
| Alertes | `alertes` | rubrique dans l'adresse (vue d'ensemble, `toutes`, `urgent`, `peremption`, `stock`) ; page |
| Achats › Commandes | `commandes` | partie dans l'adresse (`&vue=a-commander`, `&vue=bons`) ; bons : grossiste, statut, période, page, bon ouvert ; produits à commander : produit, état, dernier grossiste, page |
| Achats › Réceptions | `receptions` | partie dans l'adresse (`&vue=saisie`, `attente`, `historique`) ; filtres et page des listes (en attente : grossiste ou n° de BL, mode, employé) ; la réception en cours de saisie, mise en attente en partant, est reprise |
| Achats › Grossistes | `grossistes` | recherche (nom, téléphone, e-mail), état, dette, e-mail manquant, page |
| Achats › Payer un grossiste | `reglements` | grossiste ouvert, filtres et page de son relevé |
| Achats › Traites à payer | `traites` | recherche (grossiste ou n° de traite), état (échues et à 7 jours par défaut, échues, à venir, payées, toutes), page ; liste filtrée et paginée par le serveur ; paiement confirmé par le titulaire (depuis Dettes et créances, rubrique Traites à payer : « Payer » y mène) |
| Achats › Retours | `retours` | grossiste, statut, période, page ; retour en préparation (lots, quantités, boîtes scannées, motif, avant « Préparer le retour ») |
| Rapports | `rapports` | rubrique dans l'adresse (`synthese`, `ventes`, `clotures`, `retours`, `pertes`, `pertes-detail`, `tva`) ; période, date, tableau par jour |
| Administration | (adresse) | sous-rubrique dans l'adresse (`admin/seuils?rubrique=exceptions`, `admin/notifications?rubrique=fiche`…) ; voir `NavAdmin`. Lot M7 (2026-11-16) : deux écrans de plus pour le titulaire, **Réglages fiscaux** (`admin/reglage-fiscal`, sous Contrôle interne) et **Traçabilité** (`admin/reglage-tracabilite`, sous Sérialisation DataMatrix), et une sous-rubrique **Synchronisation › Réglages mutualisés** (`admin/synchronisation?rubrique=reglages-mutualises`, état des 11 tables, un lien vers l'écran de chaque réglage) |
| Inventaire | `inventaire` | rubrique dans l'adresse ; écarts (produit, rayon, page), historique (rayon, état, période, page) |
| Administration › Sauvegardes | `sauvegardes` | journal : résultat, période, page |
| Administration › Alertes et seuils | `seuils`, `seuils-produits` | grossistes (nom, délai à renseigner, page) ; seuils saisis (produit, état, page) |
| Administration › Nomenclature | `nomenclature` | produits à vérifier : texte, motif, page |
| Stock › Blocage des produits | `blocages` | produit ou motif, achat ou vente, page |
| Stock › Remises automatiques | `remises-auto` | texte, état (en cours et à venir par défaut), page, taille |
| Contrôle interne | `surveillance` | rubrique dans l'adresse (à examiner, `?onglet=traitees`, `?onglet=sensibles`) ; page des anomalies |
| Guide et retours | `guide` | rubrique dans l'adresse (à essayer, `?rubrique=remarque`, `?rubrique=remarques`) ; écran, genre et page des remarques |
| Factures | `factures`, `editeur-facture` | recherche, destinataire, période, page, facture ouverte (et sa version), éditeur ouvert et sa saisie (destinataire, ventes cochées, lignes libres, dates, commentaire) ; onglet dans l’adresse |
| Registre des psychotropes | `registre` | rubrique dans l'adresse (inscriptions, `?rubrique=balance`) ; produit, période, opération, page des inscriptions, page de la balance |
| Achats › Étiquettes internes | `etiquettes` | recherche (produit ou lot), péremption jusqu'au, page |
| Trésorerie | `tresorerie` | rubrique dans l'adresse (`comptes`, `mouvements`, `saisie`, `caisses`, `cheques`) ; compte affiché, période, employé, page, type de mouvement en saisie |
| Dettes et créances | `dettes` | rubrique dans l'adresse (`dettes` : vue d'ensemble ; `?onglet=alertes`, `patient`, `facture`, `bordereau`, `grossiste`, `effet`, `pret` et `titulaire` pour le titulaire seul, `tout`) ; filtres de la liste (tiers, état, période) et page |
| Financements (titulaire seul) | `financements`, `fiche-pret`, `editeur-echeancier` | rubrique dans l'adresse (`prets`, `echeances`, `titulaire`) et prêt ouvert (`&pret=…`) ; prêts : texte, état (en cours par défaut, remboursés, tous), banque, page ; échéances : prêt ou banque, état (échues et à 7 jours par défaut, échues, à venir, payées, toutes), page ; compte du titulaire : mouvement, période, page ; fiche du prêt : vue (échéancier, usages…), filtre et page de l'échéancier et des usages ; saisie de l'échéancier : « lignes incohérentes seulement », page. La saisie en cours d'un échéancier n'est pas retenue (à enregistrer avant de quitter) |
| Contrôle interne › Pertes et vigilance | `risques` | tableau dans l'adresse (par employé, `?rubrique=jours`) ; période, pages des deux tableaux |
| Administration › Caisse | `journal` | employé, catégorie, période, page du journal |
| Administration › Notifications | `notifications` | objet, statut, période, page de la file d'attente |
| Patients › Fichier des patients | `patients` | texte (nom, prénom, n° d'assuré, téléphone), organisme, chronique, crédit, droits Chifa, accord SMS, page, taille ; liste filtrée et paginée par le serveur |
| Patients › Droits expirés ou bientôt expirés | `patients/droits-chifa` | état, organisme, période, page, taille |
| Patients › Patients en attente | `patients-attente` | état des demandes, recherche (patient ou produit), page, taille (pas en fenêtre de la Caisse) |

## La Caisse

Quitter la Caisse avec un panier rempli **met la vente en attente automatiquement** (même mécanisme que « ⏸ Mettre en
attente ») : rien n'est perdu, rien n'est signalé « retiré du panier ». « Retour » sur la Caisse reprend cette vente ; si un
autre poste l'a reprise entre-temps, un message le dit. Si la mise en attente échoue (20 ventes déjà en attente, serveur
injoignable) ou pendant un encaissement, on reste sur la Caisse avec le message.

**« ⇄ Changer » (changer d'utilisateur) avec un panier rempli** met aussi la vente en attente ; le prochain employé connecté
sur ce poste arrive directement à la Caisse et la reprend. Les autres tâches longues (facture, retour fournisseur, réception en cours)
sont proposées au suivant dans le bandeau « Tâches laissées en cours sur ce poste » ; la relève de caisse est proposée avant de
changer d'utilisateur (`docs/regles-metier.md`). La déconnexion et l'inactivité gardent la règle d'avant : le panier est
abandonné et ses boîtes signalées « retirées ».

## Achats › Réceptions

Même principe : quitter l'onglet Réceptions (autre onglet d'Achats compris), se déconnecter ou changer d'utilisateur avec une
livraison en cours de saisie la **met en attente sur le serveur** (« Réceptions en attente », visible de tous les postes) ;
« Retour » la reprend. Un comptage à l'aveugle n'est repris que par son auteur ou par qui décide des écarts. Détail :
`docs/regles-metier.md`, « Réceptions en attente ».

## Navigations sans retour (volontaire)

| Cas | Pourquoi |
|---|---|
| Connexion, déconnexion, « Changer d'utilisateur », déconnexion par inactivité | l'historique est vidé : jamais l'écran d'un autre employé |
| Premier changement de mot de passe obligatoire | le retour contournerait l'obligation |
| Écrans du super administrateur (restauration, reprise) | accès sous double validation, jamais rouverts par l'historique |
| Après une validation (vente encaissée, réception validée, clôture faite, restauration lancée) | le retour ramène à l'écran, jamais à l'action déjà validée |
| Fenêtres d'autorisation sur place et de validation du super administrateur | fermées, elles ne se rouvrent pas |
