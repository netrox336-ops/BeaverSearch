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
- [ ] При SPA-shell включается Edge/Chrome DevTools probe.
- [ ] `.server.loading` не приводит к вечному `Ожидание`: максимум 2 browser probes заняты одновременно, остальные routes откладываются на следующий цикл.
- [ ] В `source-probe.log` появляется строка `cards/loading/clicks/identities/resources/replayJson/networkJson/wsFrames`.
- [ ] `/card/<17-digit SteamID64>` из player element/state распознаётся как подтверждённый SteamID64.
- [ ] JSON из XHR/fetch/DevTools Network response проходит через parser.
- [ ] API на project-поддомене не отбрасывается только из-за отличающегося host.
- [ ] 32-bit Steam AccountID/Steam2/Steam3 при наличии корректно нормализуется в SteamID64.
- [ ] Ник не используется для поиска Steam-профиля.

## CYBERSHOKE

- [ ] Diagnostics показывает CYBERSHOKE batch pages и render count.
- [ ] Mode/server cards появляются в Servers.
- [ ] Player DOM/state с Steam identity превращается в SteamID64.
- [ ] XHR/fetch JSON response body считывается через DevTools `Network.getResponseBody`.
- [ ] Текстовые WebSocket / Socket.IO frames с player identity попадают в parser stream.
- [ ] Rolling snapshots обновляют разные режимы без одновременного запуска десятков browser processes.
- [ ] Один SteamID из CYBERSHOKE и yooma.su не запускает два inventory scans.

## Browser probe / diagnostics

- [ ] На машине с Edge `RenderEngine` показывает `Microsoft Edge DevTools`; с Chrome — `Google Chrome DevTools`.
- [ ] Временный browser profile создаётся в `%TEMP%\BeaverSearch\browser` и удаляется после probe.
- [ ] Пользовательский browser profile/cookies не используются.
- [ ] При недоступном DevTools используется `--dump-dom` fallback с отдельным temporary profile.
- [ ] При занятом BrowserGate страница откладывается, а monitoring cycle не зависает в длинной очереди.
- [ ] `%LOCALAPPDATA%\BeaverSearch\source-probe.log` содержит URL реально увиденных public endpoints.
- [ ] Stop Monitoring отменяет browser/inventory work и UI не зависает.

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

## Packaging / repository

- [ ] README / CHANGELOG / TEST-CHECKLIST / THIRD-PARTY-NOTICES соответствуют v0.4.1 FixSteamID.
- [ ] Нет `bin/`, `obj/`, `.vs/`, `.tmp`.
- [ ] В репозитории отсутствует `.github/workflows` — CI/Actions для разработки намеренно не используется.
- [ ] ZIP integrity PASS для выдаваемой тестовой/финальной сборки.
- [ ] SHA-256 считается после финальной упаковки.
