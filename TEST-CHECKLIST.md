# BeaverSearch v0.4.1 FixSteamID — test checklist

## Native build / startup

- [ ] `START.bat` на Windows 10/11 x64 + .NET 8 SDK.
- [ ] STATIC-PREFLIGHT = PASS.
- [ ] HOTFIX-PREFLIGHT = PASS.
- [ ] `dotnet build` = 0 errors.
- [ ] MainWindow открывается без XamlParseException.

## Source resolver

- [ ] yooma.su и CYBERSHOKE возвращают exact SteamID64 из DOM/API/WebSocket, без поиска по нику.
- [ ] `/card/<SteamID64>` / `/profile/<SteamID64>` / explicit steam-account поля распознаются.
- [ ] CYBERSHOKE DM roster даёт SteamID64 в player stream.
- [ ] Один SteamID, найденный несколькими источниками, не запускает duplicate valuation одновременно.

## Monitoring batch / anti-freeze

- [ ] В UI одновременно находится текущая десятка community servers, а не 1000+ server rows.
- [ ] `CommunityServerBatchSize = 10`.
- [ ] Один batch планирует максимум 40 новых SteamID checks.
- [ ] Следующий batch НЕ начинается через фиксированный timeout: программа ждёт полного завершения текущих player tasks.
- [ ] Старые inventory tasks не продолжают копиться за следующими server batches.
- [ ] Player valuation concurrency максимум 4.
- [ ] Chromium probe максимум 1, cache 75 секунд, compact DOM, BelowNormal priority.
- [ ] UI остаётся отзывчивым во время source refresh / inventory / pricing.
- [ ] Stop Monitoring отменяет текущую работу и не оставляет бесконечную очередь.

## Steam inventory — mandatory

- [ ] Modern endpoint: `/inventory/<SteamID>/<appid>/2?count=1000`.
- [ ] Legacy fallback: `/profiles/<SteamID>/inventory/json/<appid>/2`.
- [ ] Перед legacy fallback выполняется anonymous profile inventory prime; пользовательские Edge/Chrome/Steam cookies не читаются.
- [ ] Steam inventory requests глобально сериализованы (`gate=1`).
- [ ] Normal spacing около 3 секунд, после 403/429 включается увеличенный cooldown/spacing.
- [ ] Modern schema `assets/descriptions` связывается по `classid + instanceid`.
- [ ] Legacy schema `rgInventory/rgDescriptions` связывается по `classid + instanceid`.
- [ ] Modern pagination `more_items + last_assetid/start_assetid` работает.
- [ ] Legacy pagination `more + more_start/start` работает.
- [ ] `success`, `marketable`, pagination flags принимают bool/number/string формы.
- [ ] Literal `null`, пустой body, 403/429, timeout, invalid JSON показываются как temporary failure с реальной причиной, а не как private `0 ₽`.
- [ ] Явно закрытый/private inventory корректно остаётся inaccessible.

## Pricing — mandatory

- [ ] CS2 primary bulk source — public SkinCash feed; USD конвертируется в RUB через CBR.
- [ ] Dota primary bulk source — public market.dota2.net RUB feed.
- [ ] Skinport используется как secondary source и получает `Accept-Encoding: br`.
- [ ] Ошибка/403 одного bulk source не завершает весь player valuation исключением.
- [ ] Отсутствующие `market_hash_name` ограниченно добираются Steam Community Market.
- [ ] Steam Market fallback выполняется по одному запросу с pacing/cache, а не массовым Parallel.ForEach.
- [ ] Если ни один price source не дал ни одной цены для непустого marketable inventory, игра помечается temporary failure, а не `0 ₽` success.
- [ ] Common `market_hash_name` повторно используются из price cache.

## Partial valuation / Results — mandatory

- [ ] Успешный CS2 результат НЕ теряется, если Dota/Rust позже получили temporary Steam failure.
- [ ] Если CS2 стоимость входит в заданный диапазон, такой игрок появляется в Results как `MATCH`, даже при partial Dota/Rust.
- [ ] Partial result НЕ записывается в 24h successful SteamChecks и будет повторён позже.
- [ ] Полностью успешный SteamID записывается в 24h cache и не пересканируется до TTL.
- [ ] Реально пустой/non-marketable inventory может корректно иметь 0 ₽.
- [ ] `PriceEngineVersion = 5`; после обновления старые `SteamChecks` очищаются автоматически, NameResolves/SteamID сохраняются.

## Manual regression test

- [ ] С Monitoring OFF проверить известный SteamID с публичным CS2 inventory.
- [ ] CS2 показывает `items / marketable / без цены / цены OK`, а не `закрыт/недоступен`, если inventory получен.
- [ ] При временном Steam throttle отображается конкретная modern/legacy причина, а не `null`.
- [ ] При наличии известных marketable skins CS2 value > 0 ₽.
- [ ] При фильтре CS2 `1..30000` подходящий результат даёт `Фильтр: CS2`.

## Cache / repository / GUI

- [ ] `cache.json` сохраняется debounce-окном и содержит `PriceEngineVersion: 5`.
- [ ] Нижний sidebar background не растянут/не повреждён.
- [ ] Application icon — BeaverSearch/beaver branding.
- [ ] Нет `bin/`, `obj/`, `.vs/`, `.tmp` в репозитории.
- [ ] `.github/workflows` отсутствует; GitHub Actions намеренно не используется.
