# BeaverSearch v0.4.1 FixSteamID

Дата: 2026-09-13.

## v5 — rebuild inventory / pricing pipeline

- По реальному логу локализована главная причина пустого `Results`: SteamID уже находились корректно, CS2 inventory у части игроков успевал загрузиться, но `Skinport /v1/items` отвечал HTTP 403. Старая реализация считала это фатальной ошибкой всей оценки игрока.
- Skinport больше не является единственной точкой отказа. CS2 использует публичный bulk feed SkinCash (USD → RUB через CBR), затем Skinport, затем ограниченный Steam Community Market fallback по `market_hash_name`.
- Dota 2 использует публичный `market.dota2.net` RUB price feed, затем Skinport и Steam Market fallback. Rust использует Skinport + Steam Market fallback.
- Skinport secondary request отправляет `Accept-Encoding: br`; его 403/5xx больше не обнуляет игрока и не завершает весь valuation исключением.
- Steam Market fallback глобально сериализован, работает с pacing/cooldown и общим price cache, поэтому не создаёт burst из десятков параллельных `priceoverview` запросов.
- `SteamInventoryService` получил два независимых публичных пути: современный `/inventory/<SteamID>/<appid>/2` и fallback `/profiles/<SteamID>/inventory/json/<appid>/2`. Перед fallback BeaverSearch посещает profile inventory page собственной anonymous cookie-session, не читая пользовательские browser cookies.
- Modern и legacy Steam inventory schemas поддерживаются отдельно (`assets/descriptions` и `rgInventory/rgDescriptions`). Пагинация поддерживается для обоих путей.
- Literal `null`, пустой body, 403/429, invalid JSON и transport errors никогда не записываются как успешный `0 ₽`.
- Валидная оценка одной игры больше не теряется из-за временной ошибки другой. Например, успешный CS2 может дать `MATCH`, даже если Dota/Rust в этот момент получили временный Steam throttle. Такой partial result отображается, но не попадает в 24h successful cache и будет дополнен позже.
- `PriceEngineVersion = 5`; старые `SteamChecks` очищаются автоматически при первом запуске, найденные SteamID/name mappings сохраняются.

## Monitoring stability

- Мониторинг остаётся пакетным: ровно до 10 community servers в текущем рабочем наборе.
- На один batch планируется максимум 40 новых SteamID scans.
- Удалён старый 90-секундный timeout перехода на следующую десятку. Следующий server batch теперь НЕ запускается, пока реально не завершились задачи текущего batch либо пользователь не остановил monitoring.
- Это исключает накопление старых inventory/price задач за новыми пакетами и повторное появление скрытой очереди из сотен игроков.
- Player valuation concurrency = 4, но сам Steam Community inventory endpoint имеет отдельный глобальный gate=1 и адаптивный cooldown.
- Chromium resolver по-прежнему: gate=1, compact DOM/API capture, 75s cache, BelowNormal process priority.

## Предыдущие FixSteamID / GUI / build исправления

- yooma.su + CYBERSHOKE объединены в общий exact-SteamID pipeline; никнейм не используется для угадывания личности.
- Поддерживаются `/card/<SteamID64>`, `/profile/<SteamID64>`, data-steamid/account fields, Steam2/Steam3, XHR/fetch и WebSocket/Socket.IO capture через CDP.
- UI не материализует каталог из 1000+ серверов; отображается текущий рабочий batch.
- Исправлен повреждённый Win32 ICO/`CS7065`; application icon возвращён к BeaverSearch branding.
- Нижний sidebar background исправлен без растягивания повреждённой нижней части.
- `START.bat` запускает STATIC-PREFLIGHT и HOTFIX-PREFLIGHT, очищает WPF `obj` и только потом выполняет native `dotnet build`.
- GitHub Actions намеренно не используются.
