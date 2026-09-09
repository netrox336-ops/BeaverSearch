# BeaverSearch v0.4.1 FixSteamID — test checklist

## Native build / startup

- [ ] `START.bat` на Windows 10/11 x64 + .NET 8 SDK.
- [ ] STATIC-PREFLIGHT = PASS.
- [ ] HOTFIX-PREFLIGHT = PASS.
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

## Monitoring batches / anti-freeze

- [ ] Одновременно отображается текущий пакет примерно из 10 community servers, а не тысячи строк.
- [ ] Следующая десятка выбирается после текущего batch workflow.
- [ ] Тяжёлых player valuations одновременно максимум 4.
- [ ] Одновременно работает максимум 1 headless Chromium probe.
- [ ] Успешный browser snapshot кэшируется 75 секунд.
- [ ] Browser child process имеет best-effort `BelowNormal` priority.
- [ ] Parser получает compact server/player fragments, а не полный multi-megabyte DOM.
- [ ] Временный browser profile создаётся отдельно; пользовательский browser profile/cookies не используются.
- [ ] Во время monitoring tick окно продолжает перемещаться/скроллиться без заметного freeze.
- [ ] Stop Monitoring отменяет browser/inventory work и UI не зависает.

## Steam Community inventory

- [ ] Steam inventory URL использует `count=1000` и context `2`.
- [ ] Одновременно выполняется максимум 1 Steam inventory HTTP request.
- [ ] Между обычными inventory requests выдерживается минимум ~2.5 секунды.
- [ ] После `403/429` включается adaptive cooldown и запросы не продолжают спамить Steam.
- [ ] Перед первым inventory request создаётся собственная anonymous Steam Community cookie/session jar; пользовательские browser cookies не читаются.
- [ ] CS2 / Dota 2 / Rust для одного игрока запрашиваются последовательно, а не fan-out одновременно.
- [ ] `403/429/5xx` не превращаются в `закрыт/недоступен = 0 ₽`, если Steam явно не сообщил private/not available.
- [ ] Пустой или literal `null` body при HTTP 403 отображается как конкретный HTTP/rate-limit error, а не `CS2: null`.
- [ ] Timeout/transport/invalid JSON содержит реальный текст ошибки и не записывается в 24h successful cache.
- [ ] Пагинация `more_items + last_assetid + start_assetid` собирает inventory больше одной страницы.
- [ ] `success`, `marketable`, `more_items` принимают bool/number/string формы.
- [ ] `market_hash_name` читается с fallback `market_name/name`.

## Inventory valuation / pricing

- [ ] Confirmed SteamID сразу уходит в profile/inventory pipeline.
- [ ] Публичный CS2 inventory с marketable-предметами получает ненулевую RUB стоимость, если price provider знает эти предметы.
- [ ] Dota 2 / Rust обрабатываются аналогично при доступности inventory/price.
- [ ] Для массовых проверок используется lazy bulk RUB catalog; отсутствующие позиции ограниченно добираются Steam Community Market по `market_hash_name`.
- [ ] Публичный непустой inventory с marketable-предметами и временно нулевым ответом price provider НЕ записывается как успешные `0 ₽` на 24 часа.
- [ ] Реально пустой/закрытый/non-marketable inventory может корректно иметь `0 ₽`.
- [ ] `PriceEngineVersion: 4`; старые ложные `SteamChecks` после миграции не блокируют повторную оценку.
- [ ] Повторный успешно оценённый SteamID <24h не запускает новый scan.

## Manual check — обязательный regression test

- [ ] С Monitoring OFF проверить `76561198316679969`.
- [ ] Если Steam не throttled, CS2 определяется как доступный и показывает количество items/marketable вместо `закрыт/недоступен`.
- [ ] Если Steam throttled, UI показывает точный `HTTP 403/429` и причину; `null` в тексте ошибки отсутствует.
- [ ] После успешного inventory fetch стоимость CS2 больше 0 ₽ при наличии marketable skins с известной ценой.
- [ ] При фильтре CS2 `1..30000` подходящая рассчитанная стоимость даёт `Фильтр: CS2`.

## Cache / массовый scan

- [ ] При одновременном завершении игроков `cache.json` не сериализуется физически после каждого игрока; записи объединяются debounce-окном ~550ms.
- [ ] После debounce в `cache.json` присутствуют последние `NameResolves`, `SteamChecks` и `PriceEngineVersion: 4`.
- [ ] Завершение приложения не оставляет повреждённый `.tmp`/cache.

## Filters

- [ ] `min/max` редактируются и диапазон используется сразу.
- [ ] При CS2 filter `1..30000` игрок с рассчитанной CS2 стоимостью в этом диапазоне попадает в Results.
- [ ] Игрок дороже max не создаёт ложный match.
- [ ] Игрок со стоимостью ниже min не создаёт ложный match.
- [ ] `yooma.su live players`, `CYBERSHOKE live players`, `SteamID confirmed`, `Inventory processing` обновляются.
- [ ] После Monitoring OFF live counters = 0.

## GUI / DPI

- [ ] 100%, 125%, 150% Windows DPI.
- [ ] Нет обрезанных нижних элементов.
- [ ] Нижний sidebar background не растянут/не повреждён.
- [ ] Application icon использует beaver branding.
- [ ] DataGrid selection остаётся тёмным.
- [ ] Hover / pressed / focus состояния согласованы.

## Packaging / repository

- [ ] CHANGELOG / TEST-CHECKLIST соответствуют текущему hotfix.
- [ ] Нет `bin/`, `obj/`, `.vs/`, `.tmp`.
- [ ] В репозитории отсутствует `.github/workflows` — CI/Actions намеренно не используется.
