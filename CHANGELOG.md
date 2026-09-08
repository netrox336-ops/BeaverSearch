# BeaverSearch v0.4.1 FixSteamID

Дата: 2026-09-08.

## Performance + Price hotfix — Steam Market / anti-freeze

- Исправлена причина ложных `0 ₽`: базовая оценка больше не зависит от загрузки полного каталога Skinport. Стоимость marketable-предметов определяется по Steam Community Market в RUB через `market_hash_name`.
- Добавлен `SteamMarketPriceProvider`: positive cache 10 минут, negative cache 2 минуты, per-item dedup lock, ограничение одновременных HTTP-запросов и pacing/retry для `429/5xx`.
- Старый `SkinportPriceProvider` временно оставлен только как compatibility facade, чтобы не делать рискованную миграцию конструктора в hotfix; фактически он делегирует Steam Market provider.
- Инвентарь сначала загружается и только затем запрашиваются цены реально встречающихся marketable-предметов. Полные внешние price-каталоги больше не разбираются на каждый цикл.
- `SteamInventoryService`, `SteamProfileService` и valuation pipeline используют `ConfigureAwait(false)`, поэтому сетевой JSON/XML parsing не должен выполняться на WPF dispatcher.
- Тяжёлых player valuation одновременно максимум 4; одновременных Steam inventory downloads максимум 6. Внешний scan gate сохранён для совместимости, но тяжёлая часть дополнительно ограничена внутри valuation service.
- Сохранение `cache.json` дебаунсится: десятки завершившихся игроков больше не вызывают десятки полных JSON-сериализаций/замен файла подряд.
- Добавлен `PriceEngineVersion = 2`. При первом запуске новой версии старые `SteamChecks` автоматически очищаются, а найденные SteamID/name mappings сохраняются. Это заставляет игроков, ранее ошибочно закэшированных как `0 ₽`, переоцениться сразу, а не ждать 24 часа.
- Публичный инвентарь с marketable-предметами, для которого Steam Market временно не дал ни одной цены, больше НЕ считается успешной оценкой `0 ₽` и НЕ попадает в 24h cache — будет повторная попытка.
- Пустой, закрытый или реально не имеющий marketable-предметов инвентарь по-прежнему может корректно иметь `0 ₽`.
- Browser probe дополнительно облегчён: общий Chromium gate снижен до 1 процесса, успешный DOM/API snapshot кэшируется 75 секунд, Chromium запускается с `BelowNormal` priority и меньшим viewport.
- Вместо многомегабайтного `document.documentElement.outerHTML` parser получает компактный набор server/player fragments + XHR/fetch/WebSocket JSON. Это снижает CPU, memory allocation и GC spike на периодическом monitoring tick.
- Browser interaction сокращён до 16 server cards за probe; остальные страницы/режимы продолжают обходиться rolling-образом в следующих циклах.

## Build hotfix — Win32 icon / CS7065

- Исправлен `CSC CS7065: Unable to read beyond the end of the stream` при WPF build.
- Причина: `Assets/Brand/BeaverSearch.ico` был физически обрезан. ICO-directory содержал четвёртый frame с offset `6319`, при этом длина самого файла также была `6319` байт, поэтому Win32 resource compiler пытался читать за концом stream.
- Первая попытка замены тоже оказалась некорректной и была поймана новым preflight (`frame 3 exceeds file size`). Она заменена на проверенный single-frame 32×32 ICO: header `0/1/1`, payload size `388`, offset `22`, полный файл `410` байт; `offset + size == file length`.
- `STATIC-PREFLIGHT.ps1` валидирует ICO header, directory table и границы каждого frame.
- `START.bat` очищает `src/BeaverSearch/obj` перед `dotnet build`.
- При build error launcher помечает `startup.log` как runtime-log, который может относиться к предыдущему запуску.

## FixSteamID r4 — CDP/API/WebSocket resolver

- Исправлен compile-time дефект первой CDP-ревизии: `async`-методы больше не используют запрещённые `ref/out` параметры; счётчик команд хранится в состоянии CDP-сессии.
- `RenderedDomLoader` использует Chromium DevTools Protocol как основной механизм; `--dump-dom` оставлен fallback.
- Перед навигацией включается `Network` domain DevTools; читаются `Network.responseReceived` и `Network.getResponseBody`.
- Перехватываются XHR/fetch/EventSource JSON и текстовые `Network.webSocketFrameReceived`.
- Socket.IO-префиксы нормализуются перед разбором JSON.
- JSON из DOM/API/WebSocket добавляется во внутренний parser stream и проходит resolver точной Steam identity.
- yooma `/card/<SteamID64>` поддерживается как прямой источник SteamID64 наряду с `/profile/<SteamID64>` и явными steam/account полями.
- `%LOCALAPPDATA%\BeaverSearch\source-probe.log` пишет browser/source telemetry и обнаруженные публичные endpoint URL.
- Пользовательский Edge/Chrome profile, cookies и авторизация не читаются: каждый probe использует отдельную временную директорию.

## FixSteamID revision — yooma.su + CYBERSHOKE

- Старый resolver `A2S/GAMEMONITORING → nickname → SteamID` выведен из runtime pipeline.
- `YoomaClient` анализирует HTTP markup, hydration, rendered DOM и browser-captured public data.
- Добавлен `CybershokeClient` для публичных CS2 mode/server pages CYBERSHOKE.
- SteamID64 читается непосредственно из player element/state/profile link.
- Поддерживаются `data-steamid`, steam/account fields, Steam Community profile URLs и site profile/card URLs.
- `SteamIdentityParser` переводит Steam AccountID, Steam2 и Steam3 в SteamID64.
- Никнейм не используется как resolver личности.
- Один SteamID из yooma.su/CYBERSHOKE попадает под общий 24h dedup и per-SteamID lock.

## UI / diagnostics

- Monitoring и Servers показывают общий каталог yooma.su + CYBERSHOKE.
- Diagnostics разделяет counters обоих источников и показывает render progress.
- Splash: `yooma + CYBER → SteamID → Inventory → Price Engine`.

## Сохранено

- 24h SteamID scan dedup + per-SteamID lock.
- Retry/backoff для `429/5xx`.
- Live min/max filters.
- `PerMonitorV2`.
- XAML/runtime preflight и startup.log.
