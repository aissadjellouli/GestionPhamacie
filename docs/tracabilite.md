# Traçabilité des boîtes

Objectif : retrouver pour chaque boîte son grossiste, sa réception, son lot, sa vente et le patient, et pouvoir rappeler un lot en quelques minutes.

## Identité d’une boîte

1. **Data Matrix GS1** quand la boîte en porte un : GTIN (01), péremption (17), lot (10), numéro de série (21). Lecture : `Gs1DataMatrix.TryParse`.
2. **Étiquette interne** sinon : EAN-13 unique commençant par 20, une par boîte, créée par `creer_boites_internes(lot, nombre)` et imprimée à la réception.

Une même boîte ne peut entrer qu’une fois en stock et être vendue qu’une fois : un second scan signale un double scan ou une contrefaçon.

## Chaîne

Grossiste → réception (BL) → lot → boîte → ligne de vente → vente → patient. Chaque étape est inscrite dans `boite_evenement`, table non modifiable.

## Mode strict

`parametre_tracabilite.boite_obligatoire` impose de scanner chaque boîte vendue. Il ne doit être activé qu’une fois tout le stock identifié (vue `v_lots_a_etiqueter`), sinon la caisse serait bloquée.

`patient_obligatoire` : `jamais`, `psychotrope`, `ordonnance` (défaut) ou `toujours`. Pour les ventes libres, le patient reste anonyme sauf accord du client (loi 18-07).

## Ventes Chifa

Les ventes en tiers payant se font dans Chifa_Officine. Pour garder le lien boîte ↔ patient, le pharmacien scanne aussi les boîtes dans notre logiciel ; l’import de la facture signée renseigne ensuite `vente.num_facture_chifa`. Geste à valider avec la pharmacie pilote.

## Rappel de lot

`rappel_lot(produit, numéro de lot, motif, utilisateur)` bloque le lot chez tous les grossistes, inscrit le blocage des boîtes en stock et renvoie la liste des patients à contacter, y compris pour les ventes faites avant l’étiquetage par boîte (fonction `ventes_lot`).

Un lot déjà bloqué n’est pas rebloqué (script 051) : le rappel ne bloque que les lots encore vendables de ce numéro (ex. nouvelle livraison chez un autre grossiste), en gardant le motif du premier blocage. Un lot bloqué chez tous les grossistes est refusé au rappel (« déjà rappelé »), sans inscription au journal ni alerte au titulaire ; l’écran Traçabilité l’indique dès la saisie du produit et du lot (date et motif du premier rappel) et propose « Voir les patients concernés », avec l’export de la liste et la préparation du retour fournisseur. Consultation : API `GET /api/tracabilite/rappels?produit=…&lot=…`, même droit que le rappel, sans rien modifier.

Parcours d’une boîte : vue `v_parcours_boite`, API `GET /api/tracabilite/boites/{code}`.

## Sérialisation nationale (vérification, décommissionnement) — 2026-11-16, script 120

Port `ISerialisationConnecteur` (`src/Pharma.Application/Serialisation/Serialisation.cs`) : vérifier une boîte (GTIN, série, lot, péremption), la décommissionner à la vente, la réactiver au retour ou à l’annulation. Réponses : `authentique` (seule acceptée), `inconnue`, `deja_decommissionnee`, `rappelee`, `expiree`.

- **Adaptateur actuel : FACTICE** (`SerialisationFactice`) : réponses déterministes paramétrables dans `serialisation_reponse_factice` (critères GTIN, série, lot, opération ; vide = toute valeur ; la première règle par id l’emporte) ; à défaut « expirée » si la péremption est passée (vérification, vente), sinon « authentique ». `parametre_serialisation.factice_hors_ligne` simule une panne. Chaque réponse est journalisée (journal du serveur) et tracée dans la file. **[À VÉRIFIER : API de la base nationale du MIPH]** : l’adaptateur réel viendra avec la version cloud.
- **File hors ligne** `verification_serialisation` : le déclencheur `trg_boite_serialisation` y inscrit, dans la transaction de l’opération, `verifier` (boîte Data Matrix créée à la réception), `decommissionner` (en_stock → vendue), `reactiver` (vendue → en_stock : retour client remis en stock, annulation). Étiquettes internes (sans GTIN + série) : jamais. **La vente n’attend jamais la base nationale.**
- **Rejeu** : tâche de fond `SerialisationHorsLigne` (API) toutes les minutes, 100 opérations par passage ; base injoignable : l’opération reste « en attente », rejouée après 1, 2, 4… minutes (au plus 30). Réponse « authentique » : réussie ; autre réponse : **refusée**, résultat et message gardés, **alerte urgente au titulaire** (type surveillance : produit, lot, série, motif ; aucune donnée de patient) — la vente déjà faite n’est pas annulée.
- **Écran** Administration › Sérialisation DataMatrix (`/admin/serialisation`, titulaire) : compteurs en attente / réussies / refusées, file paginée filtrée par statut, relance d’une opération (en attente ou refusée) ou de toute la file, réglages « File active » et « Simuler la base nationale hors ligne » (journal des actions : `serialisation_relance`, `serialisation_parametres`).
- **Identifiants GS1 paramétrables** `parametre_gs1_ai` **[À VÉRIFIER IANOR / GS1]** : code (2 à 4 chiffres), donnée (gtin, peremption, lot, serie), longueur fixe ou variable, longueur maximale. Défauts : 01 GTIN (14), 17 péremption (6), 10 lot et 21 série (variables) : lecture identique à l’existant. Chargés par le serveur au démarrage puis chaque minute (norme par défaut si la table est vide ou sans GTIN).