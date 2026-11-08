# Sécurité et droits

## Comptes

- Un compte par employé, jamais de compte partagé. Mot de passe haché en bcrypt ; code court (PIN) prévu pour la caisse.
- Connexion : `POST /api/auth/connexion` renvoie un jeton JWT (12 h par défaut).
- La clé `Jwt:Cle` de `appsettings.json` est une valeur de développement : la remplacer en production par une clé aléatoire de 64 caractères au moins, propre à chaque pharmacie.

## Droits

- 25 permissions (table `permission`, constantes `Pharma.Domain.Securite.Permissions`).
- Rôles de départ : titulaire (tout, non modifiable), pharmacien assistant, vendeur, stagiaire. Le titulaire ajuste les droits depuis l’administration.
- Chaque point d’accès déclare sa permission : `.ExigerPermission(Permissions.StockConsulter)`. La vérification se fait en base à chaque appel, donc un droit retiré s’applique immédiatement.
- Action sensible non autorisée : le poste demande le code du titulaire ; le journal enregistre qui a agi et qui a autorisé (à développer côté API).

## Garanties en base

- Le titulaire garde tous ses droits, et il reste toujours au moins un titulaire actif.
- `journal_audit`, `registre_psychotrope` et `boite_evenement` ne peuvent être ni modifiés ni supprimés.
- PostgreSQL n’accepte que les connexions locales de l’API (à régler dans `pg_hba.conf` à l’installation).
