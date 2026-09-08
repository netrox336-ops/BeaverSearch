# BeaverSearch 🦫 v0.4.1 FixSteamID

BeaverSearch — WPF-приложение для Windows 10/11, которое ищет игроков на публичных серверах **yooma.su и CYBERSHOKE**, получает их SteamID непосредственно из публичной разметки/состояния сайтов и затем проверяет публичные Steam-инвентари CS2, Dota 2 и Rust по заданным ценовым диапазонам.

## Что исправлено

Старый подход `A2S/GAMEMONITORING → ник → попытка resolver SteamID` больше не используется. A2S отдаёт ник, score и время, но не гарантирует SteamID игрока, поэтому такой pipeline постоянно оставлял игроков в `Unresolved`.

Текущий pipeline:

`yooma.su / CYBERSHOKE → live DOM/state → точный Steam identity → SteamID64 → Steam profile → inventory → price engine`

BeaverSearch **не угадывает аккаунт по нику**. Принимаются только идентификаторы, которые сам monitoring-site положил рядом с player element/state: 17-значный SteamID64, Steam AccountID, Steam2/Steam3 либо прямая Steam/profile-ссылка. AccountID/Steam2/Steam3 преобразуются в SteamID64 математически.

## Как читаются live-данные сайтов

yooma.su и CYBERSHOKE используют клиентский JavaScript. Обычный `HttpClient` нередко получает только оболочку или серверные карточки без live player identities. Поэтому 0.4.1 r3 работает в два независимых слоя:

1. быстрый HTTP/SSR слой сразу формирует доступный каталог серверов и не ждёт браузер;
2. фоновый browser probe через **Chrome DevTools Protocol** запускает установленный Microsoft Edge либо Google Chrome и собирает итоговый DOM плюс публичные XHR/fetch, WebSocket и EventSource payloads.

Browser probe идёт по одной rotating-page на каждый источник, поэтому не блокирует Monitoring. Используется временный отдельный browser profile; BeaverSearch не читает cookies, историю и авторизованный профиль браузера пользователя. Для диагностики создаётся `%LOCALAPPDATA%\BeaverSearch\source-probe.log` с результатом probe и URL захваченных публичных endpoints.

## Источники

### yooma.su

Опрос актуальных публичных routes 5X5, DUELS, AWP, PUBLIC, MINIGAMES, MANIAC, ARENA и JAIL. Старые guessed `DM`/`RETAKE`/numeric routes убраны. Parser ищет live profile links, `data-steamid`/steam-account fields, hydration state и browser-captured API/WS payloads.

### CYBERSHOKE

Опрос публичных CS2 mode pages (`DM`, `DUELS`, `RETAKE`, `5X5`, `2X2`, `BHOP`, `SURF`, `KZ`, `PUBLIC`, `AWP` и др.). HTTP/SSR каталог CYBERSHOKE обрабатывается rolling batch по 8 mode pages. Browser identity probe запускается только для одной rotating-page за цикл и не блокирует возврат server catalog; последние snapshots объединяются примерно за 95 секунд.

## Steam identity

Поддерживаются:

- `7656119...` SteamID64;
- Steam AccountID в явном `steam/account` поле элемента;
- `STEAM_X:Y:Z`;
- `[U:1:AccountID]`;
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
- SteamID/player DOM counters обоих источников;
- `SteamID confirmed`;
- `Unresolved`;
- `Inventory processing`;
- matches;
- page/render progress для обоих сайтов;
- количество захваченных browser API/WS payloads;
- подробный `%LOCALAPPDATA%\BeaverSearch\source-probe.log` для сетевой диагностики.

После `Monitoring OFF` live counters сбрасываются.

## Запуск

1. Распакуйте архив.
2. Запустите `START.bat`.
3. Launcher запускает `STATIC-PREFLIGHT.ps1`, затем `dotnet build`, затем приложение.

При build/runtime ошибке окно остаётся открытым и показывает tail `%LOCALAPPDATA%\BeaverSearch\startup.log`.

Если диагностика пишет, что Edge/Chrome не найден, установите/обновите Microsoft Edge или Google Chrome. На обычной Windows 11 Edge уже присутствует.

## Фильтры

Диапазоны CS2 / Dota 2 / Rust сохраняются сразу в `settings.json`. Проверяется `min <= max`; значения используются тем же объектом settings, который читает scanner, поэтому введённое `1 ₽` влияет на фактический поиск.

## Splash / WPF stability

Startup sequence: `yooma + CYBER → SteamID → Inventory → Price Engine`.

`STATIC-PREFLIGHT.ps1` проверяет XML/XAML, duplicate dependency-property assignments, `StaticResource`, event handlers, assets, `PanningMode`, `Storyboard.TargetName`, binding modes, первый TabItem, PerMonitorV2, отсутствие legacy A2S/GAMEMONITORING resolver и наличие обоих DOM clients.

Static preflight не заменяет нативный WPF compiler/runtime test — его выполняет `START.bat` на Windows.

## Локальные данные

`%LOCALAPPDATA%\BeaverSearch` — settings, cache и `startup.log`. Повреждённые JSON-файлы best-effort переименовываются в `.corrupt-*`.
