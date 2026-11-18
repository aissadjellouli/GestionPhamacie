# Gestion des grossistes — stratégie pour le logiciel PharmaOfficine

Nov 18, 2026 · @Aissa Djellouli Hadj

**Oui pour relier les grossistes au logiciel, non pour un logiciel de gestion complet des grossistes à ce stade.** Le plus rentable est d'avancer en trois étapes : les connecter, leur ouvrir un portail gratuit, puis leur vendre des services une fois le réseau constitué.

## Pourquoi ne pas tout faire dès le départ

Un logiciel complet pour grossistes serait un second produit, avec sa propre équipe.

- **C'est un autre métier.** Un grossiste a besoin d'un logiciel d'entreprise complet : entrepôt, préparation des commandes, tournées de livraison, crédit de centaines de pharmacies, quotas, obligations d'un établissement pharmaceutique (pharmacien directeur technique, bonnes pratiques de distribution, traçabilité des lots, psychotropes).
- **La vente est plus lente.** Peu de clients, de gros contrats, des décisions longues, souvent un système déjà en place (Sage, Odoo, logiciel maison).
- **Vous perdriez en concentration.** La force du projet est d'être prêt pour 2027 côté pharmacie. Si logiciel A ou un autre acteur arrive, c'est sur ce terrain qu'il faudra déjà être installé.

## Recommandation : trois étapes

On relie d'abord, on crée le réseau ensuite, on vend des services seulement quand le réseau est assez grand.

1. **Les connecter, sans rien leur vendre (dans P1).**
   - La pharmacie importe les catalogues et tarifs des grossistes : fichiers Excel ou CSV, emails, et API quand elles existent (comme l'application de Vecopharm).
   - Le logiciel envoie les bons de commande au format de chaque grossiste.
   - Les grossistes n'ont rien à installer.
2. **Un portail web gratuit pour les grossistes (après les premières pharmacies).**
   - Chaque grossiste publie son catalogue, ses stocks, ses prix et ses promotions, reçoit les commandes des pharmacies du réseau et met à jour leur suivi : acceptée, partielle, livrée, produits manquants.
   - C'est ce qui crée l'effet réseau : plus il y a de pharmacies, plus les grossistes veulent y être, et inversement. C'est le modèle de logiciel A et de Blink Pharma au Maroc.
   - Les données sur les ruptures deviennent précises et en temps réel, ce qui renforce le module ruptures (M3).
3. **Gagner de l'argent avec les grossistes (quand le réseau est assez grand).**
   - Abonnement pour des options avancées : statistiques de leurs ventes par région, mise en avant des promotions, API de connexion à leur propre logiciel.
   - Éventuellement, plus tard, un logiciel de gestion complet pour les petits grossistes qui n'ont rien, si la demande se confirme.

## Points de vigilance

| Sujet | Risque | Règle à suivre |
| --- | --- | --- |
| Neutralité | Si les grossistes paient et que le classement est biaisé, les pharmaciens partent | Ne jamais trafiquer le classement par prix ou délai ; signaler clairement tout contenu sponsorisé |
| Réglementation | Les prix et marges des médicaments sont encadrés ; une commission sur les ventes de médicaments remboursés pourrait poser problème | Préférer des abonnements à des pourcentages ; faire vérifier par un juriste |
| Traçabilité | Les grossistes auront aussi des obligations de sérialisation DataMatrix | Plus tard, le portail peut transmettre les numéros de série dès l'expédition : réception plus simple en pharmacie, et une raison de rejoindre le réseau |
| Données | Un grossiste ne doit jamais voir les ventes d'une pharmacie au patient | Cloisonnement strict dans le code : le grossiste ne voit que les commandes qui lui sont passées |

## En résumé

D'abord **relier** les grossistes (import des catalogues, envoi des commandes). Ensuite, un **portail gratuit** pour créer le réseau. Seulement après, leur **vendre** des services. Pour le développement, le portail grossiste peut devenir un module M7 en P3 : le modèle de données prévoit les grossistes dès maintenant, sans développer le portail tout de suite.
