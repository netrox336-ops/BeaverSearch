# BeaverSearch 🦫 v0.4.1 FixSteamID

BeaverSearch — WPF-приложение для Windows 10/11, которое ищет игроков на публичных серверах **yooma.su и CYBERSHOKE**, получает Steam identity непосредственно из публичных данных monitoring-сайтов и затем проверяет публичные Steam-инвентари CS2, Dota 2 и Rust по заданным ценовым диапазонам.

## Что исправляется в 0.4.1

Старый подход `A2S/GAMEMONITORING → ник → попытка resolver SteamID` больше не используется. A2S полезен для roster, но не даёт BeaverSearch надёжного SteamID каждого игрока, поэтому такой pipeline постоянно оставлял игроков в `Unresolved`.

Текущий pipeline:

`yooma.su / CYBERSHOKE → DOM + public XHR/fetch/WebSocket data → точный Steam identity → SteamID64 → Steam profile → inventory → price engine`

BeaverSearch **не угадывает аккаунт по нику**. Принимаются только идентификаторы, которые сам monitoring-site отдаёт в player element/state/API: 17-значный SteamID64, Steam AccountID, Steam2/Steam3 либо прямая profile/card/Steam-ссылка. AccountID/Steam2/Steam3 переводятся в SteamID64 детерминированно.

## Как читаются live-данные сайтов

yooma.su и CYBERSHOKE используют клиентский JavaScript, поэтому одного `HttpClient` недостаточно. В 0.4.1 r4 используются два слоя:

1. быстрый HTTP/SSR слой читает доступную разметку и server state;
2. browser probe через **Chromium DevTools Protocol** запускает установленный Microsoft Edge либо Google Chrome в headless-режиме с отдельным временным профилем.

DevTools probe:

- ждёт SPA hydration;
- ищет реальные server cards вместо `.server.loading`;
- пытается открыть player/online block на карточках;
- читает итоговый DOM;
- включает DevTools `Network` и забирает публичные `XHR`, `fetch`, `EventSource` и JSON response bodies через `Network.getResponseBody`;
- принимает публичные project API на основном домене и его поддоменах;
- перехватывает текстовые WebSocket frames, включая Socket.IO payloads;
- объединяет DOM/API/WebSocket данные в один parser stream.

Старый `--dump-dom` сохранён только как fallback, если DevTools probe не сработал.

Одновременно запускается максимум **2 browser probes**. Остальные страницы не стоят в длинной очереди: они откладываются на следующий monitoring cycle. Успешно обработанные страницы кэшируются, поэтому browser sweep постепенно проходит следующие режимы, не замораживая весь мониторинг.

Используется отдельный disposable browser profile. BeaverSearch не читает cookies, историю и авторизованный профиль браузера пользователя.

Для диагностики создаётся:

`%LOCALAPPDATA%\BeaverSearch\source-probe.log`

В нём пишутся URL страницы и метрики вида:

`probe cards=..., loading=..., clicks=..., identities=..., resources=..., replayJson=..., networkJson=..., wsFrames=...`

а также обнаруженные публичные endpoint URL.

## yooma.su

Parser поддерживает прямой маршрут игрока:

`/card/<SteamID64>`

а также `/profile/<SteamID64>`, explicit steam/account fields и hydration/API objects. Именно `/card/<17 цифр>` является одним из ключевых источников SteamID64 в текущем yooma UI.

Опрос охватывает режимные страницы yooma.su (PUBLIC, AWP, JAIL, ARENA, MANIAC, MINIGAMES, 5X5, DUELS и дополнительные доступные routes). Browser/API layer нужен потому, что исходная страница может содержать только `.server.loading`, а реальные серверы/игроки приходят позже.

## CYBERSHOKE

Опрос публичных CS2 mode pages (`DM`, `DUELS`, `RETAKE`, `5X5`, `2X2`, `BHOP`, `SURF`, `KZ`, `PUBLIC`, `AWP` и др.). Последние mode snapshots объединяются, чтобы постепенно покрывать сеть серверов.

Тот же DevTools layer умеет забирать JSON из XHR/fetch и текстовые WebSocket frames. Это важно для SPA/socket-сценариев, где Steam identity отсутствует в первоначальном HTML.

## Steam identity

Поддерживаются:

- `7656119...` SteamID64;
- Steam AccountID в явном `steam/account` поле;
- `STEAM_X:Y:Z`;
- `[U:1:AccountID]`;
- `/card/<SteamID64>`;
- `/profile/<SteamID64>`;
- `steamcommunity.com/profiles/<SteamID64>`.

Ник используется только как отображаемое имя. Он никогда не является доказательством личности.

## Inventory / dedup

- Найденный SteamID сразу отправляется в Steam profile + inventory pipeline.
- Один SteamID не сканируется повторно в течение 24 часов.
- Общий per-SteamID gate не позволяет yooma.su и CYBERSHOKE одновременно запустить два scan одного аккаунта.
- До 8 разных inventory scans выполняются параллельно.
- Price cache прогревается при старте мониторинга.
- Повторные price lookup одинаковых предметов используют cache/gate.
- Для `429/5xx` применяется ограниченный retry/backoff.

## Диагностика

Отдельно отображаются:

- `yooma.su live players`;
- `CYBERSHOKE live players`;
- SteamID/player counters обоих источников;
- `SteamID confirmed`;
- `Unresolved`;
- `Inventory processing`;
- matches;
- page/render progress для обоих сайтов.

Подробная browser/network телеметрия находится в `source-probe.log`. После `Monitoring OFF` live counters сбрасываются.

## Запуск

1. Клонируйте/скачайте репозиторий.
2. Запустите `START.bat`.
3. Launcher запускает `STATIC-PREFLIGHT.ps1`, затем `dotnet build`, затем приложение.

При build/runtime ошибке окно остаётся открытым и показывает tail `%LOCALAPPDATA%\BeaverSearch\startup.log`.

Если диагностика пишет, что Edge/Chrome не найден, установите/обновите Microsoft Edge или Google Chrome.

## Фильтры

Диапазоны CS2 / Dota 2 / Rust сохраняются сразу в `settings.json`. Проверяется `min <= max`; значения используются тем же объектом settings, который читает scanner, поэтому введённое `1 ₽` влияет на фактический поиск.

## Splash / WPF stability

Startup sequence: `yooma + CYBER → SteamID → Inventory → Price Engine`.

`STATIC-PREFLIGHT.ps1` проверяет XML/XAML, duplicate dependency-property assignments, `StaticResource`, event handlers, assets, `PanningMode`, `Storyboard.TargetName`, binding modes, первый TabItem, PerMonitorV2, отсутствие legacy A2S/GAMEMONITORING resolver и наличие обоих monitoring clients.

Static preflight не заменяет нативный WPF compiler/runtime test — его выполняет `START.bat` на Windows.

## Локальные данные

`%LOCALAPPDATA%\BeaverSearch` — settings, cache, `startup.log` и `source-probe.log`. Повреждённые JSON-файлы best-effort переименовываются в `.corrupt-*`.

## Разработка

Основная рабочая ветка — `main` репозитория `netrox336-ops/BeaverSearch`.

GitHub используется как рабочая база исходников. Автоматические GitHub Actions/workflow для сборки намеренно не добавляются; Windows build выполняется локально через `START.bat`.
