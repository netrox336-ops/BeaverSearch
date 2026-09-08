# BeaverSearch v0.4.1 FixSteamID — test checklist

## Native build / startup

- [ ] `START.bat` на Windows 10/11 x64 + .NET 8 SDK.
- [ ] STATIC-PREFLIGHT = PASS.
- [ ] `dotnet build` = 0 errors.
- [ ] Splash проходит `yooma + CYBER → SteamID → Inventory → Price Engine`.
- [ ] MainWindow открывается без XamlParseException.

## yooma.su

- [ ] Monitoring запускается без ручного IP.
- [ ] Diagnostics показывает yooma HTTP/render pages.
- [ ] При SPA-shell включается Edge/Chrome DevTools probe.
- [ ] `.server.loading` не приводит к вечному `Ожидание`.
- [ ] В `source-probe.log` появляется `cards/loading/clicks/identities/resources/replayJson/networkJson/wsFrames/liteChars`.
- [ ] `/card/<17-digit SteamID64>` распознаётся как подтверждённый SteamID64.
- [ ] JSON из XHR/fetch/DevTools Network response проходит parser.
- [ ] Steam AccountID/Steam2/Steam3 корректно нормализуются в SteamID64.
- [ ] Ник не используется для поиска Steam-профиля.

## CYBERSHOKE

- [ ] Mode/server cards появляются в Servers.
- [ ] Player DOM/state с Steam identity превращается в SteamID64.
- [ ] XHR/fetch response body считывается через DevTools `Network.getResponseBody`.
- [ ] Текстовые WebSocket / Socket.IO frames с player identity попадают в parser stream.
- [ ] Один SteamID из CYBERSHOKE и yooma.su не запускает два inventory scans.

## Browser probe / anti-freeze

- [ ] Одновременно работает максимум 1 headless Chromium probe.
- [ ] Успешный browser snapshot кэшируется 75 секунд; каждый 12-секундный tick не запускает новый Chromium для той же страницы.
- [ ] Browser child process имеет best-effort `BelowNormal` priority.
- [ ] Parser получает compact server/player fragments, а не полный multi-megabyte DOM; `liteChars` остаётся разумным.
- [ ] Временный browser profile создаётся в `%TEMP%\BeaverSearch\browser` и удаляется после probe.
- [ ] Пользовательский browser profile/cookies не используются.
- [ ] При недоступном DevTools используется короткий `--dump-dom` fallback.
- [ ] При занятом BrowserGate страница откладывается, monitoring cycle не строит длинную очередь Chromium.
- [ ] Во время очередного monitoring tick окно продолжает перемещаться/скроллиться без заметного freeze.
- [ ] Stop Monitoring отменяет browser/inventory work и UI не зависает.

## Steam Market / inventory valuation

- [ ] Confirmed SteamID сразу уходит в profile/inventory pipeline.
- [ ] Публичный CS2 inventory с marketable-предметами получает ненулевую RUB стоимость, если эти предметы торгуются на Steam Market.
- [ ] Dota 2 / Rust обрабатываются аналогично при доступности inventory/market price.
- [ ] Базовые цены берутся по `market_hash_name` с Steam Community Market, а не из полного Skinport catalog.
- [ ] Повторяющиеся предметы разных игроков используют общий per-item price cache.
- [ ] Тяжёлых player valuations одновременно не больше 4.
- [ ] Одновременных Steam inventory downloads не больше 6.
- [ ] Публичный непустой inventory с marketable-предметами и временно нулевым ответом price provider НЕ записывается как успешные `0 ₽` на 24 часа.
- [ ] Реально пустой/закрытый/non-marketable inventory может корректно иметь `0 ₽`.
- [ ] Старый cache без `PriceEngineVersion` автоматически сохраняет SteamID mappings, но очищает старые `SteamChecks` один раз.
- [ ] После миграции игроки, раньше ошибочно проверенные как `0 ₽`, сканируются повторно сразу.
- [ ] Повторный успешно оценённый SteamID <24h не запускает новый scan.

## Cache / массовый scan

- [ ] При одновременном завершении десятков игроков `cache.json` не сериализуется физически после каждого игрока; записи объединяются debounce-окном ~550ms.
- [ ] После завершения debounce в `cache.json` присутствуют последние `NameResolves`, `SteamChecks` и `PriceEngineVersion: 2`.
- [ ] Завершение приложения не оставляет повреждённый `.tmp`/cache.

## Filters

- [ ] `min/max` редактируются и `min <= max` валидируется.
- [ ] При CS2 filter `1..30000` игрок с рассчитанной CS2 стоимостью в этом диапазоне попадает в Results.
- [ ] Игрок дороже max не создаёт ложный match.
- [ ] Игрок со стоимостью ниже min не создаёт ложный match.
- [ ] `yooma.su live players`, `CYBERSHOKE live players`, `SteamID confirmed`, `Inventory processing` обновляются.
- [ ] После Monitoring OFF live counters = 0.

## GUI / DPI

- [ ] 100%, 125%, 150% Windows DPI.
- [ ] Нет обрезанных нижних элементов.
- [ ] DataGrid selection остаётся тёмным.
- [ ] Hover / pressed / focus состояния согласованы.

## Packaging / repository

- [ ] CHANGELOG / TEST-CHECKLIST соответствуют текущему hotfix.
- [ ] Нет `bin/`, `obj/`, `.vs/`, `.tmp`.
- [ ] В репозитории отсутствует `.github/workflows` — CI/Actions намеренно не используется.
