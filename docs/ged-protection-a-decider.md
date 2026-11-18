# GED : protection des documents, sujet à reprendre

Note du 2026-11-17. **Recommandation validée par le titulaire le 2026-11-18 (« ok recommandation GED »)** : voir § 5 ; le code (alerte, disques en rotation) reste à lancer sur « go ».

La GED regroupe les documents de l'officine : ordonnances scannées, fichiers Chifa, factures, pièces des prêts, copies des
sauvegardes (dossier `Ged__Dossier`, par défaut `C:\ProgramData\PharmaOfficine\GED`). Ce sont des données de santé.

## 1. Ce qui existe

| Protection | État |
|---|---|
| Copie sur le disque externe par la sauvegarde de l'application (documents nouveaux et modifiés, jamais de suppression) | En place |
| Restauration de la GED par le super admin (rien supprimé, document plus récent jamais écrasé) | En place |
| PC de secours : GED recopiée depuis le disque externe (`restaurer_pharmacie.ps1 -SansBase`, procédure F2) | En place |
| Copie automatique toutes les 5 minutes du serveur principal vers le 2e serveur, sans suppression (`scripts/second-serveur/copier-ged.ps1`, fréquence réglable, script 143) | En place (2026-11-17) |
| Serveur central | La GED n'y est **jamais** envoyée (décision n° 2 du titulaire, 2026-11-16, `conception-synchronisation.md`) |

Le 2e serveur est **facultatif** : une officine à un seul serveur n'a que le disque externe.

## 2. Le risque

Incendie, vol ou dégât des eaux qui touche en même temps le PC serveur (et le 2e serveur s'il est dans la même pièce) et le disque
externe : toutes les ordonnances scannées sont perdues. C'est le risque principal d'une officine à un seul serveur.

## 3. Options étudiées

| | A. GED consultable au central | B. Sauvegarde chiffrée hors de l'officine |
|---|---|---|
| Principe | Documents lisibles au central (consultation à distance) | Copie illisible au central ; seule l'officine détient la clé ; sert uniquement à restaurer |
| Utilité | Consultation à distance, support | Protège contre la perte totale |
| Données de santé au central | Oui, en clair | Non lisibles |
| Contraintes | Hébergeur agréé ARPCE, ANPDP et autorisation pour données de santé, sécurité lourde | Hébergeur agréé, chiffrement (lot 9), volume et débit Internet à mesurer ; clé à ouverture à deux ([chiffrement-data.md](chiffrement-data.md)) : la sauvegarde n'est plus perdue si l'officine perd sa clé |
| Avis proposé | **Déconseillé** : aucun usage identifié au central (consultation possible dans l'officine ou par le VPN prévu), données les plus sensibles, central exposé | **Recommandé plus tard**, prioritaire pour les officines à un seul serveur |

Autres options écartées pour la copie entre serveurs de l'officine : écriture en double par l'API (complexe), GED sur un NAS partagé
(un seul point de panne).

## 4. Recommandation proposée (à valider par le titulaire)

1. **Maintenant** : GED dans l'officine ; copie toutes les 5 minutes vers le 2e serveur quand il existe ; **deux disques externes en
   rotation, dont un toujours hors de l'officine**, rendus obligatoires dans `mise-en-production.md` pour une officine sans 2e serveur.
2. **Option** : alerte au titulaire si aucune sauvegarde réussie sur disque externe depuis 7 jours (réglable), par le mécanisme
   d'alertes existant. Aujourd'hui la barre d'état affiche « Sauvegarde automatique désactivée » sans alerter.
3. **Plus tard** (après le choix de l'hébergeur agréé et le lot 9, chiffrement) : sauvegarde chiffrée de la GED vers le central,
   illisible par lui, pour restauration seulement ; prioritaire pour les officines à un seul serveur.
4. **Jamais** de GED consultable en clair au central (sauf révision explicite de la décision n° 2).

## 5. Décisions attendues

- [x] Recommandation validée (2026-11-18, « ok recommandation GED ») ; décision n° 2 maintenue : jamais de GED lisible au central.
- [x] Rotation de deux disques, dont un toujours hors de l'officine : **obligatoire** pour une officine sans 2e serveur (à écrire dans
      `mise-en-production.md` avec le lot de code).
- [x] Alerte au titulaire si aucune sauvegarde réussie sur disque externe depuis **7 jours** (réglable) : **oui** ; code à lancer sur « go ».
- [x] Sauvegarde chiffrée de la GED hors de l'officine (option B) : **plus tard**, avec le lot 9 et l'hébergeur, même clé à ouverture à
      deux que `chiffrement-data.md`.
- [ ] À vérifier : obligations de conservation des ordonnances et documents (durées, support), avis ANPDP pour une copie hors de
      l'officine même chiffrée.
