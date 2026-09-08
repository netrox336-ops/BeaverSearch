# BeaverSearch v0.4.1 FixSteamID

Дата: 2026-09-07.

## FixSteamID r3 — live source hotfix

- Исправлено зависание первого цикла на `0 / 0`: browser probe больше не блокирует возврат HTTP/server catalog snapshot.
- Edge/Chrome теперь подключается через Chrome DevTools Protocol (CDP), а не только `--dump-dom`.
- Browser probe собирает итоговый DOM, публичные XHR/fetch response bodies, WebSocket frames и EventSource messages.
- Исправлен потенциальный вечный hang при закрытии CDP WebSocket: disposable connection завершается через `Abort()` с коротким wait.
- Удалён `--disable-background-networking`, который мог мешать live-запросам SPA.
- Добавлен короткий scroll sweep для lazy-rendered server/player blocks.
- yooma HTTP-каталог опрашивает только актуальные публичные routes; stale DM/RETAKE/numeric routes удалены.
- CYBERSHOKE server cards нормализуются после React comment/span splitting (`#41`, `15/16 | map`).
- HTTP pages обоих источников загружаются параллельно и возвращаются в UI без ожидания браузера; browser probe идёт по 1 странице на источник в фоне.
- Добавлены counters захваченных API/WS payloads и `%LOCALAPPDATA%\BeaverSearch\source-probe.log` с endpoint telemetry.

## FixSteamID revision — yooma.su + CYBERSHOKE

- Исправлена причина `0 / 0`: обычный HttpClient больше не считается достаточным для JS-rendered monitoring pages.
- `YoomaClient` анализирует HTTP markup + hydration + rendered DOM/network state.
- Добавлен `CybershokeClient` для публичных CS2 mode/server pages CYBERSHOKE.
- CYBERSHOKE работает rolling batch по 8 страниц и объединяет свежие snapshots, чтобы не создавать десятки browser processes за цикл.
- SteamID64 читается непосредственно из player element/state/profile link.
- Добавлена поддержка `data-steamid`, steam/account fields, Steam Community profile URLs и site profile URLs.
- Добавлен `SteamIdentityParser`: Steam AccountID, Steam2 и Steam3 детерминированно переводятся в SteamID64.
- Никнейм больше нигде не используется как resolver личности.
- Один и тот же SteamID, найденный одновременно на yooma.su и CYBERSHOKE, попадает под общий 24h cache и global scan gate.

## UI / diagnostics

- Monitoring и Servers теперь показывают общий каталог yooma.su + CYBERSHOKE.
- Diagnostics разделяет counters обоих источников и показывает DOM/render progress.
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
