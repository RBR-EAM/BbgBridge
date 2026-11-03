# BbgBridge

Pont minimal entre le Terminal Bloomberg ouvert sur un PC et une webapp interne affichée dans le navigateur de ce même PC.

```
navigateur (https://app.interne) ── ws://127.0.0.1:8765/ws ──► BbgBridge.exe ── localhost:8194 ──► Terminal Bloomberg
```

| Fichier | Rôle |
|---|---|
| `Program.cs` | Serveur WebSocket, contrôles de sécurité, cache, budget |
| `Protocol.cs` | Messages échangés avec le navigateur et leurs limites |
| `BloombergRequests.cs` | Requêtes BDP / BDS / BDH (`//blp/refdata`) |
| `BloombergSubscriptions.cs` | Temps réel (`//blp/mktdata`) |
| `BloombergJson.cs` | Conversion des messages Bloomberg en JSON |
| `FakeBloomberg.cs` | Mode simulé, disponible seulement en Debug |
| `appsettings.json` | Port, origines autorisées, cache, budget |
| `nuget.config` | Sources NuGet : le flux Bloomberg ne sert que pour `Bloomberg.*` |

## SDK Bloomberg

Le projet utilise `Bloomberg.Blpapi` **3.26.9.1**, épinglé dans `BbgBridge.csproj` et téléchargé depuis le flux NuGet officiel de Bloomberg. C'est la version classique, 100 % managée (une seule DLL, sans bibliothèque native). Elle dépend de `System.Security.Cryptography.Pkcs`, venu de nuget.org.

Le flux Bloomberg utilise deux domaines. Un poste ou un serveur de build derrière un proxy doit autoriser :

- `nuget.blpapi.bloomberg.com` (index et recherche) ;
- `blpapi.bloomberg.com` (téléchargement des paquets).

Pour passer à une version plus récente, changer le numéro dans le `.csproj`, puis recompiler et retester.

## Installation

1. Renseigner `AllowedOrigins` dans `appsettings.json` avec l'origine exacte de la webapp, en minuscules et sans `/` final (par exemple `"https://app.mon-entreprise.internal"`). Le pont refuse de démarrer si la liste est vide.
2. Publier :
   ```
   dotnet publish -c Release
   ```
   Le dossier `bin/Release/net10.0/win-x64/publish/` contient `BbgBridge.exe`, `appsettings.json` et `aspnetcorev2_inprocess.dll`. Ce dernier est le module IIS livré avec .NET ; il n'est pas utilisé. Déployer le dossier tel quel.
3. **Signature de code** : l'IT signe `BbgBridge.exe` à cette étape.
4. Lancer le pont **sous le compte de l'utilisateur**, à l'ouverture de session (tâche planifiée « À l'ouverture de session » ou dossier Démarrage). Ne pas en faire un service Windows : un service tourne dans une autre session que le Terminal.

Si le port est déjà pris, par exemple par un pont déjà lancé, le pont refuse de démarrer. Si le Terminal n'est pas connecté, chaque requête répond une erreur au bout de quelques secondes, et le pont se reconnecte tout seul dès que le Terminal revient.

## Configuration

| Clé | Défaut | Rôle |
|---|---|---|
| `Port` | `8765` | Port d'écoute, toujours sur `127.0.0.1` |
| `AllowedOrigins` | vide | Origines autorisées, en correspondance exacte |
| `CacheSeconds` | `3600` | Durée maximale pendant laquelle une requête identique est servie depuis la mémoire, sans appeler Bloomberg |
| `MaxDataPointsPerDay` | `250000` | Budget de points de données (titres × champs) par jour. **Valeur provisoire, à fixer avec Bloomberg.** |

## Protocole

Chaque requête reçoit une réponse avec le même `id` : soit `{"id", "result"}`, soit `{"id", "error"}`. Les réponses BDP/BDS/BDH contiennent aussi `asOf`, l'heure UTC à laquelle Bloomberg a réellement été interrogé.

Données de référence (BDP) :
```json
{ "id": "1", "type": "ReferenceDataRequest", "securities": ["AAPL US Equity"], "fields": ["PX_LAST", "BEST_EPS"], "overrides": { "BEST_FPERIOD_OVERRIDE": "1BF" } }
```

Données en masse (BDS) : c'est la même requête, avec un champ bulk.
```json
{ "id": "2", "type": "ReferenceDataRequest", "securities": ["AAPL US Equity"], "fields": ["DVD_HIST_ALL"] }
```

Historique (BDH) : dates au format `yyyyMMdd`, `periodicity` facultative (`DAILY` par défaut). Une requête est limitée à 1 million de valeurs, estimées par titres × champs × périodes (261 par an en `DAILY`, 52 en `WEEKLY`, 12 en `MONTHLY`…). Par exemple, 1000 titres × 1 champ × 3 ans en quotidien passe ; sur 4 ans, il faut découper la requête.
```json
{ "id": "3", "type": "HistoricalDataRequest", "securities": ["AAPL US Equity"], "fields": ["PX_LAST"], "startDate": "20250101", "endDate": "20251231", "periodicity": "WEEKLY" }
```

Réponse : `result` contient les messages Bloomberg tels quels, convertis en JSON.
```json
{ "id": "1", "asOf": "2026-11-03T14:31:05.123Z", "result": [ { "securityData": [ { "security": "AAPL US Equity", "fieldData": { "PX_LAST": 123.45 }, "fieldExceptions": [] } ] } ] }
```

`maxAgeSeconds`, facultatif sur BDP/BDS/BDH, fixe l'âge maximal accepté pour une réponse en cache. Au-delà, le pont interroge Bloomberg. `0` force un appel à Bloomberg ; sans `maxAgeSeconds`, la limite est `CacheSeconds`.
```json
{ "id": "4", "type": "ReferenceDataRequest", "securities": ["AAPL US Equity"], "fields": ["PX_LAST"], "maxAgeSeconds": 30 }
```

Temps réel : les champs temps réel ont leurs propres noms (`LAST_PRICE`, `BID`, `ASK`…). Bloomberg envoie au plus une mise à jour par seconde et par titre.
```json
{ "id": "s1", "type": "Subscribe", "securities": ["AAPL US Equity"], "fields": ["LAST_PRICE", "BID", "ASK"] }
{ "id": "s1", "type": "Unsubscribe" }
```

Après `Subscribe`, le pont pousse des messages portant le même `id` :
```json
{ "id": "s1", "security": "AAPL US Equity", "event": "MarketDataEvents", "data": { "LAST_PRICE": 123.45, "BID": 123.44 } }
```

`event` vaut `MarketDataEvents` pour une mise à jour. Les autres valeurs sont des statuts : `SubscriptionStarted`, `SubscriptionFailure`, `SubscriptionTerminated`, et `SessionTerminated` quand le Terminal se déconnecte. Pour relancer un abonnement, envoyer `Unsubscribe` puis `Subscribe`. Les messages poussés peuvent arriver avant la réponse `subscribed` : il faut les traiter par leur `id`. Les abonnements d'un onglet s'arrêtent quand il se ferme.

Les valeurs des overrides s'écrivent toujours en chaînes de caractères. Un titre ou un champ invalide ne fait pas échouer la requête : Bloomberg le signale dans `securityError` ou `fieldExceptions`.

Pour tester, ouvrir la console du navigateur sur une page de l'origine autorisée :
```js
const ws = new WebSocket("ws://127.0.0.1:8765/ws");
ws.onmessage = e => console.log(JSON.parse(e.data));
ws.onopen = () => ws.send(JSON.stringify({ id: "1", type: "ReferenceDataRequest", securities: ["IBM US Equity"], fields: ["PX_LAST"] }));
```

## Cache et budget

- **Cache.** Une requête BDP/BDS/BDH identique (mêmes titres, champs, overrides et dates) est servie depuis la mémoire du pont, jusqu'à `CacheSeconds` (1 h par défaut) ou jusqu'au `maxAgeSeconds` de la requête. Elle ne coûte rien sur le quota Bloomberg, donc l'utilisateur peut rafraîchir autant qu'il veut. Le cache vit uniquement en mémoire, sur le PC de l'utilisateur ; il n'est jamais écrit sur disque et disparaît à l'arrêt du pont. Sa taille totale est plafonnée à 200 Mo de JSON ; une réponse qui ne tient pas sous ce plafond n'est pas mise en cache.
- **Fraîcheur.** Une valeur intraday comme `PX_LAST` reste la même tant qu'elle est servie par le cache. `asOf` permet d'afficher l'heure de la donnée, et `maxAgeSeconds` permet de demander une valeur plus récente. On peut aussi suivre un prix avec `Subscribe`.
- **Budget.** Chaque appel réel à Bloomberg consomme titres × champs points du budget journalier du pont. Un `Subscribe` en consomme autant à l'ouverture. Une fois le budget épuisé, le pont répond une erreur au lieu d'appeler Bloomberg, et l'accès API du Terminal reste protégé. Ce budget est une protection propre au pont : il ne reproduit pas la façon dont Bloomberg compte l'usage.

## Mode simulé (développement)

```
dotnet run -- --mock
```

Le pont répond avec des données fictives sans Terminal : BDP, BDH, et des mises à jour temps réel chaque seconde. Les valeurs sont stables pour une date donnée, et tous les champs sont des nombres. En mode simulé, `AllowedOrigins` accepte aussi `http://` (par exemple `http://localhost:5173` pour un serveur de dev).

Ce mode n'existe que dans les builds **Debug**. L'exe publié en Release ignore `--mock` : il ne peut pas servir de fausses données à un gérant.

## Sécurité

Ce que garantit le pont :

- **Écoute locale uniquement.** Le pont écoute sur `127.0.0.1`, en dur dans le code. Toute connexion qui ne vient pas de la machine est refusée, même si la configuration est modifiée.
- **Origine.** Le header `Origin` doit correspondre exactement à une entrée de `AllowedOrigins`, sinon la réponse est 403, y compris quand le header est absent. Un site quelconque ouvert par l'utilisateur ne peut donc pas utiliser le pont.
- **Host.** Le header `Host` doit valoir `127.0.0.1:<port>`, ce qui bloque le DNS rebinding.
- **Protocole fermé.** Quatre types de messages, toute propriété JSON inconnue rejetée. Les noms de champs ne contiennent que lettres, chiffres et `_`. Un titre temps réel ne peut ni changer de service (`//`) ni ajouter d'options (`?`).
- **Limites.** Messages de 64 Ko, 1000 titres et 25 champs par requête, 1000 titres en temps réel au total, 10 ans d'historique et 1 million de valeurs par BDH. Une seule requête Bloomberg à la fois, et un budget journalier de points de données.
- **Rien ne sort et rien n'est écrit.** Le pont ne parle qu'au Terminal et au navigateur local, n'écrit rien sur disque et ne journalise aucune valeur Bloomberg. Il n'a aucune dépendance en dehors de .NET et du SDK Bloomberg.

Ce que doit garantir la webapp :

- Elle est servie en HTTPS et se connecte à `ws://127.0.0.1:8765/ws`, littéralement (pas `localhost`).
- Elle n'envoie **jamais** de données Bloomberg à un serveur. C'est une condition de la licence de l'API Desktop.
- Elle ne stocke pas les données Bloomberg dans `localStorage` ou `IndexedDB`, qui écrivent sur disque. Un cache en mémoire JavaScript ne pose pas de problème.
- Elle applique une CSP stricte, par exemple `default-src 'self'; connect-src 'self' ws://127.0.0.1:8765`. En cas de faille XSS, du JavaScript injecté ne pourra pas envoyer les données ailleurs.
- Chrome et Edge affichent une demande d'autorisation « accès au réseau local » à la première connexion. L'IT peut la pré-autoriser pour l'origine de la webapp par stratégie de groupe (stratégie `LocalNetworkAccessAllowedForUrls`, à vérifier dans la documentation Chrome/Edge en vigueur).

Limites connues :

- Un programme local peut se connecter au pont en falsifiant `Origin`. Il pourrait de toute façon interroger directement le Terminal sur le port 8194 : le pont n'ajoute pas d'exposition.
- Ne pas installer le pont sur une machine partagée entre plusieurs utilisateurs (RDS, Citrix) : les autres sessions verraient le même `127.0.0.1`.
