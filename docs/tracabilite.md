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

`rappel_lot(produit, numéro de lot, motif, utilisateur)` bloque le lot chez tous les grossistes, inscrit le blocage des boîtes en stock et renvoie la liste des patients à contacter, y compris pour les ventes faites avant l’étiquetage par boîte. Parcours d’une boîte : vue `v_parcours_boite`, API `GET /api/tracabilite/boites/{code}`.
