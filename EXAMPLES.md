# Exemples de messages

Structure réelle relevée sur un Terminal Bloomberg (SDK 3.26.9.1, novembre 2026). **Les valeurs sont fictives** : le dépôt ne contient aucune donnée Bloomberg. Le mode simulé (`--mock`) produit la même structure.

## Règles à connaître

- `result` est la liste des messages Bloomberg de la réponse. Une grosse requête peut être découpée en plusieurs messages : il faut alors concaténer leurs `securityData`.
- `sequenceNumber` est la position du titre dans la liste `securities` de la requête, à partir de 0.
- **Un champ sans valeur est absent, il ne vaut pas `null`.** Exemple : le `PX_LAST` du jour, avant l'ouverture.
- Les nombres restent des nombres JSON (NaN et l'infini deviennent `null`). Les dates sont des chaînes `"yyyy-MM-dd"`, les textes sont transmis tels quels.
- Les noms de champs et de sous-champs sont ceux de Bloomberg, espaces compris (`"Declared Date"`).
- `asOf` est l'heure UTC de l'appel réel à Bloomberg. Une réponse servie par le cache garde l'heure de l'appel d'origine.
- **Temps réel : un `MarketDataEvents` ne contient que les champs mis à jour.** Il faut garder la dernière valeur connue de chaque champ. Le message peut aussi contenir des champs qui n'ont pas été demandés : ignorer ceux qui ne servent pas.

## BDP : `ReferenceDataRequest`

```json
{ "id": "bdp", "type": "ReferenceDataRequest", "securities": ["IBM US Equity", "AAPL US Equity"], "fields": ["PX_LAST", "NAME", "CRNCY"] }
```

```json
{
  "id": "bdp",
  "asOf": "2026-11-03T12:49:24.4640615Z",
  "result": [
    {
      "securityData": [
        {
          "security": "IBM US Equity",
          "eidData": [],
          "fieldExceptions": [],
          "fieldData": { "PX_LAST": 123.45, "NAME": "INTL BUSINESS MACHINES CORP", "CRNCY": "USD" },
          "sequenceNumber": 0
        },
        {
          "security": "AAPL US Equity",
          "eidData": [],
          "fieldExceptions": [],
          "fieldData": { "PX_LAST": 234.56, "NAME": "APPLE INC", "CRNCY": "USD" },
          "sequenceNumber": 1
        }
      ]
    }
  ]
}
```

## BDS : `ReferenceDataRequest` avec un champ bulk

La requête est la même que pour un BDP. Le champ bulk est un tableau d'objets.

```json
{ "id": "bds", "type": "ReferenceDataRequest", "securities": ["IBM US Equity"], "fields": ["DVD_HIST_ALL"] }
```

```json
{
  "id": "bds",
  "asOf": "2026-11-03T12:50:04.3035972Z",
  "result": [
    {
      "securityData": [
        {
          "security": "IBM US Equity",
          "eidData": [],
          "fieldExceptions": [],
          "fieldData": {
            "DVD_HIST_ALL": [
              {
                "Declared Date": "2026-10-21",
                "Ex-Date": "2026-11-10",
                "Record Date": "2026-11-10",
                "Payable Date": "2026-12-10",
                "Dividend Amount": 1.23,
                "Dividend Frequency": "Quarter",
                "Dividend Type": "Regular Cash"
              },
              {
                "Declared Date": "2026-07-22",
                "Ex-Date": "2026-08-10",
                "Record Date": "2026-08-10",
                "Payable Date": "2026-09-10",
                "Dividend Amount": 1.23,
                "Dividend Frequency": "Quarter",
                "Dividend Type": "Regular Cash"
              }
            ]
          },
          "sequenceNumber": 0
        }
      ]
    }
  ]
}
```

## BDH : `HistoricalDataRequest`

Un message par titre. `securityData` y est un objet (et non un tableau), et `fieldData` contient une ligne par date. Dans cet exemple, la dernière ligne n'a pas de `PX_LAST`, car la séance du jour n'avait pas encore commencé.

```json
{ "id": "bdh", "type": "HistoricalDataRequest", "securities": ["IBM US Equity"], "fields": ["PX_LAST", "PX_VOLUME"], "startDate": "20261029", "endDate": "20261103" }
```

```json
{
  "id": "bdh",
  "asOf": "2026-11-03T12:50:10.8653669Z",
  "result": [
    {
      "securityData": {
        "security": "IBM US Equity",
        "eidData": [],
        "fieldExceptions": [],
        "fieldData": [
          { "date": "2026-10-29", "PX_LAST": 123.45, "PX_VOLUME": 4000000 },
          { "date": "2026-10-30", "PX_LAST": 124.56, "PX_VOLUME": 5000000 },
          { "date": "2026-11-02", "PX_LAST": 122.34, "PX_VOLUME": 4500000 },
          { "date": "2026-11-03", "PX_VOLUME": 15000 }
        ],
        "sequenceNumber": 0
      }
    }
  ]
}
```

## Temps réel : `Subscribe` et `Unsubscribe`

```json
{ "id": "live", "type": "Subscribe", "securities": ["IBM US Equity", "EURUSD Curncy"], "fields": ["LAST_PRICE", "BID", "ASK"] }
```

Le pont répond, et pousse des messages portant le même `id`. Les messages poussés peuvent arriver avant la réponse.

```json
{ "id": "live", "result": "subscribed" }
{ "id": "live", "security": "IBM US Equity", "event": "MarketDataEvents", "data": { "BID": 123.4 } }
{ "id": "live", "security": "EURUSD Curncy", "event": "MarketDataEvents", "data": { "LAST_PRICE": 1.2345, "BID": 1.2345, "ASK": 1.2346 } }
```

Les autres valeurs de `event` sont des statuts :
- `SubscriptionStarted` : arrive une fois par titre, au début de l'abonnement ;
- `SubscriptionFailure` et `SubscriptionTerminated` : `data` contient la raison donnée par Bloomberg ;
- `SessionTerminated` : le Terminal s'est déconnecté, et `data` vaut `null`.

```json
{ "id": "live", "type": "Unsubscribe" }
{ "id": "live", "result": "unsubscribed" }
```

## Erreurs

Le pont refuse la requête (validation, limite, budget, Terminal indisponible) :

```json
{ "id": "bdp", "error": "fields: 1 to 25 names made of letters, digits and _" }
```

Un titre ou un champ invalide ne fait pas échouer la requête : Bloomberg le signale dans le `securityData` concerné, par `securityError` pour un titre et dans `fieldExceptions` pour un champ. Le bouton « Erreurs » de la page de test envoie une requête de ce type ; son exemple reste à ajouter ici.
