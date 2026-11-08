# Fonctionnalités et état d’avancement

Légende : **Base** = prêt dans les scripts SQL · **API** = exposé par l’API · **Écran** = disponible dans le poste client · **Maquette** = dessiné seulement.

| Module | Fonction | État |
| --- | --- | --- |
| Alertes | Péremption à 3 niveaux de seuils, risque d’écoulement, valeur en dinars | Base, API, Écran |
| Alertes | Rupture avec alternatives de même DCI, stock critique, dormant, surstock | Base, API, Écran |
| Alertes | Reporter (date au choix), marquer traitée (action prise, employé) | Base, API, Écran |
| Caisse | Recherche tolérante aux fautes, lot FEFO, blocage du périmé expliqué, alternatives en rupture, panier (sans encaissement) | Base, API, Écran (partiel) |
| Caisse | Panier, encaissement, part CNAS et part patient, crédit client | Domaine, Maquette |
| Contrôle de caisse | Sessions par employé, comptage aveugle, journal des actions sensibles | Base (journal), Maquette |
| Stock | Consultation : indicateurs, lots, péremptions, grossistes, filtres | Base, API, Écran |
| Stock | Inventaire, registre des psychotropes, étiquettes | Base, Maquette |
| Traçabilité | Identité par boîte, historique (frise), avancement de l’étiquetage, rappel de lot (blocage, patients à contacter, export CSV, journal) | Base, API, Écran |
| Achats | Commande suggérée, réception, retours au bon grossiste | Base, Maquette |
| Étiquettes | EAN-13 interne par boîte, impression ZPL | Base, Infrastructure, Maquette |
| Chifa | Import des factures signées, bordereaux, rejets | Connecteur à compléter (format inconnu) |
| Rapports | Ventes, top produits, marges, écarts de caisse | Maquette |
| Administration | Seuils, droits, caisse, imprimantes, sauvegardes, reprise, langue | Base (seuils, droits, reprise), Maquette |
| Langues | Arabe (droite à gauche), français, anglais | Maquette |

La liste des prochaines étapes est dans [feuille-de-route.md](feuille-de-route.md).
