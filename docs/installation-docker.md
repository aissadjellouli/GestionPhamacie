# Installation de Docker sur le poste de développement (Windows 11)

Manuel pas à pas pour installer **Docker Desktop** sur un poste Windows 11 (Famille ou Professionnel), afin de lancer la
démonstration en conteneurs (`deploy/demo/`, voir [demo-cloud.md](demo-cloud.md)). Durée : 15 à 30 minutes, un ou deux
redémarrages. Droits administrateur nécessaires.

Docker Desktop s'appuie sur **WSL 2** (sous-système Windows pour Linux) : les conteneurs Linux tournent dans une petite machine
virtuelle gérée par Windows.

---

## 1. Prérequis

| Élément | Exigence |
| --- | --- |
| Système | Windows 11 64 bits (Famille ou Professionnel), à jour (Paramètres › Windows Update) |
| Processeur | Virtualisation matérielle activée (voir ci-dessous) |
| Mémoire | 8 Go au minimum (Docker Desktop occupe 2 à 4 Go quand il tourne) |
| Disque | 10 Go libres au moins (images .NET et PostgreSQL, volumes de données) |
| Droits | Compte administrateur du poste |

### Vérifier la virtualisation

1. Ouvrir le **Gestionnaire des tâches** (Ctrl + Maj + Échap) › onglet **Performances** › **Processeur**.
2. La ligne **Virtualisation** doit indiquer **Activé**.
3. Si elle indique « Désactivé » : redémarrer dans le BIOS / UEFI (touche F2, F10, Suppr ou Échap au démarrage selon le
   fabricant) et activer l'option **Intel VT-x** (ou « Intel Virtualization Technology ») ou **AMD-V / SVM Mode**, enregistrer,
   redémarrer.

---

## 2. Installer WSL 2

1. Ouvrir un **terminal en administrateur** : clic droit sur le menu Démarrer › **Terminal (administrateur)**.
2. Lancer :

   ```powershell
   wsl --install
   ```

3. **Redémarrer le PC** quand c'est demandé.
4. Au retour, une fenêtre **Ubuntu** peut s'ouvrir et demander un nom d'utilisateur et un mot de passe Linux : les créer (ils
   ne servent qu'à Linux, sans lien avec le compte Windows ni avec l'application).
5. Vérifier :

   ```powershell
   wsl --status
   ```

   La sortie doit indiquer **version par défaut : 2**. Sinon :

   ```powershell
   wsl --set-default-version 2
   ```

---

## 3. Installer Docker Desktop

Dans le terminal administrateur :

```powershell
winget install --id Docker.DockerDesktop -e
```

Sans `winget` : télécharger l'installateur sur le site officiel docker.com, rubrique « Docker Desktop for Windows », et
l'exécuter.

Pendant l'installation :

- laisser cochée **« Use WSL 2 instead of Hyper-V »** ;
- accepter la fermeture de session ou le redémarrage demandés à la fin.

---

## 4. Premier lancement

1. Lancer **Docker Desktop** depuis le menu Démarrer.
2. Accepter les conditions d'utilisation. **Licence** : Docker Desktop est gratuit pour les entreprises de **moins de 250
   salariés et de moins de 10 millions de dollars de chiffre d'affaires annuel** ; au-delà, un abonnement payant est
   obligatoire.
3. La création d'un compte Docker est facultative (« Continue without signing in »).
4. Attendre que l'icône de la baleine (zone de notification, en bas à droite) indique **« Engine running »**.

---

## 5. Vérifier l'installation

Dans un terminal ordinaire (non administrateur) :

```powershell
docker --version
docker compose version
docker run --rm hello-world
```

La dernière commande télécharge une petite image d'essai et doit afficher **« Hello from Docker! »**.

---

## 6. Réglages conseillés

Dans Docker Desktop › **Settings** (roue dentée) :

- **General** › décocher **« Start Docker Desktop when you sign in »** si Docker ne doit pas se lancer à chaque ouverture de
  session (il consomme de la mémoire en permanence) ;
- **Resources** › **WSL integration** : laisser l'intégration par défaut ;
- quitter Docker quand il ne sert pas : clic droit sur la baleine › **Quit Docker Desktop**.

Limiter la mémoire de WSL 2 (facultatif) : créer le fichier `%UserProfile%\.wslconfig` avec :

```ini
[wsl2]
memory=4GB
```

puis, dans un terminal : `wsl --shutdown` et relancer Docker Desktop.

---

## 7. Lancer la démonstration

Démarche complète et liste des points à vérifier : [demo-cloud.md](demo-cloud.md), « Étape 1 ». En résumé, depuis la racine
du dépôt :

```powershell
# Variables : copier le modèle puis remplacer chaque valeur A-REMPLACER (deploy\demo\.env n'est jamais commité)
Copy-Item deploy\demo\.env.example deploy\demo\.env
notepad deploy\demo\.env

# Construction et démarrage
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env up -d --build

# Contrôle
curl.exe http://127.0.0.1:5080/api/healthcheck
```

**Port 5080** : la démonstration est publiée sur le port 5080 du poste (et seulement sur 127.0.0.1), comme l'API lancée depuis
Visual Studio. Ne pas lancer les deux en même temps (« port is already allocated »), ou régler un autre port avec la variable
`PORT_API_DEMO` dans `deploy\demo\.env` (ex. `PORT_API_DEMO=5090`).

Arrêter la démonstration :

```powershell
docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env down
```

(`down -v` supprime aussi les volumes, donc la base de démonstration : elle sera recréée au prochain démarrage.)

---

## 8. Problèmes fréquents

| Symptôme | Cause probable | Que faire |
| --- | --- | --- |
| « WSL 2 installation is incomplete » | Noyau WSL absent ou ancien | `wsl --update` dans un terminal administrateur, puis relancer Docker Desktop |
| « Virtualization support not detected » | Virtualisation désactivée dans le BIOS / UEFI | Voir § 1 |
| « Docker Desktop is starting… » sans fin | Machine virtuelle WSL bloquée | `wsl --shutdown`, puis relancer Docker Desktop ; sinon redémarrer le PC |
| `docker` : commande introuvable | Terminal ouvert avant l'installation | Fermer et rouvrir le terminal (ou fermer la session Windows) |
| « port is already allocated » sur 5080 | API de Visual Studio ou autre programme sur le port | Arrêter l'autre programme, ou régler `PORT_API_DEMO` dans `deploy\demo\.env` |
| Téléchargement des images très lent ou refusé | Réseau, proxy ou pare-feu | Vérifier l'accès à Internet ; derrière un proxy : Settings › Resources › Proxies |
| PC lent quand Docker tourne | Mémoire prise par WSL 2 | Limiter la mémoire (§ 6) ou quitter Docker après usage |

---

## 9. Désinstaller

1. Paramètres › Applications › Applications installées › **Docker Desktop** › Désinstaller.
2. Facultatif : `wsl --unregister docker-desktop` (et `docker-desktop-data` s'il existe) dans un terminal administrateur pour
   libérer l'espace disque des images et volumes.
3. WSL 2 peut rester installé : il ne gêne pas le reste du poste.
