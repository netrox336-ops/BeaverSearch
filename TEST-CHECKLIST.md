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
- [ ] При SPA-shell включается Edge/Chrome DOM fallback.
- [ ] В live roster появляется 17-значный SteamID64 из элемента/profile/state.
- [ ] 32-bit Steam AccountID/Steam2/Steam3 при наличии корректно нормализуется в SteamID64.
- [ ] Ник не используется для поиска Steam-профиля.

## CYBERSHOKE

- [ ] Diagnostics показывает CYBERSHOKE batch pages и render count.
- [ ] Mode/server cards появляются в Servers.
- [ ] Player DOM/state с Steam identity превращается в SteamID64.
- [ ] Rolling batches обновляют разные режимы без одновременного запуска десятков browser processes.
- [ ] Один SteamID из CYBERSHOKE и yooma.su не запускает два inventory scans.

## Steam / inventory

- [ ] Confirmed SteamID сразу уходит в profile/inventory pipeline.
- [ ] Публичный CS2 inventory оценивается.
- [ ] Dota 2 / Rust inventory обрабатываются при доступности.
- [ ] Закрытый inventory не создаёт ложный match.
- [ ] До 8 разных SteamID могут быть `Inventory processing` одновременно.
- [ ] Повторный SteamID <24h не запускает новый scan.

## Filters / diagnostics

- [ ] `min/max` редактируются и `min <= max` валидируется.
- [ ] Значение `1 ₽` реально влияет на scanner.
- [ ] `yooma.su live players`, `CYBERSHOKE live players`, `SteamID confirmed`, `Inventory processing` обновляются.
- [ ] После Monitoring OFF live counters = 0.

## GUI / DPI

- [ ] 100%, 125%, 150% Windows DPI.
- [ ] Нет обрезанных нижних элементов.
- [ ] DataGrid selection остаётся тёмным.
- [ ] Hover / pressed / focus состояния согласованы.

## Packaging

- [ ] README / CHANGELOG / TEST-CHECKLIST / THIRD-PARTY-NOTICES соответствуют v0.4.1 FixSteamID.
- [ ] Нет `bin/`, `obj/`, `.vs/`, `.tmp`.
- [ ] ZIP integrity PASS.
- [ ] SHA-256 посчитан после финальной упаковки.
## FixSteamID r3 / live sources

- [ ] После `Monitoring ON` CYBERSHOKE server catalog появляется без ожидания завершения browser probe.
- [ ] `yooma.su` и `CYBERSHOKE` не остаются бесконечно в статусе `Ожидание`.
- [ ] Через 1–2 цикла Diagnostics показывает `render > 0` или понятный browser error.
- [ ] При сетевых live payloads счётчик `API/WS` становится больше нуля.
- [ ] `%LOCALAPPDATA%\BeaverSearch\source-probe.log` создаётся и содержит `OK`, `TIMEOUT` или `ERROR` для probe URL.
- [ ] CYBERSHOKE cards вида `#41 DM` + `15/16 | de_mirage` создают строки серверов.
- [ ] SteamID64/AccountID из `profile`, steam/account field, XHR/fetch, WebSocket или EventSource попадает в Inventory Scanner без nickname resolver.
- [ ] Stop Monitoring отменяет browser/inventory work и UI не зависает.

