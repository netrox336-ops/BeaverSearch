# BeaverSearch v0.4.1 FixSteamID

Дата: 2026-09-13.

## v6 — Steam inventory reliability / 401 + 429 fix

- Новый реальный monitoring-лог показал два разных поведения Steam Community: пустой Rust inventory приходил как `HTTP 401 Unauthorized` без деталей, а после серии запросов Dota inventory начал получать `HTTP 429 Too Many Requests`.
- `401 + empty/null body` больше не считается авторизационной или временной ошибкой. Для публичного Community inventory такой ответ нормализуется в доступный пустой app inventory `0 items`, поэтому пустой Rust/Dota больше не превращает игрока в `partial valuation`.
- Добавлен `SteamInventoryPresenceService`: один обычный profile inventory HTML читается и парсит `g_rgAppContextData`. Если Steam уже сообщает `asset_count = 0` для CS2/Dota/Rust, BeaverSearch вообще не обращается к JSON inventory endpoint этой игры.
- Presence result кэшируется на 20 минут. Это резко уменьшает число дорогих запросов к `steamcommunity.com/inventory/...` у типичных CS2-игроков без Dota/Rust inventory.
- Старый legacy fallback `/profiles/<SteamID>/inventory/json/...` полностью удалён. На практике после 403/429 он удваивал запросы к тому же Steam Community host и продлевал throttling вместо восстановления.
- Современный public inventory endpoint теперь используется один: `/inventory/<SteamID>/<appid>/2?count=2000`. Размер 2000 уменьшает количество страниц, сохраняя пагинацию через `more_items + last_assetid`.
- Все inventory JSON requests глобально сериализованы. Нормальный spacing = 10 секунд, после throttle = 15 секунд.
- После 429 включается cooldown 90/180/300/420 секунд; после 403 — 60/120/240/360 секунд. Немедленных повторов 403/429 нет.
- Player valuation concurrency уменьшен с 4 до 2. Это не мешает UI, но не создаёт длинную конкурентную очередь вокруг одного rate-limited Steam endpoint.
- `PriceEngineVersion = 6`: успешные valuation checks предыдущей версии сбрасываются один раз; найденные SteamID/name mappings сохраняются.

## v6 — защита inventory от price traffic

- Основной CS2 price source остаётся публичным bulk-feed SkinCash, Dota 2 — публичным RUB feed market.dota2.net. Skinport остаётся secondary provider.
- Steam Community Market `priceoverview` теперь исключительно last-resort fallback: максимум 4 отсутствующих имени на valuation вместо 20.
- Steam Market fallback сериализован, spacing увеличен до 6 секунд, immediate retry после 403/429 удалён, positive cache увеличен до 30 минут.
- Это специально уменьшает дополнительный трафик к `steamcommunity.com`, чтобы price lookup не ухудшал доступность raw inventory endpoint.
- Ошибка любого одного price source не уничтожает корректно полученный inventory и не создаёт ложный `0 ₽`.

## Monitoring stability

- Мониторинг работает последовательными пакетами до 10 community servers.
- На один пакет планируется максимум 40 новых SteamID scans; следующий пакет не запускается до фактического завершения текущих задач либо остановки monitoring пользователем.
- Старый 90-секундный timeout перехода на следующую десятку удалён — скрытого overlap/backlog между пакетами нет.
- UI по-прежнему отображает только текущий рабочий набор серверов, а не 1000+ строк каталога.
- Chromium resolver: gate=1, compact DOM/API capture, 75s cache, BelowNormal process priority.

## Pricing / partial results

- Skinport больше не является единственной точкой отказа. CS2: SkinCash → Skinport → очень ограниченный Steam Market fallback. Dota 2: market.dota2.net → Skinport → fallback. Rust: Skinport → fallback.
- Валидный CS2 результат не теряется из-за временной ошибки другой игры. Partial result может дать MATCH, но временно неполная valuation не сохраняется как успешная 24-часовая проверка.
- Literal `null`, пустой body, 403/429, invalid JSON и transport errors не записываются как успешный `0 ₽`.

## FixSteamID / GUI / build

- yooma.su + CYBERSHOKE объединены в exact-SteamID pipeline; никнейм не используется для угадывания личности.
- Поддерживаются `/card/<SteamID64>`, `/profile/<SteamID64>`, data-steamid/account fields, Steam2/Steam3, XHR/fetch и WebSocket/Socket.IO capture через CDP.
- Исправлены Win32 ICO/`CS7065`, BeaverSearch icon и нижний sidebar background.
- `START.bat` запускает STATIC-PREFLIGHT + HOTFIX-PREFLIGHT, очищает WPF `obj`, затем выполняет native `dotnet build`.
- GitHub Actions намеренно не используются.
