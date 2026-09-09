# BeaverSearch v0.4.1 FixSteamID

Дата: 2026-09-09.

## Inventory hotfix — `CS2: null | Dota 2: null | Rust: null`

- Исправлен новый runtime-дефект, обнаруженный на реальном monitoring batch: Steam мог отвечать `403/429` с пустым либо буквально `null` body, а BeaverSearch выводил бесполезное `CS2: null | Dota 2: null | Rust: null`.
- `null/false/{}/[]` больше не считаются текстом ошибки. В журнал теперь попадает реальный HTTP status и понятная причина, например `Steam inventory HTTP 403 Forbidden: empty/null body; ... rate limit/anti-bot`.
- Steam inventory requests глобально сериализованы: одновременно выполняется только 1 запрос к Community inventory endpoint.
- Базовый интервал между inventory requests увеличен до 2.5 секунды. После `403/429` включается адаптивный cooldown 15/30/60/90 секунд и throttled spacing 5 секунд, чтобы BeaverSearch сам не продлевал блокировку Steam постоянными повторными запросами.
- Размер страницы уменьшен до `count=1000`; пагинация через `more_items + last_assetid + start_assetid` сохранена.
- Перед первой inventory-проверкой создаётся собственная анонимная Steam Community session/cookie jar. Пользовательские cookies Edge/Chrome не читаются и авторизация Steam не требуется.
- CS2 / Dota 2 / Rust больше не запрашиваются одновременно для одного игрока. Игры проверяются последовательно; при временной ошибке CS2 две лишние inventory-проверки не запускаются. Это резко снижает burst-нагрузку и сразу показывает, на каком запросе Steam начал throttle.
- Transport timeout/invalid JSON/body read errors теперь также получают конкретный текст и никогда не кэшируются как успешный `0 ₽`.

## Inventory/valuation hotfix — public inventory falsely shown as 0 ₽

- Исправлена подтверждённая ошибка ручной проверки: публичный CS2 inventory мог отображаться как `0 ₽ (закрыт/недоступен)`.
- `403/429/5xx` больше не превращаются автоматически в «private inventory = 0 ₽». Steam error body анализируется отдельно; временный HTTP/rate-limit сбой помечается как transient и не может попасть в 24h successful cache.
- Явный private/not-available ответ Steam по-прежнему корректно считается закрытым inventory.
- Парсер `success`, `marketable`, `more_items` принимает bool/number/string формы (`true`, `1`, `"1"`), чтобы изменение JSON-типа не превращало marketable skins в нулевую оценку.
- `market_hash_name` читается с fallback на `market_name/name`; assets/descriptions связываются через `classid + instanceid`.
- `PriceEngineVersion = 4`: старые `SteamChecks`, которые могли быть записаны как ложный `0 ₽`, сбрасываются автоматически, а найденные SteamID/name mappings сохраняются.
- Если Steam inventory/price provider временно не ответил, valuation завершается ошибкой и повторяется позже вместо записи ложного `0 ₽` на 24 часа.

## Performance / sequential monitoring batches

- Мониторинг работает последовательными пакетами по 10 серверов: 10 серверов → игроки/SteamID → inventory/price → ожидание текущих scan-задач → следующая десятка.
- UI больше не материализует тысячи dynamic server rows; одновременно отображается текущий рабочий batch.
- Тяжёлых player valuation одновременно максимум 4, при этом сам Steam inventory endpoint защищён отдельным глобальным gate=1.
- Browser probe ограничен одним Chromium process, использует compact DOM fragments, BelowNormal priority и 75s cache.
- JSON/HTML source parsing выполняется вне WPF dispatcher; UI metrics throttled.
- Сохранение `cache.json` дебаунсится, чтобы массовое завершение игроков не вызывало серию полных JSON write подряд.

## Pricing

- Для массовых проверок используется лениво кэшируемый RUB catalog с ограниченным Steam Community Market fallback по `market_hash_name`.
- Каталог не загружается при старте программы: первый непустой публичный inventory инициирует нужный app catalog.
- Steam Market fallback имеет собственный positive/negative cache, dedup и retry/backoff.
- Публичный inventory с marketable-предметами, для которого price provider не вернул цены, не считается успешным `0 ₽` и не записывается в 24h cache.

## Build / UI hotfixes

- Исправлен `CSC CS7065: Unable to read beyond the end of the stream` из-за повреждённого Win32 ICO.
- `STATIC-PREFLIGHT.ps1` валидирует ICO header/directory/frame bounds.
- `START.bat` очищает WPF `obj` перед build.
- Application icon возвращён к BeaverSearch/beaver branding.
- Нижний sidebar background исправлен: убрано проблемное растягивание нижней части изображения.

## FixSteamID — CDP/API/WebSocket resolver

- `RenderedDomLoader` использует Chromium DevTools Protocol как основной механизм; `--dump-dom` оставлен fallback.
- Читаются `Network.responseReceived`, `Network.getResponseBody`, XHR/fetch/EventSource JSON и текстовые WebSocket/Socket.IO frames.
- yooma `/card/<SteamID64>` и `/profile/<SteamID64>` поддерживаются как прямые источники SteamID64.
- Поддерживаются explicit `data-steamid`, Steam AccountID, Steam2, Steam3 и Steam Community profile URLs.
- Никнейм не используется как resolver личности.
- `%LOCALAPPDATA%\BeaverSearch\source-probe.log` содержит browser/source telemetry и найденные public endpoints.

## Источники мониторинга

- yooma.su и CYBERSHOKE объединены в общий pipeline.
- Один SteamID, найденный на обоих источниках, защищён общим per-SteamID lock и 24h dedup после успешной valuation.
- Diagnostics показывает counters/render progress отдельно для yooma.su и CYBERSHOKE.
