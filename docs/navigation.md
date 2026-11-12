# Navigation et retour à l'écran précédent

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
  chaque composant qui affiche une pagination.
- **Un état retrouvé est lu une seule fois** : un composant rouvert ensuite sur le même écran (ex. une nouvelle facture) repart à vide.
- **Saisies gardées pour la session** (Retours avant « Préparer », facture en préparation) : retrouvées par « Retour » tant que l'employé reste connecté ; la Caisse et les Réceptions, elles, sont mises en attente sur le serveur.
- **Les onglets sont dans l'adresse** (`achats?onglet=…`, `factures?suivi=…`, `dettes?onglet=…`, `surveillance?onglet=traitees`) :
  changer d'onglet est un changement d'écran, et « Retour » revient à l'onglet précédent.
- **Une fiche contact ouverte est rouverte au retour**. Un raccourci de la fiche (Factures, Relevé de dette…) laisse la fiche
  ouverte jusqu'au changement d'écran pour que l'historique la retienne.
- Un écran peut agir avant d'être quitté (`Historique.AvantDeQuitter`) ; s'il répond « non », on reste sur l'écran.

## Navigations avec retour

| D'où | Vers | Ce que « Retour » retrouve |
|---|---|---|
| Fiche patient | Relevé de dette (Caisse), Factures du patient | l'écran d'origine, fiche rouverte |
| Fiche grossiste | Règlements et relevé, Bons de commande | idem |
| Fiche organisme ou client | Factures | idem |
| Fiche employé | Journal des actions, Utilisateurs et droits | idem |
| Menu principal (11 onglets), menu Administration (11 écrans) | tout écran | écran précédent avec son état |
| Alertes | Seuils, Achats (règlements, commandes) | Alertes avec son filtre |
| Stock | Traçabilité, Registre, Étiquettes, Inventaire | Stock : recherche, filtre, rayon |
| Traçabilité | Stock, Étiquettes, retour fournisseur d'un lot rappelé | Traçabilité |
| Inventaire, Registre | Stock | l'écran d'origine |
| Dettes et créances | « Payer » / « Encaisser » (Caisse ou Achats › Règlements) | Dettes : onglet du tiers, « En alerte seulement » |
| Achats › Commandes | « Réceptionner » (Achats › Réceptions) | Commandes : filtres, page, bon ouvert |
| Caisse | Clôture, Relevé | Caisse (vente reprise, panneau de crédit rouvert) |
| Contrôle interne | Risques | Contrôle interne (onglet) |
| Administration › Caisse | Imprimantes | Administration › Caisse (filtres et page du journal) |
| Barre d'état (en bas) | Caisse ou Clôture, Sauvegardes | écran précédent |
| Nom d'un tiers cliquable (17 écrans) | sa fiche (fenêtre) | Échap ou « Fermer » referme la fiche |

### État retrouvé, écran par écran

| Écran | Clé | Retenu |
|---|---|---|
| Caisse | `caisse` | vente mise en attente en partant (reprise au retour), panneau « Règlement d'un crédit » ouvert |
| Caisse › Règlement d'un crédit | `credit` | patient ouvert, recherche, filtres et page du relevé |
| Stock | `stock` | recherche, filtre, rayon |
| Alertes | `alertes` | filtre |
| Achats › Commandes | `commandes` | grossiste, statut, période, page, bon ouvert |
| Achats › Réceptions | `receptions` | filtres et page de la liste ; la réception en cours de saisie, mise en attente en partant, est reprise |
| Achats › Règlements | `reglements` | grossiste ouvert, filtres et page de son relevé |
| Achats › Retours | `retours` | grossiste, statut, période, page ; retour en préparation (lots, quantités, boîtes scannées, motif, avant « Préparer le retour ») |
| Rapports | `rapports` | période, date, tableau par jour |
| Factures | `factures`, `editeur-facture` | recherche, destinataire, période, page, facture ouverte (et sa version), éditeur ouvert et sa saisie (destinataire, ventes cochées, lignes libres, dates, commentaire) ; onglet dans l’adresse |
| Trésorerie | `tresorerie` | compte affiché, période, employé, page, type de mouvement en saisie |
| Dettes et créances | `dettes` | « En alerte seulement » ; onglet dans l'adresse |
| Contrôle interne › Risques | `risques` | période, pages des deux tableaux |
| Administration › Caisse | `journal` | employé, catégorie, période, page du journal |
| Administration › Notifications | `notifications` | objet, statut, période, page de la file d'attente |

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
