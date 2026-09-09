# BeaverSearch v0.4.1 FixSteamID

Дата: 2026-09-09.

## Inventory/valuation hotfix — public inventory falsely shown as 0 ₽

- Исправлена подтверждённая ошибка ручной проверки: публичный CS2 inventory мог отображаться как `0 ₽ (закрыт/недоступен)`.
- Причина: BeaverSearch запрашивал `steamcommunity.com/inventory/<SteamID>/730/2` с `count=5000`. После ужесточения Steam Community такой размер страницы может возвращать HTTP 403 даже для публичного inventory.
- Размер Steam inventory page уменьшен до `2000`, сохранена полноценная пагинация через `more_items + last_assetid + start_assetid`.
- Одновременных Steam inventory HTTP-запросов теперь максимум 2; между запросами есть pacing 450 мс. Это снижает вероятность временного IP throttle во время массового server batch.
- `403/429/5xx` больше не превращаются автоматически в «private inventory = 0 ₽». Steam error body анализируется отдельно; временный HTTP/rate-limit сбой помечается как transient и не может попасть в 24h successful cache.
- Явный private/not-available ответ Steam по-прежнему корректно считается закрытым inventory.
- Парсер `success`, `marketable`, `more_items` теперь принимает bool/number/string формы (`true`, `1`, `"1"`), чтобы изменение JSON-типа не превращало marketable skins в нулевую оценку.
- `market_hash_name` читается с fallback на `market_name/name`; assets/descriptions продолжают связываться через `classid + instanceid`.
- `PriceEngineVersion` поднят до `4`: старые `SteamChecks`, которые могли быть записаны как ложный `0 ₽`, сбрасываются автоматически, а уже найденные SteamID/name mappings сохраняются.
- Если Steam inventory/price provider временно не ответил, valuation завершается ошибкой и повторяется позже вместо записи ложного `0 ₽` на 24 часа.

## Performance / sequential monitoring batches

- Мониторинг работает последовательными пакетами по 10 серверов: 10 серверов → игроки/SteamID → inventory/price → ожидание текущих scan-задач → следующая десятка.
- UI больше не материализует тысячи dynamic server rows; одновременно отображается текущий рабочий batch.
- Тяжёлых player valuation одновременно максимум 4.
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
