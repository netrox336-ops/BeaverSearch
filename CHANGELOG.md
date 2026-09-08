# BeaverSearch v0.4.1 FixSteamID

Дата: 2026-09-08.

## Build hotfix — Win32 icon / CS7065

- Исправлен `CSC CS7065: Unable to read beyond the end of the stream` при WPF build.
- Причина: `Assets/Brand/BeaverSearch.ico` был физически обрезан. ICO-directory содержал четвёртый frame с offset `6319`, при этом длина самого файла также была `6319` байт, поэтому Win32 resource compiler пытался читать за концом stream.
- Повреждённый ICO заменён на валидный multi-size icon.
- `STATIC-PREFLIGHT.ps1` теперь валидирует ICO header, directory table и границы каждого frame, чтобы аналогичный дефект больше не проходил как `PASS`.
- `START.bat` очищает `src/BeaverSearch/obj` перед `dotnet build`, чтобы старые WPF `*_wpftmp`/BAML/Win32-resource артефакты не влияли на новый build.
- При build error launcher помечает `startup.log` как runtime-log, который может относиться к предыдущему запуску, чтобы старые XAML ошибки не выглядели текущими.

## FixSteamID r4 — CDP/API/WebSocket resolver

- Исправлен compile-time дефект первой CDP-ревизии: `async`-методы больше не используют запрещённые `ref/out` параметры; счётчик команд хранится в состоянии CDP-сессии.
- `RenderedDomLoader` переведён с одного `--dump-dom` на Chromium DevTools Protocol как основной механизм; `--dump-dom` оставлен fallback.
- Перед навигацией включается `Network` domain DevTools, поэтому BeaverSearch видит реальные `Network.responseReceived` и может прочитать response body через `Network.getResponseBody`.
- Перехватываются `XHR`, `fetch`, `EventSource` и JSON-ответы независимо от того, содержит ли URL слова `api`, `player` или `server`.
- Разрешён захват публичных endpoint на поддоменах того же проекта (например API/cloud host), а статические JS/CSS/images/fonts отбрасываются.
- Добавлен захват текстовых `Network.webSocketFrameReceived`. Socket.IO-префиксы нормализуются перед разбором JSON.
- JSON из DOM/API/WebSocket добавляется во внутренний parser stream и проходит существующий безопасный resolver Steam identity.
- yooma route `/card/<SteamID64>` поддерживается как прямой источник SteamID64 наряду с `/profile/<SteamID64>` и явными steam/account полями.
- Browser probe больше не выстраивает все страницы в длинную очередь: одновременно заняты максимум 2 Chromium slot; остальные страницы откладываются на следующий monitoring cycle. Успешные probes кэшируются, поэтому обход продвигается rolling-образом.
- Для fallback `--dump-dom` используется отдельный temporary profile, чтобы не конфликтовать с уже закрывающимся DevTools browser profile.
- `%LOCALAPPDATA%\BeaverSearch\source-probe.log` пишет `cards/loading/clicks/identities`, resource count, replay JSON, Network JSON, WebSocket frames и обнаруженные endpoint URL.
- Пользовательский Edge/Chrome profile, cookies и авторизация не читаются: каждый probe использует отдельную временную директорию.

## FixSteamID r3 — live source hotfix

- Исправлено зависание первого цикла на `0 / 0`: browser probe больше не должен удерживать все monitoring pages в последовательной очереди.
- Подготовлен общий browser/data layer для yooma.su и CYBERSHOKE.
- Добавлена диагностика `%LOCALAPPDATA%\BeaverSearch\source-probe.log`.
- CYBERSHOKE server cards нормализуются после React comment/span splitting (`#41`, `15/16 | map`).

## FixSteamID revision — yooma.su + CYBERSHOKE

- Старый resolver `A2S/GAMEMONITORING → nickname → SteamID` выведен из runtime pipeline.
- `YoomaClient` анализирует HTTP markup, hydration, rendered DOM и browser-captured public data.
- Добавлен `CybershokeClient` для публичных CS2 mode/server pages CYBERSHOKE.
- SteamID64 читается непосредственно из player element/state/profile link.
- Добавлена поддержка `data-steamid`, steam/account fields, Steam Community profile URLs и site profile/card URLs.
- Добавлен `SteamIdentityParser`: Steam AccountID, Steam2 и Steam3 детерминированно переводятся в SteamID64.
- Никнейм нигде не используется как resolver личности.
- Один и тот же SteamID, найденный одновременно на yooma.su и CYBERSHOKE, попадает под общий 24h cache и global scan gate.

## UI / diagnostics

- Monitoring и Servers показывают общий каталог yooma.su + CYBERSHOKE.
- Diagnostics разделяет counters обоих источников и показывает render progress.
- Settings показывает оба monitoring provider.
- Splash: `yooma + CYBER → SteamID → Inventory → Price Engine`.
- Ручные server entries помечаются `Ручной`, а не `yooma.su profile`.

## Сохранено из 0.4.0 / раннего 0.4.1

- До 8 parallel inventory scans.
- 24h SteamID scan dedup + per-SteamID lock.
- Price cache warmup/cache gates.
- Retry/backoff для `429/5xx`.
- Live min/max filters.
- `PerMonitorV2`.
- XAML duplicate-property preflight и проверки прошлых WPF crash-классов.
- startup.log + launcher tail при ошибке.
