# ADR 0001 – Architecture locale sans cloud

**Statut** : accepté

**Contexte** : connexion internet irrégulière dans de nombreuses officines, données de santé sensibles, pharmaciens attachés à garder leurs données sur place.

**Décision** : tout fonctionne sur le réseau local. Un PC serveur héberge PostgreSQL et une API ASP.NET Core en service Windows ; les postes passent par l’API.

**Conséquences** : pas d’abonnement cloud ; sauvegardes locales indispensables (script quotidien, disques externes) ; support à distance par outil auto-hébergé ; une future version multi-pharmacies demandera une synchronisation.
